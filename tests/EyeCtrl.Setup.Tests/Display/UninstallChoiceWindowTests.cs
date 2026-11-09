namespace EyeCtrl.Setup.Tests.Display;

using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    public void Loaded_StartsTheArmingAndTheFiveMinuteTimers()
    {
        WithWindow(window =>
        {
            Check.That(window.IsArming).IsFalse();
            Check.That(window.IsChoiceTimerRunning).IsFalse();

            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            Check.WithCustomMessage("The 1 s arming timer did not start on Loaded.").That(window.IsArming).IsTrue();
            Check.WithCustomMessage("The 5-minute no-choice timer did not start on Loaded.")
                .That(window.IsChoiceTimerRunning).IsTrue();
            Check.That(window.IsArmed).IsFalse();

            window.CompleteArmingForTests();
            Click(window.KeepButton);
            Check.WithCustomMessage("The no-choice timer kept running after the window answered.")
                .That(window.IsChoiceTimerRunning).IsFalse();
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
            RaiseEscape(window);
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
            Check.That(window.ResolveAnswer().Choice).IsEqualTo(UninstallChoice.Cancel));
    }

    [Fact]
    public void EveryCancelRoute_NamesItsPath()
    {
        // O6a: the log must say which of the five routes cancelled, not just that it cancelled.
        WithWindow(window =>
        {
            Arm(window);
            Click(window.CancelButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
            Check.That(window.CancelPath).IsEqualTo(UninstallCancelPath.Button);
        });

        WithWindow(window =>
        {
            Arm(window);
            Click(window.DeleteButton);
            Arm(window);
            Click(window.KeepDataButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
            Check.That(window.CancelPath).IsEqualTo(UninstallCancelPath.KeepData);
        });

        WithWindow(window =>
        {
            RaiseEscape(window);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
            Check.That(window.CancelPath).IsEqualTo(UninstallCancelPath.Escape);
        });

        WithWindow(window =>
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.Close();
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
            Check.That(window.CancelPath).IsEqualTo(UninstallCancelPath.Close);
        });

        WithWindow(window =>
        {
            window.ExpireForTests();
            Check.That(window.Result).IsEqualTo(UninstallChoice.Cancel);
            Check.That(window.CancelPath).IsEqualTo(UninstallCancelPath.Timeout);
        });

        WithWindow(window =>
        {
            // A window that could not be shown at all is still a cancel, but names no route.
            var answer = window.ResolveAnswer();
            Check.That(answer.Choice).IsEqualTo(UninstallChoice.Cancel);
            Check.That(answer.CancelPath).IsEqualTo(UninstallCancelPath.Unavailable);
        });
    }

    [Fact]
    public void ANonCancelAnswer_HasNoCancelPath()
    {
        WithWindow(window =>
        {
            Arm(window);
            Click(window.KeepButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Keep);
            Check.That(window.CancelPath).IsEqualTo(UninstallCancelPath.None);
            Check.That(window.ResolveAnswer().CancelPath).IsEqualTo(UninstallCancelPath.None);
        });

        WithWindow(window =>
        {
            Arm(window);
            Click(window.DeleteButton);
            Arm(window);
            Click(window.ConfirmDeleteButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Delete);
            Check.That(window.ResolveAnswer().CancelPath).IsEqualTo(UninstallCancelPath.None);
        });
    }

    [Fact]
    public void OnlyTheScreenInFront_IsDwellable_AndEveryChoiceButtonUsesInvokeMode()
    {
        WithWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            var all = new[]
            {
                window.KeepButton, window.CancelButton, window.DeleteButton,
                window.KeepDataButton, window.ConfirmDeleteButton,
            };

            foreach (var button in all)
                Check.That(Dwell.GetMode(button)).IsEqualTo(DwellMode.Invoke);

            // Screen 1 is in front: its three buttons are targets, screen 2's are not.
            Check.That(Dwell.TargetIdForTests(window.KeepButton)).IsNotNull();
            Check.That(Dwell.TargetIdForTests(window.CancelButton)).IsNotNull();
            Check.That(Dwell.TargetIdForTests(window.DeleteButton)).IsNotNull();
            Check.WithCustomMessage("A hidden screen-2 button was dwellable behind screen 1.")
                .That(Dwell.TargetIdForTests(window.KeepDataButton)).IsNull();
            Check.WithCustomMessage("A hidden screen-2 button was dwellable behind screen 1.")
                .That(Dwell.TargetIdForTests(window.ConfirmDeleteButton)).IsNull();

            // The production screen switch: a real Delete click swaps which three are registered.
            Arm(window);
            Click(window.DeleteButton);
            Check.That(window.ShowingConfirm).IsTrue();
            Check.WithCustomMessage("The screen switch did not reset the gate to the 1 s arming delay.")
                .That(window.ArmingInterval).IsEqualTo(UninstallChoiceWindow.ArmingDelay);

            Check.WithCustomMessage("A screen-1 button stayed dwellable behind screen 2.")
                .That(Dwell.TargetIdForTests(window.KeepButton)).IsNull();
            Check.WithCustomMessage("A screen-1 button stayed dwellable behind screen 2.")
                .That(Dwell.TargetIdForTests(window.CancelButton)).IsNull();
            Check.WithCustomMessage("A screen-1 button stayed dwellable behind screen 2.")
                .That(Dwell.TargetIdForTests(window.DeleteButton)).IsNull();
            Check.That(Dwell.TargetIdForTests(window.KeepDataButton)).IsNotNull();
            Check.That(Dwell.TargetIdForTests(window.ConfirmDeleteButton)).IsNotNull();
        });
    }

    [Fact]
    public void Reactivation_ReArmsAFreshDwell_WithoutAnySuspend()
    {
        // fixup-06 L3/Q3: nothing was dropped before activation, so the disable-first in
        // ArmDwellTargets is what makes this a restart. If it only re-enabled the already-enabled
        // buttons, the ids would not change and the old dwell would survive.
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var keepBefore = Dwell.TargetIdForTests(window.KeepButton);
            var deleteBefore = Dwell.TargetIdForTests(window.DeleteButton);
            Check.That(keepBefore).IsNotNull();
            Check.That(deleteBefore).IsNotNull();
            window.CompleteArmingForTests();
            Check.That(window.IsArmed).IsTrue();

            window.RaiseActivated();
            var keepAfter = Dwell.TargetIdForTests(window.KeepButton);
            var deleteAfter = Dwell.TargetIdForTests(window.DeleteButton);
            Check.WithCustomMessage("Reactivation did not register a dwell target.")
                .That(keepAfter).IsNotNull();
            Check.WithCustomMessage("Reactivation reused the old target id; its old progress would survive.")
                .That(keepAfter).IsNotEqualTo(keepBefore);
            Check.That(deleteAfter).IsNotNull().And.IsNotEqualTo(deleteBefore);
            Check.WithCustomMessage("Reactivation did not restart the 1 s arming gate.")
                .That(window.IsArmed).IsFalse();
            Check.That(window.IsArming).IsTrue();
            Check.That(window.ArmingInterval).IsEqualTo(UninstallChoiceWindow.ArmingDelay);

            Click(window.KeepButton);
            Check.WithCustomMessage("A click before the fresh arming delay fired.")
                .That(window.Result).IsNull();

            window.CompleteArmingForTests();
            Click(window.KeepButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Keep);
        });
    }

    [Fact]
    public void AHiddenWindow_DropsEveryDwellTarget_AndBecomingVisibleNeedsAFreshDwell()
    {
        // fixup-06 L3/Q4: hiding is the edge that drops the targets; becoming visible re-arms.
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var keepBefore = Dwell.TargetIdForTests(window.KeepButton);
            Check.That(keepBefore).IsNotNull();
            window.CompleteArmingForTests();

            window.RaiseHidden();
            Check.WithCustomMessage("A hidden window kept a dwell target.")
                .That(Dwell.TargetIdForTests(window.KeepButton)).IsNull();
            Check.That(Dwell.TargetIdForTests(window.DeleteButton)).IsNull();
            Check.That(window.IsArmed).IsFalse();
            Check.WithCustomMessage("A hidden window kept its 1 s arming timer running.")
                .That(window.IsArming).IsFalse();
            Click(window.KeepButton);
            Check.That(window.Result).IsNull();

            window.RaiseVisible();
            var keepAfter = Dwell.TargetIdForTests(window.KeepButton);
            Check.WithCustomMessage("Becoming visible did not register a dwell target.")
                .That(keepAfter).IsNotNull();
            Check.WithCustomMessage("Becoming visible reused the old target id.")
                .That(keepAfter).IsNotEqualTo(keepBefore);
            Check.That(window.IsArmed).IsFalse();
            Check.That(window.IsArming).IsTrue();

            window.CompleteArmingForTests();
            Click(window.KeepButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Keep);
        });
    }

    [Fact]
    public void LosingActivation_DoesNotDropTheTargetsOrTheGate()
    {
        // fixup-06 L2: Q8 is topmost, so being inactive usually does not mean being covered; the
        // App's occlusion filter handles real coverage. Dropping here would leave a visible Q8 dead
        // to gaze until it is clicked.
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var keepBefore = Dwell.TargetIdForTests(window.KeepButton);
            window.CompleteArmingForTests();
            Check.That(window.IsArmed).IsTrue();

            window.RaiseDeactivated();

            Check.WithCustomMessage("Losing activation dropped the dwell target.")
                .That(Dwell.TargetIdForTests(window.KeepButton)).IsEqualTo(keepBefore);
            Check.WithCustomMessage("Losing activation disarmed the window.")
                .That(window.IsArmed).IsTrue();
            Click(window.KeepButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Keep);
        });
    }

    [Fact]
    public void APreArmInvoke_RestartsTheGateBeyondADwell_SoARestingGazeNeverFires()
    {
        // fixup-06 M3(b): the detector re-arms under a still gaze, so a covered-then-uncovered (or
        // just inactive) Q8 would fire again ~dwell+cooldown after every activation. Every pre-arm
        // Invoke instead pushes the gate beyond that, so a gaze that never leaves never fires.
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check.That(window.ArmingInterval).IsEqualTo(UninstallChoiceWindow.ArmingDelay);

            for (var i = 0; i < 5; i++)
            {
                Click(window.KeepButton);
                Check.WithCustomMessage($"Pre-arm Invoke {i + 1} fired a choice.")
                    .That(window.Result).IsNull();
                Check.That(window.IsArmed).IsFalse();
                Check.WithCustomMessage($"Pre-arm Invoke {i + 1} did not hold the gate past the worst dwell restart.")
                    .That(window.ArmingInterval).IsEqualTo(TimeSpan.FromMilliseconds(2800));
            }

            // The gaze left and the gate elapsed: the window is normally armed again.
            window.CompleteArmingForTests();
            Check.That(window.IsArmed).IsTrue();
            Click(window.KeepButton);
            Check.That(window.Result).IsEqualTo(UninstallChoice.Keep);
        });
    }

    [Fact]
    public void TheRestingGate_CoversOneBlinkRestart_NotJustOneDwell()
    {
        // fixup-07 N1: at grace 0 a silence >= 150 ms restarts the dwell. A blink b ms into the
        // dwell pushes the next Invoke to cooldown + x + b + dwell after the previous one, and x
        // can be nearly a whole dwell. The gate must beat cooldown + 2*dwell + blink.
        var owner = new SetupDwellTiming(DefaultDwellMs: 800, CooldownMs: 400);
        var worstRestartMs = owner.CooldownMs + (2 * owner.DefaultDwellMs) + SetupDwellTiming.BlinkAllowanceMs;

        Check.That(owner.RestingGate).IsEqualTo(TimeSpan.FromMilliseconds(2800));
        Check.WithCustomMessage(
                $"A blink restart at {worstRestartMs} ms would land after the {owner.RestingGate.TotalMilliseconds} ms gate.")
            .That(owner.RestingGate.TotalMilliseconds).IsStrictlyGreaterThan(worstRestartMs);
        Check.That(SetupDwellTiming.Default.RestingGate.TotalMilliseconds)
            .IsStrictlyGreaterThan(UninstallChoiceWindow.ArmingDelay.TotalMilliseconds);
    }

    [Fact]
    public void APreArmInvoke_HoldsTheGatePastABlinkRestartRhythm()
    {
        // fixup-07 N1: the detector's own restart after one blink of the allowance must still land
        // inside the gate, so that Invoke is ignored too (and restarts the gate again).
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            var restartRhythmMs =
                SetupDwellTiming.Default.CooldownMs
                + (2 * SetupDwellTiming.Default.DefaultDwellMs)
                + SetupDwellTiming.BlinkAllowanceMs;

            for (var i = 0; i < 3; i++)
            {
                Click(window.KeepButton);
                Check.That(window.Result).IsNull();
                Check.That(window.IsArmed).IsFalse();
                Check.WithCustomMessage(
                        $"A blink-restart Invoke rhythm of {restartRhythmMs} ms armed the window.")
                    .That(window.ArmingInterval.TotalMilliseconds).IsStrictlyGreaterThan(restartRhythmMs);
            }
        });
    }

    [Fact]
    public void TheRestingGate_IsFlooredAtTheArmingDelay()
    {
        // fixup-07 L1: the gate must never arm Q8 earlier than its own 1 s rule. With N1's wider
        // formula the legal minimum is already above the floor (200/0 -> 1.2 s), so the floor is
        // exercised through the constructor's explicit gate.
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check.That(window.RestingGazeGate).IsEqualTo(UninstallChoiceWindow.ArmingDelay);
            Click(window.KeepButton);
            Check.That(window.Result).IsNull();
            Check.That(window.ArmingInterval).IsEqualTo(UninstallChoiceWindow.ArmingDelay);
        }, TimeSpan.FromMilliseconds(900));

        Check.WithCustomMessage("The legal minimum timing is below the floor; update this test.")
            .That(new SetupDwellTiming(200, 0).RestingGate.TotalMilliseconds)
            .IsStrictlyGreaterThan(UninstallChoiceWindow.ArmingDelay.TotalMilliseconds);
    }

    [Fact]
    public void StartArming_ResetsARestingGateBackToTheArmingDelay()
    {
        // fixup-07 L2/M3c: after a pre-arm Invoke the gate is long; the next activation or screen
        // switch must return to the ordinary 1 s, not inherit the resting gate.
        WithActivationWindow(window =>
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Click(window.KeepButton);
            Check.That(window.ArmingInterval).IsEqualTo(SetupDwellTiming.Default.RestingGate);
            Check.That(window.IsArming).IsTrue();

            window.RaiseActivated();
            Check.WithCustomMessage("Reactivation kept the resting gate instead of resetting to 1 s.")
                .That(window.ArmingInterval).IsEqualTo(UninstallChoiceWindow.ArmingDelay);
        });
    }

    [Fact]
    public void TheProductionFactory_PassesTheOwnersDwellTimingToTheWindow()
    {
        // fixup-07 L2/M3e: WpfUninstallChoice must hand the parsed owner timing to the window. A
        // fallback to the default gate would be shorter than one dwell + cooldown for a slow dwell.
        var paths = new SetupPaths(
            @"C:\Program Files", @"C:\Users\test\AppData\Roaming", @"C:\Users\test\AppData\Local");
        var files = new EyeCtrl.Setup.Tests.TestSupport.InMemoryFileSystem();
        files.AddFile(
            paths.SettingsPath,
            """{"externalDwell":{"defaultDwellMs":1500,"cooldownMs":900}}""");
        var ui = SetupUi.Load(paths, files);

        RunSta(() =>
        {
            using var pipe = Dwell.UseTestPipeForTests(
                "eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));
            var window = UninstallChoiceWindow.CreateWindow(ui);
            try
            {
                Check.That(window.RestingGazeGate).IsEqualTo(ui.Dwell.RestingGate);
                Check.WithCustomMessage("The factory dropped the owner's dwell timing.")
                    .That(window.RestingGazeGate).IsEqualTo(TimeSpan.FromMilliseconds(4700));
            }
            finally
            {
                try { window.Close(); } catch (InvalidOperationException) { }
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

            // L2: screen 2's "Nein, behalten" occupies exactly screen 1's Delete cell, measured in
            // screen pixels at 2.0 (not by Grid column). Drive the production path: a real Delete
            // click switches the screens.
            var deleteTopLeft = window.DeleteButton.PointToScreen(new Point(0, 0));
            var deleteBottomRight = window.DeleteButton.PointToScreen(new Point(420, 160));

            Arm(window);
            Click(window.DeleteButton);
            Check.That(window.ShowingConfirm).IsTrue();
            host.Relayout();

            var noTopLeft = window.KeepDataButton.PointToScreen(new Point(0, 0));
            var noBottomRight = window.KeepDataButton.PointToScreen(new Point(420, 160));

            Check.WithCustomMessage($"'Nein' starts at {noTopLeft}; Delete started at {deleteTopLeft}.")
                .That(noTopLeft.X).IsCloseTo(deleteTopLeft.X, 0.5);
            Check.WithCustomMessage($"'Nein' starts at {noTopLeft}; Delete started at {deleteTopLeft}.")
                .That(noTopLeft.Y).IsCloseTo(deleteTopLeft.Y, 0.5);
            Check.WithCustomMessage($"'Nein' ends at {noBottomRight}; Delete ended at {deleteBottomRight}.")
                .That(noBottomRight.X).IsCloseTo(deleteBottomRight.X, 0.5);
            Check.WithCustomMessage($"'Nein' ends at {noBottomRight}; Delete ended at {deleteBottomRight}.")
                .That(noBottomRight.Y).IsCloseTo(deleteBottomRight.Y, 0.5);
        });
    }

    [Fact]
    public void TheWindow_SubscribesTheVisibilityEdge_ToTheHideShowPath()
    {
        // fixup-06 L3/Q4: the WPF IsVisibleChanged event cannot be raised on a window that is
        // never shown, so the behaviour is tested through OnWindowVisibilityChanged and this pins
        // the one-line subscription that reaches it.
        var source = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(RepoFile("src", "EyeCtrl.Setup", "Display", "UninstallChoiceWindow.cs")),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "UninstallChoiceWindow no longer subscribes IsVisibleChanged: hiding would not drop the "
                + "targets and becoming visible would not re-arm (review-05 L3/Q4).")
            .That(source.Contains(
                "IsVisibleChanged+=(_,e)=>OnWindowVisibilityChanged((bool)e.NewValue);")).IsTrue();
    }

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "EyeCtrl.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("repo root not found"),
            Path.Combine(parts));
    }

    private static Rect Box(FrameworkElement element, FrameworkElement root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    /// <summary>The real click path: the routed event the UIA Invoke peer and a mouse both raise.</summary>
    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    /// <summary>Raises the real routed KeyDown the window's handler listens for.</summary>
    private static void RaiseEscape(UninstallChoiceWindow window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var source = PresentationSource.FromVisual(window) ?? HwndSource.FromHwnd(hwnd);
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
        window.RaiseEvent(args);
    }

    private static void Arm(UninstallChoiceWindow window) => window.CompleteArmingForTests();

    private static void WithWindow(Action<UninstallChoiceWindow> action) =>
        WithWindowCore(action, () => new UninstallChoiceWindow(English, OwnerWorkArea));

    /// <summary>
    /// The same window through a subclass whose only job is to deliver the activation callbacks
    /// the OS delivers: it calls the very <c>OnActivated</c>/<c>OnDeactivated</c> overrides the
    /// production window runs. A window is never shown to make them fire.
    /// </summary>
    private static void WithActivationWindow(
        Action<ActivationChoiceWindow> action, TimeSpan? restingGate = null) =>
        WithWindowCore(action, () => new ActivationChoiceWindow(
            English, OwnerWorkArea, restingGate ?? SetupDwellTiming.Default.RestingGate));

    private static void WithWindowCore<T>(Action<T> action, Func<T> create)
        where T : UninstallChoiceWindow
    {
        RunSta(() =>
        {
            using var pipe = Dwell.UseTestPipeForTests(
                "eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));
            var window = create();
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

    private sealed class ActivationChoiceWindow : UninstallChoiceWindow
    {
        public ActivationChoiceWindow(StringTable strings, Rect workArea, TimeSpan restingGate)
            : base(strings, workArea, restingGate) { }

        public void RaiseActivated() => OnActivated(EventArgs.Empty);

        public void RaiseDeactivated() => OnDeactivated(EventArgs.Empty);

        public void RaiseHidden() => OnWindowVisibilityChanged(false);

        public void RaiseVisible() => OnWindowVisibilityChanged(true);
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
