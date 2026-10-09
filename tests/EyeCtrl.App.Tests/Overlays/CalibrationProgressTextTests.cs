namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Calibration;

/// <summary>
/// T08 fixup R1: the progress line's English, pinned as literals at the pure seam the window now
/// draws through. Mutants M1 ("Recording..." ↔ "Computing...") and M2 (the "· n / total"
/// arguments swapped) were invisible before.
///
/// <para>
/// The helper takes the view model's 0-BASED index and adds the 1 itself (T08 review-02 N1), so
/// the "· 2 / 9" pin also kills a dropped "+ 1" ("· 0 / 9" or "· 1 / 9") that used to live at the
/// unpinned call site.
/// </para>
/// </summary>
public class CalibrationProgressTextTests
{
    [Fact]
    public void Collecting_ShowsTheTargetCount()
    {
        Check.That(CalibrationWindow.ProgressTextFor(
                CalibrationState.Collecting, index: 1, total: 9, measureOnly: false))
             .IsEqualTo("Look at the dot · 2 / 9");
    }

    [Fact]
    public void Training_ShowsRecordingForTheProbe_AndComputingForANormalRun()
    {
        Check.That(CalibrationWindow.ProgressTextFor(
                CalibrationState.Training, index: 0, total: 9, measureOnly: true))
             .IsEqualTo("Recording...");
        Check.That(CalibrationWindow.ProgressTextFor(
                CalibrationState.Training, index: 0, total: 9, measureOnly: false))
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
                 .That(CalibrationWindow.ProgressTextFor(state, index: 0, total: 9, measureOnly: false))
                 .IsEqualTo(string.Empty);
        }
    }
}
