// #claude
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SoundtrackFinder.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SoundtrackFinder.Api;

/// <summary>Body for creating/updating/restoring a song.</summary>
public class SongDto
{
    /// <summary>Gets or sets the id (only used when restoring a snapshot).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the Shazam key (only used when restoring a snapshot).</summary>
    public string? Key { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the start in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Gets or sets the end in seconds.</summary>
    public double EndSeconds { get; set; }

    /// <summary>Gets or sets the Apple Music link.</summary>
    public string? AppleMusicUrl { get; set; }

    /// <summary>Gets or sets the manual flag (only used when restoring a snapshot).</summary>
    public bool? Manual { get; set; }
}

/// <summary>Body for splitting a song.</summary>
public class SplitDto
{
    /// <summary>Gets or sets the split position in seconds.</summary>
    public double At { get; set; }
}

/// <summary>Body for merging two songs.</summary>
public class MergeDto
{
    /// <summary>Gets or sets the id of the song merged into the addressed one.</summary>
    public string OtherId { get; set; } = string.Empty;
}

/// <summary>Body for switching the "Jetzt läuft" pop-ups.</summary>
public class NowPlayingDto
{
    /// <summary>Gets or sets a value indicating whether pop-ups are wanted.</summary>
    public bool Enabled { get; set; }
}

/// <summary>Body for adding an entry to the exclusion list.</summary>
public class ExcludeDto
{
    /// <summary>Gets or sets the text to exclude (usually the artist).</summary>
    public string Pattern { get; set; } = string.Empty;
}

/// <summary>Body for saving songs into Jellyfin.</summary>
public class ExportDto
{
    /// <summary>Gets or sets the song ids (null/empty = all songs of the title).</summary>
    public List<string>? SongIds { get; set; }
}

/// <summary>
/// API for the dashboard page (admins) and the "Soundtracks" page (all signed-in users).
/// Everything a normal user may do only requires a login; analysis, settings and bulk jobs need an admin.
/// Items are always looked up with the caller's library access and parental control.
/// </summary>
[ApiController]
[Route("SoundtrackFinder")]
[Authorize]
public class SoundtrackController : ControllerBase
{
    private const double MinSongSeconds = 1;

    private readonly AnalysisService _service;
    private readonly ResultStore _store;
    private readonly MusicExporter _exporter;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly UserSettingsStore _userSettings;
    private readonly NowPlayingNotifier _nowPlaying;

    /// <summary>Initializes a new instance of the <see cref="SoundtrackController"/> class.</summary>
    public SoundtrackController(AnalysisService service, ResultStore store, MusicExporter exporter, ILibraryManager libraryManager, IUserManager userManager, UserSettingsStore userSettings, NowPlayingNotifier nowPlaying)
    {
        _userSettings = userSettings;
        _nowPlaying = nowPlaying;
        _service = service;
        _store = store;
        _exporter = exporter;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    // language of the page that sent the request (texts in error messages and warnings)
    private string Lang => Loc.Normalize(Request?.Headers["X-STF-Lang"].ToString()) ?? Loc.ConfigLanguage;

    private Guid CurrentUserId()
        => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var g) ? g : Guid.Empty;

    private string CurrentUserName()
        => User.FindFirst(ClaimTypes.Name)?.Value is { Length: > 0 } n ? n : (User.Identity?.Name ?? string.Empty);

    private User? CurrentUser()
    {
        var id = CurrentUserId();
        return id == Guid.Empty ? null : _userManager.GetUserById(id);
    }

    /// <summary>Looks an item up with the caller's permissions (null if missing or not visible to the caller).</summary>
    private BaseItem? Find(Guid id) => _libraryManager.GetItemById<BaseItem>(id, CurrentUserId());

