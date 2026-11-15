namespace EyeCtrl.App.Tests;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// The window half of the keep-out layout: applying a computed layout must move the tile
/// viewport and raise the hint row exactly as the pure geometry says, and ShowFullScreen must
/// actually compose and apply it. Nothing here shows the window — ApplyLayout is a pure property
/// write, and the show path is pinned at the source (PointToScreen-based layout cannot run
/// headless).
/// </summary>
[Collection("WpfApplication")]
public class TaskSwitcherWindowLayoutTests
{
    [StaFact]
    public void ApplyLayout_MovesTheTileViewportAndRaisesTheHintRowAboveABottomBand()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new TaskSwitcherWindow());
        var tiles = (ScrollViewer)window.FindName("TilesScroll")!;
        var hintRow = (Grid)window.FindName("HintRow")!;

        // Docked top at 112 DIP on the owner's screen: area (40,136,1840,940), block 1800x840.
        var top = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 63);
        window.ApplyLayout(top, screenWidthDip: 1920);

        Check.That(tiles.Margin).IsEqualTo(new Thickness(40, 136, 40, 0));
        Check.That(tiles.Width).IsEqualTo(1800);
        Check.That(tiles.Height).IsEqualTo(840);
        Check.That(hintRow.Height).IsEqualTo(TaskSwitcherLayout.HintRowHeight);
        Check.That(hintRow.Margin.Bottom).IsEqualTo(0);

        // The reserved hint-row height is the close disc plus its 12 DIP top/bottom margins. Pin
        // the coupling: growing the disc without growing the reserved row would clip the bottom
        // full tile row the geometry promised.
        Check.WithCustomMessage("HintRowHeight must stay the close disc plus its 24 DIP of margins")
            .That(TaskSwitcherLayout.HintRowHeight).IsEqualTo(TaskSwitcherWindow.CloseButtonSize + 24);

        // Docked bottom at 112 DIP: the hint row must sit fully above the band (112 + 24 = 136).
        var bottom = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 1088, 1920, 112), "Bottom", tileCount: 63);
        window.ApplyLayout(bottom, screenWidthDip: 1920);

        Check.That(hintRow.Margin.Bottom).IsEqualTo(136);
        Check.That(tiles.Margin.Top).IsEqualTo(40);
        Check.That(tiles.Height).IsEqualTo(7 * TaskSwitcherLayout.CellHeight);
    }

    /// <summary>
    /// Source-scan pin: ShowFullScreen must hand the coordinator's keep-out to the pure helper
    /// (converted once through the live DPI scale) and apply the result before showing. A
    /// regression that ignores the argument, computes but never applies, or applies the scale by
    /// multiplying (the 2026-08-17 double-scale class) leaves every pure test green while the
    /// tiles land under the toolbar.
    /// </summary>
    [Fact]
    public void ShowFullScreen_ComputesTheLayoutFromTheKeepOut_AndAppliesIt()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var start = source.IndexOf("public void ShowFullScreen(ToolbarKeepOut? toolbarKeepOut)",
            StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = SourceScanner.StripComments(
            source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)]);

        Check.WithCustomMessage(
                "ShowFullScreen ignores the toolbar keep-out — the tiles start under the docked band.")
            .That(body.Contains("ComputeLayout(toolbarKeepOut")).IsTrue();
        Check.WithCustomMessage(
                "ShowFullScreen computes the layout but never applies it to the window.")
            .That(body.Contains("ApplyLayout(")).IsTrue();
        Check.WithCustomMessage(
                "The physical toolbar bounds must be converted with the live DPI scale, not by "
                + "multiplying DIPs into physical pixels again (the double-scale class).")
            .That(body.Contains("DpiHelper.ScaleX") && body.Contains("DpiHelper.ScaleY")).IsTrue();
        Check.WithCustomMessage(
                "ShowFullScreen must not scale the physical bounds a second time — ToDip already "
                + "divides by the scale.")
            .That(body.Contains("DpiHelper.LogicalToPhysical")).IsFalse();
    }
}
