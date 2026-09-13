namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
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
        // Ledger #15 mechanism B: keyboard Top → anchor at 25% height; Bottom → 75%.
        // Pinned at 2.0 scale (the ScreenGeometryTests pattern), 1920x1080 logical.
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var top = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: true, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalScreenHeight: 1080);
            var bottom = ScreenGeometry.KeyboardEdgeBarAnchor(
                keyboardAtTop: false, logicalRadius: UIConstants.ButtonRadius,
                logicalWindowMargin: ScreenGeometry.EdgeButtonWindowMarginLogical,
                logicalScreenWidth: 1920, logicalScreenHeight: 1080);

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
    public void Definitions_FlipTakesTheKeyboardFacingSlot_InBothDirections()
    {
        foreach (var (keyboardAtTop, flipSign) in new[] { (true, -1.0), (false, 1.0) })
        {
            var defs = KeyboardEdgeButtons.Definitions(600, keyboardAtTop);
            var flip = defs.Single(d => d.Id == KeyboardEdgeButtons.FlipId);
            var close = defs.Single(d => d.Id == KeyboardEdgeButtons.CloseId);

            Check.That(Math.Sign(flip.VerticalOffset)).IsEqualTo(flipSign);
            Check.That(Math.Sign(close.VerticalOffset)).IsEqualTo(-flipSign);
            Check.That(flip.DwellMs).IsEqualTo(600);
            Check.That(close.DwellMs).IsEqualTo(600);
            Check.That(flip.Icon).IsEqualTo(ActionIcon.KeyboardEdge);
            Check.That(close.Icon).IsEqualTo(ActionIcon.Close);
            Check.That(flip.Label).IsEqualTo("Flip");
            Check.That(close.Label).IsEqualTo("Close");
        }
    }

    [Fact]
    public void CloseActivation_SendsHideMessage()
    {
        var messenger = new WeakReferenceMessenger();
        KeyboardHideRequestedMessage? received = null;
        messenger.Register<object?, KeyboardHideRequestedMessage>(this, (_, m) => received = m);

        var close = KeyboardEdgeButtons.Definitions(600, keyboardAtTop: false)
            .Single(d => d.Id == KeyboardEdgeButtons.CloseId);
        close.Activate(messenger);

        Check.That(received).IsNotNull();
    }

    [Fact]
    public void FlipActivation_SendsEdgeToggledMessage()
    {
        var messenger = new WeakReferenceMessenger();
        KeyboardEdgeToggledMessage? received = null;
        messenger.Register<object?, KeyboardEdgeToggledMessage>(this, (_, m) => received = m);

        var flip = KeyboardEdgeButtons.Definitions(600, keyboardAtTop: true)
            .Single(d => d.Id == KeyboardEdgeButtons.FlipId);
        flip.Activate(messenger);

        Check.That(received).IsNotNull();
    }
}
