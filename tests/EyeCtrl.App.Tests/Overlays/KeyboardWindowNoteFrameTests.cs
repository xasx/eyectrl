namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The note-targeting state, owner decision on review-02 F3: an accent FRAME around the keyboard
/// instead of a floating label. The frame border is always present with a CONSTANT thickness and
/// a transparent brush, so flipping <c>IsTargetingNote</c> changes only the brush — the layout
/// the SizeToContent window reads never moves, and no key ever shifts under a gaze about to type.
/// </summary>
[Collection("WpfApplication")]
public class KeyboardWindowNoteFrameTests
{
    [StaFact]
    public void FlippingIsTargetingNote_NeverChangesTheKeyboardLayoutSize()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
        window.SetViewModel(vm);

        // The content's measure is the same input SizeToContent reads; nothing is shown. The
        // frame's thickness is constant, so its DesiredSize is identical in all three states.
        var frame = (Border)window.Content;
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var transparent = frame.DesiredSize;
        Check.That(transparent.Height).IsStrictlyGreaterThan(0);

        messenger.Send(new NoteEditingChangedMessage(Editing: true)); // frame lit
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check.WithCustomMessage("lighting the targeting frame must not change the keyboard's layout size")
            .That(frame.DesiredSize).IsEqualTo(transparent);

        messenger.Send(new NoteEditingChangedMessage(Editing: false)); // frame off again
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check.That(frame.DesiredSize).IsEqualTo(transparent);
    }

    [StaFact]
    public void TheFrameBrush_IsTheAccentWhileTargeting_AndTransparentOtherwise()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
        window.SetViewModel(vm);
        var frame = (Border)window.Content;

        Check.WithCustomMessage("the frame must start transparent (an always-present border)")
            .That(ReferenceEquals(frame.BorderBrush, System.Windows.Media.Brushes.Transparent)).IsTrue();

        messenger.Send(new NoteEditingChangedMessage(Editing: true));
        Check.WithCustomMessage("while targeting, the frame is the accent (a note's Edit border colour)")
            .That(ReferenceEquals(frame.BorderBrush, Tokens.BrushAccent)).IsTrue();

        messenger.Send(new NoteEditingChangedMessage(Editing: false));
        Check.That(ReferenceEquals(frame.BorderBrush, System.Windows.Media.Brushes.Transparent)).IsTrue();
    }
}
