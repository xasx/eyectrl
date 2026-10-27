namespace EyeCtrl.App.Tests;

using System.IO;
using EyeCtrl.App;

/// <summary>
/// Pins the committed status-chime asset's format and level (Wave B ticket B3). It must be playable by
/// the same <see cref="WavHeader"/>-validated path as the dwell cue, short and clear, and
/// distinguishable from the dwell cue rather than a copy of it.
/// </summary>
public class StatusChimeAssetTests
{
    private static string AssetPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "status-chime.wav");

    private static (Pcm16Header Header, byte[] Samples) ReadAsset(string path)
    {
        var header = WavHeader.TryRead(path)!;
        var samples = new byte[header.DataByteCount];
        using var fs = File.OpenRead(path);
        fs.Seek(header.DataOffset, SeekOrigin.Begin);
        fs.ReadExactly(samples);
        return (header, samples);
    }

    [Fact]
    public void TheCommittedStatusChime_IsShortPcm16_44k1_AndClearlyAudible()
    {
        var header = WavHeader.TryRead(AssetPath());

        Check.WithCustomMessage(
                "status-chime.wav is missing from the test output - check the csproj Content include.")
            .That(header).IsNotNull();
        Check.That(header!.SampleRateHz).IsEqualTo(44_100);
        Check.That(header.ChannelCount).IsEqualTo(1);
        Check.That(header.BitsPerSample).IsEqualTo(16);

        var seconds = (double)header.DataByteCount / (header.SampleRateHz * header.ChannelCount * 2);
        Check.That(seconds).IsStrictlyGreaterThan(0.10);
        Check.That(seconds).IsStrictlyLessThan(0.30);

        var (_, samples) = ReadAsset(AssetPath());
        var peak = 0;
        for (var i = 0; i + 1 < samples.Length; i += 2)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(samples, i)));

        // Peak 0.12 (-18.4 dBFS): audible over a quiet room, still a gentle chime rather than an
        // alarm startle (the dwell cue is 0.10; this one may be a touch firmer).
        Check.That(peak).IsStrictlyGreaterThan((int)(0.05 * 32_767));
        Check.That(peak).IsLessOrEqualThan((int)(0.12 * 32_767) + 1);
    }

    [Fact]
    public void TheStatusChime_IsNotACopyOfTheDwellCue()
    {
        var cuePath = Path.Combine(AppContext.BaseDirectory, "Assets", "dwell-cue.wav");
        var (chimeHeader, chimeSamples) = ReadAsset(AssetPath());
        var (cueHeader, cueSamples) = ReadAsset(cuePath);

        Check.WithCustomMessage("The status chime must be a distinct sound from the dwell cue.")
            .That(chimeHeader.DataByteCount).Not.IsEqualTo(cueHeader.DataByteCount);
        Check.That(chimeSamples.AsSpan().SequenceEqual(cueSamples)).IsFalse();
    }
}
