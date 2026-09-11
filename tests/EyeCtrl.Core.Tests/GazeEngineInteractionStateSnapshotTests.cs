namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// <c>GazeEngine.OnGazePoint</c> snapshots <see cref="InteractionManager.CurrentState"/> once per sample
/// and decides from that snapshot. A deterministic race against the dispatcher thread is impractical, so
/// these tests pin the observable per-state behaviour the snapshot must preserve — every decision in one
/// sample agreeing on one state, across the transitions the snapshot is read for.
/// </summary>
public class GazeEngineInteractionStateSnapshotTests
{
    private static (GazeEngine Engine, FakeGazeProvider Provider, FakeInputSimulator Simulator) NewEngine()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();
        return (engine, provider, simulator);
    }

    [Fact]
    public void WhilePaused_DoesNotMoveCursor()
    {
        var (engine, provider, simulator) = NewEngine();
        using var _ = engine;
        engine.InteractionManager.TogglePause();
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Paused);

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        Check.That(simulator.Actions).Not.HasElementThatMatches(a => a.Action == "Move");
    }

    [Fact]
    public void WhilePaused_StillPublishesCursorMoved()
    {
        // The ring and the toolbar keep receiving gaze while paused — that is what lets a toolbar dwell
        // unpause. The snapshot gates the cursor move, not the publish.
        var (engine, provider, _) = NewEngine();
        using var e = engine;
        var published = new List<PhysicalPoint>();
        engine.CursorMoved += (p, _) => published.Add(p);
        engine.InteractionManager.TogglePause();

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        Check.That(published).HasSize(1);
        Check.That(published[0].X).IsEqualTo(500.0);
    }

    [Fact]
    public void WhilePaused_DoesNotFeedFixationDetector()
    {
        // The Paused snapshot returns early, so the fixation detector never sees the sample. Observed
        // through the centroid tracker, which only starts tracking on FixationStarted.
        var (engine, provider, _) = NewEngine();
        using var e = engine;
        engine.InteractionManager.TogglePause();

        for (int i = 0; i <= 30; i++)   // >300 ms of perfectly stable gaze
            provider.SimulateGaze(new GazePoint(500, 300, i * 20_000, true));

        Check.That(engine.StableCentroid).IsNull();
    }

    [Fact]
    public void WhileFreeLook_FeedsFixationDetector()
    {
        // Control for the test above: the same stream while active does start a fixation.
        var (engine, provider, _) = NewEngine();
        using var e = engine;

        for (int i = 0; i <= 30; i++)
            provider.SimulateGaze(new GazePoint(500, 300, i * 20_000, true));

        Check.That(engine.StableCentroid).IsNotNull();
    }

    [Fact]
    public void AfterResume_MovesCursorAgain()
    {
        var (engine, provider, simulator) = NewEngine();
        using var _ = engine;
        engine.InteractionManager.TogglePause();
        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        engine.InteractionManager.TogglePause();
        Check.That(engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
        provider.SimulateGaze(new GazePoint(600, 400, 20_000, true));

        Check.That(simulator.Actions).HasSize(1);
        Check.That(simulator.Actions[0].Action).IsEqualTo("Move");
        Check.That(simulator.Actions[0].Point.X).IsEqualTo(600.0);
    }
}
