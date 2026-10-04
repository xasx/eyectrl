namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Fixation suppression is a claim set: one claim per <see cref="FixationSuppressor"/>, suppressed
/// while any is held, and the release side effects (end the reported run, re-arm) fire only when the
/// LAST claim goes. Before it, one shared bool let any owner's release cancel another owner's hold
/// (architecture-check-2026-10, before-adoption item 1). Each test names the production mutation
/// that turns it red.
/// </summary>
public class FixationSuppressionClaimTests
{
    private const long StepUs = 30_000;

    private static GazeEngine NewEngine(ScriptedGazeProvider provider)
    {
        var s = new EyeCtrlSettings();
        s.Cursor.FilterMode = GazeFilterMode.Direct;
        return new GazeEngine(provider, new RecordingInputSimulator(), s);
    }

    private static long FeedValid(ScriptedGazeProvider p, long fromUs, int samples)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            p.Push(new GazePoint(1000, 1000, ts, true));
            ts += StepUs;
        }
        return ts;
    }

    [Fact]
    public void AnotherOwnersRelease_DoesNotLiftAHeldClaim()
    {
        // Mutation: lift suppression on any release (drop the "claims empty" check) → red.
        using var engine = NewEngine(new ScriptedGazeProvider());

        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);
        engine.AcquireFixationSuppression(FixationSuppressor.ModesMenu);
        engine.ReleaseFixationSuppression(FixationSuppressor.ModesMenu);
        Check.That(engine.SuppressFixation).IsTrue();

        engine.ReleaseFixationSuppression(FixationSuppressor.Keyboard);
        Check.That(engine.SuppressFixation).IsFalse();
    }

    [Theory]
    [InlineData(FixationSuppressor.Keyboard, FixationSuppressor.ExternalDwell)]
    [InlineData(FixationSuppressor.Calibration, FixationSuppressor.ModesMenu)]
    [InlineData(FixationSuppressor.ExternalDwell, FixationSuppressor.Keyboard)]
    public void EveryOwnerPair_KeepsSuppressionUntilBothRelease(FixationSuppressor first, FixationSuppressor second)
    {
        using var engine = NewEngine(new ScriptedGazeProvider());

        engine.AcquireFixationSuppression(first);
        engine.AcquireFixationSuppression(second);
        engine.ReleaseFixationSuppression(first);
        Check.That(engine.SuppressFixation).IsTrue();
        engine.ReleaseFixationSuppression(second);
        Check.That(engine.SuppressFixation).IsFalse();
    }

    [Fact]
    public void ReleaseWithoutAClaim_IsANoOp()
    {
        // The calibration owner releases twice (Active/Failed, then Idle), and a hide can arrive
        // without a show. Mutation: count claims instead of keying them by owner → a stray release
        // drops another owner's claim and this goes red.
        using var engine = NewEngine(new ScriptedGazeProvider());

        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);
        engine.ReleaseFixationSuppression(FixationSuppressor.Calibration); // never acquired
        engine.ReleaseFixationSuppression(FixationSuppressor.Calibration);
        Check.That(engine.SuppressFixation).IsTrue();
    }

    [Fact]
    public void RepeatedAcquireBySameOwner_NeedsOnlyOneRelease()
    {
        using var engine = NewEngine(new ScriptedGazeProvider());

        engine.AcquireFixationSuppression(FixationSuppressor.ExternalDwell);
        engine.AcquireFixationSuppression(FixationSuppressor.ExternalDwell);
        engine.ReleaseFixationSuppression(FixationSuppressor.ExternalDwell);
        Check.That(engine.SuppressFixation).IsFalse();
    }

    [Fact]
    public void ReleaseEffects_FireOnlyWhenTheLastClaimGoes()
    {
        // The release edge ends the reported run and re-arms; it is applied on the next sample even
        // while suppressed. External dwell takes no OnFixationEnded of its own, so a run reported
        // before it acquired keeps the state at Fixation. Mutation: run the release effects on every
        // release → the ModesMenu release below ends the run while the keyboard still holds its
        // claim, and the state reads FreeLook.
        var provider = new ScriptedGazeProvider();
        using var engine = NewEngine(provider);
        var im = engine.InteractionManager;

        long ts = FeedValid(provider, 0, samples: 15);                  // ~450 ms held: onset reported
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Fixation);

        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);
        engine.AcquireFixationSuppression(FixationSuppressor.ModesMenu);
        engine.ReleaseFixationSuppression(FixationSuppressor.ModesMenu);
        ts = FeedValid(provider, ts, samples: 2);
        Check.WithCustomMessage("A non-final release ran the release effects and ended the run.")
            .That(im.CurrentState).IsEqualTo(InteractionState.Fixation);

        engine.ReleaseFixationSuppression(FixationSuppressor.Keyboard);
        FeedValid(provider, ts, samples: 1);
        Check.WithCustomMessage("The last release must end the reported run (fixup-02 N1).")
            .That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void ConcurrentOwners_OnTwoThreads_LeaveAConsistentClaimSet()
    {
        // External dwell claims on the gaze thread; the other owners on the dispatcher. Mutation:
        // drop the lock → the HashSet races (lost claims or a corrupted set) and the final state is
        // wrong or a thread throws.
        using var engine = NewEngine(new ScriptedGazeProvider());
        const int Iterations = 200_000;
        using var start = new ManualResetEventSlim();

        void Churn(FixationSuppressor owner)
        {
            start.Wait();
            for (int i = 0; i < Iterations; i++)
            {
                engine.AcquireFixationSuppression(owner);
                engine.ReleaseFixationSuppression(owner);
            }
        }

        var a = Task.Run(() => Churn(FixationSuppressor.ExternalDwell));
        var b = Task.Run(() => Churn(FixationSuppressor.Keyboard));
        start.Set();
        Task.WaitAll(a, b);

        Check.That(engine.SuppressFixation).IsFalse();

        // The set must still track each owner exactly: a claim held now must survive the other
        // owner's release.
        engine.AcquireFixationSuppression(FixationSuppressor.Keyboard);
        engine.ReleaseFixationSuppression(FixationSuppressor.ExternalDwell);
        Check.That(engine.SuppressFixation).IsTrue();
    }
}