    private void Touch(SongEntry s)
    {
        s.ModifiedBy = CurrentUserName();
        s.ModifiedAt = DateTime.UtcNow;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Progress overview.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> Status()
    {
        var eligible = _service.GetEligibleItems();
        var results = _store.All().ToDictionary(r => r.ItemId);
        int analyzed = eligible.Count(i => results.TryGetValue(i.Id, out var r) && r.Analyzed);
        var times = results.Values.Where(r => r.Analyzed && r.Error == null && r.AnalysisSeconds > 0).Select(r => r.AnalysisSeconds).ToList();
        double avg = times.Count > 0 ? times.Average() : 0;
        int remaining = eligible.Count - analyzed;
        return new
        {
            total = eligible.Count,
            analyzed,
            remaining,
            withSongs = eligible.Count(i => results.TryGetValue(i.Id, out var r) && r.VisibleSongs.Any()),
            averageSeconds = avg,
            etaSeconds = avg * remaining,
            currentItem = _service.CurrentItem,
            phase = Loc.T(Lang, _service.Phase, ("n", _service.PhaseCount)),
            progress = _service.CurrentProgress,
            taskRunning = _service.TaskRunning,
            queue = _service.QueueLength,
            exportRunning = _exporter.AllRunning,
            exportDone = _exporter.AllDone,
            exportTotal = _exporter.AllTotal,
            exportCurrent = _exporter.Current,
            exportWarning = _exporter.CheckTarget(out bool fatal, Lang),
            exportPossible = !fatal,
            appUrl = AnalysisService.AppUrl()
        };
    }

    /// <summary>The signed-in user (for the Soundtracks page).</summary>
    [HttpGet("Me")]
    public ActionResult<object> Me()
        => new
        {
            name = CurrentUserName(),
            isAdmin = User.IsInRole("Administrator"),
            nowPlayingAvailable = Config.NowPlayingEnabled,
            nowPlaying = _userSettings.NowPlayingEnabled(CurrentUserId())
        };

    /// <summary>Switches the "Jetzt läuft" pop-ups on/off for the signed-in user.</summary>
    [HttpPut("Me/NowPlaying")]
    public ActionResult SetNowPlaying([FromBody] NowPlayingDto dto)
    {
        var id = CurrentUserId();
        if (id == Guid.Empty)
        {
            return BadRequest(Loc.T(Lang, "srv.signedInOnly"));
        }

        _userSettings.SetNowPlaying(id, dto.Enabled);
        return NoContent();
    }

    /// <summary>Diagnosis: what "Jetzt läuft" does on the signed-in user's playing devices.</summary>
    [HttpGet("Me/NowPlaying/Status")]
    public ActionResult<object> NowPlayingStatus()
        => new { enabled = Config.NowPlayingEnabled && _userSettings.NowPlayingEnabled(CurrentUserId()), devices = _nowPlaying.GetStatus(CurrentUserId()) };

    /// <summary>Sends a test pop-up to all devices of the signed-in user.</summary>
    [HttpPost("Me/NowPlaying/Test")]
    public async Task<ActionResult<object>> NowPlayingTest()
        => new { devices = await _nowPlaying.SendTestAsync(CurrentUserId(), Lang).ConfigureAwait(false) };

    /// <summary>Searches songs in all titles the user may see ("In welchen Filmen kommt … vor?").</summary>
    [HttpGet("Search")]
    public ActionResult<object> Search([FromQuery] string? q)
    {
        var query = (q ?? string.Empty).Trim();
        if (query.Length < 2)
        {
            return new { query, total = 0, songs = Array.Empty<object>() };
        }

        var groups = VisibleOccurrences()
            .Where(o => (o.Song.Artist + " – " + o.Song.Title).Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(o => SongGroupKey(o.Song))
            .Select(g => new
            {
                artist = g.First().Song.Artist,
                title = g.First().Song.Title,
                titleCount = g.Select(o => o.ItemId).Distinct().Count(),
                occurrences = g.OrderBy(o => o.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Song.StartSeconds)
                    .Select(o => new { itemId = o.ItemId, itemName = o.ItemName, songId = o.Song.Id, start = o.Song.StartSeconds })
                    .ToList()
            })
            .OrderByDescending(g => g.titleCount)
            .ThenBy(g => g.artist + g.title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new { query, total = groups.Count, songs = groups.Take(60) };
    }

    /// <summary>Statistics over all titles the user may see.</summary>
    [HttpGet("Stats")]
    public ActionResult<object> Stats()
    {
        var occ = VisibleOccurrences();
        var artists = occ.Where(o => !string.IsNullOrWhiteSpace(o.Song.Artist))
            .GroupBy(o => o.Song.Artist.Trim().ToLowerInvariant())
            .Select(g => new
            {
                artist = g.First().Song.Artist.Trim(),
                titles = g.Select(o => o.ItemId).Distinct().Count(),
                songs = g.Select(o => SongGroupKey(o.Song)).Distinct().Count(),
                minutes = Math.Round(g.Sum(o => Math.Max(0, o.Song.EndSeconds - o.Song.StartSeconds)) / 60.0, 1)
            })
            .OrderByDescending(a => a.titles).ThenByDescending(a => a.songs).ThenBy(a => a.artist, StringComparer.OrdinalIgnoreCase)
            .Take(15).ToList();
        var songs = occ.GroupBy(o => SongGroupKey(o.Song))
            .Select(g => new
            {
                artist = g.First().Song.Artist,
                title = g.First().Song.Title,
                titles = g.Select(o => o.ItemId).Distinct().Count(),
                count = g.Count()
            })
            .OrderByDescending(s => s.titles).ThenByDescending(s => s.count).ThenBy(s => s.title, StringComparer.OrdinalIgnoreCase)
            .Take(15).ToList();
        return new
        {
            titlesWithSongs = occ.Select(o => o.ItemId).Distinct().Count(),
            entries = occ.Count,
            distinctSongs = occ.Select(o => SongGroupKey(o.Song)).Distinct().Count(),
            distinctArtists = occ.Where(o => !string.IsNullOrWhiteSpace(o.Song.Artist)).Select(o => o.Song.Artist.Trim().ToLowerInvariant()).Distinct().Count(),
            totalMinutes = Math.Round(occ.Sum(o => Math.Max(0, o.Song.EndSeconds - o.Song.StartSeconds)) / 60.0),
            topArtists = artists,
            topSongs = songs
        };
    }

    private static string SongGroupKey(SongEntry s)
        => ((s.Artist ?? string.Empty).Trim() + "|" + (s.Title ?? string.Empty).Trim()).ToLowerInvariant();

    // all visible songs of all titles the caller may see (library access + parental control)
    private List<(Guid ItemId, string ItemName, SongEntry Song)> VisibleOccurrences()
    {
        var items = _service.GetEligibleItems(CurrentUser()).ToDictionary(i => i.Id, AnalysisService.DisplayName);
        var list = new List<(Guid, string, SongEntry)>();
        foreach (var r in _store.All())
        {
            if (items.TryGetValue(r.ItemId, out var name))
            {
                list.AddRange(r.VisibleSongs.Select(s => (r.ItemId, name, s)));
            }
        }

        return list;
    }

    /// <summary>All movie/series libraries.</summary>
    [HttpGet("Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> Libraries()
        => _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType is null
                || f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.movies
                || f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows
                || f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.mixed)
            .Select(f => new { id = f.ItemId, name = f.Name })
            .ToList();

    /// <summary>All eligible titles with their state.</summary>
    [HttpGet("Items")]
    public ActionResult<object> Items()
    {
        var results = _store.All().ToDictionary(r => r.ItemId);
        return _service.GetEligibleItems(CurrentUser())
            .Select(i =>
            {
                results.TryGetValue(i.Id, out var r);
                return new
                {
                    id = i.Id,
                    name = AnalysisService.DisplayName(i),
                    analyzed = r?.Analyzed ?? false,
                    songs = r?.VisibleSongs.Count() ?? 0,
                    exported = r?.VisibleSongs.Count(s => !string.IsNullOrEmpty(s.ExportedPath)) ?? 0,
                    error = r?.Error,
                    analyzedAt = r?.AnalyzedAt
                };
            })
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Everything the timeline needs for one title.</summary>
    [HttpGet("Items/{id}")]
    public ActionResult<object> Item([FromRoute] Guid id)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        var r = _store.Get(id) ?? new ItemResult { ItemId = id };
        var states = MusicExporter.States(item, r);
        double runtime = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0;
        if (runtime <= 0)
        {
            // fall back to what we know (analysis or songs)
            runtime = Math.Max(
                r.MusicSegments.Select(m => m.End).DefaultIfEmpty(0).Max(),
                r.VisibleSongs.Select(s => s.EndSeconds).DefaultIfEmpty(0).Max() + 60);
        }

        Guid? prevId = null, nextId = null;
        string? seriesName = null;
        if (item is Episode ep)
        {
            seriesName = ep.SeriesName;
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery(CurrentUser())
                {
                    AncestorIds = new[] { ep.SeriesId },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    IsVirtualItem = false,
                    Recursive = true
                })
                .OfType<Episode>()
                .OrderBy(e => e.ParentIndexNumber ?? 0)
                .ThenBy(e => e.IndexNumber ?? 0)
                .ToList();
            int idx = episodes.FindIndex(e => e.Id == ep.Id);
            if (idx > 0)
            {
                prevId = episodes[idx - 1].Id;
            }

            if (idx >= 0 && idx < episodes.Count - 1)
            {
                nextId = episodes[idx + 1].Id;
            }
        }

        return new
        {
            id,
            name = AnalysisService.DisplayName(item),
            isEpisode = item is Episode,
            seriesName,
            prevId,
            nextId,
            runtimeSeconds = runtime,
            analyzed = r.Analyzed,
            analyzedAt = r.AnalyzedAt,
            error = r.Error,
            requests = r.Requests,
            musicSegments = r.MusicSegments.Select(m => new { start = m.Start, end = m.End }),
            snippets = r.Snippets.Select(s => new { start = s.Start, matched = s.Matched, excluded = s.Excluded, label = s.Label }),
            songs = r.VisibleSongs.Select(s => new
            {
                id = s.Id,
                key = s.Key,
                start = s.StartSeconds,
                end = s.EndSeconds,
                artist = s.Artist,
                title = s.Title,
                appleMusicUrl = s.AppleMusicUrl,
                shazamUrl = s.ShazamUrl,
                manual = s.Manual,
                modifiedBy = s.ModifiedBy,
                modifiedAt = s.ModifiedAt,
                hits = s.Hits,
                exportState = states.TryGetValue(s.Id, out var st) ? st : "none"
            }),
            exportWarning = _exporter.CheckTarget(out bool fatal, Lang),
            exportPossible = !fatal
        };
    }

    /// <summary>Queues an analysis now ("Neu analysieren" / test button).</summary>
    [HttpPost("Items/{id}/Analyze")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Analyze([FromRoute] Guid id)
    {
        if (Find(id) == null)
        {
            return NotFound();
        }

        _service.Enqueue(id);
        return NoContent();
    }

    /// <summary>Queues all not yet analyzed titles now.</summary>
    [HttpPost("AnalyzeAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult AnalyzeAll()
    {
        _service.EnqueueAll(includeAnalyzed: false);
        return NoContent();
    }

    /// <summary>Queues every title for a new analysis ("Alle neu analysieren"); manual songs are kept.</summary>
    [HttpPost("ReanalyzeAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> ReanalyzeAll() => new { queued = _service.EnqueueAll(includeAnalyzed: true) };

    /// <summary>Stops the running analysis / saving and clears the queue.</summary>
    [HttpPost("Stop")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Stop()
    {
        _service.StopAll();
        _exporter.StopExportAll();
        return NoContent();
    }

    /// <summary>Adds a song manually. Returns the new id.</summary>
    [HttpPost("Items/{id}/Songs")]
    public async Task<ActionResult<object>> AddSong([FromRoute] Guid id, [FromBody] SongDto dto)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        var (start, end) = Clamp(item, dto.StartSeconds, dto.EndSeconds);
        var song = new SongEntry
        {
            Artist = (dto.Artist ?? string.Empty).Trim(),
            Title = (dto.Title ?? string.Empty).Trim(),
            StartSeconds = start,
            EndSeconds = end,
            AppleMusicUrl = string.IsNullOrWhiteSpace(dto.AppleMusicUrl) ? null : dto.AppleMusicUrl.Trim(),
            Manual = true
        };
        Touch(song);
        var r = _store.Update(id, r =>
        {
            if (string.IsNullOrEmpty(r.Name))
            {
                r.Name = AnalysisService.DisplayName(item);
            }

            r.Songs.Add(song);
        });
        await _service.ApplyOverviewAsync(item, r, CancellationToken.None).ConfigureAwait(false);
        return new { id = song.Id };
    }

    /// <summary>Edits a song (marks it manual, never overwritten).</summary>
    [HttpPut("Items/{id}/Songs/{songId}")]
    public async Task<ActionResult> EditSong([FromRoute] Guid id, [FromRoute] string songId, [FromBody] SongDto dto)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        var (start, end) = Clamp(item, dto.StartSeconds, dto.EndSeconds);
        bool found = false;
        var r = _store.Update(id, r =>
        {
            var s = r.Songs.FirstOrDefault(x => x.Id == songId && !x.Deleted);
            if (s == null)
            {
                return;
            }

            found = true;
            s.Artist = (dto.Artist ?? string.Empty).Trim();
            s.Title = (dto.Title ?? string.Empty).Trim();
            s.StartSeconds = start;
            s.EndSeconds = end;
            s.AppleMusicUrl = string.IsNullOrWhiteSpace(dto.AppleMusicUrl) ? null : dto.AppleMusicUrl.Trim();
            s.Manual = true;
            Touch(s);
        });
        if (!found)
        {
            return NotFound();
        }

        await _service.ApplyOverviewAsync(item, r, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Deletes a song (kept as tombstone so a re-analysis does not bring it back) and its saved file.</summary>
    [HttpDelete("Items/{id}/Songs/{songId}")]
    public async Task<ActionResult> DeleteSong([FromRoute] Guid id, [FromRoute] string songId)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        var files = new List<string>();
        var r = _store.Update(id, r =>
        {
            var s = r.Songs.FirstOrDefault(x => x.Id == songId && !x.Deleted);
            if (s != null)
            {
                Touch(s);
                RemoveSong(r, s, files);
            }
        });
        files.ForEach(f => _exporter.DeleteFile(f));
        await _service.ApplyOverviewAsync(item, r, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Splits a song at a position into two songs. Returns the id of the second part.</summary>
    [HttpPost("Items/{id}/Songs/{songId}/Split")]
    public async Task<ActionResult<object>> Split([FromRoute] Guid id, [FromRoute] string songId, [FromBody] SplitDto dto)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        string? newId = null;
        var r = _store.Update(id, r =>
        {
            var s = r.Songs.FirstOrDefault(x => x.Id == songId && !x.Deleted);
            if (s == null || dto.At < s.StartSeconds + MinSongSeconds || dto.At > s.EndSeconds - MinSongSeconds)
            {
                return;
            }

            var second = new SongEntry
            {
                Key = s.Key,
                Artist = s.Artist,
                Title = s.Title,
                AppleMusicUrl = s.AppleMusicUrl,
                ShazamUrl = s.ShazamUrl,
                StartSeconds = Math.Round(dto.At, 1),
                EndSeconds = s.EndSeconds,
                Manual = true
            };
            s.EndSeconds = Math.Round(dto.At, 1);
            s.Manual = true;
            Touch(s);
            Touch(second);
            r.Songs.Add(second);
            newId = second.Id;
        });
        if (newId == null)
        {
            return BadRequest(Loc.T(Lang, "srv.splitInside"));
        }

        await _service.ApplyOverviewAsync(item, r, CancellationToken.None).ConfigureAwait(false);
        return new { id = newId };
    }

    /// <summary>Merges another song into this one (covers both ranges, keeps this name).</summary>
    [HttpPost("Items/{id}/Songs/{songId}/Merge")]
    public async Task<ActionResult> Merge([FromRoute] Guid id, [FromRoute] string songId, [FromBody] MergeDto dto)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        bool ok = false;
        var files = new List<string>();
        var r = _store.Update(id, r =>
        {
            var a = r.Songs.FirstOrDefault(x => x.Id == songId && !x.Deleted);
            var b = r.Songs.FirstOrDefault(x => x.Id == dto.OtherId && !x.Deleted);
            if (a == null || b == null || a == b)
            {
                return;
            }

            a.StartSeconds = Math.Min(a.StartSeconds, b.StartSeconds);
            a.EndSeconds = Math.Max(a.EndSeconds, b.EndSeconds);
            a.Hits += b.Hits;
            a.AppleMusicUrl ??= b.AppleMusicUrl;
            a.Manual = true;
            Touch(a);
            RemoveSong(r, b, files);
            ok = true;
        });
        if (!ok)
        {
            return NotFound();
        }

        files.ForEach(f => _exporter.DeleteFile(f));
        await _service.ApplyOverviewAsync(item, r, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Restores a complete song list (used for "Rückgängig").</summary>
    [HttpPut("Items/{id}/Songs")]
    public async Task<ActionResult> Restore([FromRoute] Guid id, [FromBody] List<SongDto> songs)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        var files = new List<string>();
        var wanted = songs.Where(d => !string.IsNullOrEmpty(d.Id)).GroupBy(d => d.Id!).ToDictionary(g => g.Key, g => g.First());
        var r = _store.Update(id, r =>
        {
            foreach (var s in r.Songs.Where(s => !s.Deleted && !wanted.ContainsKey(s.Id)).ToList())
            {
                RemoveSong(r, s, files);
            }

            foreach (var d in wanted.Values)
            {
                var (start, end) = Clamp(item, d.StartSeconds, d.EndSeconds);
                var s = r.Songs.FirstOrDefault(x => x.Id == d.Id);
                if (s == null)
                {
                    s = new SongEntry { Id = d.Id!, Key = d.Key ?? string.Empty };
                    r.Songs.Add(s);
                }

                s.Deleted = false;
                s.Artist = (d.Artist ?? string.Empty).Trim();
                s.Title = (d.Title ?? string.Empty).Trim();
                s.StartSeconds = start;
                s.EndSeconds = end;
                s.AppleMusicUrl = string.IsNullOrWhiteSpace(d.AppleMusicUrl) ? null : d.AppleMusicUrl.Trim();
                s.Manual = d.Manual ?? true;
                Touch(s);
            }
        });
        files.ForEach(f => _exporter.DeleteFile(f));
        await _service.ApplyOverviewAsync(item, r, CancellationToken.None).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Cuts the scene of a song from the own media file as MP3 (download).</summary>
    [HttpGet("Items/{id}/Songs/{songId}/Mp3")]
    [Produces("audio/mpeg")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Mp3([FromRoute] Guid id, [FromRoute] string songId, CancellationToken ct)
    {
        var item = Find(id);
        var s = _store.Get(id)?.Songs.FirstOrDefault(x => x.Id == songId && !x.Deleted);
        if (item == null || s == null)
        {
            return NotFound();
        }

        var bytes = await _exporter.RenderDownloadAsync(item, s, _service.TempPath, ct).ConfigureAwait(false);
        var name = string.Join("_", $"{s.Artist} - {s.Title} ({item.Name}).mp3".Split(System.IO.Path.GetInvalidFileNameChars()));
        return File(bytes, "audio/mpeg", name);
    }

    /// <summary>
    /// Streams the audio of the title from <paramref name="start"/> as MP3 ("Probehören").
    /// Plays until <paramref name="end"/> (or the end of the title) or until the client stops; ffmpeg is
    /// throttled to twice real time so stopping early costs almost nothing.
    /// Auth: header or ?ApiKey= (an audio element cannot send headers).
    /// </summary>
    [HttpGet("Items/{id}/Stream")]
    [Produces("audio/mpeg")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Stream([FromRoute] Guid id, [FromQuery] double start, [FromQuery] double? end)
    {
        var item = Find(id);
        if (item == null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        var ct = HttpContext.RequestAborted;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        start = Math.Max(0, start);
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString("0.###", inv),
            "-readrate", "2", "-readrate_initial_burst", "8",
            "-i", item.Path
        };
        if (end.HasValue && end.Value > start)
        {
            args.AddRange(new[] { "-t", (end.Value - start).ToString("0.###", inv) });
        }

        args.AddRange(new[] { "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "160k", "-f", "mp3", "pipe:1" });

        var psi = new System.Diagnostics.ProcessStartInfo(_service.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        Response.ContentType = "audio/mpeg";
        Response.Headers.CacheControl = "no-store";
        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(Response.Body, 16 * 1024, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException)
        {
            // listener stopped / connection closed
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await errTask.ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    /// <summary>Saves songs of a title into the Jellyfin music folder.</summary>
    [HttpPost("Items/{id}/Export")]
    public async Task<ActionResult<object>> Export([FromRoute] Guid id, [FromBody] ExportDto? dto, CancellationToken ct)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        try
        {
            var ids = dto?.SongIds is { Count: > 0 } list ? list : null;
            int written = await _exporter.ExportItemAsync(item, ids, ct, Lang).ConfigureAwait(false);
            return new { written };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Removes saved songs of a title from Jellyfin (deletes the MP3s); the songs stay on the timeline.</summary>
    [HttpPost("Items/{id}/Unexport")]
    public async Task<ActionResult<object>> Unexport([FromRoute] Guid id, [FromBody] ExportDto? dto, CancellationToken ct)
    {
        var item = Find(id);
        if (item == null)
        {
            return NotFound();
        }

        var ids = dto?.SongIds is { Count: > 0 } list ? list : null;
        int removed = await _exporter.UnexportItemAsync(item, ids, ct).ConfigureAwait(false);
        return new { removed };
    }

    /// <summary>Adds an entry to the exclusion list and removes matching recognized songs everywhere.</summary>
    [HttpPost("Exclude")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> Exclude([FromBody] ExcludeDto dto, CancellationToken ct)
    {
        var pattern = (dto.Pattern ?? string.Empty).Trim();
        var plugin = Plugin.Instance;
        if (pattern.Length < 2 || plugin == null)
        {
            return BadRequest(Loc.T(Lang, "srv.minChars"));
        }

        var cfg = plugin.Configuration;
        if (!(cfg.ExcludePatterns ?? Array.Empty<string>()).Any(p => string.Equals(p?.Trim(), pattern, StringComparison.OrdinalIgnoreCase)))
        {
            cfg.ExcludePatterns = (cfg.ExcludePatterns ?? Array.Empty<string>()).Append(pattern).ToArray();
            plugin.UpdateConfiguration(cfg);
        }

        int removed = await _service.ApplyExclusionsAsync(ct).ConfigureAwait(false);
        return new { pattern, removed };
    }

    /// <summary>Saves the songs of all titles into the Jellyfin music folder (background).</summary>
    [HttpPost("ExportAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult ExportAll()
    {
        try
        {
            _exporter.StartExportAll();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private static void RemoveSong(ItemResult r, SongEntry s, List<string> filesToDelete)
    {
        if (!string.IsNullOrEmpty(s.ExportedPath))
        {
            filesToDelete.Add(s.ExportedPath);
            s.ExportedPath = null;
            s.ExportedSignature = null;
        }

        if (string.IsNullOrEmpty(s.Key))
        {
            r.Songs.Remove(s);
        }
        else
        {
            s.Deleted = true;
            s.Manual = true;
        }
    }

    private static (double Start, double End) Clamp(BaseItem item, double start, double end)
    {
        double max = item.RunTimeTicks.HasValue ? Math.Max(2, TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds) : double.MaxValue;
        start = Math.Round(Math.Clamp(start, 0, Math.Max(0, max - MinSongSeconds)), 1);
        if (end <= start)
        {
            end = start + 30;
        }

        end = Math.Round(Math.Clamp(Math.Max(end, start + MinSongSeconds), start + MinSongSeconds, max), 1);
        return (start, end);
    }
}
