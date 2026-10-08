namespace EyeCtrl.App.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

// Both tests call WpfApplicationGate.EnsureApplicationWithTokens and construct real WPF windows
// (EyeCareExerciseWindow, EyeCareBannerWindow), so this class must join the collection that
// serialises every WpfApplicationGate caller against the process-wide Application singleton — see
// WpfApplicationCollection's doc for why Xunit.StaFact's per-test STA thread makes that race real.
//
// This class also reads DpiHelper (PhysicalToLogicalX/Y below), so it would otherwise qualify
// for "DpiHelper Tests (App)" too. A class can only join ONE xUnit collection, and the
// Application singleton is the more severe race of the two, so WpfApplication wins. That costs
// nothing: "WpfApplication" is itself DisableParallelization, and xUnit v3 runs such a collection
// exclusively — never alongside another collection — so a DpiHelper writer
// (ActionMenuGeometryTests, ScreenGeometryTests) cannot run while these tests do. This comment
// previously described a residual exposure here; that was xUnit v2 semantics and did not apply.
// See NonParallelCollectionPin for the pin on the scheduling behaviour.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("WpfApplication")]
public class EyeCareHooksTests
{
    [StaFact]
    [Trait("Category", "UI")]
    public void ShowSummary_SetsTextBlocks()
    {
        // EyeCareExerciseWindow.xaml reads {StaticResource ...} tokens (Task 8 migration), so the
        // OverlayStyles.xaml dictionary must be merged into Application.Current before
        // construction — see WpfApplicationGate. This test predates that migration and never
        // established the precondition itself.
        WpfApplicationGate.EnsureApplicationWithTokens();

        // Construct the exercise window and verify summary card text blocks are populated.
        // This is a UI smoke test — it needs a display (STA thread + WPF dispatcher).
        var window = new EyeCareExerciseWindow();
        window.Show();

        var outcome = new EyeCareExerciseOutcome("Smooth Pursuit", 92, 4.3, null, 45, "Smooth tracking!");
        var task = window.ShowSummaryAsync(outcome);

        // Pump the dispatcher briefly so the summary card renders
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new System.Windows.Threading.DispatcherOperationCallback(_ => { frame.Continue = false; return null; }),
            null);
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        // T02: the window formats through the eye-care surface's provider — invariant while that
        // surface is English — so an English UI renders "4.3" even on this German machine.
        // Before T02 the window read CultureInfo.Current and this assertion had to mirror the
        // machine's separator; now the invariant form is the correct, machine-independent
        // expectation. Assert the literals the user sees.
        Check.That(window.SummaryAccuracy.Text).IsEqualTo("92%");
        Check.That(window.SummarySteadiness.Text).IsEqualTo("4.3");

        window.Close();
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void GazeMessage_RoutedToExerciseWindow()
    {
        // Same precondition as ShowSummary_SetsTextBlocks above: both EyeCareBannerWindow.xaml
        // and EyeCareExerciseWindow.xaml read StaticResource tokens now, so the dictionary must
        // be merged before either is constructed.
        WpfApplicationGate.EnsureApplicationWithTokens();

        var messenger = new WeakReferenceMessenger();
        var banner = new EyeCareBannerWindow();
        var exerciseWindow = new EyeCareExerciseWindow();
        var hooks = new EyeCareHooks(
            messenger,
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            banner,
            exerciseWindow,
            new PhysicalPoint(960, 540));

        exerciseWindow.Show();

        // Start an exercise (this sets _gazeSink = exerciseWindow.OnGaze)
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            // Run a short exercise; we don't need to await completion — just start it
            var runTask = hooks.ShowExerciseAsync("Test", 10_000_000,
                _ => new PhysicalPoint(500, 500),
                (_, _) => { });
            // Pump one frame so the window is showing and _gazeSink is set
            await System.Windows.Threading.Dispatcher.Yield();

            // Send a gaze message via the messenger
            messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(123, 456), 0));

            // Pump another frame so the message handler fires
            await System.Windows.Threading.Dispatcher.Yield();

            // The GazeRing should have moved to the DPI-converted position
            var dipX = EyeCtrl.Core.Services.DpiHelper.PhysicalToLogicalX(123) - exerciseWindow.GazeRing.Width / 2;
            var dipY = EyeCtrl.Core.Services.DpiHelper.PhysicalToLogicalY(456) - exerciseWindow.GazeRing.Height / 2;

            Check.That(System.Windows.Controls.Canvas.GetLeft(exerciseWindow.GazeRing)).IsCloseTo(dipX, 0.5);
            Check.That(System.Windows.Controls.Canvas.GetTop(exerciseWindow.GazeRing)).IsCloseTo(dipY, 0.5);

            exerciseWindow.Cancel();
            tcs.TrySetResult(true);
        });

        // Pump dispatcher until the test logic completes
        var pumpFrame = new System.Windows.Threading.DispatcherFrame();
        tcs.Task.ContinueWith(_ => pumpFrame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(pumpFrame);

        exerciseWindow.Close();
        banner.Close();
    }
}
