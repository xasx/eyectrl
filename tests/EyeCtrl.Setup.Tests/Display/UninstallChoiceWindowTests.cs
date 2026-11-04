namespace EyeCtrl.Setup.Tests.Display;

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using EyeCtrl.Core.Localization;
using EyeCtrl.Sdk.Abstractions;
using EyeCtrl.Sdk.Wpf;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Display;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// Q8's logic, headless: the 1 s arming on both screens, Delete only after screen 2's "yes",
/// screen 2's safe answer exactly under screen 1's Delete, and cancel on timeout/Esc/close.
/// No test shows a window.
/// </summary>
[Collection(SetupUiWindowsCollection.Name)]
public sealed class UninstallChoiceWindowTests
{
    private static readonly StringTable English = StringTable.EmbeddedEnglish;
    private static readonly Rect OwnerWorkArea = new(0, 0, 1920, 1152);

    [Fact]
    public void ArmingGate_IgnoresClicksUntilTheDelayElapsed_OnScreenOne()
    {
        WithWindow(window =>
        {
            Check.That(UninstallChoiceWindow.ArmingDelay).IsEqualTo(TimeSpan.FromSeconds(1));
            Check.That(window.IsArmed).IsFalse();

            Click(window.KeepButton);
            Check.WithCustomMessage("A click before the 1 s arming fired a choice.").That(window.Result).IsNull();

            window.CompleteArmingForTests();
            Check.That(window.IsArmed).IsTrue();
            Click(window.KeepButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Keep);
        });
    }

    [Fact]
    public void DeletingBeforeArming_DoesNotEvenOpenTheConfirmation()
    {
        WithWindow(window =>
        {
            Click(window.DeleteButton);

            Check.That(window.ShowingConfirm).IsFalse();
            Check.That(window.Result).IsNull();
        });
    }

    [Fact]
    public void BeforeArming_NoRealButtonOnEitherScreenFires()
    {
        WithWindow(window =>
        {
            Click(window.KeepButton);
            Click(window.CancelButton);
            Click(window.DeleteButton);

            Check.WithCustomMessage("A screen-1 button fired before the arming delay.")
                .That(window.Result).IsNull();
            Check.That(window.ShowingConfirm).IsFalse();
        });

        WithWindow(window =>
        {
            Arm(window);
            Click(window.DeleteButton);

            Click(window.ConfirmDeleteButton);
            Click(window.KeepDataButton);
            Check.WithCustomMessage("A screen-2 button fired before its arming delay.")
                .That(window.Result).IsNull();
        });
    }

