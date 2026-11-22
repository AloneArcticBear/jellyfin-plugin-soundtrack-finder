// #claude
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// Runs analyses (nightly task and manual queue) and keeps the overview in sync.
/// </summary>
public sealed class AnalysisService : IHostedService, IDisposable
{
    /// <summary>Header line of the block in the overview.</summary>
    public const string Marker = "🎵 Songs";

    // header line of the block: "🎵 Songs" (since 1.2.1) or "🎵 Songs:" (1.0 – 1.2), at the start of a line
    private static readonly Regex BlockStart = new(@"(?<=^|\n)🎵 Songs(:|[ \t]*(\r?\n|$))", RegexOptions.Compiled);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _paths;
    private readonly ShazamClient _shazam;
    private readonly ResultStore _store;
    private readonly MusicExporter _exporter;
    private readonly ILogger<AnalysisService> _logger;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly ConcurrentQueue<Guid> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly ConcurrentDictionary<Guid, byte> _selfUpdates = new();
    private CancellationTokenSource _stopCts = new();
    private CancellationTokenSource? _workerCts;
    private Task? _worker;

    /// <summary>Initializes a new instance of the <see cref="AnalysisService"/> class.</summary>
    public AnalysisService(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, IApplicationPaths paths, ShazamClient shazam, ResultStore store, MusicExporter exporter, ILogger<AnalysisService> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _paths = paths;
        _shazam = shazam;
        _store = store;
        _exporter = exporter;
        _logger = logger;
    }

    /// <summary>Gets the name of the title currently analyzed.</summary>
    public string? CurrentItem { get; private set; }

    /// <summary>Gets the current phase.</summary>
    public string Phase { get; private set; } = "Bereit";

    /// <summary>Gets the progress of the current title (0..1).</summary>
    public double CurrentProgress { get; private set; }

    /// <summary>Gets a value indicating whether the nightly task is running.</summary>
    public bool TaskRunning { get; private set; }

    /// <summary>Gets the number of queued manual analyses.</summary>
    public int QueueLength => _queue.Count;

    /// <summary>Gets the ffmpeg path.</summary>
    public string FfmpegPath => _mediaEncoder.EncoderPath;

