namespace EyeCtrl.App.Tests;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Interfaces;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class EyeCareHooksTests
{
    [StaFact]
    [Trait("Category", "UI")]
    public void ShowSummary_SetsTextBlocks()
    {
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

        Check.That(window.SummaryAccuracy.Text).IsEqualTo("92%");
        Check.That(window.SummarySteadiness.Text).IsEqualTo("4.3");

        window.Close();
    }
}
