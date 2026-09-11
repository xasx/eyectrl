namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The engine keeps publishing <c>CursorMoved</c> during a blink so display surfaces can hold the last
/// point — these pin the flag that tells the dwell surfaces downstream to skip such a sample. Without
/// it on the frame, <c>isInGap</c> reached only the in-process fixation gate and every dwell surface
/// (which sees gaze as a UI-thread message) went on accumulating against the frozen position.
/// </summary>
public class GazeEngineFrameGapFlagTests
{
    private static (GazeEngine Engine, FakeGazeProvider Provider, List<GazeFrame> Frames) NewEngine()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;   // MaxGapInterpolationMs default is 150 ms

        var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);
        var frames = new List<GazeFrame>();
        engine.CursorMoved += frames.Add;
        engine.Start();
        return (engine, provider, frames);
    }

    [Fact]
    public void ValidSample_PublishesFrameNotInGap()
    {
        var (engine, provider, frames) = NewEngine();
        using var _ = engine;

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        Check.That(frames).HasSize(1);
        Check.That(frames[0].IsInGap).IsFalse();
        Check.That(frames[0].Position.X).IsEqualTo(500.0);
        Check.That(frames[0].TimestampMicroseconds).IsEqualTo(0L);
    }

    [Fact]
    public void ShortGapSample_PublishesHeldPositionFlaggedInGap()
    {
        var (engine, provider, frames) = NewEngine();
        using var _ = engine;

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));
        provider.SimulateGaze(new GazePoint(0, 0, 50_000, false));   // 50 ms < 150 ms budget

        Check.That(frames).HasSize(2);
        Check.That(frames[1].IsInGap).IsTrue();
        Check.That(frames[1].Position.X).IsEqualTo(500.0);   // the HELD point, not the (0,0) raw sample
        Check.That(frames[1].Position.Y).IsEqualTo(300.0);
    }

    [Fact]
    public void LongGapAndItsTail_AreBothFlaggedInGap()
    {
        var (engine, provider, frames) = NewEngine();
        using var _ = engine;

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));
        provider.SimulateGaze(new GazePoint(0, 0, 200_000, false));  // long gap breaks the sequence
        provider.SimulateGaze(new GazePoint(0, 0, 220_000, false));  // ...and its tail

        Check.That(frames).HasSize(3);
        Check.That(frames[1].IsInGap).IsTrue();
        Check.That(frames[2].IsInGap).IsTrue();
    }

    [Fact]
    public void RecoverySample_IsNotFlaggedInGap()
    {
        var (engine, provider, frames) = NewEngine();
        using var _ = engine;

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));
        provider.SimulateGaze(new GazePoint(0, 0, 50_000, false));
        provider.SimulateGaze(new GazePoint(520, 310, 100_000, true));

        Check.That(frames[^1].IsInGap).IsFalse();
        Check.That(frames[^1].Position.X).IsEqualTo(520.0);
    }
}