    /// <summary>Gets the temp path.</summary>
    public string TempPath => _paths.TempDirectory;

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Returns all eligible movies/episodes of the configured libraries (only those the user may see, if given).</summary>
    /// <param name="user">User whose library access and parental control apply (null = everything).</param>
    public IReadOnlyList<BaseItem> GetEligibleItems(Jellyfin.Database.Implementations.Entities.User? user = null)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        };
        var ids = Config.LibraryIds.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
        if (ids.Length > 0)
        {
            query.AncestorIds = ids;
        }

        return _libraryManager.GetItemList(query).Where(i => !string.IsNullOrEmpty(i.Path)).ToList();
    }

    /// <summary>Gets a display name for an item.</summary>
    public static string DisplayName(BaseItem item)
    {
        if (item is Episode ep)
        {
            return $"{ep.SeriesName} S{ep.ParentIndexNumber ?? 0:00}E{ep.IndexNumber ?? 0:00} – {ep.Name}";
        }

        return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
    }

    /// <summary>Queues a manual analysis.</summary>
    public void Enqueue(Guid id)
    {
        if (!_queue.Contains(id))
        {
            _queue.Enqueue(id);
            _queueSignal.Release();
        }
    }

    /// <summary>Stops the running analysis and clears the queue.</summary>
    public void StopAll()
    {
        while (_queue.TryDequeue(out _))
        {
        }

        var old = _stopCts;
        _stopCts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
    }

    /// <summary>Nightly task body.</summary>
    public async Task RunScheduledAsync(IProgress<double> progress, CancellationToken ct)
    {
        var done = _store.All().Where(r => r.Analyzed).Select(r => r.ItemId).ToHashSet();
        var todo = GetEligibleItems().Where(i => !done.Contains(i.Id)).ToList();
        if (Config.MaxItemsPerRun > 0)
        {
            todo = todo.Take(Config.MaxItemsPerRun).ToList();
        }

        TaskRunning = true;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
                try
                {
                    await AnalyzeAsync(todo[i], linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break; // stopped by user
                }

                progress.Report(100.0 * (i + 1) / todo.Count);
            }
        }
        finally
        {
            TaskRunning = false;
        }
    }

    /// <summary>Analyzes one item (exclusive).</summary>
    public async Task AnalyzeAsync(BaseItem item, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var cfg = Config;
        int requests = 0;
        try
        {
            CurrentItem = DisplayName(item);
            CurrentProgress = 0;
            Phase = "Musik suchen";
            _logger.LogInformation("Soundtrack Finder analysiert {Name}", CurrentItem);

            double duration = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0;
            int len = Math.Clamp(cfg.SnippetLengthSeconds, 4, 14);
            var scan = await AudioScanner.ScanAsync(
                FfmpegPath, item.Path, len, Math.Max(5, cfg.SnippetSpacingSeconds), cfg.MusicSensitivity, duration, p => CurrentProgress = p * 0.4, ct).ConfigureAwait(false);
            var snippets = scan.Snippets;

            // too many candidates -> take an even subset
            if (cfg.MaxRequestsPerItem > 0 && snippets.Count > cfg.MaxRequestsPerItem)
            {
                double step = (double)snippets.Count / cfg.MaxRequestsPerItem;
                snippets = Enumerable.Range(0, cfg.MaxRequestsPerItem).Select(i => snippets[(int)(i * step)]).ToList();
            }

            Phase = $"Shazam ({snippets.Count} Ausschnitte)";
            var hits = new List<(double Time, ShazamMatch Match)>();
            var snippetInfos = new List<SnippetInfo>();
            for (int i = 0; i < snippets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var m = await _shazam.RecognizeAsync(snippets[i].SignatureUri, snippets[i].SampleMs, cfg.RequestDelayMs, ct).ConfigureAwait(false);
                    requests++;
                    bool excluded = m != null && IsExcluded(m.Artist, m.Title);
                    snippetInfos.Add(new SnippetInfo
                    {
                        Start = snippets[i].StartSeconds,
                        Matched = m != null,
                        Excluded = excluded,
                        Label = m == null ? null : (string.IsNullOrEmpty(m.Artist) ? m.Title : m.Artist + " – " + m.Title)
                    });
                    if (m != null && !excluded)
                    {
                        hits.Add((snippets[i].StartSeconds, m));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Shazam-Anfrage fehlgeschlagen");
                }

                CurrentProgress = 0.4 + (0.6 * (i + 1) / Math.Max(1, snippets.Count));
            }

            var auto = MergeHits(hits, len, Math.Max(5, cfg.SnippetSpacingSeconds));
            var orphanFiles = new List<string>();
            var result = _store.Update(item.Id, r =>
            {
                r.Name = DisplayName(item);
                r.Analyzed = true;
                r.AnalyzedAt = DateTime.UtcNow;
                r.AnalysisSeconds = sw.Elapsed.TotalSeconds;
                r.Requests = requests;
                r.Error = null;
                r.MusicSegments = scan.Segments;
                r.Snippets = snippetInfos;

                var keep = r.Songs.Where(s => s.Manual).ToList();
                var oldAuto = r.Songs.Where(s => !s.Manual).ToList();
                foreach (var a in auto)
                {
                    // never override what the user corrected or deleted at that position
                    if (keep.Any(m => Overlaps(m, a) && (m.Key == a.Key || (!m.Deleted && OverlapRatio(a, m) >= 0.5))))
                    {
                        continue;
                    }

                    // keep id + export state of the same song found before (stable ids for the UI, no orphaned files)
                    var prev = oldAuto.FirstOrDefault(o => o.Key == a.Key && Overlaps(o, a));
                    if (prev != null)
                    {
                        oldAuto.Remove(prev);
                        a.Id = prev.Id;
                        a.ExportedPath = prev.ExportedPath;
                        a.ExportedSignature = prev.ExportedSignature;
                    }

                    keep.Add(a);
                }

                orphanFiles.AddRange(oldAuto.Where(o => !string.IsNullOrEmpty(o.ExportedPath)).Select(o => o.ExportedPath!));
                r.Songs = keep;
            });

            foreach (var f in orphanFiles)
            {
                _exporter.DeleteFile(f);
            }

            await ApplyOverviewAsync(item, result, ct).ConfigureAwait(false);
            _logger.LogInformation("Soundtrack Finder: {Name} fertig, {Count} Songs", CurrentItem, result.VisibleSongs.Count());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Soundtrack Finder: Fehler bei {Name}", item.Name);
            _store.Update(item.Id, r =>
            {
                r.Name = DisplayName(item);
                r.Analyzed = true;
                r.AnalyzedAt = DateTime.UtcNow;
                r.Error = ex.Message;
                r.Requests = requests;
            });
        }
        finally
        {
            CurrentItem = null;
            CurrentProgress = 0;
            Phase = "Bereit";
            _runLock.Release();
        }
    }

    private static bool Overlaps(SongEntry a, SongEntry b)
        => a.StartSeconds < b.EndSeconds && b.StartSeconds < a.EndSeconds;

    private static double OverlapRatio(SongEntry a, SongEntry b)
    {
        double len = Math.Max(1, a.EndSeconds - a.StartSeconds);
        double ov = Math.Min(a.EndSeconds, b.EndSeconds) - Math.Max(a.StartSeconds, b.StartSeconds);
        return Math.Max(0, ov) / len;
    }

    private static List<SongEntry> MergeHits(List<(double Time, ShazamMatch Match)> hits, int len, int spacing)
    {
        var result = new List<SongEntry>();
        var open = new Dictionary<string, SongEntry>();
        foreach (var (time, m) in hits.OrderBy(h => h.Time))
        {
            if (open.TryGetValue(m.Key, out var e) && time <= e.EndSeconds + (spacing * 3))
            {
                e.EndSeconds = time + len;
                e.Hits++;
                continue;
            }

            e = new SongEntry
            {
                Key = m.Key,
                Artist = m.Artist,
                Title = m.Title,
                AppleMusicUrl = m.AppleMusicUrl,
                ShazamUrl = m.ShazamUrl,
                StartSeconds = time,
                EndSeconds = time + len,
                Hits = 1
            };
            open[m.Key] = e;
            result.Add(e);
        }

        return result;
    }

    /// <summary>
    /// Builds the song block for the overview. Jellyfin's web client renders the overview as Markdown,
    /// so the songs are a Markdown list (one song per line there); in plain-text clients like Streamyfin
    /// the same text is a readable list. Repeats of the same song directly after each other share a line.
    /// </summary>
    public static string BuildBlock(ItemResult r)
    {
        var songs = r.VisibleSongs.ToList();
        if (songs.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<(List<double> Times, string Name)>();
        foreach (var s in songs)
        {
            var name = CleanForOverview(string.IsNullOrWhiteSpace(s.Artist) ? s.Title : s.Artist + " – " + s.Title);
            if (lines.Count > 0 && lines[^1].Name == name)
            {
                lines[^1].Times.Add(s.StartSeconds);
            }
            else
            {
                lines.Add((new List<double> { s.StartSeconds }, name));
            }
        }

        var sb = new StringBuilder();
        sb.Append(Marker);
        if (Config.OverviewLink)
        {
            // right under the heading: Jellyfin-Web only shows the first 6 lines of a description before "Mehr".
            // Opens exactly this title on the Soundtracks page (new tab); rendered as "🎵 Songs ▶ anhören & bearbeiten".
            sb.Append("\n[▶ anhören & bearbeiten](").Append(AppUrl(r.ItemId)).Append(')');
        }

        sb.Append('\n');
        foreach (var (times, name) in lines)
        {
            sb.Append("\n- ").Append(string.Join(", ", times.Select(AudioScanner.FormatTime))).Append(" · ").Append(name);
        }

        return sb.ToString();
    }

    /// <summary>Address of the Soundtracks page, optionally opening one title.</summary>
    public static string AppUrl(Guid? itemId = null)
    {
        var baseUrl = (Config.PublicUrl ?? string.Empty).Trim().TrimEnd('/');
        var url = baseUrl + "/SoundtrackFinder/App";
        return itemId.HasValue ? url + "?item=" + itemId.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture) : url;
    }

    private static string CleanForOverview(string text)
    {
        // one line, and no characters the web client would read as HTML
        var t = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return t.Replace('<', '‹').Replace('>', '›');
    }

    /// <summary>Removes an existing song block (current or 1.0/1.1 format) from an overview.</summary>
    public static string StripBlock(string? overview)
    {
        if (string.IsNullOrEmpty(overview))
        {
            return string.Empty;
        }

        var m = BlockStart.Match(overview);
        return m.Success ? overview[..m.Index].TrimEnd() : overview;
    }

    /// <summary>Writes (or removes) the block into the item's overview.</summary>
    public async Task ApplyOverviewAsync(BaseItem item, ItemResult? r, CancellationToken ct)
    {
        string baseText = StripBlock(item.Overview);
        string block = r != null && Config.WriteToOverview ? BuildBlock(r) : string.Empty;
        string newText = string.IsNullOrEmpty(block) ? baseText : (string.IsNullOrEmpty(baseText) ? block : baseText + "\n\n" + block);
        if (string.Equals(newText, item.Overview ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        item.Overview = newText;
        _selfUpdates[item.Id] = 0;
        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = Task.Delay(5000, CancellationToken.None).ContinueWith(_ => _selfUpdates.TryRemove(item.Id, out byte _), TaskScheduler.Default);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (item == null || _selfUpdates.ContainsKey(item.Id) || !Config.WriteToOverview)
        {
            return;
        }

        var r = _store.Get(item.Id);
        if (r == null || !r.VisibleSongs.Any())
        {
            return;
        }

        if (item.Overview != null && item.Overview.Contains(BuildBlock(r), StringComparison.Ordinal))
        {
            return;
        }

        // metadata refresh replaced the overview -> put the block back
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000).ConfigureAwait(false);
                var fresh = _libraryManager.GetItemById(item.Id) ?? item;
                await ApplyOverviewAsync(fresh, r, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Songliste konnte nicht wieder eingefügt werden");
            }
        });
    }

    /// <summary>Checks a song against the exclusion list (case-insensitive part of "artist – title").</summary>
    public static bool IsExcluded(string? artist, string? title)
    {
        var text = (artist ?? string.Empty) + " – " + (title ?? string.Empty);
        foreach (var raw in Config.ExcludePatterns ?? Array.Empty<string>())
        {
            var p = raw?.Trim();
            if (!string.IsNullOrEmpty(p) && !p.StartsWith('#') && text.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes automatically recognized songs that match the exclusion list from all titles
    /// (songs the user added or edited are kept), deletes their saved files and updates the overviews.
    /// </summary>
    /// <returns>Number of removed songs.</returns>
    public async Task<int> ApplyExclusionsAsync(CancellationToken ct)
    {
        await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int removed = 0;
            foreach (var r in _store.All())
            {
                var hit = r.Songs.Where(s => !s.Manual && !s.Deleted && IsExcluded(s.Artist, s.Title)).Select(s => s.Id).ToHashSet();
                var snippetHit = r.Snippets.Any(x => x.Matched && !x.Excluded && x.Label != null && IsExcluded(x.Label, null));
                if (hit.Count == 0 && !snippetHit)
                {
                    continue;
                }

                var files = new List<string>();
                var updated = _store.Update(r.ItemId, x =>
                {
                    foreach (var s in x.Songs.Where(s => hit.Contains(s.Id) && !s.Manual).ToList())
                    {
                        if (!string.IsNullOrEmpty(s.ExportedPath))
                        {
                            files.Add(s.ExportedPath);
                        }

                        x.Songs.Remove(s);
                    }

                    foreach (var sn in x.Snippets.Where(sn => sn.Matched && sn.Label != null))
                    {
                        sn.Excluded = IsExcluded(sn.Label, null);
                    }
                });
                removed += hit.Count;
                files.ForEach(f => _exporter.DeleteFile(f));

                var item = _libraryManager.GetItemById(r.ItemId);
                if (item != null && hit.Count > 0)
                {
                    await ApplyOverviewAsync(item, updated, ct).ConfigureAwait(false);
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("Soundtrack Finder: {Count} Songs wegen der Ausschlussliste entfernt", removed);
            }

            return removed;
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
        => _ = Task.Run(() => RefreshAllOverviewsAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

    /// <summary>
    /// Applies the exclusion list and brings all overviews in line with the current format and setting
    /// (shortly after start and after every settings change). Only items whose text changes are written.
    /// </summary>
    private async Task RefreshAllOverviewsAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            await ApplyExclusionsAsync(ct).ConfigureAwait(false);
            int changed = 0;
            foreach (var r in _store.All().Where(r => r.Songs.Count > 0))
            {
                ct.ThrowIfCancellationRequested();
                var item = _libraryManager.GetItemById(r.ItemId);
                if (item == null)
                {
                    continue;
                }

                var before = item.Overview;
                await ApplyOverviewAsync(item, r, ct).ConfigureAwait(false);
                if (!string.Equals(before, item.Overview, StringComparison.Ordinal))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                _logger.LogInformation("Soundtrack Finder: Songliste in {Count} Beschreibungen aktualisiert", changed);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Soundtrack Finder: Beschreibungen konnten nicht aktualisiert werden");
        }
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(ct).ConfigureAwait(false);
            if (!_queue.TryDequeue(out var id))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(id);
            if (item == null)
            {
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
            try
            {
                await AnalyzeAsync(item, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Soundtrack Finder: Analyse abgebrochen");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Soundtrack Finder: Fehler in der Warteschlange");
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemUpdated;
        _workerCts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoop(_workerCts.Token), CancellationToken.None);
        _ = Task.Run(() => RefreshAllOverviewsAsync(TimeSpan.FromSeconds(60), _workerCts.Token), CancellationToken.None);
        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        _workerCts?.Cancel();
        _stopCts.Cancel();
        if (_worker != null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _workerCts?.Dispose();
        _stopCts.Dispose();
        _runLock.Dispose();
        _maintenanceLock.Dispose();
        _queueSignal.Dispose();
    }
}
