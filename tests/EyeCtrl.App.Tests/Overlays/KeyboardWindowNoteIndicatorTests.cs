namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Fixup m2 (execution deviations): the "→ Note" indicator is an OVERLAY and must never change
/// the keyboard's SizeToContent size or position — keys moving under a gaze that is about to type
/// are a dwell hazard. The layout the window sizes to is the DockPanel's alone; the Canvas the
/// indicator floats in always measures 0×0.
/// </summary>
[Collection("WpfApplication")]
public class KeyboardWindowNoteIndicatorTests
{
    [StaFact]
    public void FlippingIsTargetingNote_NeverChangesTheKeyboardLayoutSize()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
        window.SetViewModel(vm);

        // The content's measure is the same input SizeToContent reads; nothing is shown.
        var content = (Border)window.Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var before = content.DesiredSize;
        Check.That(before.Height).IsStrictlyGreaterThan(0);

        messenger.Send(new NoteEditingChangedMessage(Editing: true)); // → Note lit
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check.WithCustomMessage("lighting → Note must not change the keyboard's layout size")
            .That(content.DesiredSize).IsEqualTo(before);

        messenger.Send(new NoteEditingChangedMessage(Editing: false)); // → Note off again
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check.That(content.DesiredSize).IsEqualTo(before);
    }
}
