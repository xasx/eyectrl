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

    /// <summary>
    /// Source-scan pin: BringToFront must earn the foreground right before calling
    /// SetForegroundWindow. The overlays are WS_EX_NOACTIVATE, so the process never holds
    /// foreground and Windows' foreground lock silently rejects a bare call — switcher
    /// selections fired, the overlay closed, and no window came forward (2026-08-18 field
    /// diagnosis). A bare AllowSetForegroundWindow(ASFW_ANY) is the same bug again: it
    /// delegates a right the caller must already hold.
    /// </summary>
    [Fact]
    public void BringToFront_EarnsForeground_BeforeSetForegroundWindow()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));
        var body = ExtractMethod(source, "public void BringToFront(IntPtr hwnd)");

        Check.WithCustomMessage(
                "BringToFront must simulate an ALT press around SetForegroundWindow (the "
                + "standard foreground-right grant) — without it the foreground lock silently "
                + "rejects the switch for this non-activating overlay process. Restore the "
                + "keybd_event bracket.")
            .That(body.Contains("keybd_event")).IsTrue();
        Check.WithCustomMessage(
                "AllowSetForegroundWindow cannot grant a right the caller does not hold; for "
                + "this process it is a no-op and its presence masks the missing ALT grant.")
            .That(body.Contains("AllowSetForegroundWindow(NativeMethods")).IsFalse();
        // Minimized windows (2026-08-18 field follow-up: WhatsApp/ZCode/Claude stayed on the
        // taskbar while Edge/Word/Task-Manager switched): SetForegroundWindow reports success
        // on an iconic window WITHOUT un-minimizing it, so the success return skips every
        // fallback and the window never appears. The restore must come first.
        Check.WithCustomMessage(
                "BringToFront must restore minimized windows (IsIconic → SW_RESTORE) before "
                + "foregrounding — SetForegroundWindow alone leaves iconic windows minimized "
                + "while reporting success.")
            .That(body.Contains("IsIconic") && body.Contains("SW_RESTORE")).IsTrue();
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
