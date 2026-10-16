namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;

/// <summary>
/// Review 01 F2: the lag button's keep-clear list must include the visible edge-button bars — and
/// in particular the keyboard's Close/Flip pair, which tracks the keyboard's edge at 75 % of the
/// work area's height and reaches into the button's resting place (measured: Flip's 60 DIP window
/// extent overlaps the button's hit area across its full width). The bounds come from the same data
/// the bar's window lays itself out with (<c>EdgeButtonBarWindow.SyncButtons</c>): the view model's
/// seed centres and each button's radius plus the window margin.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class LagMarkChromeTests
{
    /// <summary>The user's screen at 2.0 less the taskbar, in DIP (the fit harness's reference).</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1152);

    private static void WithDpi(double scale, Action action)
    {
        var (x, y) = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(scale, scale);
            action();
        }
        finally
        {
            DpiHelper.Update(x, y);
        }
    }

    /// <summary>A no-op toolbar that is never on screen: the offers' collector reads it.</summary>
    private sealed class NoToolbar : IToolbarOverlay
    {
        public bool IsVisible => false;
        public bool TargetsInitialized => false;
        public bool IsSliding => false;
        public bool IsOnScreen => false;
        public double ExtentDip => 0;
        public void Show() { }
        public void Hide() { }
        public void Initialize(double buttonSize, int dwellTimeMs) { }
        public void SetMode(bool isDocked, string edge) { }
        public void SetSelectionModeState(SelectionMode mode) { }
        public void SetPausedState(bool isPaused) { }
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() { }
        public void ShowAtRest() { }
        public void ShowThenSlideIn() { }
        public void SlideOutThenHide(Action onHidden) { }
        public PhysicalRect GetPhysicalBounds() => default;
    }

    /// <summary>
    /// The bar's bounds at the reviewer's measured geometry, and the button then clearing it by the
    /// gap. Mutation: drop the edge bars from <see cref="LagMarkChrome"/> → the button rests under
    /// Flip and the clearance assertion goes RED.
    /// </summary>
    [StaFact]
    public void WithTheKeyboardEdgeBarUp_TheButtonClearsIt_ByTheGap()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var bar = new TestFakes.FakeEdgeButtonBar();
            var modBar = new TestFakes.FakeEdgeButtonBar();
            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            var context = TestFakes.MakeDragContext(new TestFakes.FakeInputSimulator()) with
            {
                Settings = settings,
                Messenger = messenger,
                KeyboardVM = new KeyboardViewModel(settings, messenger),
                ActionMenuVM = new ActionMenuViewModel(settings, messenger),
                OverlayManager = TestFakes.MakeOverlayManager(
                    edgeButtonBar: bar, actionMenuModBar: modBar, toolbar: new NoToolbar()),
            };

            // The keyboard is up at the bottom: its Close/Flip pair sits at the keyboard edge bar
            // anchor (75 % of the work area's height), from the same definitions production shows.
            var anchor = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: false,
                logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920,
                logicalWorkAreaTop: 0,
                logicalWorkAreaHeight: 1152);
            bar.ShowButtons(KeyboardEdgeButtons.Definitions(450), anchor);

            var chrome = LagMarkChrome.CollectFromProduction(context);
            var barBounds = LagMarkChrome.EdgeBarBounds(bar.EdgeBarVm!);
            Check.WithCustomMessage("the visible edge bar is not in the keep-clear chrome")
                .That(chrome.Any(r => r == barBounds)).IsTrue();

            // The reviewer's measured bar: Close at 804 DIP, Flip at 924, radius 50 + margin 10.
            var barRect = barBounds!.Value;
            Check.That(barRect).IsEqualTo(new Rect(1770, 744, 120, 240));

            var window = new LagMarkWindow(dwellMs: 25, keepClear: () => chrome);
            try
            {
                window.Prepare(WorkArea);
                var placed = window.ShownRectDip;
                Check.WithCustomMessage($"the button {placed} sits over the keyboard's edge bar {barRect}")
                    .That(window.Top + window.Height).IsEqualTo(barRect.Top - LagMarkPlacement.KeepClearGapDip);
                var inflated = barRect;
                inflated.Inflate(LagMarkPlacement.KeepClearGapDip, LagMarkPlacement.KeepClearGapDip);
                // The strict overlap: a gap-clear placement touches the inflated edge, and
                // Rect.IntersectsWith counts touching edges.
                Check.WithCustomMessage($"the button {placed} overlaps the keyboard edge bar {barRect}")
                    .That(placed.Right > inflated.Left && placed.Left < inflated.Right
                          && placed.Bottom > inflated.Top && placed.Top < inflated.Bottom).IsFalse();
                Check.That(WorkArea.Contains(placed)).IsTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AHiddenEdgeBar_ContributesNoChrome()
    {
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        Check.That(LagMarkChrome.EdgeBarBounds(vm)).IsNull();
    }

    /// <summary>
    /// A HIDDEN bar contributes nothing even while its button set is still seeded (the frame between
    /// the visibility flip and the teardown): the visibility gate decides, not the button set.
    /// Mutation: dropping <c>if (!vm.IsVisible) return null;</c> is RED.
    /// </summary>
    [Fact]
    public void AHiddenBar_WithSeededButtons_StillContributesNothing()
    {
        WithDpi(2.0, () =>
        {
            var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
            vm.ShowButtons(
                KeyboardEdgeButtons.Definitions(450),
                ScreenGeometry.KeyboardEdgeBarAnchor(
                    keyboardAtTop: false,
                    logicalRadius: UIConstants.ButtonRadius,
                    logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                    logicalScreenWidth: 1920,
                    logicalWorkAreaTop: 0,
                    logicalWorkAreaHeight: 1152));
            Check.WithCustomMessage("the seeded bar must compute bounds while visible")
                .That(LagMarkChrome.EdgeBarBounds(vm)).IsNotNull();

            vm.IsVisible = false;   // hidden, but the seeded buttons are still in place
            Check.That(LagMarkChrome.EdgeBarBounds(vm)).IsNull();
        });
    }

    /// <summary>
    /// Review 02, R2: <see cref="LagMarkChrome.EdgeBarBounds"/> re-derives the edge bar window's own
    /// bounding-box arithmetic (<c>SyncButtons</c>), so it is pinned against the REAL window's
    /// laid-out Left/Top/Width/Height — not against a literal. The window is built and laid out by
    /// its production path (ShowButtons + SyncButtons) and never shown. Mutation: the window's
    /// <c>ButtonMargin</c> drifts (+40) → the two disagree → RED.
    /// </summary>
    [StaFact]
    public void EdgeBarBounds_MatchesTheRealEdgeButtonBarWindowsLayout()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(2.0, () =>
        {
            var window = new EdgeButtonBarWindow();
            try
            {
                var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
                window.DataContext = vm;
                var anchor = ScreenGeometry.KeyboardEdgeBarAnchor(
                    keyboardAtTop: false,
                    logicalRadius: UIConstants.ButtonRadius,
                    logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                    logicalScreenWidth: 1920,
                    logicalWorkAreaTop: 0,
                    logicalWorkAreaHeight: 1152);
                window.ShowButtons(KeyboardEdgeButtons.Definitions(450), anchor);
                window.SyncButtons();

                Check.WithCustomMessage("the window laid nothing out, so the pin would be vacuous")
                    .That(window.Width).IsStrictlyGreaterThan(0);
                var computed = LagMarkChrome.EdgeBarBounds(vm);
                Check.That(computed).IsNotNull();
                Check.WithCustomMessage($"the computed bounds {computed} differ from the window's own layout")
                    .That(computed!.Value)
                    .IsEqualTo(new Rect(window.Left, window.Top, window.Width, window.Height));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Bars that are down, or up with no placed buttons, contribute no chrome.</summary>
    [Fact]
    public void TheEdgeBarEntrances_SkipBarsWithNothingPlaced()
    {
        var context = TestFakes.MakeDragContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(
                edgeButtonBar: new TestFakes.FakeEdgeButtonBar(),
                actionMenuModBar: new TestFakes.FakeEdgeButtonBar(),
                toolbar: new NoToolbar()),
        };

        Check.That(LagMarkChrome.VisibleEdgeBarsInDip(context)).IsEmpty();
    }
}
