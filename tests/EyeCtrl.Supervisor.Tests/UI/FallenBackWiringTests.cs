namespace EyeCtrl.Supervisor.Tests.UI;

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;
using NFluent;
using Xunit;

/// <summary>
/// fixup-01 F1: the per-episode wiring the composition root used to keep, driven headless through
/// <see cref="FallenBackAutoRetry.Attach"/> against a real but NEVER-SHOWN
/// <see cref="FallenBackDialog"/> - no Show() anywhere in this class, and no mutant may show one.
/// What review-01's surviving mutants broke is pinned here: a Close cancels the countdown (M1),
/// the countdown runs the SAME retry path as the Retry button (M3), the Dismiss button cancels on
/// its own subscription (M6), and the relaunch the shared path receives is PAUSED (M5's decision).
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
public class FallenBackWiringTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The shared retry path stand-in: records every (trigger, paused) it is handed.</summary>
    private sealed class Recorder
    {
        public readonly List<(FallenBackRetryTrigger Trigger, bool Paused)> Calls = new();
        public void Retry(FallenBackRetryTrigger trigger, bool paused) => Calls.Add((trigger, paused));
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

    [Fact]
    public void TheRetryButton_RunsTheSharedRetryPath_WithAPausedRelaunch()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            try
            {
                var recorder = new Recorder();
                var autoRetry = new FallenBackAutoRetry();
                autoRetry.Attach(dialog, recorder.Retry, T0);
                Check.That(autoRetry.IsCountdownArmed).IsTrue();
                Check.That(dialog.CountdownText.Visibility).IsEqualTo(Visibility.Visible);

                dialog.OnRetry();
                Check.That(recorder.Calls).HasSize(1);
                Check.That(recorder.Calls[0].Trigger).IsEqualTo(FallenBackRetryTrigger.DialogRetry);
                Check.That(recorder.Calls[0].Paused).IsTrue();
                Check.That(autoRetry.IsCountdownArmed).IsFalse();
                Check.That(dialog.CountdownText.Visibility).IsEqualTo(Visibility.Collapsed);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void TheCountdown_RunsTheSharedRetryPath_WithAPausedRelaunch()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            try
            {
                var recorder = new Recorder();
                var autoRetry = new FallenBackAutoRetry();
                autoRetry.Attach(dialog, recorder.Retry, T0);

                autoRetry.Tick(T0.AddSeconds(59));
                Check.That(recorder.Calls).IsEmpty();

                autoRetry.Tick(T0.AddSeconds(60));
                Check.That(recorder.Calls).HasSize(1);
                Check.That(recorder.Calls[0].Trigger).IsEqualTo(FallenBackRetryTrigger.Countdown);
                Check.That(recorder.Calls[0].Paused).IsTrue();

                autoRetry.Tick(T0.AddSeconds(61));
                Check.That(recorder.Calls).HasSize(1);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void AClose_CancelsTheCountdown()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            var closedRaised = false;
            dialog.Closed += (_, _) => closedRaised = true;
            try
            {
                var recorder = new Recorder();
                var autoRetry = new FallenBackAutoRetry();
                autoRetry.Attach(dialog, recorder.Retry, T0);

                dialog.Close();
                // The seam's cancel rides THIS event (the title-bar X raises no Dismissed). A
                // never-shown window must still raise it, or the close hook needs its own seam.
                Check.WithCustomMessage(
                    "Close() on a never-shown FallenBackDialog raised no Closed event.")
                    .That(closedRaised).IsTrue();

                autoRetry.Tick(T0.AddSeconds(60));
                autoRetry.Tick(T0.AddSeconds(600));
                Check.That(recorder.Calls).IsEmpty();
                Check.That(autoRetry.IsCountdownArmed).IsFalse();
            }
            finally
            {
                if (!closedRaised) dialog.Close();
            }
        });
    }

    [Fact]
    public void ADismiss_CancelsTheCountdown_EvenWithoutTheClose()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            var suppressClose = false;
            // Isolate the Dismiss button's OWN subscription from the window's Close: OnDismiss
            // raises Dismissed and then Close()s, and Closed would cancel all the same - that
            // masking is what let review-01's M6 survive. Cancel the close here so only the
            // Dismissed subscription can stop the countdown.
            dialog.Closing += (_, e) =>
            {
                if (suppressClose) e.Cancel = true;
            };
            try
            {
                var recorder = new Recorder();
                var autoRetry = new FallenBackAutoRetry();
                autoRetry.Attach(dialog, recorder.Retry, T0);

                suppressClose = true;
                dialog.OnDismiss();
                suppressClose = false;

                autoRetry.Tick(T0.AddSeconds(60));
                autoRetry.Tick(T0.AddSeconds(600));
                Check.That(recorder.Calls).IsEmpty();
                Check.That(autoRetry.IsCountdownArmed).IsFalse();
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void ASecondEpisode_WiresItsRetryButton_EvenWithoutACountdown()
    {
        // fixup-02 N2: the second episode's dialog waits for a click - and that click must still
        // WORK. A slip that wires the button only where the countdown armed (mutant M8) leaves the
        // user with a dead Retry and no way back to EyeCtrl until the Supervisor restarts.
        RunSta(() =>
        {
            var first = new FallenBackDialog(StringTable.EmbeddedEnglish);
            var second = new FallenBackDialog(StringTable.EmbeddedEnglish);
            try
            {
                var recorder = new Recorder();
                var autoRetry = new FallenBackAutoRetry();
                autoRetry.Attach(first, recorder.Retry, T0);
                Check.That(autoRetry.IsCountdownArmed).IsTrue();

                autoRetry.Tick(T0.AddSeconds(60));   // the session's one countdown fires
                Check.That(recorder.Calls).HasSize(1);

                autoRetry.Attach(second, recorder.Retry, T0.AddSeconds(70));
                Check.That(second.CountdownText.Visibility).IsEqualTo(Visibility.Collapsed);
                Check.That(autoRetry.IsCountdownArmed).IsFalse();

                recorder.Calls.Clear();
                second.OnRetry();
                Check.That(recorder.Calls).HasSize(1);
                Check.That(recorder.Calls[0].Trigger).IsEqualTo(FallenBackRetryTrigger.DialogRetry);
                Check.That(recorder.Calls[0].Paused).IsTrue();
            }
            finally
            {
                first.Close();
                second.Close();
            }
        });
    }
}
