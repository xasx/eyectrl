namespace EyeCtrl.App.Tests;

using System.IO;
using EyeCtrl.App;

/// <summary>
/// Pins the committed cue asset's format and level (spec §Asset). The RIFF parse carries the
/// check — SoundPlayer.Load() is NOT asserted: measured 2026-09-24, Load() succeeds on garbage
/// bytes for a file-path player (the header is only validated in Play()).
/// </summary>
public class DwellCueAssetTests
{
    private static string AssetPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dwell-cue.wav");

    [Fact]
    public void TheCommittedCue_IsQuietShortPcm16_44k1()
    {
        var header = WavHeader.TryRead(AssetPath());

        Check.WithCustomMessage("dwell-cue.wav is missing from the test output — check the csproj Content include.")
            .That(header).IsNotNull();
        Check.That(header!.SampleRateHz).IsEqualTo(44_100);
        Check.That(header.ChannelCount).IsEqualTo(1);
        Check.That(header.BitsPerSample).IsEqualTo(16);

        var seconds = (double)header.DataByteCount / (header.SampleRateHz * header.ChannelCount * 2);
        Check.That(seconds).IsStrictlyGreaterThan(0.03);
        Check.That(seconds).IsStrictlyLessThan(0.08);

        // Peak ≤ −20 dBFS (amplitude 0.1): amplitude IS the volume — SoundPlayer has none (D4).
        // Seek by the RETURNED offset (G2): 'data' need not be the last chunk for other tools.
        var peak = 0;
        using (var fs = File.OpenRead(AssetPath()))
        {
            fs.Seek(header.DataOffset, SeekOrigin.Begin);
            var remaining = header.DataByteCount;
            var buf = new byte[4_096];
            int read;
            while (remaining > 0 && (read = fs.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i + 1 < read; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buf, i)));
                remaining -= read;
            }
        }
        Check.That(peak).IsLessOrEqualThan((int)(0.1 * 32_767) + 1);
    }

    [Fact]
    public void TryRead_NegativeChunkSize_ReturnsNullWithoutHanging()
    {
        // Pre-fix this input HUNG, not threw: the chunk walk reads 8 header bytes, then seeks
        // size + (size & 1). For -8 that seek is -8 (net advance 0); for -9 it is -8 too
        // ((-9) & 1 == 1). The same chunk head is re-read forever, no exception ever fires.
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(36));   // riffSize = fileSize - 8 (never checked)
        wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("fmt "u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(16));   // fmt chunk size
        wav.AddRange(BitConverter.GetBytes((short)1));    // PCM tag
        wav.AddRange(BitConverter.GetBytes((short)1));    // mono
        wav.AddRange(BitConverter.GetBytes(44_100));      // sample rate
        wav.AddRange(BitConverter.GetBytes(44_100 * 2));  // byte rate
        wav.AddRange(BitConverter.GetBytes((short)2));    // block align
        wav.AddRange(BitConverter.GetBytes((short)16));   // bits per sample
        wav.AddRange("LIST"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(-8));    // negative chunk size after a valid fmt

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, wav.ToArray());
            Check.That(WavHeader.TryRead(path)).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
