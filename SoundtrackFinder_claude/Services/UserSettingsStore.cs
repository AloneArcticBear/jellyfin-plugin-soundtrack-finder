// #claude
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// Per-user settings of the plugin (currently: "Jetzt läuft" pop-ups on/off), stored as JSON in the plugin data folder.
/// </summary>
public class UserSettingsStore
{
    private readonly object _lock = new();
    private HashSet<Guid>? _nowPlayingOff;

    private static string FilePath
    {
        get
        {
            var dir = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "soundtrackfinder_usersettings.json");
        }
    }

    /// <summary>Gets a value indicating whether the user wants "Jetzt läuft" pop-ups.</summary>
    /// <param name="userId">User id.</param>
    public bool NowPlayingEnabled(Guid userId)
    {
        lock (_lock)
        {
            return !Load().Contains(userId);
        }
    }

    /// <summary>Switches "Jetzt läuft" pop-ups on or off for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="enabled">On or off.</param>
    public void SetNowPlaying(Guid userId, bool enabled)
    {
        lock (_lock)
        {
            var off = Load();
            bool changed = enabled ? off.Remove(userId) : off.Add(userId);
            if (changed)
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new { NowPlayingOff = off.ToList() }));
                File.Move(tmp, FilePath, true);
            }
        }
    }

    private HashSet<Guid> Load()
    {
        if (_nowPlayingOff != null)
        {
            return _nowPlayingOff;
        }

        _nowPlayingOff = new HashSet<Guid>();
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("NowPlayingOff", out var list))
                {
                    foreach (var e in list.EnumerateArray())
                    {
                        if (e.TryGetGuid(out var g))
                        {
                            _nowPlayingOff.Add(g);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // start with defaults
        }

        return _nowPlayingOff;
    }
}
