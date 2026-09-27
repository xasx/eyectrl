namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Fixup-01 F2 (both reviewers): the per-frame <see cref="GazeFrame.ActivationProgress"/> the user's
/// feedback consumes was unpinned end to end — publishing a constant 0, or a wrong scale, left every
/// suite green because only VM tests injected hand-made frames.
/// <list type="bullet">
/// <item>Mutations that must red: publish a constant 0; scale by <c>credited / (fix + dwell)</c> instead
/// of <c>(credited − fix) / dwell</c>; publish the frame BEFORE the detector update.</item>
/// </list>
/// The last one is why the one-step-past-onset sample is pinned: with publish-before-update it still
/// carries the previous sample's value (0), and only the update-then-publish order gives the credited
/// fraction.
/// </summary>
public class GazeFrameActivationProgressTests
{
    private static EyeCtrlSettings Settings()
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct;
        s.Dwell.FixationRadiusPixels = 75;
        s.Dwell.FixationDetectionMs = 300;
        s.Dwell.ActivationButtonDwellMs = 700;
        return s;
    }

    [Fact]
    public void PublishedProgress_ZeroBeforeOnset_CreditedFractionAfter_HoldsOnGapFrames_OneAtActivation()
    {
        var s = Settings();
        var provider = new ScriptedGazeProvider();
        using var engine = new GazeEngine(provider, new RecordingInputSimulator(), s);
        var frames = new List<GazeFrame>();
        int activations = 0;
        engine.CursorMoved += frames.Add;
        engine.FixationActivated += _ => activations++;

        long ts = 0;
        for (int i = 0; i < 21; i++) // 0..600 ms → credited 600, onset at 300
        {
            provider.Push(new GazePoint(1000, 1000, ts, true));
            ts += 30_000;
        }
        double heldProgress = frames[20].ActivationProgress;
        Check.That(heldProgress).IsCloseTo(300.0 / 700.0, 1e-9);

        provider.Push(new GazePoint(0, 0, ts, false));            // 630, gap-held
        provider.Push(new GazePoint(0, 0, ts + 30_000, false));   // 660, gap-held
        ts += 60_000;
        for (int i = 0; i < 15; i++) // resume at 660 ms; activation once credited reaches 1000
        {
            provider.Push(new GazePoint(1000, 1000, ts, true));
            ts += 30_000;
        }

        Check.That(frames[5].ActivationProgress).IsEqualTo(0.0);                   // before onset
        // One step past onset: credited 330 → (330 − 300) / 700. Publish-before-update gives 0 here.
        Check.That(frames[11].ActivationProgress).IsCloseTo(30.0 / 700.0, 1e-9);
        Check.That(frames[21].IsInGap).IsTrue();
        Check.That(frames[21].ActivationProgress).IsEqualTo(heldProgress);         // held, not zeroed
        Check.That(frames[22].ActivationProgress).IsEqualTo(heldProgress);
        Check.That(activations).IsEqualTo(1);

        int activationFrame = frames.FindIndex(f => f.ActivationProgress >= 1.0);
        Check.That(activationFrame).IsStrictlyGreaterThan(0);
        Check.That(frames[activationFrame].ActivationProgress).IsEqualTo(1.0);     // the activation sample
    }
}
