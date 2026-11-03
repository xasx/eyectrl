namespace EyeCtrl.App.Tests;

using System.IO;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using NFluent;

/// <summary>
/// Source-scan pin: the task switcher must push dwell-target positions through
/// <c>UIElement.PointToScreen</c> — physical device pixels, DPI-virtualization-aware — the
/// way every other overlay does (Keyboard, Toolbar, QuickActions, Settings). It was the one
/// overlay computing centers manually (TransformToAncestor + DpiHelper.LogicalToPhysicalX),
/// and under a DPI-virtualized process the targets landed far from the tiles: tiles visible,
/// nothing dwellable (2026-08-17 field report).
/// </summary>
[Collection("WpfApplication")]
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
    /// Source-scan pin: <c>BringToFront</c> must keep what its two fallible calls return and
    /// log the classified outcome. Both failures of this path so far arrived as bare symptoms
    /// — "tasks are not dwellable" (2026-08-18) and "the alt key seems stuck" (2026-08-25) —
    /// and each cost a diagnosis round, because SendInput's accepted count and
    /// SetForegroundWindow's bool were both discarded. A gaze-only user cannot bisect this by
    /// hand; the log line is the only evidence the next report will carry.
    /// </summary>
    [Fact]
    public void BringToFront_Logs_WhyASwitchFailed()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));
        var body = ExtractMethod(source, "public void BringToFront(IntPtr hwnd)");

        Check.WithCustomMessage(
                "SendInput's return is the only signal that the ALT grant was refused (UIPI "
                + "silently drops injection into an elevated foreground window). Discarding it "
                + "makes GrantBlocked indistinguishable from SwitchRefused in the field.")
            .That(body.Contains("= NativeMethods.SendInput")).IsTrue();
        Check.WithCustomMessage(
                "The outcome must go through ForegroundSwitchDiagnosis.Classify — the pure "
                + "classifier is where the three field-distinguishable causes are defined and "
                + "unit-tested.")
            .That(body.Contains("ForegroundSwitchDiagnosis.Classify")).IsTrue();
        Check.WithCustomMessage(
                "A failed switch must be logged at Warning. Below Warning it is invisible in "
                + "the shipped build (Serilog's minimum level is Information, and a silent "
                + "failure here is exactly what cost the last two diagnosis rounds).")
            .That(body.Contains("_logger.LogWarning")).IsTrue();
    }

    /// <summary>
    /// Source-scan pin: while the fullscreen switcher is up, the toolbar underneath must be
    /// dwell-deactivated — dwell has no z-order, so a top-row tile dwell would also fire the
    /// toolbar button under it (2026-08-18 field report). On close, reactivation must be
    /// conditional on the toolbar actually being on screen — Toolbar.IsOnScreen, not IsVisible,
    /// which stays true through a slide-out (2026-09-06 slide transition).
    /// </summary>
    [Fact]
    public void Switcher_DeactivatesToolbarDwell_WhileCoveringIt()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Services", "Coordinators", "OverlayVmBindingCoordinator.cs"));
        var start = source.IndexOf("private void OnTaskSwitcherVMPropertyChanged", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = SourceScanner.StripComments(source[start..end]);

        Check.WithCustomMessage(
                "Showing the switcher must ToolbarVM.SetActive(false) first — dwell has no "
                + "z-order and the toolbar underneath keeps its targets live without it.")
            .That(body.Contains("ToolbarVM.SetActive(false)")).IsTrue();
        Check.WithCustomMessage(
                "Hiding the switcher must NOT unconditionally reactivate the toolbar — "
                + "auto-hide may have hidden it while the switcher was up, and activating a "
                + "hidden toolbar creates gaze targets for invisible buttons. Guard the "
                + "SetActive(true) on Toolbar.IsOnScreen.")
            .That(body.Contains("Toolbar.IsOnScreen") && body.Contains("ToolbarVM.SetActive(true)")).IsTrue();
        Check.WithCustomMessage(
                "Toolbar.IsVisible is true during a slide-out, while the band is leaving and the "
                + "window is about to hide — re-arming dwell there is the very hazard this pin "
                + "exists to prevent. Use Toolbar.IsOnScreen, which is false then and true during "
                + "a slide-in.")
            .That(body.Contains("Toolbar.IsVisible")).IsFalse();
    }

    /// <summary>
    /// Source-scan pin: <c>BringToFront</c> must not spend the ALT foreground grant on a dead
    /// hwnd. The tile snapshot is one-shot (AppWindow per ShowOverlay), so a window that
    /// closes mid-session leaves a tile whose dwell dispatches here on a dead hwnd; the ALT
    /// down+up pair injected anyway lands in whatever currently holds foreground — the exact
    /// key-event-misdelivery class the 2026-08-25 stuck-ALT report made load-bearing. The
    /// IsWindow check must therefore come BEFORE NativeMethods.SendInput, alongside the
    /// guards Minimize/MaximizeOrRestore/CloseWindow already carry.
    /// </summary>
    [Fact]
    public void BringToFront_SkipsTheAltGrant_OnADeadHwnd()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));
        var body = ExtractMethod(source, "public void BringToFront(IntPtr hwnd)");

        var isWindow = body.IndexOf("IsWindow", StringComparison.Ordinal);
        var sendInput = body.IndexOf("NativeMethods.SendInput", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "BringToFront must reject a dead hwnd BEFORE the ALT grant: a window that "
                + "closes mid-session leaves a tile whose dwell would otherwise fire a stray "
                + "ALT down+up pair into whatever currently holds foreground (the 2026-08-25 "
                + "stuck-ALT hazard), and the switch silently never happens.")
            .That(isWindow).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage(
                "The IsWindow check must precede NativeMethods.SendInput — the grant is only "
                + "worth spending on a target that can receive it.")
            .That(isWindow < sendInput).IsTrue();
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return source[start..end];
    }

    /// <summary>
    /// A5: the tiles ScrollViewer's Auto scrollbar was an affordance gaze cannot operate (no
    /// dwell target), and a one-shot Loaded position sync would desync targets from tiles if
    /// it ever scrolled. Disabled truly disables wheel scrolling; HorizontalScrollBarVisibility
    /// already defaults to Hidden, so all tiles stay top-aligned in row 0.
    /// </summary>
    [StaFact]
    public void TilesScroll_ScrollbarIsDisabled_GazeCannotOperateScrolling()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();

        var scroll = (ScrollViewer)window.FindName("TilesScroll")!;
        Check.That(scroll.VerticalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Disabled);
    }

    /// <summary>
    /// A5's reachability determinator: a tile registers a dwell target only if its on-screen
    /// face overlaps the ScrollViewer's viewport. Partial overlap registers (the visible part
    /// is real estate), a boundary touch does not (zero visible pixels — a live target there
    /// is the hazard this filter exists for), and fully-clipped tiles get nothing.
    /// </summary>
    [Fact]
    public void TileOverlapsViewport_RegistersPartialOverlap_SkipsClippedAndTouching()
    {
        // The viewport's physical rect, e.g. the ScrollViewer on a 1920x1264 window: row 0
        // bottom sits ~43 DIP above the window bottom (the hint/close row).
        var viewport = new PhysicalRect(0, 0, 1920, 1221);

        // Fully below the viewport: skipped.
        Check.That(TaskSwitcherWindow.TileOverlapsViewport(
                new PhysicalRect(100, 1300, 184, 104), viewport)).IsFalse();

        // Fully above the viewport: skipped.
        Check.That(TaskSwitcherWindow.TileOverlapsViewport(
                new PhysicalRect(100, -300, 184, 104), viewport)).IsFalse();

        // Partial overlap (spans 1200..1304 against a 1221 bottom): registered.
        Check.That(TaskSwitcherWindow.TileOverlapsViewport(
                new PhysicalRect(100, 1200, 184, 104), viewport)).IsTrue();

        // Boundary touch (top edge exactly on the viewport bottom): skipped — zero visible area.
        Check.That(TaskSwitcherWindow.TileOverlapsViewport(
                new PhysicalRect(100, 1221, 184, 104), viewport)).IsFalse();
    }

    /// <summary>
    /// A5 companion pin: UpdateTilePositions must keep the viewport-intersection guard. The
    /// PointToScreen path itself cannot run headless (PointToScreen needs an HWND — which is
    /// why the existing pin is a source scan), but the guard's presence can be pinned the
    /// same way.
    /// </summary>
    [Fact]
    public void UpdateTilePositions_SkipsTilesOutsideTheScrollViewerViewport()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var body = ExtractMethod(source, "private void UpdateTilePositions()");

        Check.WithCustomMessage(
                "UpdateTilePositions must skip tiles whose face does not intersect the "
                + "ScrollViewer's viewport: without the guard a fully clipped tile keeps a "
                + "LIVE dwell target at its true on-screen position — inside the gazeable "
                + "bottom band for a range of window heights — and gaze resting there "
                + "silently switches windows (and, with the close disc beside it, contests "
                + "it under nearest-hit arbitration).")
            .That(body.Contains("TileOverlapsViewport")).IsTrue();
    }
}
