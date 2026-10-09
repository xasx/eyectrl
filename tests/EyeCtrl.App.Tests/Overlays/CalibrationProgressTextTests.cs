namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Calibration;

/// <summary>
/// T08 fixup R1: the progress line's English, pinned as literals at the pure seam the window now
/// draws through. Mutants M1 ("Recording..." ↔ "Computing...") and M2 (the "· n / total"
/// arguments swapped) were invisible before; the count's 1-based position is pinned by the "2 / 9"
/// case, so an argument swap cannot pass as its own text.
/// </summary>
public class CalibrationProgressTextTests
{
    [Fact]
    public void Collecting_ShowsTheTargetCount()
    {
        Check.That(CalibrationWindow.ProgressTextFor(
                CalibrationState.Collecting, currentTargetOneBased: 2, totalTargets: 9, measureOnly: false))
             .IsEqualTo("Look at the dot · 2 / 9");
    }

    [Fact]
    public void Training_ShowsRecordingForTheProbe_AndComputingForANormalRun()
    {
        Check.That(CalibrationWindow.ProgressTextFor(
                CalibrationState.Training, currentTargetOneBased: 1, totalTargets: 9, measureOnly: true))
             .IsEqualTo("Recording...");
        Check.That(CalibrationWindow.ProgressTextFor(
                CalibrationState.Training, currentTargetOneBased: 1, totalTargets: 9, measureOnly: false))
             .IsEqualTo("Computing...");
    }

    [Fact]
    public void StatesWithoutAProgressLine_RenderNothing()
    {
        foreach (var state in new[]
                 {
                     CalibrationState.Idle, CalibrationState.Active, CalibrationState.Failed,
                 })
        {
            Check.WithCustomMessage($"{state} draws no progress line")
                 .That(CalibrationWindow.ProgressTextFor(state, 1, 9, measureOnly: false))
                 .IsEqualTo(string.Empty);
        }
    }
}
