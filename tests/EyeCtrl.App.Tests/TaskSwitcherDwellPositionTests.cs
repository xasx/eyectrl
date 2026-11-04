namespace EyeCtrl.App.Tests;

using System.IO;
using System.Text.RegularExpressions;
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
        // Comments stripped (Switcher_DeactivatesToolbarDwell pattern): the guard's own
        // comment narrates "IsWindow is authoritative at the point of use", so a raw IndexOf
        // would still pass with the actual NativeMethods.IsWindow guard deleted.
        var body = SourceScanner.StripComments(
            ExtractMethod(source, "public void BringToFront(IntPtr hwnd)"));

        var isWindow = body.IndexOf("NativeMethods.IsWindow", StringComparison.Ordinal);
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
    /// it ever scrolled. Disabled truly disables wheel scrolling. The horizontal axis relies
    /// on the WPF platform default (Disabled — unlike the vertical default of Visible) and is
    /// pinned too: it constrains the WrapPanel to the viewport width, which is what makes the
    /// tiles wrap into rows at all, and it is what lets UpdateTilePositions treat the
    /// ScrollViewer's whole area as its viewport (no scrollbar chrome on either axis).
    /// </summary>
    [StaFact]
    public void TilesScroll_ScrollbarIsDisabled_GazeCannotOperateScrolling()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();

        var scroll = (ScrollViewer)window.FindName("TilesScroll")!;
        Check.That(scroll.VerticalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Disabled);
        Check.WithCustomMessage(
                "TilesScroll's horizontal visibility must stay Disabled (the WPF platform "
                + "default, pinned because UpdateTilePositions relies on it): Hidden or Auto "
                + "would let the content exceed the viewport width — one infinite row of "
                + "tiles, no wrapping — and the viewport-rect math would register targets "
                + "against a geometry that is not the drawn one.")
            .That(scroll.HorizontalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Disabled);
    }

    /// <summary>
    /// A5's reachability determinator, tightened after external review (I1): a tile carries a
    /// dwell target only for its VISIBLE face — the intersection with the viewport. Partial
    /// overlap registers the CLIPPED rect (never the hidden remainder below the viewport), a
    /// boundary touch or fully-clipped tile yields a non-positive dimension → no target.
    /// </summary>
    [Fact]
    public void VisibleTileRect_ReturnsOnlyTheVisibleFace_ClipsPartialRows()
    {
        // The viewport's physical rect, e.g. the ScrollViewer on a 1920x1264 window: the
        // bottom row holds the centered hint plus the 100 DIP close disc with 12 DIP margins,
        // so row 0's bottom sits ~124 DIP above the window bottom → viewport height ~1140.
        var viewport = new PhysicalRect(0, 0, 1920, 1140);

        // Partial overlap (face spans 1100..1204 against a 1140 bottom): the target is the
        // CLIPPED rect — bottom exactly at the viewport edge. The hidden 64 DIP below the
        // viewport must NOT be part of the target: it sits under the hint row and beside the
        // close disc, and a live rect there silently switches windows when gaze rests on it
        // (external review I1 — the exact hazard this clamp exists for).
        var clipped = TaskSwitcherWindow.VisibleTileRect(new PhysicalRect(100, 1100, 184, 104), viewport);
        Check.That(clipped.X).IsEqualTo(100);
        Check.That(clipped.Y).IsEqualTo(1100);
        Check.That(clipped.Width).IsEqualTo(184);
        Check.That(clipped.Height).IsEqualTo(40);

        // Horizontally clipped (face spans 1800..1984 against a 1920 right edge): same clamp.
        var sideClipped = TaskSwitcherWindow.VisibleTileRect(new PhysicalRect(1800, 100, 184, 104), viewport);
        Check.That(sideClipped.X).IsEqualTo(1800);
        Check.That(sideClipped.Width).IsEqualTo(120);
        Check.That(sideClipped.Height).IsEqualTo(104);

        // Fully below the viewport: non-positive height → no target.
        Check.That(TaskSwitcherWindow.VisibleTileRect(
                new PhysicalRect(100, 1300, 184, 104), viewport).Height).IsStrictlyLessThan(0);

        // Fully above the viewport: non-positive height → no target.
        Check.That(TaskSwitcherWindow.VisibleTileRect(
                new PhysicalRect(100, -300, 184, 104), viewport).Height).IsStrictlyLessThan(0);

        // Boundary touch (top edge exactly on the viewport bottom): zero height → no target.
        Check.That(TaskSwitcherWindow.VisibleTileRect(
                new PhysicalRect(100, 1140, 184, 104), viewport).Height).IsEqualTo(0);
    }

    /// <summary>
    /// A5 companion pin, hardened after external review (m1): comments stripped, and the
    /// guarded shape itself asserted — a comment naming the helper, or deleting only the
    /// <c>continue</c> so the check becomes dead code, must both fail this pin. UpdateTilePositions
    /// must pass the VISIBLE rect — the viewport intersection — to UpdateTilePosition. The
    /// PointToScreen path itself cannot run headless (PointToScreen needs an HWND — which is
    /// why this pin is a source scan), but the clamp's presence can be pinned the same way.
    /// </summary>
    [Fact]
    public void UpdateTilePositions_RegistersOnlyTheVisibleFace_SkipsTheRest()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var body = SourceScanner.StripComments(ExtractMethod(source, "private void UpdateTilePositions()"));

        Check.WithCustomMessage(
                "UpdateTilePositions must skip tiles whose visible area is empty, with the "
                + "guard in one piece (condition AND continue in one statement): deleting "
                + "only the continue leaves the check as dead code and registers clipped "
                + "tiles again — a live target where nothing is drawn.")
            .That(Regex.IsMatch(body,
                @"if\s*\(\s*visibleRect\.Width\s*<=\s*0\s*\|\|\s*visibleRect\.Height\s*<=\s*0\s*\)\s*continue;"))
            .IsTrue();
        Check.WithCustomMessage(
                "The dwell target must be the VISIBLE rect (the viewport intersection), not "
                + "the full face: a partially clipped tile's hidden part sits under the hint "
                + "row beside the close disc, and a live target there silently switches "
                + "windows when gaze rests on it (external review I1).")
            .That(body.Contains("UpdateTilePosition(tile.Hwnd, visibleRect)")).IsTrue();
    }
}
