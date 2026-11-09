namespace EyeCtrl.Setup.Tests.Display;

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// The display-only progress/result window: its texts, its 10 s / 60 s self-close, and — since
/// owner decision Q13 — that it is an ordinary window (not click-through, not topmost, in the
/// taskbar, with a title and EyeCtrl's icon). The window's HWND is created but never shown; the
/// style test reads the real bits.
/// </summary>
[Collection(SetupUiWindowsCollection.Name)]
public sealed class SetupProgressWindowTests
{
    private static readonly StringTable English = StringTable.EmbeddedEnglish;
    private const string AppData = @"C:\Users\test\AppData\Roaming\EyeCtrl";

    [Fact]
    public void ProgressText_IsTheVerbsOwn_AndEveryStageAdvances()
    {
        RunSta(() =>
        {
            var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
            try
            {
                Check.That(window.TitleText).IsEqualTo(English.T("update.progress.title"));
                Check.That(window.DetailText).IsEqualTo(English.T("update.progress.detail"));

                foreach (var stage in Enum.GetValues<SetupStage>())
                {
                    window.Stage(stage);
                    Check.That(window.StageText).IsEqualTo(SetupDisplayText.Stage(English, SetupVerb.Update, stage));
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ARollback_StagesNameThePreviousVersion_NotTheNewOne()
    {
        RunSta(() =>
        {
            var window = new SetupProgressWindow(English, SetupVerb.Rollback, close: () => { });
            try
            {
                window.Stage(SetupStage.Swap);
                Check.WithCustomMessage("a rollback's swap must not reuse the update's 'new version' text.")
                    .That(window.StageText).IsEqualTo(English.T("rollback.progress.swap"));
                Check.That(window.StageText).Not.IsEqualTo(English.T("update.progress.swap"));

                window.Stage(SetupStage.Start);
                Check.WithCustomMessage("a rollback's start must not reuse the update's 'new version' text.")
                    .That(window.StageText).IsEqualTo(English.T("rollback.progress.start"));
                Check.That(window.StageText).Not.IsEqualTo(English.T("update.progress.start"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ASuccessResult_SchedulesTenSeconds_AFailureSixty()
    {
        RunSta(() =>
        {
            var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
            try
            {
                var success = new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.Ok, Version = "1.0.0" };
                window.ShowResult(SetupDisplayText.Result(English, success, AppData));
                Check.That(window.IsCloseScheduled).IsTrue();
                Check.That(window.ScheduledCloseDelay).IsEqualTo(TimeSpan.FromSeconds(10));

                var failure = new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.Failed };
                window.ShowResult(SetupDisplayText.Result(English, failure, AppData));
                Check.That(window.ScheduledCloseDelay).IsEqualTo(TimeSpan.FromSeconds(60));

                var degraded = new SetupResult
                {
                    Verb = SetupVerb.Update, Outcome = SetupOutcome.Degraded, Version = "0.9.0",
                };
                window.ShowResult(SetupDisplayText.Result(English, degraded, AppData));
                Check.WithCustomMessage("Q12's degraded must read as a failure (60 s) and never as a success.")
                    .That(window.ScheduledCloseDelay).IsEqualTo(TimeSpan.FromSeconds(60));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheResultReplacesTheProgress_WithItsOwnText()
    {
        RunSta(() =>
        {
            var window = new SetupProgressWindow(English, SetupVerb.Uninstall, close: () => { });
            try
            {
                window.Stage(SetupStage.Stop);
                var result = new SetupResult
                {
                    Verb = SetupVerb.Uninstall, Outcome = SetupOutcome.Uninstalled, Detail = "kept",
                };
                window.ShowResult(SetupDisplayText.Result(English, result, AppData));

                Check.That(window.TitleText).IsEqualTo(English.T("uninstall.done.kept"));
                Check.That(window.DetailText).IsEqualTo(string.Empty);
                Check.That(window.StageText).IsEqualTo(string.Empty);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheSelfCloseTimer_ActuallyFires()
    {
        RunSta(() =>
        {
            var closed = false;
            var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => closed = true);
            try
            {
                window.ScheduleClose(TimeSpan.FromMilliseconds(40));

                // Pump the thread's dispatcher; the window is never shown.
                var frame = new DispatcherFrame();
                var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
                stop.Tick += (_, _) => frame.Continue = false;
                stop.Start();
                Dispatcher.PushFrame(frame);
                stop.Stop();

                Check.WithCustomMessage("The result's self-close timer did not fire.").That(closed).IsTrue();
                Check.That(window.IsCloseScheduled).IsFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheWindowIsOrdinary_NotTopmostOrClickThrough_AndInTheTaskbar()
    {
        RunSta(() =>
        {
            var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
            try
            {
                // EnsureHandle creates the real HWND without showing the window.
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                Check.That(hwnd).IsNotEqualTo(IntPtr.Zero);

                Check.WithCustomMessage("Q13: the progress window must be an ordinary, non-layered window.")
                    .That(window.AllowsTransparency).IsFalse();
                Check.WithCustomMessage("Q13: the screen must not be topmost.")
                    .That(window.Topmost).IsFalse();
                Check.WithCustomMessage("Q13: the screen must have a taskbar button.")
                    .That(window.ShowInTaskbar).IsTrue();
                Check.WithCustomMessage("Q13: an ordinary window may activate once when it appears.")
                    .That(window.ShowActivated).IsTrue();
                Check.WithCustomMessage("The taskbar button and the title need the verb's title.")
                    .That(window.Title).IsEqualTo(English.T("update.progress.title"));
                Check.WithCustomMessage("EyeCtrl's icon is missing from the taskbar button.")
                    .That(window.Icon).IsNotNull();

                Check.WithCustomMessage("WS_EX_TOPMOST is set: the screen would stay above other windows.")
                    .That(SetupProgressWindow.HasTopmostStyle(hwnd)).IsFalse();
                Check.WithCustomMessage("WS_EX_TRANSPARENT is set: the screen would still pass clicks through.")
                    .That(SetupProgressWindow.HasClickThroughStyle(hwnd)).IsFalse();
                Check.WithCustomMessage("WS_EX_LAYERED is set: the screen is not an ordinary window.")
                    .That(SetupProgressWindow.HasLayeredStyle(hwnd)).IsFalse();
                Check.WithCustomMessage("WS_EX_NOACTIVATE is set: the screen could not activate.")
                    .That(SetupProgressWindow.HasNoActivateStyle(hwnd)).IsFalse();
                Check.WithCustomMessage("WS_EX_TOOLWINDOW is set: the screen would have no taskbar button.")
                    .That(SetupProgressWindow.HasToolWindowStyle(hwnd)).IsFalse();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AtDpiTwo_TheProgressWindowKeepsItsDipBounds_AsPhysicalPixels()
    {
        RunSta(() =>
        {
            // fixup-04: every bound is a DIP, so at the owner's 200 % only the physical pixels
            // double. The window is never shown; its production content is hosted off-screen at
            // exactly 2.0, the same technique the choice window's target pin uses.
            var workArea = new Rect(0, 0, 1920, 1152);
            var window = new SetupProgressWindow(
                English, SetupVerb.Update, close: () => { }, workArea: workArea);
            try
            {
                Check.That(window.Width).IsCloseTo(1344, 0.01);
                Check.That(window.Height).IsCloseTo(806.4, 0.01);
                Check.That(window.Left).IsCloseTo(288, 0.01);
                Check.That(window.Top).IsCloseTo(172.8, 0.01);

                var content = (FrameworkElement)window.Content;
                window.Content = null;
                using var host = FitHost.Open(
                    content, width: window.Width, height: window.Height, inheritFrom: window);

                var origin = content.PointToScreen(new Point(0, 0));
                var right = content.PointToScreen(
                    new Point(SetupWindowMetrics.ProgressBounds(workArea).Width, 0));
                Check.WithCustomMessage(
                        $"1344 DIP measured {right.X - origin.X} px, not 2688 at the owner's 200 %.")
                    .That(right.X - origin.X).IsCloseTo(2688, 1.01);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ClosingTheWindowEarly_LeavesTheDisplayWorking_AndForcesNothingAgain()
    {
        RunSta(() =>
        {
            var created = 0;
            using var display = new WpfSetupDisplay(
                SetupVerb.Update,
                SetupUi.English,
                AppData,
                createWindow: () =>
                {
                    created++;
                    return new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
                },
                showWindow: window =>
                {
                    // The owner closes the display early. EnsureHandle gives the real HWND (so
                    // Closed fires) without the window ever being painted.
                    _ = new WindowInteropHelper(window).EnsureHandle();
                    window.Close();
                });

            var watch = Stopwatch.StartNew();
            display.Stage(SetupStage.Verify);
            Check.That(created).IsEqualTo(1);

            // A later stage must not throw, must not force the closed window back, and must not
            // recreate it; the display is gone but the install is unaffected.
            display.Stage(SetupStage.Swap);
            Check.That(created).IsEqualTo(1);

            var result = new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.Ok, Version = "1.0.0" };
            display.Finish(result);
            watch.Stop();

            Check.That(created).IsEqualTo(1);
            Check.WithCustomMessage("Finish waited for a result timer of a window the owner had already closed.")
                .That(watch.Elapsed.TotalSeconds).IsStrictlyLessThan(5.0);
        });
    }

    [Theory]
    [InlineData(SetupVerb.Update)]
    [InlineData(SetupVerb.Rollback)]
    public void EveryStage_ShowsTheOneProgressWindowOnce(SetupVerb verb)
    {
        // S6e r03 L1 / mutant T5: a display is one screen lifecycle. A Show() per stage would
        // restart the window (and take the foreground again); the owner must see it appear once.
        RunSta(() =>
        {
            var created = 0;
            var shown = 0;
            SetupProgressWindow? window = null;
            using var display = new WpfSetupDisplay(
                verb,
                SetupUi.English,
                AppData,
                createWindow: () =>
                {
                    created++;
                    window = new SetupProgressWindow(English, verb, close: () => { });
                    return window;
                },
                showWindow: w =>
                {
                    shown++;
                    // A real HWND without a paint, so Closed can fire when the test closes it.
                    _ = new WindowInteropHelper(w).EnsureHandle();
                });

            foreach (var stage in Enum.GetValues<SetupStage>())
                display.Stage(stage);

            Check.WithCustomMessage("A stage created a second window.").That(created).IsEqualTo(1);
            Check.WithCustomMessage("A stage showed the progress window again.").That(shown).IsEqualTo(1);

            // The result uses the same one window and never shows it a second time. Queue the
            // close at ApplicationIdle: the dispatcher's default-priority ShowResult call runs
            // first, so Finish's result half is exercised (review-01 F1), then the idle close lets
            // Wait return. A Finish that re-shows the window (mutant T5b) then counts a second Show.
            var shownWindow = window!;
            shownWindow.Dispatcher.BeginInvoke(shownWindow.Close, DispatcherPriority.ApplicationIdle);
            display.Finish(new SetupResult { Verb = verb, Outcome = SetupOutcome.Ok, Version = "1.0.0" });

            Check.That(created).IsEqualTo(1);
            Check.That(shown).IsEqualTo(1);
        });
    }

    [Fact]
    public void TheFactory_ShowsOnlyForTheThreeDisplayVerbs()
    {
        var factory = new WpfSetupDisplayFactory(SetupUi.English, AppData);
        foreach (var verb in new[] { SetupVerb.Update, SetupVerb.Rollback, SetupVerb.Uninstall })
        {
            using var display = factory.Create(verb);
            Check.That(display).IsInstanceOf<WpfSetupDisplay>();
        }

        foreach (var verb in new[] { SetupVerb.Repair, SetupVerb.Status })
        {
            using var display = factory.Create(verb);
            Check.That(display).IsSameReferenceAs(NullSetupDisplay.Instance);
        }
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
