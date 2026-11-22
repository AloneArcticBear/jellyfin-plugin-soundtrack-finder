// #claude
// C# port of the Shazam audio signature algorithm as implemented by SongRec / shazamio.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// One spectral peak of a signature.
/// </summary>
internal readonly record struct FrequencyPeak(int FftPassNumber, ushort PeakMagnitude, ushort CorrectedPeakFrequencyBin);

/// <summary>
/// Generates Shazam signatures from 16 kHz mono signed 16-bit PCM.
/// </summary>
internal sealed class ShazamSignatureGenerator
{
    private const int FftSize = 2048;
    private const int Bins = 1025;
    private const int RingSize = 256;

    private static readonly float[] Hanning = BuildHanning();
    private static readonly int[] NeighborOffsets = { -10, -7, -4, -3, 1, 2, 5, 8 };
    private static readonly int[] OtherOffsets = { -53, -45, 165, 172, 179, 186, 193, 200, 214, 221, 228, 235, 242, 249 };

    private readonly short[] _samplesRing = new short[FftSize];
    private int _samplesPos;

    private readonly float[][] _fftOutputs = NewRing();
    private int _fftPos;

    private readonly float[][] _spreadOutputs = NewRing();
    private int _spreadPos;
    private int _spreadWritten;

    private readonly SortedDictionary<int, List<FrequencyPeak>> _bands = new();

    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];

    /// <summary>
    /// Builds an encoded signature data URI from PCM samples (16 kHz mono).
    /// </summary>
    /// <param name="samples">PCM samples.</param>
    /// <returns>Data URI and sample duration in ms.</returns>
    public static (string Uri, int SampleMs) Create(ReadOnlySpan<short> samples)
    {
        var gen = new ShazamSignatureGenerator();
        int usable = samples.Length - (samples.Length % 128);
        for (int i = 0; i < usable; i += 128)
        {
            gen.DoFft(samples.Slice(i, 128));
            gen.DoPeakSpreading();
            if (gen._spreadWritten >= 46)
            {
                gen.DoPeakRecognition();
            }
        }

        var bytes = gen.Encode(samples.Length);
        return ("data:audio/vnd.shazam.sig;base64," + Convert.ToBase64String(bytes), (int)(samples.Length / 16.0));
    }

    private static float[][] NewRing()
    {
        var r = new float[RingSize][];
        for (int i = 0; i < RingSize; i++)
        {
            r[i] = new float[Bins];
        }

        return r;
    }

    private static float[] BuildHanning()
    {
        var h = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            h[i] = (float)(0.5 - (0.5 * Math.Cos(2.0 * Math.PI * (i + 1) / 2049.0)));
        }

        return h;
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;

    private void DoFft(ReadOnlySpan<short> batch)
    {
        batch.CopyTo(_samplesRing.AsSpan(_samplesPos, 128));
        _samplesPos = (_samplesPos + 128) % FftSize;

        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = (float)(_samplesRing[(_samplesPos + i) % FftSize] * Hanning[i]);
            _im[i] = 0;
        }

        Fft.Transform(_re, _im);

        var output = _fftOutputs[_fftPos];
        for (int i = 0; i < Bins; i++)
        {
            float re = (float)_re[i], im = (float)_im[i];
            float v = ((re * re) + (im * im)) / (1 << 17);
            output[i] = MathF.Max(v, 0.0000000001f);
        }

        _fftPos = (_fftPos + 1) % RingSize;
    }

    private void DoPeakSpreading()
    {
        var last = _fftOutputs[Mod(_fftPos - 1, RingSize)];
        var spread = _spreadOutputs[_spreadPos];
        Array.Copy(last, spread, Bins);

        for (int p = 0; p <= 1022; p++)
        {
            spread[p] = Math.Max(spread[p], Math.Max(spread[p + 1], spread[p + 2]));
        }

        for (int p = 0; p <= 1024; p++)
        {
            foreach (var former in new[] { 1, 3, 6 })
            {
                var f = _spreadOutputs[Mod(_spreadPos - former, RingSize)];
                f[p] = Math.Max(f[p], spread[p]);
            }
        }

        _spreadPos = (_spreadPos + 1) % RingSize;
        _spreadWritten++;
    }

    private void DoPeakRecognition()
    {
        var fft46 = _fftOutputs[Mod(_fftPos - 46, RingSize)];
        var fft49 = _spreadOutputs[Mod(_spreadPos - 49, RingSize)];

        for (int bin = 10; bin <= 1014; bin++)
        {
            if (fft46[bin] >= 1.0f / 64.0f && fft46[bin] >= fft49[bin - 1])
            {
                float maxNeighbor = 0;
                foreach (var off in NeighborOffsets)
                {
                    maxNeighbor = Math.Max(maxNeighbor, fft49[bin + off]);
                }

                if (fft46[bin] > maxNeighbor)
                {
                    float maxOther = maxNeighbor;
                    foreach (var off in OtherOffsets)
                    {
                        var other = _spreadOutputs[Mod(_spreadPos + off, RingSize)];
                        maxOther = Math.Max(maxOther, other[bin - 1]);
                    }

                    if (fft46[bin] > maxOther)
                    {
                        int fftNumber = _spreadWritten - 46;
                        float mag = (MathF.Log(MathF.Max(1.0f / 64, fft46[bin])) * 1477.3f) + 6144f;
                        float magBefore = (MathF.Log(MathF.Max(1.0f / 64, fft46[bin - 1])) * 1477.3f) + 6144f;
                        float magAfter = (MathF.Log(MathF.Max(1.0f / 64, fft46[bin + 1])) * 1477.3f) + 6144f;
                        float var1 = (mag * 2) - magBefore - magAfter;
                        if (var1 <= 0)
                        {
                            continue;
                        }

                        float var2 = (magAfter - magBefore) * 32 / var1;
                        int corrected = (bin * 64) + (int)var2;
                        float freqHz = corrected * (16000f / 2 / 1024 / 64);

                        int band;
                        if (freqHz >= 250 && freqHz < 520)
                        {
                            band = 0;
                        }
                        else if (freqHz >= 520 && freqHz < 1450)
                        {
                            band = 1;
                        }
                        else if (freqHz >= 1450 && freqHz < 3500)
                        {
                            band = 2;
                        }
                        else if (freqHz >= 3500 && freqHz <= 5500)
                        {
                            band = 3;
                        }
                        else
                        {
                            continue;
                        }

                        if (!_bands.TryGetValue(band, out var list))
                        {
                            list = new List<FrequencyPeak>();
                            _bands[band] = list;
                        }

                        list.Add(new FrequencyPeak(fftNumber, (ushort)mag, (ushort)corrected));
                    }
                }
            }
        }
    }

    /// <summary>Encodes the collected peaks in Shazam's binary signature format.</summary>
    internal byte[] Encode(int numberSamples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // header (48 bytes)
        w.Write(0xcafe2580u); // magic1
        w.Write(0u); // crc32 placeholder
        w.Write(0u); // size minus header placeholder
        w.Write(0x94119c00u); // magic2
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write(3u << 27); // sample rate id 3 = 16000 Hz
        w.Write(0u);
        w.Write(0u);
        w.Write((uint)(numberSamples + (16000 * 0.24)));
        w.Write((15u << 19) + 0x40000u);

        w.Write(0x40000000u);
        w.Write(0u); // size minus header placeholder

        foreach (var (band, peaks) in _bands)
        {
            using var pms = new MemoryStream();
            using var pw = new BinaryWriter(pms);
            int fftPass = 0;
            foreach (var peak in peaks)
            {
                if (peak.FftPassNumber - fftPass >= 255)
                {
                    pw.Write((byte)0xff);
                    pw.Write((uint)peak.FftPassNumber);
                    fftPass = peak.FftPassNumber;
                }

                pw.Write((byte)(peak.FftPassNumber - fftPass));
                pw.Write(peak.PeakMagnitude);
                pw.Write(peak.CorrectedPeakFrequencyBin);
                fftPass = peak.FftPassNumber;
            }

            pw.Flush();
            var data = pms.ToArray();
            w.Write(0x60030040u + (uint)band);
            w.Write((uint)data.Length);
            w.Write(data);
            int pad = (4 - (data.Length % 4)) % 4;
            for (int i = 0; i < pad; i++)
            {
                w.Write((byte)0);
            }
        }

        w.Flush();
        var buf = ms.ToArray();
        uint sizeMinusHeader = (uint)(buf.Length - 48);
        BitConverter.TryWriteBytes(buf.AsSpan(8, 4), sizeMinusHeader);
        BitConverter.TryWriteBytes(buf.AsSpan(52, 4), sizeMinusHeader);
        uint crc = Crc32.Compute(buf.AsSpan(8));
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), crc);
        return buf;
    }

    /// <summary>Number of peaks found (for diagnostics).</summary>
    internal int PeakCount => _bands.Values.Sum(l => l.Count);
}

/// <summary>Minimal radix-2 FFT.</summary>
internal static class Fft
{
    public static void Transform(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + (len / 2);
                    double tr = (re[b] * cr) - (im[b] * ci);
                    double ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double ncr = (cr * wr) - (ci * wi);
                    ci = (cr * wi) + (ci * wr);
                    cr = ncr;
                }
            }
        }
    }
}

/// <summary>Standard CRC-32 (IEEE).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