    [Fact]
    public void ArmingGate_HoldsAgain_OnTheConfirmationScreen()
    {
        WithWindow(window =>
        {
            Arm(window);
            Click(window.DeleteButton);
            Check.That(window.ShowingConfirm).IsTrue();
            Check.That(window.IsArmed).IsFalse();

            Click(window.ConfirmDeleteButton);
            Click(window.KeepDataButton);
            Check.WithCustomMessage("A click before screen 2's arming fired a choice.").That(window.Result).IsNull();

            window.CompleteArmingForTests();
            Click(window.KeepDataButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
        });
    }

    [Fact]
    public void Delete_HappensOnlyAfterScreenTwoYes()
    {
        WithWindow(window =>
        {
            Arm(window);
            Click(window.DeleteButton);
            Check.WithCustomMessage("Screen 2's opening already deleted.").That(window.Result).IsNull();

            Arm(window);
            Click(window.ConfirmDeleteButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Delete);
        });
    }

    [Fact]
    public void ScreenTwo_PutsNoUnderScreenOnesDelete_AndYesOnTheFarLeft()
    {
        WithWindow(window =>
        {
            // Screen 1, left to right: keep, cancel, delete.
            Check.That(Grid.GetColumn(window.KeepButton)).IsEqualTo(0);
            Check.That(Grid.GetColumn(window.CancelButton)).IsEqualTo(2);
            Check.That(Grid.GetColumn(window.DeleteButton)).IsEqualTo(4);

            // Screen 2: the safe answer (No) keeps the delete button's column; Yes is far left.
            Check.That(Grid.GetColumn(window.KeepDataButton)).IsEqualTo(4);
            Check.That(Grid.GetColumn(window.ConfirmDeleteButton)).IsEqualTo(0);

            Check.That(((TextBlock)window.KeepDataButton.Content).Text)
                .IsEqualTo(English.T("uninstall.confirmDelete.no"));
            Check.That(((TextBlock)window.ConfirmDeleteButton.Content).Text)
                .IsEqualTo(English.T("uninstall.confirmDelete.yes"));
        });
    }

    [Fact]
    public void Timeout_EscapeAndClose_AllMeanCancel()
    {
        WithWindow(window =>
        {
            window.ExpireForTests();
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
            Check.That(UninstallChoiceWindow.ChoiceTimeout).IsEqualTo(TimeSpan.FromMinutes(5));
        });

        WithWindow(window =>
        {
            window.PressEscapeForTests();
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
        });

        WithWindow(window =>
        {
            // A real Close on a window whose handle exists (nothing is shown): Alt+F4 and any
            // foreign close take this path, and it must be Cancel.
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Close();
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
        });

        WithWindow(window =>
            Check.That(window.ResolveUnaskedResult()).IsEqualTo(UninstallChoice.Cancel));
    }

    [Fact]
    public void EveryChoiceButton_IsAnInvokeModeDwellTarget()
    {
        WithWindow(window =>
        {
            var buttons = new[]
            {
                window.KeepButton, window.CancelButton, window.DeleteButton,
                window.KeepDataButton, window.ConfirmDeleteButton,
            };

            foreach (var button in buttons)
            {
                Check.That(Dwell.GetEnabled(button)).IsTrue();
                Check.That(Dwell.GetMode(button)).IsEqualTo(DwellMode.Invoke);
            }
        });
    }

    [Fact]
    public void TargetsAre420By160_WithA120Gap_AndNothingInTheCentre()
    {
        WithWindow(window =>
        {
            Check.That(window.Width).IsCloseTo(1564, 0.01);
            Check.That(window.Height).IsCloseTo(806.4, 0.01);
            Check.That(window.Left).IsCloseTo(178, 0.01);
            Check.That(window.Top).IsCloseTo(172.8, 0.01);

            var content = (FrameworkElement)window.Content;
            window.Content = null;
            using var host = FitHost.Open(
                content, width: window.Width, height: window.Height, inheritFrom: window);

            var keep = Box(window.KeepButton, content);
            var cancel = Box(window.CancelButton, content);
            var delete = Box(window.DeleteButton, content);

            foreach (var box in new[] { keep, cancel, delete })
            {
                Check.That(box.Width).IsCloseTo(420, 0.01);
                Check.That(box.Height).IsCloseTo(160, 0.01);
            }

            Check.WithCustomMessage($"The gap keep→cancel is {cancel.Left - keep.Right}, not 120.")
                .That(cancel.Left - keep.Right).IsCloseTo(120, 0.01);
            Check.WithCustomMessage($"The gap cancel→delete is {delete.Left - cancel.Right}, not 120.")
                .That(delete.Left - cancel.Right).IsCloseTo(120, 0.01);
            Check.That(keep.Top).IsCloseTo(cancel.Top, 0.01).And.IsCloseTo(delete.Top, 0.01);

            // The row sits on the bottom margin, so the window's centre has no target.
            Check.That(keep.Bottom).IsCloseTo(window.Height - 32 - 48, 0.5);
            var centre = new Point(window.Width / 2, window.Height / 2);
            foreach (var box in new[] { keep, cancel, delete })
                Check.WithCustomMessage($"A target covers the window's centre: {box}.").That(box.Contains(centre)).IsFalse();

            // 420 DIP at the user's 200 % is 840 physical px; the FitHost guard proves the scale.
            var origin = window.KeepButton.PointToScreen(new Point(0, 0));
            var physical = window.KeepButton.PointToScreen(new Point(420, 0)).X - origin.X;
            Check.That(physical).IsCloseTo(840, 1.01);
        });
    }

    private static Rect Box(FrameworkElement element, FrameworkElement root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    /// <summary>The real click path: the routed event the UIA Invoke peer and a mouse both raise.</summary>
    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void Arm(UninstallChoiceWindow window) => window.CompleteArmingForTests();

    private static void WithWindow(Action<UninstallChoiceWindow> action)
    {
        RunSta(() =>
        {
            using var pipe = Dwell.UseTestPipeForTests(
                "eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));
            var window = new UninstallChoiceWindow(English, OwnerWorkArea);
            try
            {
                action(window);
            }
            finally
            {
                try { window.Close(); } catch (InvalidOperationException) { }
            }
        });
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
