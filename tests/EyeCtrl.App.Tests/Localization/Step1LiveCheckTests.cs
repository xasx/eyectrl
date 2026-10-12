namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// T11's supervised live check, App side: each Step-1 crisis surface is shown ON THE REAL SCREEN,
/// in the shipped German, at the screen's real DPI, for a fixed time, then closed. Nothing is
/// simulated — no input, no cursor moves, no dialog to answer — the windows are click-through
/// overlays exactly as a caregiver would see them.
///
/// <para>
/// The banners go up through their PRODUCTION show paths (<c>ShowCaregiverAlert</c>,
/// <c>ShowDriftNotice</c>), because their content sits under <c>Opacity="0"</c> in XAML until
/// those run; the message, the opacity and the position all come from production. Before every
/// hold, <see cref="LiveCheck.AssertVisible"/> proves the window is visible, topmost, on-screen
/// and not transparent — the proof that caught the first run's invisible banner.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> The class is <c>Category=UI</c> so CI's <c>Category!=UI</c> filter excludes
/// it, and every test SKIPS unless <c>EYECTRL_LIVE_CHECK=1</c>. It is only ever run with the
/// owner's explicit go-ahead, under supervision, one case at a time:
/// <c>dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~&lt;case&gt;"</c>. The
/// order a viewer should see them in is the declaration order below: the struggle alert, the
/// poor-tracking alert, the drift notice, TrackStatus.
/// </para>
/// </summary>
[Collection("WpfApplication")]
[Trait("Category", "UI")]
public class Step1LiveCheckTests
{
    /// <summary>How long one window stays up. Long enough to find it by gaze and read it.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(20);

    [StaFact]
    public void TheCaregiverAlert_ShowsTheGermanStruggleBanner()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            _ = ActivateShippedGerman();
            var window = new CaregiverAlertWindow();
            LiveCheck.ShowAndHold(window, nameof(CaregiverAlertWindow) + " (struggle)", Hold,
                () => window.ShowCaregiverAlert(CaregiverAlertCondition.SustainedStruggle));
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheCaregiverAlert_ShowsTheGermanPoorTrackingBanner()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            _ = ActivateShippedGerman();
            var window = new CaregiverAlertWindow();
            LiveCheck.ShowAndHold(window, nameof(CaregiverAlertWindow) + " (poor tracking)", Hold,
                () => window.ShowCaregiverAlert(CaregiverAlertCondition.SustainedPoorTracking));
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheDriftNotice_ShowsTheGermanBanner()
    {
        LiveCheck.Require();
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            _ = ActivateShippedGerman();
            var window = new DriftNoticeWindow();
            // The notice's real life is 8 s + a 1 s fade; show it twice so the owner has time to
            // land on it. No production behavior is changed.
            var again = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(10) };
            again.Tick += (_, _) =>
            {
                again.Stop();
                window.ShowDriftNotice();
            };
            again.Start();
            LiveCheck.ShowAndHold(window, nameof(DriftNoticeWindow), Hold, window.ShowDriftNotice);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheTrackStatusWindow_ShowsTheGermanWords()
    {
        LiveCheck.Require();
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
            LiveCheck.ShowAndHold(window, nameof(TrackStatusWindow), Hold);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

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
}
