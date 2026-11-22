// #claude
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// "Jetzt läuft": while a movie/episode is played, shows a short pop-up on the playing device when a recognized
/// song starts (or when playback starts / jumps into a song). Only for clients that support pop-up messages
/// (DisplayMessage, e.g. the Jellyfin web app) and users who did not switch it off.
/// Clients report the position about every 10 s, so the start of the next song is timed in between.
/// </summary>
public sealed class NowPlayingNotifier : IHostedService, IDisposable
{
    private const double JumpToleranceSeconds = 6;

    private readonly ISessionManager _sessionManager;
    private readonly ResultStore _store;
    private readonly UserSettingsStore _userSettings;
    private readonly ILogger<NowPlayingNotifier> _logger;
    private readonly ConcurrentDictionary<string, State> _states = new();
    private readonly ConcurrentDictionary<string, byte> _loggedUnsupported = new();

    /// <summary>Initializes a new instance of the <see cref="NowPlayingNotifier"/> class.</summary>
    public NowPlayingNotifier(ISessionManager sessionManager, ResultStore store, UserSettingsStore userSettings, ILogger<NowPlayingNotifier> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _userSettings = userSettings;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        foreach (var s in _states.Values)
        {
            s.Timer?.Dispose();
        }

        _states.Clear();
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Handle(e, isStart: true);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => Handle(e, isStart: false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session != null && _states.TryRemove(e.Session.Id, out var st))
        {
            st.Timer?.Dispose();
        }
    }

