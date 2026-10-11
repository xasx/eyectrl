namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;

/// <summary>
/// T11's supervised live check, App side: each Step-1 crisis surface is shown ON THE REAL SCREEN,
/// in the shipped German, at the screen's real DPI, for a fixed time, then closed. Nothing is
/// simulated — no input, no cursor moves, no dialog to answer — the windows are click-through
/// overlays exactly as a caregiver would see them.
///
/// <para>
/// <b>Not a gate.</b> The class is <c>Category=UI</c> so CI's <c>Category!=UI</c> filter excludes
/// it, and every test SKIPS unless <c>EYECTRL_LIVE_CHECK=1</c>. It is only ever run with the
/// owner's explicit go-ahead, under supervision. The full sequence:
/// <c>dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~Step1LiveCheck"</c>
/// (see the class test order in the report; four windows, about 80 seconds in total).
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class Step1LiveCheckTests
{
    /// <summary>How long one window stays up. Long enough to find it by gaze and read it.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);

    [StaFact]
    public void TheCaregiverAlert_ShowsTheGermanPoorTrackingBanner()
    {
        RequireLiveCheck();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            var german = ActivateShippedGerman();
            var window = new CaregiverAlertWindow();
            window.Prepare(
                CaregiverAlertWindow.MessageFor(german, CaregiverAlertCondition.SustainedPoorTracking),
                SystemParameters.WorkArea);
            ShowFor(window, Hold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheCaregiverAlert_ShowsTheGermanStruggleBanner()
    {
        RequireLiveCheck();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            var german = ActivateShippedGerman();
            var window = new CaregiverAlertWindow();
            window.Prepare(
                CaregiverAlertWindow.MessageFor(german, CaregiverAlertCondition.SustainedStruggle),
                SystemParameters.WorkArea);
            ShowFor(window, Hold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheDriftNotice_ShowsTheGermanBanner()
    {
        RequireLiveCheck();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            _ = ActivateShippedGerman();
            var window = new DriftNoticeWindow();
            // The notice's real life is 8 s + a 1 s fade; show it twice so the owner has time to
            // land on it, then close. No production behavior is changed.
            var again = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(10) };
            again.Tick += (_, _) =>
            {
                again.Stop();
                window.ShowDriftNotice();
            };
            window.ShowDriftNotice();
            again.Start();
            ShowFor(window, Hold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheTrackStatusWindow_ShowsTheGermanWords()
    {
        RequireLiveCheck();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            // The XAML's {loc:T} labels resolve when the window loads, so the German table must be
            // current before the constructor runs; the values are the real ones a track-status
            // update would write.
            var german = ActivateShippedGerman();
            var window = new TrackStatusWindow();
            window.PositionTopRight();
            window.DistanceText.Text = german.T("track-status.distance.mm", 600.0);
            window.QualityText.Text = TrackStatusWindow.QualityWord(german, DistanceQuality.Good);
            ShowFor(window, Hold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The live-check gate: these tests are opt-in with <c>EYECTRL_LIVE_CHECK=1</c> because they
    /// paint windows on the owner's screen, which they control by gaze.
    /// </summary>
    private static void RequireLiveCheck() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("EYECTRL_LIVE_CHECK") is "1" or "true" or "TRUE",
            "The T11 live check paints real windows on the screen; set EYECTRL_LIVE_CHECK=1 with the owner's go-ahead.");

    /// <summary>Activates the shipped German table exactly as the de-DE App loads it.</summary>
    private static StringTable ActivateShippedGerman()
    {
        var warnings = new List<string>();
        var table = ShippedGerman.Load(warnings.Add);
        Check.WithCustomMessage("The shipped German warned while loading:\n  " + string.Join("\n  ", warnings))
            .That(warnings).IsEmpty();
        StringTable.Activate(table);
        return table;
    }

    /// <summary>
    /// Shows <paramref name="window"/> on the real screen and pumps its dispatcher for
    /// <paramref name="duration"/>, then closes it. A plain <c>Show</c> (never <c>ShowDialog</c>):
    /// the production overlays are non-modal, and the pump is what lets them lay out, draw and
    /// run their own timers.
    /// </summary>
    private static void ShowFor(Window window, TimeSpan duration)
    {
        window.Show();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
            window.Close();
        }
    }
}
