// #claude
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>One recognized (or manually added) song.</summary>
public class SongEntry
{
    /// <summary>Gets or sets the entry id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the Shazam track key (empty for manual songs).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the start in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Gets or sets the end in seconds.</summary>
    public double EndSeconds { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the Apple Music link.</summary>
    public string? AppleMusicUrl { get; set; }

    /// <summary>Gets or sets the Shazam link.</summary>
    public string? ShazamUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether the user created or edited the entry (never overwritten).</summary>
    public bool Manual { get; set; }

    /// <summary>Gets or sets a value indicating whether the user deleted the entry (kept as tombstone).</summary>
    public bool Deleted { get; set; }

    /// <summary>Gets or sets how many snippets matched.</summary>
    public int Hits { get; set; }

    /// <summary>Gets or sets the name of the user who last changed the entry.</summary>
    public string? ModifiedBy { get; set; }

    /// <summary>Gets or sets when the entry was last changed by a user.</summary>
    public DateTime? ModifiedAt { get; set; }

    /// <summary>Gets or sets the path of the file saved into the Jellyfin music folder.</summary>
    public string? ExportedPath { get; set; }

    /// <summary>Gets or sets the content signature at export time (to detect later edits).</summary>
    public string? ExportedSignature { get; set; }

    /// <summary>Gets the signature of the exportable content.</summary>
    [JsonIgnore]
    public string ContentSignature => string.Join(
        "|",
        StartSeconds.ToString("0.#", CultureInfo.InvariantCulture),
        EndSeconds.ToString("0.#", CultureInfo.InvariantCulture),
        Artist,
        Title);
}

/// <summary>A section the scanner considered music.</summary>
public class MusicSegment
{
    /// <summary>Gets or sets the start in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Gets or sets the end in seconds.</summary>
    public double End { get; set; }
}

/// <summary>One snippet that was sent to Shazam.</summary>
public class SnippetInfo
{
    /// <summary>Gets or sets the snippet start in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Gets or sets a value indicating whether Shazam recognized something.</summary>
    public bool Matched { get; set; }

    /// <summary>Gets or sets the recognized "Artist – Title" (if any).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets a value indicating whether the match was dropped because of the exclusion list.</summary>
    public bool Excluded { get; set; }
}

/// <summary>Analysis result of one movie or episode.</summary>
public class ItemResult
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the item was analyzed.</summary>
    public bool Analyzed { get; set; }

    /// <summary>Gets or sets the analysis date.</summary>
    public DateTime? AnalyzedAt { get; set; }

    /// <summary>Gets or sets the analysis duration in seconds.</summary>
    public double AnalysisSeconds { get; set; }

    /// <summary>Gets or sets the number of Shazam requests.</summary>
    public int Requests { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the songs.</summary>
    public List<SongEntry> Songs { get; set; } = new();

    /// <summary>Gets or sets the sections detected as music.</summary>
    public List<MusicSegment> MusicSegments { get; set; } = new();

    /// <summary>Gets or sets the snippets sent to Shazam.</summary>
    public List<SnippetInfo> Snippets { get; set; } = new();

    /// <summary>Gets the visible songs ordered by time.</summary>
    [JsonIgnore]
    public IEnumerable<SongEntry> VisibleSongs => Songs.Where(s => !s.Deleted).OrderBy(s => s.StartSeconds).ThenBy(s => s.EndSeconds);
}

/// <summary>
/// JSON-file backed storage in the plugin data folder.
/// Objects handed out are never mutated afterwards (copy-on-write), so callers can read them without locking.
/// </summary>
public class ResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private Dictionary<Guid, ItemResult>? _cache;

    private static string FilePath
    {
        get
        {
            var dir = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "soundtrackfinder_results.json");
        }
    }

    /// <summary>Gets all results (read only!).</summary>
    public List<ItemResult> All()
    {
        lock (_lock)
        {
            return Load().Values.ToList();
        }
    }

    /// <summary>Gets one result (read only!).</summary>
    public ItemResult? Get(Guid id)
    {
        lock (_lock)
        {
            return Load().TryGetValue(id, out var r) ? r : null;
        }
    }

    /// <summary>Modifies (or creates) a result on a copy, publishes and saves it.</summary>
    /// <param name="id">Item id.</param>
    /// <param name="change">Change to apply.</param>
    /// <returns>The new published result.</returns>
    public ItemResult Update(Guid id, Action<ItemResult> change)
    {
        lock (_lock)
        {
            var all = Load();
            var r = all.TryGetValue(id, out var existing) ? Clone(existing) : new ItemResult { ItemId = id };
            change(r);
            all[id] = r;
            Save(all);
            return r;
        }
    }

    /// <summary>Removes a result.</summary>
    /// <param name="id">Item id.</param>
    public void Remove(Guid id)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.Remove(id))
            {
                Save(all);
            }
        }
    }

    private static ItemResult Clone(ItemResult r)
        => JsonSerializer.Deserialize<ItemResult>(JsonSerializer.Serialize(r))!;

    private Dictionary<Guid, ItemResult> Load()
    {
        if (_cache != null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<ItemResult>>(File.ReadAllText(FilePath)) ?? new();
                _cache = list.ToDictionary(r => r.ItemId);
                return _cache;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // corrupt file -> start fresh but keep a backup
            try
            {
                File.Copy(FilePath, FilePath + ".bak", true);
            }
            catch (IOException)
            {
            }
        }

        _cache = new Dictionary<Guid, ItemResult>();
        return _cache;
    }

    private static void Save(Dictionary<Guid, ItemResult> all)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all.Values.ToList(), JsonOptions));
        File.Move(tmp, FilePath, true);
    }
}
