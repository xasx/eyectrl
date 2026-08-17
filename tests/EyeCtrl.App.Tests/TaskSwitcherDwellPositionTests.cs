namespace EyeCtrl.App.Tests;

using System.IO;
using NFluent;

/// <summary>
/// Source-scan pin: the task switcher must push dwell-target positions through
/// <c>UIElement.PointToScreen</c> — physical device pixels, DPI-virtualization-aware — the
/// way every other overlay does (Keyboard, Toolbar, QuickActions, Settings). It was the one
/// overlay computing centers manually (TransformToAncestor + DpiHelper.LogicalToPhysicalX),
/// and under a DPI-virtualized process the targets landed far from the tiles: tiles visible,
/// nothing dwellable (2026-08-17 field report).
/// </summary>
public class TaskSwitcherDwellPositionTests
{
    [Fact]
    public void TilePositions_UsePointToScreen_NeverManualDpiScaling()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var body = ExtractMethod(source, "private void UpdateTilePositions()");

        Check.WithCustomMessage(
                "UpdateTilePositions no longer uses PointToScreen — the one overlay that "
                + "deviated from it was undwellable under DPI virtualization. PointToScreen "
                + "returns physical device pixels; any manual DpiHelper multiplication on top "
                + "of a position transform is the bug this pin exists for.")
            .That(body.Contains("PointToScreen")).IsTrue();
        Check.WithCustomMessage(
                "UpdateTilePositions still scales positions with DpiHelper.LogicalToPhysical — "
                + "DpiHelper's scale does not know Windows' DPI virtualization; PointToScreen "
                + "does. Remove the manual scaling (see QuickActionsGridWindow's comment).")
            .That(body.Contains("LogicalToPhysicalX(center")).IsFalse();
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
