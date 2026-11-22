// #claude
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>A snippet chosen for recognition.</summary>
internal sealed record Snippet(double StartSeconds, string SignatureUri, int SampleMs);

/// <summary>Result of a scan: snippets plus the sections classified as music.</summary>
internal sealed record ScanResult(List<Snippet> Snippets, List<MusicSegment> Segments, double DurationSeconds);

/// <summary>
/// Decodes the audio track once with ffmpeg (16 kHz mono), detects music-like
/// sections with a simple energy-modulation heuristic and builds Shazam signatures
/// for snippets inside those sections.
/// </summary>
internal static class AudioScanner
{
    private const int Rate = 16000;
    private const int FrameSamples = 320; // 20 ms

    public static async Task<ScanResult> ScanAsync(
        string ffmpegPath,
        string mediaPath,
        int snippetSeconds,
        int spacingSeconds,
        int sensitivity,
        double durationSeconds,
        Action<double> progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", mediaPath, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-f", "s16le", "pipe:1" })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        using var reg = ct.Register(() =>
        {
            try
            {
                proc.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        int windowSamples = snippetSeconds * Rate;
        var ring = new short[windowSamples];
        long totalSamples = 0;

        int framesPerWindow = windowSamples / FrameSamples;
        var frameRms = new double[framesPerWindow];
        int frameIdx = 0;
        double frameSumSq = 0;
        int frameFill = 0;

        double lastSnippet = double.NegativeInfinity;
        var snippets = new List<Snippet>();
        var segments = new List<MusicSegment>();

        // thresholds derived from sensitivity (1 strict ... 10 everything)
        sensitivity = Math.Clamp(sensitivity, 1, 10);
        double lowEnergyMax = 0.20 + (sensitivity * 0.04); // speech has many quiet 20 ms frames
        double silenceRms = 220 - (sensitivity * 18);       // int16 RMS

        var stream = proc.StandardOutput.BaseStream;
        var buffer = new byte[Rate * 2]; // 1 s
        int carry = 0;
        long nextCheck = Rate;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            int bytes = carry + read;
            int sampleCount = bytes / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                short s = (short)(buffer[2 * i] | (buffer[(2 * i) + 1] << 8));
                ring[(int)(totalSamples % windowSamples)] = s;
                totalSamples++;

                frameSumSq += (double)s * s;
                frameFill++;
                if (frameFill == FrameSamples)
                {
                    frameRms[frameIdx % framesPerWindow] = Math.Sqrt(frameSumSq / FrameSamples);
                    frameIdx++;
                    frameSumSq = 0;
                    frameFill = 0;
                }

                if (totalSamples >= nextCheck)
                {
                    nextCheck += Rate;
                    double now = (double)totalSamples / Rate;
                    bool music = IsMusic(frameRms, Math.Min(frameIdx, framesPerWindow), lowEnergyMax, silenceRms, sensitivity);
                    if (music)
                    {
                        // the analysed window covers [now - window, now]
                        double segStart = Math.Max(0, now - Math.Min(now, snippetSeconds));
                        if (segments.Count > 0 && segStart <= segments[^1].End + 1)
                        {
                            segments[^1].End = now;
                        }
                        else
                        {
                            segments.Add(new MusicSegment { Start = segStart, End = now });
                        }
                    }

                    if (music && totalSamples >= windowSamples && now - snippetSeconds - lastSnippet >= spacingSeconds)
                    {
                        var window = new short[windowSamples];
                        int start = (int)(totalSamples % windowSamples);
                        Array.Copy(ring, start, window, 0, windowSamples - start);
                        Array.Copy(ring, 0, window, windowSamples - start, start);
                        var (uri, ms) = ShazamSignatureGenerator.Create(window);
                        lastSnippet = now - snippetSeconds;
                        snippets.Add(new Snippet(lastSnippet, uri, ms));
                    }

                    if (durationSeconds > 0)
                    {
                        progress(Math.Min(1.0, now / durationSeconds));
                    }
                }
            }

            carry = bytes % 2;
            if (carry == 1)
            {
                buffer[0] = buffer[bytes - 1];
            }
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var err = await stderrTask.ConfigureAwait(false);
        if (totalSamples == 0)
        {
            throw new IOException("ffmpeg lieferte keine Tonspur: " + err.Trim());
        }

        return new ScanResult(snippets, segments, (double)totalSamples / Rate);
    }

    private static bool IsMusic(double[] rms, int count, double lowEnergyMax, double silenceRms, int sensitivity)
    {
        if (count == 0)
        {
            return false;
        }

        var span = rms.AsSpan(0, count);
        double mean = 0;
        foreach (var v in span)
        {
            mean += v;
        }

        mean /= count;
        if (mean < silenceRms)
        {
            return false;
        }

        if (sensitivity >= 10)
        {
            return true;
        }

        int low = 0;
        foreach (var v in span)
        {
            if (v < 0.5 * mean)
            {
                low++;
            }
        }

        double lowRatio = (double)low / count;
        return lowRatio <= lowEnergyMax;
    }

    /// <summary>Runs ffmpeg and throws with its error output on failure.</summary>
    public static async Task RunFfmpegAsync(string ffmpegPath, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        using var reg = ct.Register(() =>
        {
            try
            {
                proc.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
        });
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        var outTask = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        await outTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            throw new IOException("ffmpeg-Fehler: " + err.Trim());
        }
    }

    /// <summary>Cuts a section of the audio track into an MP3 file and returns its bytes.</summary>
    public static async Task<byte[]> CutMp3Async(string ffmpegPath, string mediaPath, double start, double end, string tempDir, int bitrateKbps, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var outFile = Path.Combine(tempDir, "stf_" + Guid.NewGuid().ToString("N") + ".mp3");
        double dur = Math.Max(1, end - start);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            await RunFfmpegAsync(
                ffmpegPath,
                new[]
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", Math.Max(0, start).ToString("0.###", inv),
                    "-i", mediaPath,
                    "-t", dur.ToString("0.###", inv),
                    "-map", "0:a:0", "-vn", "-ac", "2", "-c:a", "libmp3lame", "-b:a", bitrateKbps.ToString(inv) + "k", "-f", "mp3", outFile
                },
                ct).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(outFile, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(outFile);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Formats seconds as h:mm:ss.</summary>
    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

}
