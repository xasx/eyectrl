namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (App)")]
public class KeyboardEdgeButtonsTests
{
    public KeyboardEdgeButtonsTests()
    {
        DpiHelper.Initialize(1.0, 1.0);
    }

    [Fact]
    public void Anchor_TracksTheKeyboardEdge_InsteadOfScreenCenter()
    {
        // Ledger #15 mechanism B: keyboard Top → anchor at 25% of the WORK-AREA height,
        // Bottom → 75%, offset from the work-area top. Pinned at 2.0 scale (the
        // ScreenGeometryTests pattern), 1920x1080 logical work area with top 0.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var top = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: true, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalWorkAreaTop: 0, logicalWorkAreaHeight: 1080);
            var bottom = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: false, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalWorkAreaTop: 0, logicalWorkAreaHeight: 1080);

            // Y = (1080 * 2) * fraction; X = 3840 - (30 + 50 + 10) * 2 = 3660.
            Check.That(top.Y).IsCloseTo(540.0, 1e-9);
            Check.That(bottom.Y).IsCloseTo(1620.0, 1e-9);
            Check.That(top.X).IsCloseTo(3660.0, 1e-9);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void Anchor_IsMeasuredAgainstTheWorkArea_NotTheScreen()
    {
        // The keyboard positions itself via SystemParameters.WorkArea (KeyboardWindow's
        // PlaceAtEdge), so the bar tracking it must share that rectangle: with a 24-DIP
        // bottom taskbar on a 1080 screen the bottom anchor sits at 0.75 * 1056 = 792
        // (physical 1584 at 2x), NOT 0.75 * 1080 — and a nonzero work-area top shifts the
        // whole band down by exactly that top (I3, external implementation review).
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var bottom = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: false, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalWorkAreaTop: 0, logicalWorkAreaHeight: 1056);

            Check.That(bottom.Y).IsCloseTo(1584.0, 1e-9);

            var withTop = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: true, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalWorkAreaTop: 24, logicalWorkAreaHeight: 1056);

            // Y = (24 + 1056 * 0.25) * 2 = (24 + 264) * 2 = 576.
            Check.That(withTop.Y).IsCloseTo(576.0, 1e-9);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    [Fact]
    public void Definitions_SlotOrderIsFixed_CloseAboveFlip()
    {
        // Owner pass 2026-09-16: "the buttons shall not flip. Close always above flip" —
        // the ANCHOR tracks the keyboard (the two anchor facts above), but the pair's
        // internal order is constant at both edges, so a learned gaze path never swaps.
        var defs = KeyboardEdgeButtons.Definitions(600);
        var flip = defs.Single(d => d.Id == KeyboardEdgeButtons.FlipId);
        var close = defs.Single(d => d.Id == KeyboardEdgeButtons.CloseId);

        Check.That(close.VerticalOffset).IsStrictlyLessThan(0);
        Check.That(flip.VerticalOffset).IsStrictlyGreaterThan(0);
        Check.That(flip.DwellMs).IsEqualTo(600);
        Check.That(close.DwellMs).IsEqualTo(600);
        Check.That(flip.Icon).IsEqualTo(ActionIcon.KeyboardEdge);
        Check.That(close.Icon).IsEqualTo(ActionIcon.Close);
        // Icon-only since 2026-10-06: no label is drawn, the name survives as the UIA name.
        Check.That(flip.Label).IsNull();
        Check.That(close.Label).IsNull();
        Check.That(flip.AccessibleName).IsEqualTo("Flip");
        Check.That(close.AccessibleName).IsEqualTo("Close");
    }

    [Fact]
    public void CloseActivation_SendsHideMessage()
    {
        var messenger = new WeakReferenceMessenger();
        KeyboardHideRequestedMessage? received = null;
        messenger.Register<object, KeyboardHideRequestedMessage>(this, (_, m) => received = m);

        var close = KeyboardEdgeButtons.Definitions(600)
            .Single(d => d.Id == KeyboardEdgeButtons.CloseId);
        close.Activate(messenger);

        Check.That(received).IsNotNull();
    }

    [Fact]
    public void FlipActivation_SendsEdgeToggledMessage()
    {
        var messenger = new WeakReferenceMessenger();
        KeyboardEdgeToggledMessage? received = null;
        messenger.Register<object, KeyboardEdgeToggledMessage>(this, (_, m) => received = m);

        var flip = KeyboardEdgeButtons.Definitions(600)
            .Single(d => d.Id == KeyboardEdgeButtons.FlipId);
        flip.Activate(messenger);

        Check.That(received).IsNotNull();
    }

    [Fact]
    public void GuardTests_KeyboardPairClaimsFreeBar_ButNeverClobbersOrClearsOthers()
    {
        // The shared-bar hand-off (spec B4 #10 / §6): show only when the bar is free or
        // already ours; hide only while ours. Real EdgeButtonBarViewModel, no WPF windows.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var barVm = new EdgeButtonBarViewModel(settings, messenger);

        // Free bar (no definitions): claimable.
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsFalse();

        // Our pair: re-claimable (flip rebuild) and clearable (keyboard hide).
        barVm.ShowButtons(
            KeyboardEdgeButtons.Definitions(600),
            new PhysicalPoint(1800, 800));
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsTrue();

        // Another context's button (scroll stop shape): never clobbered, never cleared.
        barVm.ShowButtons(
            new[] { new EdgeButtonDefinition(
                "scroll_stop", Symbol: string.Empty, 600,
                _ => { }, VerticalOffset: 0) },
            new PhysicalPoint(1800, 800));
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsFalse();

        // Hidden bar (Hide clears everything): claimable again.
        barVm.Hide();
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(barVm)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(barVm)).IsFalse();

        // Null-tolerant (window not constructed).
        Check.That(KeyboardCoordinator.ShouldShowKeyboardEdgeBar(null)).IsTrue();
        Check.That(KeyboardCoordinator.ShouldHideKeyboardEdgeBar(null)).IsFalse();
    }

    [Fact]
    public void RetryTests_KeyboardPairReclaimsOnlyWhenWantedVisibleAndBarFree()
    {
        // The B4 #10 reverse-overlap retry: a keyboard whose show was skipped while scroll/
        // drag owned the bar re-claims it when that context's unguarded Hide empties the
        // bar — but only while the pair is still wanted, the keyboard is still up, and the
        // bar is FREE ("already ours" is the flip path, not a retry).
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var barVm = new EdgeButtonBarViewModel(settings, messenger);

        Check.That(KeyboardCoordinator.ShouldRetryKeyboardEdgeBar(true, true, barVm)).IsTrue();

        Check.That(KeyboardCoordinator.ShouldRetryKeyboardEdgeBar(false, true, barVm)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldRetryKeyboardEdgeBar(true, false, barVm)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldRetryKeyboardEdgeBar(true, true, null)).IsFalse();

        barVm.ShowButtons(
            new[] { new EdgeButtonDefinition(
                "scroll_stop", Symbol: string.Empty, 600,
                _ => { }, VerticalOffset: 0) },
            new PhysicalPoint(1800, 800));
        Check.That(KeyboardCoordinator.ShouldRetryKeyboardEdgeBar(true, true, barVm)).IsFalse();
    }
}