    private void Handle(PlaybackProgressEventArgs e, bool isStart)
    {
        try
        {
            var session = e.Session;
            var item = e.Item;
            if (session == null || item == null || !e.PlaybackPositionTicks.HasValue)
            {
                return;
            }

            if (!Config.NowPlayingEnabled || !_userSettings.NowPlayingEnabled(session.UserId))
            {
                Forget(session.Id);
                return;
            }

            if (!session.SupportedCommands.Contains(GeneralCommandType.DisplayMessage))
            {
                if (_loggedUnsupported.TryAdd(session.Id, 0))
                {
                    _logger.LogInformation("Soundtrack Finder: {Device} ({Client}) meldet (noch) keine Einblendungen – „Jetzt läuft“ wird dort übersprungen, bis die App sich neu meldet", session.DeviceName, session.Client);
                }

                Forget(session.Id);
                return;
            }

            _loggedUnsupported.TryRemove(session.Id, out _);
            var songs = _store.Get(item.Id)?.VisibleSongs.ToList();
            if (songs == null || songs.Count == 0)
            {
                Forget(session.Id);
                return;
            }

            // Between the client's reports (every ~10 s) Jellyfin sends its own estimated positions every second.
            // These are sometimes a second ahead (timing race in Jellyfin), and the plugin estimates the position
            // itself anyway – so only the client's own reports move the clock.
            if (e.IsAutomated && _states.TryGetValue(session.Id, out var known) && known.ItemId == item.Id)
            {
                lock (known)
                {
                    if (known.Timer == null)
                    {
                        Schedule(known);
                    }
                }

                return;
            }

            double pos = TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds;
            var st = _states.GetOrAdd(session.Id, _ => new State());
            lock (st)
            {
                bool jumped = isStart || st.ItemId != item.Id || Math.Abs(pos - st.Expected()) > JumpToleranceSeconds;
                if (jumped)
                {
                    st.LastSongId = null; // after starting or seeking, the song at the new position may be shown again
                }

                st.ItemId = item.Id;
                st.SessionId = session.Id;
                st.Device = $"{session.DeviceName} ({session.Client})";
                st.Position = pos;
                st.At = DateTime.UtcNow;
                st.Paused = e.IsPaused;
                st.Songs = songs;

                var current = SongAt(songs, pos);
                if (current != null && current.Id != st.LastSongId)
                {
                    // a song that is already running is only shown right after a jump/start or shortly after it began
                    if (!e.IsPaused && (jumped || pos - current.StartSeconds <= 15))
                    {
                        Announce(session.Id, current, st.Device);
                    }

                    st.LastSongId = current.Id;
                }

                Schedule(st);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Soundtrack Finder: Jetzt-läuft-Verarbeitung fehlgeschlagen");
        }
    }

    // sets a timer to the start of the next song (re-done on every progress report)
    private void Schedule(State st)
    {
        st.Timer?.Dispose();
        st.Timer = null;
        if (st.Paused || st.Songs == null)
        {
            return;
        }

        double now = st.Expected();
        var next = st.Songs.Where(s => s.StartSeconds > now + 0.3 && s.Id != st.LastSongId).OrderBy(s => s.StartSeconds).FirstOrDefault();
        if (next == null)
        {
            return;
        }

        double delay = next.StartSeconds - now;
        if (delay > 900)
        {
            return; // far away: later progress reports will schedule it
        }

        st.Timer = new Timer(_ => OnTimer(st), null, TimeSpan.FromSeconds(Math.Max(0.2, delay)), Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(State st)
    {
        lock (st)
        {
            if (st.Paused || st.Songs == null || st.SessionId == null)
            {
                return;
            }

            var current = SongAt(st.Songs, st.Expected());
            if (current != null && current.Id != st.LastSongId)
            {
                Announce(st.SessionId, current, st.Device);
                st.LastSongId = current.Id;
            }

            Schedule(st);
        }
    }

    private static SongEntry? SongAt(List<SongEntry> songs, double pos)
        => songs.Where(s => s.StartSeconds <= pos + 0.25 && pos < s.EndSeconds).OrderByDescending(s => s.StartSeconds).FirstOrDefault();

    private void Announce(string sessionId, SongEntry song, string? device = null)
    {
        // Jellyfin-Web shows only the text of a message (not the header), so the note goes into the text
        var text = "🎵 " + (string.IsNullOrWhiteSpace(song.Artist) ? song.Title : song.Artist + " – " + song.Title);
        _logger.LogInformation("Soundtrack Finder: Jetzt läuft auf {Device}: {Song}", device ?? sessionId, text);
        var cmd = new MessageCommand
        {
            Header = Loc.T("np.header"),
            Text = text,
            TimeoutMs = Math.Clamp(Config.NowPlayingSeconds, 2, 30) * 1000L // with a timeout the web app shows a small pop-up, not a dialog
        };
        _ = Task.Run(async () =>
        {
            try
            {
                var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
                if (session == null)
                {
                    return;
                }

                int count = await MessageDelivery.SendAsync(_sessionManager, session, cmd, _logger, CancellationToken.None).ConfigureAwait(false);
                if (count > 1)
                {
                    _logger.LogDebug("Soundtrack Finder: Einblendung an {Count} offene Fenster/Tabs von {Device} gesendet", count, device ?? sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Soundtrack Finder: Einblendung konnte nicht gesendet werden");
            }
        });
    }

    /// <summary>What "Jetzt läuft" is doing on the user's devices right now (for the diagnosis on the Soundtracks page).</summary>
    public List<object> GetStatus(Guid userId)
    {
        var list = new List<object>();
        foreach (var s in _sessionManager.Sessions.Where(s => s.UserId == userId && s.NowPlayingItem != null))
        {
            bool supports = s.SupportedCommands.Contains(GeneralCommandType.DisplayMessage);
            double pos = TimeSpan.FromTicks(s.PlayState?.PositionTicks ?? 0).TotalSeconds;
            var songs = _store.Get(s.NowPlayingItem.Id)?.VisibleSongs.ToList() ?? new List<SongEntry>();
            var next = songs.Where(x => x.StartSeconds > pos).OrderBy(x => x.StartSeconds).FirstOrDefault();
            var current = SongAt(songs, pos);
            _states.TryGetValue(s.Id, out var st);
            list.Add(new
            {
                device = s.DeviceName,
                client = s.Client,
                item = s.NowPlayingItem.Name,
                position = pos,
                paused = s.PlayState?.IsPaused ?? false,
                supports,
                tracked = st != null,
                songCount = songs.Count,
                current = current == null ? null : Name(current),
                next = next == null ? null : Name(next),
                nextStart = next?.StartSeconds,
                connections = MessageDelivery.OpenConnections(s).Count
            });
        }

        return list;
    }

    /// <summary>Sends a test pop-up to all devices of the user; returns per device whether it was sent.</summary>
    public async Task<List<object>> SendTestAsync(Guid userId, string? lang = null)
    {
        var result = new List<object>();
        foreach (var s in _sessionManager.Sessions.Where(s => s.UserId == userId).OrderByDescending(s => s.LastActivityDate))
        {
            bool supports = s.SupportedCommands.Contains(GeneralCommandType.DisplayMessage);
            int connections = 0;
            if (supports)
            {
                try
                {
                    connections = await MessageDelivery.SendAsync(
                        _sessionManager,
                        s,
                        new MessageCommand { Header = "Test", Text = Loc.T(lang ?? Loc.ConfigLanguage, "np.testText"), TimeoutMs = Math.Clamp(Config.NowPlayingSeconds, 2, 30) * 1000L },
                        _logger,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Soundtrack Finder: Test-Einblendung an {Device} fehlgeschlagen", s.DeviceName);
                    supports = false;
                }
            }

            result.Add(new { device = s.DeviceName, client = s.Client, sent = supports, playing = s.NowPlayingItem?.Name, connections });
        }

        return result;
    }

    private static string Name(SongEntry s) => string.IsNullOrWhiteSpace(s.Artist) ? s.Title : s.Artist + " – " + s.Title;

    private void Forget(string sessionId)
    {
        if (_states.TryRemove(sessionId, out var st))
        {
            st.Timer?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var s in _states.Values)
        {
            s.Timer?.Dispose();
        }
    }

    private sealed class State
    {
        public Guid ItemId { get; set; }

        public string? SessionId { get; set; }

        public string? Device { get; set; }

        public double Position { get; set; }

        public DateTime At { get; set; } = DateTime.MinValue;

        public bool Paused { get; set; }

        public string? LastSongId { get; set; }

        public List<SongEntry>? Songs { get; set; }

        public Timer? Timer { get; set; }

        // position now, extrapolated from the last report
        public double Expected()
            => At == DateTime.MinValue ? double.NaN : (Paused ? Position : Position + (DateTime.UtcNow - At).TotalSeconds);
    }
}
