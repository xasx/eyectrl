namespace EyeCtrl.Setup.Tests.Display;

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;

/// <summary>
/// The display-only progress/result window: its texts, its 10 s / 60 s self-close, and the two
/// extended styles that make it click-through and non-activating. The window's HWND is created
/// but never shown; the style test reads the real bits.
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
                    Check.That(window.StageText).IsEqualTo(SetupDisplayText.Stage(English, stage));
                }
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
    public void TheWindowCarriesTransparentAndNoActivate()
    {
        RunSta(() =>
        {
            var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
            try
            {
                // EnsureHandle raises SourceInitialized, so this exercises the production wiring
                // (not a test helper that applies the styles itself).
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                Check.That(hwnd).IsNotEqualTo(IntPtr.Zero);
                Check.WithCustomMessage("AllowsTransparency is false: WS_EX_TRANSPARENT would pass no clicks.")
                    .That(window.AllowsTransparency).IsTrue();
                Check.That(window.Topmost).IsTrue();
                Check.WithCustomMessage("WS_EX_LAYERED is missing: WS_EX_TRANSPARENT only works on a layered window.")
                    .That(SetupProgressWindow.HasLayeredStyle(hwnd)).IsTrue();
                Check.WithCustomMessage("WS_EX_TRANSPARENT is missing: the screen would block TD Control.")
                    .That(SetupProgressWindow.HasClickThroughStyle(hwnd)).IsTrue();
                Check.WithCustomMessage("WS_EX_NOACTIVATE is missing: the screen could take focus.")
                    .That(SetupProgressWindow.HasNoActivateStyle(hwnd)).IsTrue();
            }
            finally
            {
                window.Close();
            }
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
