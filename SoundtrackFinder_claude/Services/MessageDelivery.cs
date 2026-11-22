// #claude
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// Sends a pop-up message to every open connection of a session.
/// Jellyfin itself only sends to the connection that was active last. All browser tabs/windows of one browser
/// share one session, so a message often landed in a different tab than the one playing the video.
/// The connections are read from Jellyfin's WebSocketController (private field, checked against 12.4);
/// if that is not possible, the normal Jellyfin call is used.
/// </summary>
internal static class MessageDelivery
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly SemaphoreSlim SendLock = new(1, 1); // a WebSocket must not send two messages at once

    /// <summary>Open connections (tabs/windows/apps) of the session; empty if they cannot be read.</summary>
    public static List<IWebSocketConnection> OpenConnections(SessionInfo session)
    {
        var result = new List<IWebSocketConnection>();
        foreach (var controller in session.SessionControllers ?? Array.Empty<ISessionController>())
        {
            try
            {
                var type = controller.GetType();
                if (type.GetField("_sockets", Flags)?.GetValue(controller) is not IEnumerable<IWebSocketConnection> sockets)
                {
                    continue;
                }

                List<IWebSocketConnection> snapshot;
                if (type.GetField("_socketsLock", Flags)?.GetValue(controller) is ReaderWriterLockSlim rw)
                {
                    rw.EnterReadLock();
                    try
                    {
                        snapshot = sockets.ToList();
                    }
                    finally
                    {
                        rw.ExitReadLock();
                    }
                }
                else
                {
                    snapshot = sockets.ToList();
                }

                result.AddRange(snapshot.Where(s => s.State == WebSocketState.Open));
            }
            catch (Exception)
            {
                // session is just closing or Jellyfin changed internally: fall back to the normal call
            }
        }

        return result.Distinct().ToList();
    }

    /// <summary>Sends the message to all open connections of the session; returns how many received it.</summary>
    public static async Task<int> SendAsync(ISessionManager sessionManager, SessionInfo session, MessageCommand cmd, ILogger logger, CancellationToken ct)
    {
        var connections = OpenConnections(session);
        if (connections.Count > 0)
        {
            var command = new GeneralCommand { Name = GeneralCommandType.DisplayMessage };
            command.Arguments["Header"] = cmd.Header ?? string.Empty;
            command.Arguments["Text"] = cmd.Text;
            if (cmd.TimeoutMs.HasValue)
            {
                command.Arguments["TimeoutMs"] = cmd.TimeoutMs.Value.ToString(CultureInfo.InvariantCulture);
            }

            int sent = 0;
            foreach (var connection in connections)
            {
                if (await TrySendAsync(connection, command, logger, ct).ConfigureAwait(false))
                {
                    sent++;
                }
            }

            if (sent > 0)
            {
                return sent;
            }
        }

        await sessionManager.SendMessageCommand(null!, session.Id, cmd, ct).ConfigureAwait(false);
        return 1;
    }

    private static async Task<bool> TrySendAsync(IWebSocketConnection connection, GeneralCommand command, ILogger logger, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await SendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.SendAsync(
                    new OutboundWebSocketMessage<GeneralCommand>
                    {
                        Data = command,
                        MessageType = SessionMessageType.GeneralCommand,
                        MessageId = Guid.NewGuid()
                    },
                    ct).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                // Jellyfin was sending something on the same connection at that moment: try again shortly
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Soundtrack Finder: Einblendung an eine Verbindung fehlgeschlagen");
                return false;
            }
            finally
            {
                SendLock.Release();
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return false;
    }
}
