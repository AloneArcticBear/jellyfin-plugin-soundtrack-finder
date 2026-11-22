// #claude
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>Result of one Shazam lookup.</summary>
internal sealed record ShazamMatch(string Key, string Title, string Artist, string? AppleMusicUrl, string? ShazamUrl);

/// <summary>
/// Talks to the (unofficial) Shazam recognition endpoint.
/// </summary>
public sealed class ShazamClient
{
    private static readonly string[] UserAgents =
    {
        "Dalvik/2.1.0 (Linux; U; Android 5.0.2; VS980 4G Build/LRX22G)",
        "Dalvik/2.1.0 (Linux; U; Android 8.0.0; SM-G930F Build/R16NW)",
        "Dalvik/2.1.0 (Linux; U; Android 9; SM-G960F Build/PPR1.180610.011)",
        "Dalvik/2.1.0 (Linux; U; Android 10; Pixel 3 Build/QQ3A.200805.001)",
        "Dalvik/2.1.0 (Linux; U; Android 11; SM-A515F Build/RP1A.200720.012)"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShazamClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _nextAllowed = DateTime.MinValue;

    /// <summary>Initializes a new instance of the <see cref="ShazamClient"/> class.</summary>
    /// <param name="httpClientFactory">Http client factory.</param>
    /// <param name="logger">Logger.</param>
    public ShazamClient(IHttpClientFactory httpClientFactory, ILogger<ShazamClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Recognizes a snippet. Returns null if nothing was found.</summary>
    internal async Task<ShazamMatch?> RecognizeAsync(string signatureUri, int sampleMs, int delayMs, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var wait = _nextAllowed - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _nextAllowed = DateTime.UtcNow.AddMilliseconds(Math.Max(500, delayMs));

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var url = $"https://amp.shazam.com/discovery/v5/de/DE/android/-/tag/{Guid.NewGuid().ToString().ToUpperInvariant()}/{Guid.NewGuid().ToString().ToUpperInvariant()}"
                    + "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&video=v3";
                var body = new
                {
                    geolocation = new { altitude = 300, latitude = 52.5, longitude = 13.4 },
                    signature = new { samplems = sampleMs, timestamp = now, uri = signatureUri },
                    timestamp = now,
                    timezone = "Europe/Berlin"
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgents[Random.Shared.Next(UserAgents.Length)]);
                req.Headers.TryAddWithoutValidation("Content-Language", "de_DE");

                var client = _httpClientFactory.CreateClient("SoundtrackFinder");
                client.Timeout = TimeSpan.FromSeconds(30);
                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                {
                    var backoff = TimeSpan.FromSeconds(60 * (attempt + 1));
                    _logger.LogWarning("Shazam antwortet {Status}, warte {Seconds}s", (int)resp.StatusCode, backoff.TotalSeconds);
                    _nextAllowed = DateTime.UtcNow + backoff;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Shazam-Fehler {Status}", (int)resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(json);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ShazamMatch? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("track", out var track))
        {
            return null;
        }

        string key = track.TryGetProperty("key", out var k) ? k.ToString() : string.Empty;
        string title = track.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        string artist = track.TryGetProperty("subtitle", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        string? shazamUrl = track.TryGetProperty("url", out var u) ? u.GetString() : null;

        string? apple = null;
        if (track.TryGetProperty("hub", out var hub))
        {
            apple = FindAppleUrl(hub);
        }

        if (string.IsNullOrEmpty(key))
        {
            key = (artist + "|" + title).ToLowerInvariant();
        }

        return string.IsNullOrEmpty(title) ? null : new ShazamMatch(key, title, artist, apple, shazamUrl);
    }

    private static string? FindAppleUrl(JsonElement el)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(el);
        string? fallback = null;
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            switch (cur.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in cur.EnumerateObject())
                    {
                        stack.Push(p.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var p in cur.EnumerateArray())
                    {
                        stack.Push(p);
                    }

                    break;
                case JsonValueKind.String:
                    var v = cur.GetString();
                    if (v != null && v.StartsWith("https://music.apple.com/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (v.Contains("/song/", StringComparison.OrdinalIgnoreCase) || v.Contains("?i=", StringComparison.Ordinal))
                        {
                            return v;
                        }

                        fallback ??= v;
                    }

                    break;
            }
        }

        return fallback;
    }
}
