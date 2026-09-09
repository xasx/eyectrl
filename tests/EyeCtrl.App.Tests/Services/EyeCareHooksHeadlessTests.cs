namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The gaze-routing contract in <see cref="EyeCareHooks"/>: while a banner or an exercise is up,
/// every gaze sample must reach THAT overlay's dwell detector — and must stop reaching it once the
/// overlay finishes.
/// <para>
/// <b>Why this file exists.</b> The only test covering this lived in <c>EyeCareHooksTests</c>,
/// which constructs both real WPF windows and therefore carries
/// <c>[Trait("Category", "UI")]</c>. CI runs <c>--filter "Category!=UI"</c>, so the routing has
/// never been executed in CI. Narrowing the two overlays to <see cref="IEyeCareBanner"/> and
/// <see cref="IEyeCareExercise"/> removed the only reason a window was needed to observe it.
/// </para>
/// <para>
/// What stays in the UI test and cannot move here: the assertion that the real gaze ring lands at
/// the DPI-converted position (<c>Canvas.GetLeft(exerciseWindow.GazeRing)</c>) and that the summary
/// card's text blocks are populated. Those are claims about a rendered window, and a fake asserting
/// them would only be testing itself. This file covers the routing; that one covers the drawing.
/// </para>
/// </summary>
public class EyeCareHooksHeadlessTests
{
    private sealed record GazeSample(double X, double Y, long TimestampUs);

    private sealed class FakeBanner : IEyeCareBanner
    {
        private readonly TaskCompletionSource<bool> _shown = new();

        public List<GazeSample> Gaze { get; } = new();
        public int ShowCalls { get; private set; }
        public int CancelCalls { get; private set; }

        /// <summary>Completes the pending ShowAsync, as a user taking the break or snoozing would.</summary>
        public TaskCompletionSource<bool> Result { get; } = new();

        public Task<bool> ShowAsync(string message, PhysicalPoint screenCenter, int autoSnoozeSeconds)
        {
            ShowCalls++;
            _shown.TrySetResult(true);
            return Result.Task;
        }

        public void OnGaze(PhysicalPoint gaze, long timestampUs) =>
            Gaze.Add(new GazeSample(gaze.X, gaze.Y, timestampUs));

        public void Cancel() => CancelCalls++;
    }

    private sealed class FakeExercise : IEyeCareExercise
    {
        public List<GazeSample> Gaze { get; } = new();
        public int RunCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public EyeCareExerciseOutcome? Summary { get; private set; }

        public TaskCompletionSource<bool> Result { get; } = new();

        public Task RunAsync(string exerciseName, long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze)
        {
            RunCalls++;
            return Result.Task;
        }

        public void OnGaze(PhysicalPoint gaze, long timestampUs) =>
            Gaze.Add(new GazeSample(gaze.X, gaze.Y, timestampUs));

        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome)
        {
            Summary = outcome;
            return Task.CompletedTask;
        }

        public void Cancel() => CancelCalls++;
    }

    private sealed class Harness
    {
        public IMessenger Messenger { get; } = new WeakReferenceMessenger();
        public FakeBanner Banner { get; } = new();
        public FakeExercise Exercise { get; } = new();
        public EyeCareHooks Hooks { get; }

        public Harness()
        {
            Hooks = new EyeCareHooks(
                Messenger,
                Dispatcher.CurrentDispatcher,
                Banner,
                Exercise,
                new PhysicalPoint(960, 540));
        }

        public void SendGaze(double x, double y, long us) =>
            Messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(x, y), us));
    }

    /// <summary>Drains queued dispatcher work. No STA thread needed — nothing here is a Window.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void GazeReachesNeitherOverlay_WhenNothingIsShowing()
    {
        // The default. A stray sink left armed from a previous break would feed dwell into a
        // hidden overlay, which can activate a button nobody can see.
        var h = new Harness();

        h.SendGaze(100, 100, 0);
        Pump();

        Check.That(h.Banner.Gaze).IsEmpty();
        Check.That(h.Exercise.Gaze).IsEmpty();
    }

    [Fact]
    public void WhileTheBannerIsUp_GazeGoesToTheBanner()
    {
        var h = new Harness();

        var showing = h.Hooks.ShowBannerAsync("Time for a break");
        Pump();

        h.SendGaze(123, 456, 7_000);
        Pump();

        Check.That(h.Banner.ShowCalls).IsEqualTo(1);
        Check.That(h.Banner.Gaze).HasSize(1);
        Check.That(h.Banner.Gaze[0]).IsEqualTo(new GazeSample(123, 456, 7_000));
        Check.That(h.Exercise.Gaze).IsEmpty();

        h.Banner.Result.TrySetResult(true);
        Pump();
        Check.That(showing.IsCompleted).IsTrue();
    }

    [Fact]
    public void AfterTheBannerCloses_GazeStopsReachingIt()
    {
        // The half that matters most: the sink must be cleared on completion. Otherwise the
        // banner's dwell detector keeps accumulating against a dismissed overlay for the rest of
        // the session, and its buttons can fire unseen.
        var h = new Harness();

        _ = h.Hooks.ShowBannerAsync("Break");
        Pump();
        h.Banner.Result.TrySetResult(false);   // snoozed
        Pump();

        h.SendGaze(10, 20, 1);
        Pump();

        Check.That(h.Banner.Gaze).IsEmpty();
    }

    [Fact]
    public void WhileAnExerciseRuns_GazeGoesToTheExercise()
    {
        var h = new Harness();

        _ = h.Hooks.ShowExerciseAsync("Smooth Pursuit", 10_000_000,
            _ => new PhysicalPoint(500, 500), (_, _) => { });
        Pump();

        h.SendGaze(321, 654, 99);
        Pump();

        Check.That(h.Exercise.RunCalls).IsEqualTo(1);
        Check.That(h.Exercise.Gaze).HasSize(1);
        Check.That(h.Exercise.Gaze[0]).IsEqualTo(new GazeSample(321, 654, 99));
        Check.That(h.Banner.Gaze).IsEmpty();
    }

    [Fact]
    public void AfterTheExerciseEnds_GazeStopsReachingIt()
    {
        var h = new Harness();

        _ = h.Hooks.ShowExerciseAsync("Smooth Pursuit", 10_000_000,
            _ => new PhysicalPoint(500, 500), (_, _) => { });
        Pump();
        h.Exercise.Result.TrySetResult(true);
        Pump();

        h.SendGaze(1, 2, 3);
        Pump();

        Check.That(h.Exercise.Gaze).IsEmpty();
    }

    [Fact]
    public void CancelActive_StopsBothOverlays()
    {
        // Reached on shutdown and when the user leaves eye-care mode. Both overlays are cancelled
        // regardless of which was showing, so neither is left holding a dwell detector.
        var h = new Harness();

        h.Hooks.CancelActive();
        Pump();

        Check.That(h.Banner.CancelCalls).IsEqualTo(1);
        Check.That(h.Exercise.CancelCalls).IsEqualTo(1);
    }

    [Fact]
    public void ShowSummary_ForwardsTheOutcome()
    {
        var h = new Harness();
        var outcome = new EyeCareExerciseOutcome("Smooth Pursuit", 92, 4.3, null, 45, "Smooth tracking!");

        _ = h.Hooks.ShowSummaryAsync(outcome);
        Pump();

        Check.That(h.Exercise.Summary).IsNotNull();
        Check.That(h.Exercise.Summary!.ExerciseName).IsEqualTo("Smooth Pursuit");
        Check.That(h.Exercise.Summary.AccuracyPercent).IsEqualTo(92);
    }
}
