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
    /// delegates a right the caller must already hold. The ALT tap must be one atomic
    /// SendInput pair issued BEFORE the switch — see the split-bracket pin below.
    /// </summary>
    [Fact]
    public void BringToFront_EarnsForeground_BeforeSetForegroundWindow()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));
        var body = ExtractMethod(source, "public void BringToFront(IntPtr hwnd)");

        Check.WithCustomMessage(
                "BringToFront must simulate an ALT tap before SetForegroundWindow (the "
                + "standard foreground-right grant) — without it the foreground lock silently "
                + "rejects the switch for this non-activating overlay process.")
            .That(body.Contains("VK_MENU") && body.Contains("SendInput")).IsTrue();
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

    /// <summary>
    /// Source-scan pin: the ALT tap in <c>BringToFront</c> must be a single atomic
    /// <c>SendInput</c> down+up pair issued before <c>SetForegroundWindow</c>, never a split
    /// down/switch/up bracket. The split form delivers the WM_SYSKEYDOWN(VK_MENU) to the old
    /// foreground window and — after the focus switch redirects it — the matching WM_SYSKEYUP
    /// to the new one, leaving the old app with ALT latched (menu mode, Alt-shortcut
    /// misfires). It only bit when the app holding focus reacted to the orphaned down, hence
    /// "sometimes"; and a gaze-only user has no physical ALT press to clear the state, hence
    /// "must reboot" (2026-08-25 field report: "alt key seems stuck after launch").
    /// </summary>
    [Fact]
    public void BringToFront_AltGrant_IsAtomicPair_BeforeTheSwitch()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));
        var body = ExtractMethod(source, "public void BringToFront(IntPtr hwnd)");

        // Match the NativeMethods.-prefixed call sites, not prose: the method's comments
        // narrate the old bracket and name both APIs, so bare substring matches hit text.
        var sendInput = body.IndexOf("NativeMethods.SendInput", StringComparison.Ordinal);
        var switchCall = body.IndexOf("NativeMethods.SetForegroundWindow", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The ALT grant must complete (down AND up) before SetForegroundWindow runs. "
                + "A key event left down across the focus switch orphans its keyup on the "
                + "wrong window — see the 2026-08-25 stuck-ALT field report.")
            .That(sendInput).IsStrictlyGreaterThan(-1);
        Check.That(switchCall).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "SendInput must come before SetForegroundWindow in BringToFront — the grant "
                + "must be earned before it is spent.")
            .That(sendInput < switchCall).IsTrue();
        Check.WithCustomMessage(
                "keybd_event is the split-bracket API behind the stuck-ALT field report; the "
                + "atomic SendInput pair must not regress to it.")
            .That(body.Contains("keybd_event(")).IsFalse();
    }

    /// <summary>
    /// Source-scan pin: while the fullscreen switcher is up, the toolbar underneath must be
    /// dwell-deactivated — dwell has no z-order, so a top-row tile dwell would also fire the
    /// toolbar button under it (2026-08-18 field report). On close, reactivation must be
    /// conditional on the toolbar actually being visible (auto-hide may have hidden it).
    /// </summary>
    [Fact]
    public void Switcher_DeactivatesToolbarDwell_WhileCoveringIt()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Services", "Coordinators", "OverlayVmBindingCoordinator.cs"));
        var start = source.IndexOf("private void OnTaskSwitcherVMPropertyChanged", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = source[start..end];

        Check.WithCustomMessage(
                "Showing the switcher must ToolbarVM.SetActive(false) first — dwell has no "
                + "z-order and the toolbar underneath keeps its targets live without it.")
            .That(body.Contains("ToolbarVM.SetActive(false)")).IsTrue();
        Check.WithCustomMessage(
                "Hiding the switcher must NOT unconditionally reactivate the toolbar — "
                + "auto-hide may have hidden it while the switcher was up, and activating a "
                + "hidden toolbar creates gaze targets for invisible buttons. Guard the "
                + "SetActive(true) on the toolbar window's visibility.")
            .That(body.Contains("Toolbar.IsVisible") && body.Contains("ToolbarVM.SetActive(true)")).IsTrue();
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
