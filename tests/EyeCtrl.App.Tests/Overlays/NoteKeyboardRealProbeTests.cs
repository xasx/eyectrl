namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// K1: the note's Edit card must be placed against the keyboard when the keyboard is ALREADY up at
/// creation, and it must follow the keyboard's FINAL rect after a flip and a re-show.
/// <para>
/// The headless tests used a fake probe: they set the rects and call <c>RaiseChanged()</c> after
/// <c>CreateNoteAt</c>, an event production never sends when the keyboard was already visible
/// (the editor's hand-over keeps it up, so <c>BeginEditing</c> requests no show). These tests use
/// the production pair — the real <see cref="KeyboardProbe"/> over a real <see cref="KeyboardWindow"/>
/// with the shipped qwertz layout — and never a fake.
/// </para>
/// <para>
/// The keyboard window is SHOWN only through <see cref="InvisibleTestWindow.Prepare"/> (opacity 0,
/// non-activating): layout, the window's on-screen rect and LocationChanged/SizeChanged run exactly
/// as in production, and nothing is painted.
/// </para>
/// <para>
/// SCREEN-INDEPENDENT: the manager and the keyboard window share the SAME work area (the real
/// <see cref="SystemParameters.WorkArea"/> in DIP, or the rig's seam), the flip is asserted against
/// that work area's edges, and the essence is an EQUALITY with the placement derived through
/// <see cref="NoteLayout.Place"/> from the probe's final rect — so the tests hold on a small CI
/// runner where the card has no clear spot at all. The 24 DIP clearance is asserted only where the
/// real work area genuinely leaves the room for it.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteKeyboardRealProbeTests
{
    private const int TextSizeDip = 36;

    [StaFact]
    public void CreateWhileTheKeyboardIsUp_IsPlacedAgainstTheLiveKeyboard()
    {
        // The scenario: a note is created while the keyboard is already visible (the previous
        // editor's commit-first hand-over keeps it up), so no probe raise follows. The first
        // placement must already be the one derived against the keyboard on screen.
        using var rig = new Rig();
        rig.CreateNote();
        AssertPlacedAgainst(rig, rig.ProbeRects, "created while the keyboard was up");
    }

    [StaFact]
    public void FlippingTheKeyboardMidEdit_FollowsTheFinalRect()
    {
        using var rig = new Rig();
        AssertFlipFollowsTheFinalRect(rig);
    }

    [StaFact]
    public void ReShowingTheKeyboardAtTheSamePlace_FollowsTheFinalRect()
    {
        // A re-show at the same size and place fires no SizeChanged/Loaded/LocationChanged; only
        // the VM's IsVisible flip reaches the probe (KeyboardProbe.cs:78-85). The card must be
        // placed against the re-shown rect again.
        using var rig = new Rig();
        rig.CreateNote();
        var original = rig.ProbeRects;
        AssertPlacedAgainst(rig, original, "before the hide");

        rig.HideKeyboard();
        Check.WithCustomMessage("with the keyboard hidden the card must be the no-avoid placement")
            .That(rig.Placement).IsEqualTo(rig.Derive(Array.Empty<PhysicalRect>()));

        rig.ShowKeyboard();
        var shown = rig.ProbeRects;
        Check.WithCustomMessage("the re-show must return the keyboard to the same rect")
            .That(shown).HasSize(1);
        AssertSameRect(original[0], shown[0], "the re-shown keyboard");
        AssertPlacedAgainst(rig, shown, "after the re-show");
    }

    [StaFact]
    public void OnASmallWorkArea_TheSameEssenceHolds()
    {
        // The CI runner's screen is small (~768 DIP tall). The rig's work-area seam proves the
        // same assertions hold on a 1366x768 stage, where the card has no clear spot anywhere.
        using var rig = new Rig(() => new Rect(0, 0, 1366, 768));
        AssertFlipFollowsTheFinalRect(rig);
    }

    [StaFact]
    public void AKeyboardPageSwitchMidEdit_RePlacesAgainstTheNewKeyboardHeight()
    {
        // B item 2: the Fn page is 126.5 DIP taller than the letters page at the owner's key size
        // 1.75 (MEASURED from the real window: 736 -> 862.5). The page switch is a sub-layout
        // swap: KeyboardWindow rebuilds the grid, SizeToContent re-lays the window out, its own
        // SizeChanged re-anchors it and the KeyboardProbe queues its raise at Background
        // (KeyboardProbe.cs:87-93). The editing note must be re-placed against the NEW height -
        // both on the way to Fn and back.
        using var rig = new Rig(keySizeMultiplier: 1.75);
        rig.CreateNote();
        var letters = rig.KeyboardRectDip;
        var lettersPlacement = rig.Placement;

        Check.WithCustomMessage($"the owner's letters page is 736 DIP tall at 1.75 (measured {letters.Height:F1})")
            .That(letters.Height).IsCloseTo(736, 1.0);

        AssertPageSwitchFollows(rig, "function", "letters -> Fn");
        var fn = rig.KeyboardRectDip;
        Check.WithCustomMessage($"the Fn page is 862.5 DIP tall at 1.75 (measured {fn.Height:F1})")
            .That(fn.Height).IsCloseTo(862.5, 1.0);
        Check.WithCustomMessage("the taller keyboard must have MOVED the editing card")
            .That(rig.Placement.Window).IsNotEqualTo(lettersPlacement.Window);

        AssertPageSwitchFollows(rig, "letters", "Fn -> letters");
        Check.WithCustomMessage("the letters page must return the keyboard to its height")
            .That(rig.KeyboardRectDip.Height).IsCloseTo(letters.Height, 1.0);
        Check.WithCustomMessage("and the card must return to its letters-page placement")
            .That(rig.Placement.Window).IsEqualTo(lettersPlacement.Window);
    }

    [StaFact]
    public void OnASmallWorkArea_AKeyboardPageSwitchStillFollows()
    {
        // The same page-switch essence on the CI runner's ~768 DIP stage: the card may have no
        // clear spot at either height (the placement is clamped to the inset at both), so the
        // assertion is the EQUALITY with the derivation against the new rect - not movement.
        using var rig = new Rig(() => new Rect(0, 0, 1366, 768));
        rig.CreateNote();
        var letters = rig.KeyboardRectDip;

        AssertPageSwitchFollows(rig, "function", "small: letters -> Fn");
        Check.WithCustomMessage("the Fn page must be taller, small stage included")
            .That(rig.KeyboardRectDip.Height).IsStrictlyGreaterThan(letters.Height);
        AssertPageSwitchFollows(rig, "letters", "small: Fn -> letters");
    }

    /// <summary>The page-switch essence: after the sub-layout swap the published placement IS the
    /// one derived against the keyboard's NEW rect, through the manager's own Edit inputs. The
    /// keyboard's height must have changed (that is what makes this a re-place), and where the
    /// derivation genuinely moved, the clearance rule is checked too.</summary>
    private static void AssertPageSwitchFollows(Rig rig, string page, string when)
    {
        var oldProbe = rig.ProbeRects;
        var oldExpected = rig.Derive(oldProbe);
        rig.SwitchPage(page);
        var probe = rig.ProbeRects;

        Check.WithCustomMessage($"{when}: the real keyboard must be visible AND sourced")
            .That(probe).Not.IsEmpty();
        var expected = rig.Derive(probe);
        Check.WithCustomMessage(
                $"{when}: the card {rig.Placement.Window} must be the placement derived against the "
                + $"NEW keyboard {rig.KeyboardRectDip} (expected {expected.Window}, before {oldExpected.Window})")
            .That(rig.Placement).IsEqualTo(expected);
        Check.WithCustomMessage($"{when}: the page switch must have resized the keyboard")
            .That(Math.Abs(probe[0].Height - oldProbe[0].Height) > 0.5).IsTrue();

        if (expected.Window != oldExpected.Window)
        {
            Check.WithCustomMessage($"{when}: the derivation changed, so the card must have moved")
                .That(rig.Placement.Window).IsNotEqualTo(oldExpected.Window);
            if (HasClearBand(rig.WorkAreaDip, rig.KeyboardRectDip, rig.Placement.Window.Height))
                AssertClearOfTheKeyboard(rig.Placement, rig.KeyboardRectDip, when);
        }
    }

    /// <summary>The creation essence: the published placement IS the one derived against the
    /// probe's current (live) rects, through the manager's own Edit inputs. The 24 DIP clearance
    /// is added only when the work area really leaves that much room beside the keyboard.</summary>
    private static void AssertPlacedAgainst(Rig rig, IReadOnlyList<PhysicalRect> avoid, string when)
    {
        var keyboard = rig.KeyboardRectDip;
        Check.WithCustomMessage("the keyboard probe reported no rect; the assertion would pass vacuously")
            .That(keyboard.Width).IsStrictlyGreaterThan(0);

        var expected = rig.Derive(avoid);
        Check.WithCustomMessage(
                $"{when}: the card {rig.Placement.Window} must be the placement derived against the "
                + $"live keyboard {keyboard} (expected {expected.Window})")
            .That(rig.Placement).IsEqualTo(expected);

        if (HasClearBand(rig.WorkAreaDip, keyboard, rig.Placement.Window.Height))
            AssertClearOfTheKeyboard(rig.Placement, keyboard, when);
    }

    /// <summary>The flip essence: the keyboard lands at the other edge of the SAME work area, and
    /// the card's placement is the one derived from the NEW rect — not from the one it left.</summary>
    private static void AssertFlipFollowsTheFinalRect(Rig rig)
    {
        // The production order of the edge-bar Flip: the dwell sends KeyboardEdgeToggledMessage;
        // the probe queues its raise at Background, the coordinator queues SetScreenEdge at
        // Normal. The re-place must read the keyboard's FINAL rect.
        rig.CreateNote();
        var top = rig.KeyboardRectDip;
        var oldRects = rig.ProbeRects;
        AssertPlacedAgainst(rig, oldRects, "before the flip");
        if (top.Height <= rig.WorkAreaDip.Height)
            Check.WithCustomMessage($"the keyboard must start docked to the work area's top ({top})")
                .That(top.Top).IsCloseTo(rig.WorkAreaDip.Top, 2.0);

        rig.FlipTo("Bottom");
        var bottom = rig.KeyboardRectDip;
        var newRects = rig.ProbeRects;
        var after = rig.Placement;

        Check.WithCustomMessage($"the flip must move the keyboard down (top {top.Top} -> {bottom.Top})")
            .That(bottom.Top).IsStrictlyGreaterThan(top.Top);
        if (bottom.Height <= rig.WorkAreaDip.Height)
            Check.WithCustomMessage(
                    $"the flipped keyboard {bottom} must dock to the work area's bottom edge {rig.WorkAreaDip}")
                .That(bottom.Bottom).IsCloseTo(rig.WorkAreaDip.Bottom, 2.0);

        var expected = rig.Derive(newRects);
        Check.WithCustomMessage(
                $"after the flip the card {after.Window} must be the placement derived against the "
                + $"FINAL keyboard {bottom} (expected {expected.Window})")
            .That(after).IsEqualTo(expected);
        Check.WithCustomMessage(
                "the card must NOT be the placement derived against the keyboard's OLD rect")
            .That(after).IsNotEqualTo(rig.Derive(oldRects));

        if (HasClearBand(rig.WorkAreaDip, bottom, after.Window.Height))
            AssertClearOfTheKeyboard(after, bottom, "after the flip");
    }

    /// <summary>Whether the work area really leaves room for the drawn Edit card plus the 24 DIP
    /// clearance on the side of the keyboard: the smaller CI work area does not.</summary>
    private static bool HasClearBand(Rect workArea, Rect keyboard, double drawnHeight)
    {
        double margin = NoteLayout.Constants.EdgeMarginDip;
        double insetTop = workArea.Top + margin;
        double insetBottom = workArea.Bottom - margin;
        bool atTop = Math.Abs(keyboard.Top - workArea.Top) <= Math.Abs(keyboard.Bottom - workArea.Bottom);
        double band = atTop ? insetBottom - keyboard.Bottom : keyboard.Top - insetTop;
        return band >= drawnHeight + NoteLayout.Constants.AvoidGapDip;
    }

    /// <summary>The sweep's three rules, on the real probe's rect: the card, every registered
    /// Edit control cell (K2a: the row's three cells under the text) and the number tag keep the
    /// 24 DIP clearance.</summary>
    private static void AssertClearOfTheKeyboard(NotePlacement placement, Rect keyboard, string when)
    {
        Check.WithCustomMessage($"{when}: the card {placement.Card} must clear the keyboard {keyboard}")
            .That(GapFrom(placement.Card, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);

        foreach (var slot in NoteLayout.SlotsFor(NoteState.Edit))
        {
            var (cx, cy) = NoteLayout.ButtonCenter(placement, slot, NoteState.Edit);
            var cell = new Rect(
                placement.Window.X + cx - (LayoutConstants.NoteCellDip / 2),
                placement.Window.Y + cy - (LayoutConstants.NoteCellDip / 2),
                LayoutConstants.NoteCellDip, LayoutConstants.NoteCellDip);
            Check.WithCustomMessage($"{when}: cell {slot} {cell} must clear the keyboard {keyboard}")
                .That(GapFrom(cell, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
        }

        var tag = NoteLayout.TagRect(placement, TextSizeDip);
        var tagRect = new Rect(placement.Window.X + tag.X, placement.Window.Y + tag.Y, tag.Width, tag.Height);
        Check.WithCustomMessage($"{when}: the number tag {tagRect} must clear the keyboard {keyboard}")
            .That(GapFrom(tagRect, keyboard)).IsGreaterOrEqualThan(NoteLayout.Constants.AvoidGapDip);
    }

    private static void AssertSameRect(PhysicalRect expected, PhysicalRect actual, string what)
    {
        Check.WithCustomMessage($"{what}: expected {expected}, got {actual}")
            .That(Math.Abs(actual.X - expected.X) <= 1.0
                && Math.Abs(actual.Y - expected.Y) <= 1.0
                && Math.Abs(actual.Width - expected.Width) <= 1.0
                && Math.Abs(actual.Height - expected.Height) <= 1.0)
            .IsTrue();
    }

    /// <summary>The production pair plus the note manager, on one work area: a real keyboard window
    /// shown invisibly and positioned by the same work-area rect the note manager derives against
    /// (the real one by default, or the test's seam).</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly double _savedScaleX;
        private readonly double _savedScaleY;
        private readonly KeyboardWindow _window;
        private readonly KeyboardViewModel _vm;
        private readonly IMessenger _messenger;
        private readonly KeyboardProbe _probe;

        public Rig(Func<Rect>? workArea = null, double keySizeMultiplier = 1.5)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();
            _dispatcher = Dispatcher.CurrentDispatcher;
            _savedScaleX = DpiHelper.ScaleX;
            _savedScaleY = DpiHelper.ScaleY;
            _messenger = new WeakReferenceMessenger();
            WorkArea = workArea ?? (() => SystemParameters.WorkArea);

            var layout = KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));
            var settings = new EyeCtrlSettings();
            settings.Keyboard.ScreenEdge = "Top";
            _vm = new KeyboardViewModel(settings, _messenger);

            _window = new KeyboardWindow();
            _window.SetViewModel(_vm);
            _window.SetScreenEdge("Top");
            _window.LoadLayout(layout, keySizeMultiplier, keyDwellMs: 450, predictionDwellMs: 800, cooldownMs: 0);
            InvisibleTestWindow.Prepare(_window);
            _window.Show();
            Pump();

            var dpi = VisualTreeHelper.GetDpi(_window);
            DpiHelper.Initialize(dpi.DpiScaleX, dpi.DpiScaleY);
            Scale = dpi.DpiScaleX;

            // Constructed BEFORE the show flip, as production constructs it once at startup.
            _probe = new KeyboardProbe(_vm, _dispatcher, _messenger, () => _window, () => null);

            Manager = new NoteManager(_messenger, hitTester: InactiveNoteHitTester.Instance,
                workAreaDip: () => WorkAreaDip, noteSettings: new NoteSettings { TextSizeDip = TextSizeDip });
            Manager.SetKeyboardProbe(_probe);

            ShowKeyboard();

            // The anchor lies under the top keyboard: the middle of the keyboard rect, clamped
            // into the work area. Chosen AFTER the show so it follows the host's geometry.
            var keyboard = KeyboardRectDip;
            AnchorDip = new Point(
                Math.Clamp(keyboard.Left + (keyboard.Width / 2), WorkAreaDip.Left + 10, WorkAreaDip.Right - 10),
                Math.Clamp(keyboard.Top + (Math.Min(keyboard.Height, WorkAreaDip.Height) / 2),
                    WorkAreaDip.Top + 10, WorkAreaDip.Bottom - 10));
        }

        private Func<Rect> WorkArea { get; }

        public Rect WorkAreaDip => WorkArea();

        public Point AnchorDip { get; }

        public double Scale { get; }

        public NoteManager Manager { get; }

        public Size CardSize => NoteLayout.EditCardSize(TextSizeDip, 0, WorkAreaDip.Height);

        public IReadOnlyList<PhysicalRect> ProbeRects => _probe.Rects();

        public NotePlacement Placement => Manager.PlacementForTest(Manager.ActiveNotes[^1].Id)!;

        public PhysicalPoint Phys(Point dip) => new(dip.X * Scale, dip.Y * Scale);

        public void CreateNote() => Manager.CreateNoteAt(Phys(AnchorDip));

        /// <summary>The keyboard's on-screen rect in DIP, through the SAME physical-to-DIP scale
        /// the note manager uses — the rect it derived against, not a hand-copied one.</summary>
        public Rect KeyboardRectDip
        {
            get
            {
                var rects = ProbeRects;
                Check.WithCustomMessage("the real keyboard must be visible AND sourced for the probe")
                    .That(rects).Not.IsEmpty();
                return ToDip(rects[0]);
            }
        }

        /// <summary>The manager's own Edit derivation, through the same inputs: the probe's rects
        /// (converted exactly as <c>NoteManager.PhysicalRectToDip</c> does), the work area, the
        /// displaced candidates and the tag growth.</summary>
        public NotePlacement Derive(IReadOnlyList<PhysicalRect> avoidPhysical)
        {
            var avoidDip = avoidPhysical.Select(ToDip).ToList();
            var placed = NoteLayout.Place(
                Phys(AnchorDip), NoteSide.Right, CardSize, WorkAreaDip, avoidDip, Scale,
                exclude: null, allowDisplaced: true,
                topGrowthDip: NoteLayout.TagProtrusionDip(TextSizeDip))!;
            return NoteLayout.WithTagClearance(placed, TextSizeDip);
        }

        /// <summary>The production show sequence (KeyboardCoordinator.SetKeyboardVisible): the VM
        /// flip, Show(), then the explicit show placement — against the rig's work area.</summary>
        public void ShowKeyboard()
        {
            _vm.IsVisible = true;
            _window.Show();
            _window.PlaceForShow(WorkAreaDip, _window.ActualWidth, _window.ActualHeight);
            Pump();
        }

        /// <summary>The production page switch (the Fn/ABC keys): the VM's sub-layout swap, which
        /// rebuilds the grid. The window is SizeToContent, re-anchors on its own SizeChanged, and
        /// the probe's SizeChanged subscription queues its raise at Background - Pump drains it.</summary>
        public void SwitchPage(string subLayoutId)
        {
            _vm.SwitchSubLayout(subLayoutId);
            Pump();
        }

        /// <summary>The production hide half: the VM flip and the window hide, as the coordinator does.</summary>
        public void HideKeyboard()
        {
            _vm.IsVisible = false;
            _window.Hide();
            Pump();
        }

        /// <summary>The production Flip half: the dwell's message, then the coordinator's
        /// SetScreenEdge on the Normal queue. The explicit Place against the rig's work area is the
        /// seam that keeps a fake stage consistent (a no-op when the rig uses the real one).</summary>
        public void FlipTo(string edge)
        {
            _messenger.Send(new KeyboardEdgeToggledMessage());
            _dispatcher.BeginInvoke(() =>
            {
                _window.SetScreenEdge(edge);
                _window.Place(WorkAreaDip, _window.ActualWidth, _window.ActualHeight, KeyboardMove.Flip);
            });
            Pump();
        }

        private static Rect ToDip(PhysicalRect r) => new(
            DpiHelper.PhysicalToLogicalX(r.X), DpiHelper.PhysicalToLogicalY(r.Y),
            DpiHelper.PhysicalToLogicalX(r.X + r.Width) - DpiHelper.PhysicalToLogicalX(r.X),
            DpiHelper.PhysicalToLogicalY(r.Y + r.Height) - DpiHelper.PhysicalToLogicalY(r.Y));

        /// <summary>Drains the probe's Background raise and the window's layout/render work.</summary>
        private void Pump() => _dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

        public void Dispose()
        {
            Manager.Dispose();
            _probe.Dispose();
            _vm.IsVisible = false;
            _window.Hide();
            _window.Close();
            DpiHelper.Initialize(_savedScaleX, _savedScaleY);
        }
    }

    /// <summary>The distance between a window and a rect: zero when they touch or overlap.</summary>
    private static double GapFrom(Rect window, Rect rect)
    {
        double dx = Math.Max(rect.Left - window.Right, 0) + Math.Max(window.Left - rect.Right, 0);
        double dy = Math.Max(rect.Top - window.Bottom, 0) + Math.Max(window.Top - rect.Bottom, 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
