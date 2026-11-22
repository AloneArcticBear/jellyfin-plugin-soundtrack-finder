// #claude
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// Saves song sections (cut from the user's own media file) as tagged MP3s into a
/// folder that Jellyfin reads as a music library.
/// </summary>
public sealed class MusicExporter : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ResultStore _store;
    private readonly ILogger<MusicExporter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Timer _refreshTimer;
    private CancellationTokenSource? _allCts;

    /// <summary>Initializes a new instance of the <see cref="MusicExporter"/> class.</summary>
    public MusicExporter(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IMediaEncoder mediaEncoder, IApplicationPaths paths, ResultStore store, ILogger<MusicExporter> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _refreshTimer = new Timer(_ => RefreshMusicLibraries(), null, Timeout.Infinite, Timeout.Infinite);
        _mediaEncoder = mediaEncoder;
        _store = store;
        _logger = logger;
        _ = paths;
    }

    /// <summary>Gets a value indicating whether "save all" is running.</summary>
    public bool AllRunning { get; private set; }

    /// <summary>Gets the number of titles done in "save all".</summary>
    public int AllDone { get; private set; }

    /// <summary>Gets the number of titles in "save all".</summary>
    public int AllTotal { get; private set; }

    /// <summary>Gets the title currently saved.</summary>
    public string? Current { get; private set; }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private static string Root => (Config.MusicExportPath ?? string.Empty).Trim().TrimEnd('/', '\\');

    /// <summary>Export state per song id: "none", "saved" or "stale" (edited, renumbered or file missing).</summary>
    public static Dictionary<string, string> States(BaseItem item, ItemResult r)
    {
        var songs = r.VisibleSongs.ToList();
        var root = Root;
        var album = string.IsNullOrEmpty(root) ? null : AlbumInfo.For(item, root);
        var result = new Dictionary<string, string>();
        for (int i = 0; i < songs.Count; i++)
        {
            var s = songs[i];
            if (string.IsNullOrEmpty(s.ExportedPath))
            {
                result[s.Id] = "none";
                continue;
            }

            bool ok = File.Exists(s.ExportedPath) && s.ExportedSignature == s.ContentSignature
                && (album == null || string.Equals(s.ExportedPath, Path.Combine(album.Directory, album.FileName(i + 1, s)), StringComparison.Ordinal));
            result[s.Id] = ok ? "saved" : "stale";
        }

        return result;
    }

    /// <summary>Checks the target folder. Returns null if everything is fine, otherwise a German warning.</summary>
    /// <param name="fatal">True if saving is impossible.</param>
    public string? CheckTarget(out bool fatal)
    {
        fatal = true;
        var root = Root;
        if (string.IsNullOrEmpty(root))
        {
            return "Kein Zielordner eingestellt (Einstellungen → „Zielordner für Songs“).";
        }

        if (!Path.IsPathRooted(root))
        {
            return "Der Zielordner muss ein absoluter Pfad im Container sein, z. B. /media/Soundtracks.";
        }

        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".stf_write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Zielordner {root} ist nicht beschreibbar ({ex.Message}). Ist er im Container ohne :ro eingebunden?";
        }

        fatal = false;
        var covered = _libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType == CollectionTypeOptions.music)
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Any(loc => IsUnder(root, loc));
        return covered
            ? null
            : $"Keine Musik-Bibliothek enthält {root}. Die Dateien werden gespeichert, erscheinen aber erst, wenn du in Jellyfin eine Bibliothek vom Typ „Musik“ mit diesem Ordner anlegst.";
    }

    /// <summary>Saves songs of one title. With <paramref name="songIds"/> only those (plus renumbered ones), otherwise all.</summary>
    /// <returns>Number of written files.</returns>
    public async Task<int> ExportItemAsync(BaseItem item, ICollection<string>? songIds, CancellationToken ct)
    {
        CheckTarget(out bool fatal);
        if (fatal)
        {
            throw new InvalidOperationException(CheckTarget(out _));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = _store.Get(item.Id);
            if (r == null)
            {
                return 0;
            }

            var songs = r.VisibleSongs.ToList();
            var album = AlbumInfo.For(item, Root);
            int written = 0;
            var results = new Dictionary<string, (string Path, string Sig)>();
            string? poster = album.PosterPath != null && File.Exists(album.PosterPath) ? album.PosterPath : null;

            for (int i = 0; i < songs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = songs[i];
                int track = i + 1;
                string target = Path.Combine(album.Directory, album.FileName(track, s));
                bool requested = songIds == null || songIds.Contains(s.Id);
                bool alreadyExported = !string.IsNullOrEmpty(s.ExportedPath);
                if (!requested && !alreadyExported)
                {
                    continue;
                }

                bool upToDate = alreadyExported
                    && string.Equals(s.ExportedPath, target, StringComparison.Ordinal)
                    && s.ExportedSignature == s.ContentSignature
                    && File.Exists(target);
                if (upToDate)
                {
                    continue;
                }

                // only renumbered/changed songs that were saved before are rewritten without being asked
                Directory.CreateDirectory(album.Directory);
                await WriteSongAsync(item, s, album, track, songs.Count, poster, target, ct).ConfigureAwait(false);
                if (alreadyExported && !string.Equals(s.ExportedPath, target, StringComparison.Ordinal))
                {
                    DeleteFile(s.ExportedPath!, report: false);
                }

                results[s.Id] = (target, s.ContentSignature);
                written++;
            }

            if (poster != null && written > 0)
            {
                await WriteFolderImageAsync(poster, album.Directory, ct).ConfigureAwait(false);
            }

            if (results.Count > 0)
            {
                _store.Update(item.Id, res =>
                {
                    foreach (var song in res.Songs)
                    {
                        if (results.TryGetValue(song.Id, out var v))
                        {
                            song.ExportedPath = v.Path;
                            song.ExportedSignature = v.Sig;
                        }
                    }
                });
                ScheduleLibraryRefresh();
            }

            return written;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Saves all titles that have songs (background, can be stopped).</summary>
    public void StartExportAll()
    {
        if (AllRunning)
        {
            return;
        }

        CheckTarget(out bool fatal);
        if (fatal)
        {
            throw new InvalidOperationException(CheckTarget(out _));
        }

        _allCts = new CancellationTokenSource();
        var ct = _allCts.Token;
        var ids = _store.All().Where(r => r.VisibleSongs.Any()).Select(r => r.ItemId).ToList();
        AllRunning = true;
        AllDone = 0;
        AllTotal = ids.Count;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var id in ids)
                    {
                        ct.ThrowIfCancellationRequested();
                        var item = _libraryManager.GetItemById(id);
                        if (item != null)
                        {
                            Current = AnalysisService.DisplayName(item);
                            try
                            {
                                await ExportItemAsync(item, null, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex, "Soundtrack Finder: Speichern von {Name} fehlgeschlagen", item.Name);
                            }
                        }

                        AllDone++;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Soundtrack Finder: Speichern abgebrochen");
                }
                finally
                {
                    AllRunning = false;
                    Current = null;
                }
            },
            CancellationToken.None);
    }

    /// <summary>Stops "save all".</summary>
    public void StopExportAll() => _allCts?.Cancel();

    /// <summary>Removes the saved file of a song (if any) and returns true if something was deleted.</summary>
    public bool DeleteFile(string path, bool report = true)
    {
        var root = Root;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root) || !IsUnder(path, root))
        {
            return false; // never touch anything outside the export folder
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            var dir = Path.GetDirectoryName(path);

            // clean up empty album (and series) folders
            while (!string.IsNullOrEmpty(dir) && IsUnder(dir, root) && !PathEquals(dir, root))
            {
                var rest = Directory.EnumerateFileSystemEntries(dir).Where(f => !Path.GetFileName(f).Equals("folder.jpg", StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.Count > 0)
                {
                    break;
                }

                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    File.Delete(f);
                }

                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }

            if (report)
            {
                ScheduleLibraryRefresh();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Soundtrack Finder: Datei {Path} konnte nicht gelöscht werden", path);
            return false;
        }
    }

    /// <summary>
    /// Re-scans the music libraries that contain the export folder, like "Bibliothek scannen" in the dashboard.
    /// Several changes within a few seconds are combined into one scan.
    /// </summary>
    public void ScheduleLibraryRefresh() => _refreshTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);

    private void RefreshMusicLibraries()
    {
        try
        {
            var root = Root;
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            foreach (var folder in _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionTypeOptions.music
                    && (f.Locations ?? Array.Empty<string>()).Any(loc => IsUnder(root, loc) || IsUnder(loc, root))))
            {
                if (!Guid.TryParse(folder.ItemId, out var libId))
                {
                    continue;
                }

                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ImageRefreshMode = MetadataRefreshMode.Default
                };
                _providerManager.QueueRefresh(libId, options, RefreshPriority.High);
                _logger.LogInformation("Soundtrack Finder: Musik-Bibliothek {Name} wird neu eingelesen", folder.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Soundtrack Finder: Musik-Bibliothek konnte nicht neu eingelesen werden");
        }
    }

    private async Task WriteSongAsync(BaseItem item, SongEntry s, AlbumInfo album, int track, int trackCount, string? poster, string target, CancellationToken ct)
    {
        var cfg = Config;
        double start = Math.Max(0, s.StartSeconds);
        double dur = Math.Max(1, s.EndSeconds - s.StartSeconds);
        double fade = Math.Clamp(cfg.ExportFadeSeconds, 0, 10);
        var tmp = Path.Combine(album.Directory, ".stf_" + Guid.NewGuid().ToString("N") + ".mp3");

        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", start.ToString("0.###", Inv), "-i", item.Path
        };
        if (poster != null)
        {
            args.AddRange(new[] { "-i", poster });
        }

        args.AddRange(new[] { "-t", dur.ToString("0.###", Inv), "-map", "0:a:0" });
        if (poster != null)
        {
            args.AddRange(new[]
            {
                "-map", "1:v:0", "-c:v", "mjpeg", "-q:v", "3", "-filter:v", "scale='min(800,iw)':-2",
                "-disposition:v:0", "attached_pic", "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)"
            });
        }

        args.AddRange(new[] { "-ac", "2", "-c:a", "libmp3lame", "-b:a", Math.Clamp(cfg.ExportBitrateKbps, 96, 320).ToString(Inv) + "k" });
        if (fade > 0 && dur > fade * 3)
        {
            args.AddRange(new[]
            {
                "-filter:a",
                $"afade=t=in:st=0:d={fade.ToString("0.##", Inv)},afade=t=out:st={(dur - fade).ToString("0.###", Inv)}:d={fade.ToString("0.##", Inv)}"
            });
        }

        void Meta(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.Add("-metadata");
                args.Add(key + "=" + value);
            }
        }

        Meta("title", string.IsNullOrWhiteSpace(s.Title) ? "Unbekannter Song" : s.Title);
        Meta("artist", string.IsNullOrWhiteSpace(s.Artist) ? "Unbekannt" : s.Artist);
        Meta("album", album.Album);
        Meta("album_artist", string.IsNullOrWhiteSpace(cfg.ExportAlbumArtist) ? "Soundtracks" : cfg.ExportAlbumArtist);
        Meta("track", track.ToString(Inv) + "/" + trackCount.ToString(Inv));
        Meta("disc", album.Disc?.ToString(Inv));
        Meta("date", album.Year?.ToString(Inv));
        Meta("genre", "Soundtrack");
        Meta("comment", album.Comment(s));
        args.AddRange(new[] { "-id3v2_version", "3", "-f", "mp3", tmp });

        try
        {
            await AudioScanner.RunFfmpegAsync(_mediaEncoder.EncoderPath, args, ct).ConfigureAwait(false);
            File.Move(tmp, target, true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private async Task WriteFolderImageAsync(string poster, string dir, CancellationToken ct)
    {
        var target = Path.Combine(dir, "folder.jpg");
        if (File.Exists(target))
        {
            return;
        }

        var tmp = Path.Combine(dir, ".stf_folder_" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            await AudioScanner.RunFfmpegAsync(
                _mediaEncoder.EncoderPath,
                new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", poster, "-frames:v", "1", "-filter:v", "scale='min(1000,iw)':-2", "-q:v", "3", "-f", "image2", tmp },
                ct).ConfigureAwait(false);
            File.Move(tmp, target, true);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "folder.jpg konnte nicht erstellt werden");
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);

    private static bool IsUnder(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd('/', '\\') + "/";
        var r = Path.GetFullPath(root).TrimEnd('/', '\\') + "/";
        return p.StartsWith(r, StringComparison.Ordinal);
    }

    /// <summary>Makes a string safe and readable as a file or folder name (works on Linux, Windows/SMB and macOS).</summary>
    internal static string Safe(string? name, int max = 100)
    {
        var sb = new StringBuilder();
        var text = (name ?? string.Empty).Trim().Replace(": ", " - ", StringComparison.Ordinal);
        foreach (var c in text)
        {
            switch (c)
            {
                case ':':
                case '/':
                case '\\':
                case '|':
                    sb.Append('-');
                    break;
                case '?':
                case '*':
                case '"':
                case '<':
                case '>':
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim().TrimEnd('.').Trim();
        if (s.Length > max)
        {
            s = s[..max].Trim();
        }

        return string.IsNullOrEmpty(s) || s.StartsWith('.') ? "_" + s : s;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _allCts?.Dispose();
        _refreshTimer.Dispose();
        _writeLock.Dispose();
    }

    /// <summary>Where and how the songs of one title are stored.</summary>
    internal sealed class AlbumInfo
    {
        private AlbumInfo(string directory, string album, int? disc, int? year, string? posterPath, string? episodeTag, string sceneName)
        {
            Directory = directory;
            Album = album;
            Disc = disc;
            Year = year;
            PosterPath = posterPath;
            EpisodeTag = episodeTag;
            SceneName = sceneName;
        }

        public string Directory { get; }

        public string Album { get; }

        public int? Disc { get; }

        public int? Year { get; }

        public string? PosterPath { get; }

        public string? EpisodeTag { get; }

        public string SceneName { get; }

        public static AlbumInfo For(BaseItem item, string root)
        {
            if (item is Episode ep)
            {
                string series = string.IsNullOrWhiteSpace(ep.SeriesName) ? "Serie" : ep.SeriesName;
                int season = ep.ParentIndexNumber ?? 0;
                int episode = ep.IndexNumber ?? 0;
                string seasonName = season == 0 ? "Specials" : "Staffel " + season.ToString(Inv);
                string? poster = ImagePath(ep.Season) ?? ImagePath(ep.Series) ?? ImagePath(ep);
                string tag = $"S{season:00}E{episode:00}";
                return new AlbumInfo(
                    Path.Combine(root, Safe(series), Safe(seasonName)),
                    $"{series} – {seasonName}",
                    episode > 0 ? episode : null,
                    ep.Series?.ProductionYear ?? ep.ProductionYear,
                    poster,
                    tag,
                    $"{tag} {ep.Name}");
            }

            string name = item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
            return new AlbumInfo(Path.Combine(root, Safe(name)), name, null, item.ProductionYear, ImagePath(item), null, item.Name);
        }

        public string FileName(int track, SongEntry s)
        {
            string artist = string.IsNullOrWhiteSpace(s.Artist) ? "Unbekannt" : s.Artist;
            string title = string.IsNullOrWhiteSpace(s.Title) ? "Unbekannter Song" : s.Title;
            string prefix = EpisodeTag != null ? EpisodeTag + " - " : string.Empty;
            return Safe($"{prefix}{track:00} - {artist} - {title}", 150) + ".mp3";
        }

        public string Comment(SongEntry s)
            => $"Szene {AudioScanner.FormatTime(s.StartSeconds)}–{AudioScanner.FormatTime(s.EndSeconds)} aus {SceneName} (Soundtrack Finder)";

        private static string? ImagePath(BaseItem? item)
        {
            if (item == null)
            {
                return null;
            }

            try
            {
                var p = item.GetImagePath(ImageType.Primary, 0);
                return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
