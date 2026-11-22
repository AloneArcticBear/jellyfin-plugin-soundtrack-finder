// #claude
using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SoundtrackFinder.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the library ids to scan (empty = all movie/series libraries).</summary>
    public string[] LibraryIds { get; set; } = System.Array.Empty<string>();

    /// <summary>Gets or sets the minimum distance between two Shazam snippets in seconds.</summary>
    public int SnippetSpacingSeconds { get; set; } = 25;

    /// <summary>Gets or sets the snippet length in seconds.</summary>
    public int SnippetLengthSeconds { get; set; } = 12;

    /// <summary>Gets or sets the pause between two Shazam requests in milliseconds.</summary>
    public int RequestDelayMs { get; set; } = 4000;

    /// <summary>Gets or sets the maximum number of Shazam requests per title.</summary>
    public int MaxRequestsPerItem { get; set; } = 250;

    /// <summary>Gets or sets the music detection sensitivity (1 = strict, 10 = everything is music).</summary>
    public int MusicSensitivity { get; set; } = 5;

    /// <summary>Gets or sets a value indicating whether the song list is written into the overview.</summary>
    public bool WriteToOverview { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a link to the Soundtracks page is added under the song list.</summary>
    public bool OverviewLink { get; set; } = true;

    /// <summary>
    /// Gets or sets the public address of Jellyfin (for the link in the overview), e.g. https://jellyfin.example.com.
    /// Defaults to JELLYFIN_PublishedServerUrl; empty = relative link (works in the Jellyfin web app).
    /// </summary>
    public string PublicUrl { get; set; } = Environment.GetEnvironmentVariable("JELLYFIN_PublishedServerUrl") ?? string.Empty;

    /// <summary>Gets or sets a value indicating whether "Jetzt läuft" pop-ups are shown while watching (users can switch it off for themselves).</summary>
    public bool NowPlayingEnabled { get; set; } = true;

    /// <summary>Gets or sets how long a "Jetzt läuft" pop-up is shown (seconds).</summary>
    public int NowPlayingSeconds { get; set; } = 6;

    /// <summary>Gets or sets the maximum number of titles per nightly run (0 = unlimited).</summary>
    public int MaxItemsPerRun { get; set; } = 0;

    /// <summary>Gets or sets the folder (path inside the container) songs are saved to for a Jellyfin music library.</summary>
    public string MusicExportPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the fade in/out length of saved songs in seconds.</summary>
    public double ExportFadeSeconds { get; set; } = 1;

    /// <summary>Gets or sets the MP3 bitrate of saved songs.</summary>
    public int ExportBitrateKbps { get; set; } = 192;

    /// <summary>
    /// Gets or sets the exclusion list: recognized songs whose "artist – title" contains one of these
    /// texts (case-insensitive) are ignored, e.g. sound-effect libraries or cover factories.
    /// </summary>
    public string[] ExcludePatterns { get; set; } =
    {
        "Sound Ideas", "ASMR", "Sound Effects", "Soundeffekte", "White Noise", "Karaoke", "TV Theme Tune Factory"
    };

    /// <summary>Gets or sets the album artist tag of saved songs.</summary>
    public string ExportAlbumArtist { get; set; } = "Soundtracks";
}
