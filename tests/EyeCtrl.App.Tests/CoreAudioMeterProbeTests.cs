namespace EyeCtrl.App.Tests;

using NFluent;
using Xunit;

/// <summary>
/// Review-02 N1's seam tests for <see cref="CoreAudioInterop.CoreAudioMeterProbe"/>: the meter
/// source and the millisecond clock are injected, so the sampling loop runs with a managed fake
/// meter — no COM, no audio device, no real meter window. The rule under test: a sampler
/// superseded (newer click or new show) after at least SilentAfterMs of watching with no peak is
/// SILENT, one superseded earlier is NO VERDICT, and a peak above the threshold ends the window
/// at once.
/// </summary>
public class CoreAudioMeterProbeTests
{
    /// <summary>A managed IAudioMeterInformation: each GetPeakValue advances the fake clock by
    /// <paramref name="advanceMs"/>, so the probe's 1.5 s window passes in a few milliseconds of
    /// real waiting. A null scripted value answers a failed HRESULT (an unreadable meter).</summary>
    private sealed class FakeMeterInformation(long advanceMs, Func<int, float?> script)
        : CoreAudioInterop.IAudioMeterInformation
    {
        public long NowMs;
        public int Reads;

        public int GetPeakValue(out float peak)
        {
            var value = script(Reads);
            Reads++;
            NowMs += advanceMs;
            if (value is null)
            {
                peak = 0;
                return unchecked((int)0x80004005); // E_FAIL: the read itself failed
            }
            peak = value.Value;
            return CoreAudioInterop.SOk;
        }

        public int GetMeteringChannelCount(out int channelCount)
        {
            channelCount = 0;
            return unchecked((int)0x80004005);
        }

        public int GetChannelsPeakValues(int channelCount, IntPtr peakValues) =>
            unchecked((int)0x80004005);

        public int QueryHardwareSupport(out int hardwareSupportMask)
        {
            hardwareSupportMask = 0;
            return unchecked((int)0x80004005);
        }
    }

    private static CoreAudioInterop.CoreAudioMeterProbe Probe(FakeMeterInformation meter) =>
        new(() => meter, () => meter.NowMs);

    [Fact]
    public void ASamplerSupersededBeforeSilentAfterMs_HasNoVerdict()
    {
        // Two samples are 20 ms of watching — far short of a normal 10-40 ms start plus a device
        // wake — so a newer click/show must not turn this click into a silent one. (Mutant N1:
        // an early supersession answering the max instead of null.)
        var meter = new FakeMeterInformation(advanceMs: 10, _ => 0f);
        var result = Probe(meter).SamplePeak(() => meter.Reads >= 2);

        Check.WithCustomMessage(
                "a sampler superseded before SilentAfterMs reported a verdict — an early "
                + "supersession cannot say this click was silent (review-02 N1)")
            .That(result).IsNull();
        Check.That(meter.Reads).IsEqualTo(2);
    }

    [Fact]
    public void ASamplerSupersededAfterSilentAfterMs_WithNoPeak_IsSilent()
    {
        // A truly silent click never ends its own window early, so in the real failure case (the
        // next key arrives within 1.5 s) supersession is exactly how it ends. Past 500 ms the
        // normal start window has passed: the verdict is silent (0), not nothing.
        // (Kills the bound mutant 500 → WindowMs; the EARLY test kills 500 → 0. The stop bound is
        // the LITERAL 500 on purpose: reading the production constant here would make the test
        // follow the mutant — 500 → WindowMs survived exactly that way once.)
        var meter = new FakeMeterInformation(advanceMs: 100, _ => 0f);
        var result = Probe(meter).SamplePeak(() => meter.NowMs >= 500);

        Check.WithCustomMessage(
                "a sampler superseded after SilentAfterMs with no peak reported no verdict — a "
                + "silent click followed by the next key would vanish from every count "
                + "(review-02 N1)")
            .That(result).IsNotNull();
        Check.That(result!.Value).IsEqualTo(0f);
    }

    [Fact]
    public void APeakAboveTheThreshold_EndsTheWindowAtOnce()
    {
        // The threshold early-exit keeps a reached click from polling the whole 1.5 s window.
        // The fake clock advances 100 ms per read, so a missing early exit shows up as ~15 reads.
        var meter = new FakeMeterInformation(advanceMs: 100, _ => 0.5f);
        var result = Probe(meter).SamplePeak(() => false);

        Check.That(result).IsNotNull();
        Check.That(result!.Value).IsEqualTo(0.5f);
        Check.WithCustomMessage(
                "a peak above the threshold did not end the window — every reached click would "
                + "poll the full meter window")
            .That(meter.Reads).IsEqualTo(1);
    }

    [Fact]
    public void AMeterWhoseReadsFailEveryTime_HasNoVerdict()
    {
        // An unreadable meter can never say the click was silent; it stays a no-verdict (which
        // the hide line counts as unmetered).
        var meter = new FakeMeterInformation(advanceMs: 250, _ => null);
        var result = Probe(meter).SamplePeak(() => false);

        Check.WithCustomMessage("a meter that could not be read produced a silent verdict")
            .That(result).IsNull();
        Check.That(meter.Reads).IsStrictlyGreaterThan(1); // it polled to the cap, not just once
    }

    [Fact]
    public void AMeterSourceThatYieldsNoMeter_HasNoVerdict()
    {
        // An unconfigured endpoint (AcquireSharedMeter answers null) is a no-verdict, never 0.
        var probe = new CoreAudioInterop.CoreAudioMeterProbe(() => null, () => 0);

        Check.That(probe.SamplePeak(() => false)).IsNull();
    }
}
