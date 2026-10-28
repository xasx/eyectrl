namespace EyeCtrl.Core.Tests.Status;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Status;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// Wave B ticket B3's half of B1 review 01 F4: a connected window that opens and closes BETWEEN two
/// 10 Hz polls is invisible to <c>IsConnected</c> snapshots, and a synthetic constant 100 ms window
/// aliases a fixed 100 ms poll into a false continuous disconnect (the real 09-24 storm is safe only
/// because its windows are ≥ 101 ms and its down windows jitter). The provider's connect-edge counter
/// makes any edge in the gap restart the continuous-disconnect clock.
/// </summary>
public class ControlStatusAliasingTests
{
    private const int PollIntervalMs = 100;

    private static (ControlStatusModel Model, FakeTimeProvider Time) NewModel()
    {
        var time = new FakeTimeProvider();
        var model = new ControlStatusModel(GazeProviderType.StreamEngine, time);
        model.Start();
        return (model, time);
    }

    [Fact]
    public void HundredMillisecondWindows_ThatEveryPollMisses_NeverSetDisconnected()
    {
        var (model, time) = NewModel();
        model.OnGazeSample(true);

        long edges = 0;
        for (int poll = 0; poll < 150; poll++)
        {
            // The window opened and closed since the previous poll: the snapshot is false, the
            // provider saw a connect. 15 s of wall time, > 10 s of accumulated false.
            model.Poll(isConnected: false, connectEdgeCount: edges++);
            time.Advance(TimeSpan.FromMilliseconds(PollIntervalMs));
        }

        Check.That(model.Current.Disconnected).IsFalse();
    }

    [Fact]
    public void WithoutConnectEdges_ElevenSecondsOfFalse_StillDisconnects()
    {
        // The hardening must not swallow a genuine unplug: no edges, the clock runs unchanged.
        var (model, time) = NewModel();
        model.OnGazeSample(true);

        for (int poll = 0; poll < 120; poll++)
        {
            model.Poll(isConnected: false, connectEdgeCount: 7);
            time.Advance(TimeSpan.FromMilliseconds(PollIntervalMs));
        }

        Check.That(model.Current.Disconnected).IsTrue();
    }

    [Fact]
    public void WhenTheEdgeStreamStops_TheClockRunsFromTheLastEdge()
    {
        var (model, time) = NewModel();
        model.OnGazeSample(true);

        long edges = 0;
        for (int poll = 0; poll < 90; poll++)          // 9 s of aliased windows
        {
            model.Poll(isConnected: false, connectEdgeCount: edges++);
            time.Advance(TimeSpan.FromMilliseconds(PollIntervalMs));
        }
        Check.That(model.Current.Disconnected).IsFalse();

        for (int poll = 0; poll < 90; poll++)          // 9 s unplugged, no more edges
        {
            model.Poll(isConnected: false, connectEdgeCount: edges);
            time.Advance(TimeSpan.FromMilliseconds(PollIntervalMs));
        }
        Check.WithCustomMessage("9 s after the last edge is still short of the 10 s bound.")
            .That(model.Current.Disconnected).IsFalse();

        for (int poll = 0; poll < 20; poll++)          // the 10 s bound passes without an edge
        {
            model.Poll(isConnected: false, connectEdgeCount: edges);
            time.Advance(TimeSpan.FromMilliseconds(PollIntervalMs));
        }
        Check.That(model.Current.Disconnected).IsTrue();
    }
}
