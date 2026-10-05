namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The manual-selection right-edge buttons. Close is the point of this task: it is the user's
/// gaze-driven way out of the modal magnifier view, and until now it was a bare "✕" glyph with no
/// <c>Icon</c> or <c>Label</c> — unlike its action-menu twin, which has carried both since
/// <see cref="ActionMenuEdgeButtons"/> was introduced.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ManualSelectionEdgeButtonsTests
{
    [Fact]
    public void TheBarCarries_CloseAndRecapture()
    {
        var defs = ManualSelectionEdgeButtons.Definitions(dwellMs: 600);

        Check.That(defs.Select(d => d.Id)).ContainsExactly(
            "ManualSelection_close", "ManualSelection_recapture");
    }

    [Fact]
    public void Close_CarriesIconAndLabel()
    {
        // The escape hatch: it must be recognisable without training, the same reasoning
        // ActionMenuEdgeButtons gives for never hiding Close's label.
        var def = ManualSelectionEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.CloseId);

        Check.That(def.Icon).IsEqualTo(ActionIcon.Close);
        Check.That(def.Label).IsEqualTo("Close");
    }

    [Fact]
    public void Recapture_CarriesIconAndLabel()
    {
        // The drawn replacement for the old ↻ glyph. A labelled button must carry an icon, or the
        // label is drawn over the symbol — the defect this ticket fixes.
        var def = ManualSelectionEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.RecaptureId);

        Check.That(def.Icon).IsEqualTo(ActionIcon.Recapture);
        Check.That(def.Label).IsEqualTo("Retake");
        Check.That(def.Symbol).IsEmpty();
    }

    [Fact]
    public void TheyUseTheActionMenuSlots()
    {
        // Same two slots the action-menu Close/Help pair occupies (ActionMenuEdgeButtonsTests
        // pins this from the other side), so the bar's layout is one convention, not one per
        // context.
        var defs = ManualSelectionEdgeButtons.Definitions(dwellMs: 600);

        Check.That(defs[0].VerticalOffset).IsStrictlyLessThan(0);
        Check.That(defs[1].VerticalOffset).IsEqualTo(-defs[0].VerticalOffset);
        Check.That(defs.Select(d => d.RadiusLogical)).ContainsExactly(
            UIConstants.ButtonRadius, UIConstants.ButtonRadius);
    }

    [Fact]
    public void Close_SendsTheCloseMessage()
    {
        var messenger = new WeakReferenceMessenger();
        var closed = 0;
        messenger.Register<ManualSelectionCloseMessage>(this, (_, _) => closed++);

        ManualSelectionEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.CloseId).Activate(messenger);

        Check.That(closed).IsEqualTo(1);
    }

    [Fact]
    public void Recapture_SendsTheRecaptureMessage()
    {
        var messenger = new WeakReferenceMessenger();
        var recaptured = 0;
        messenger.Register<ManualSelectionRecaptureMessage>(this, (_, _) => recaptured++);

        ManualSelectionEdgeButtons.Definitions(dwellMs: 600)
            .Single(d => d.Id == ManualSelectionEdgeButtons.RecaptureId).Activate(messenger);

        Check.That(recaptured).IsEqualTo(1);
    }
}
