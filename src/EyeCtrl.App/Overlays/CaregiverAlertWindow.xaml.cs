namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Localization;
using Serilog;

/// <summary>
/// The Phase-3 caregiver alert banner (design §10 resolution): a bottom-center banner shown while
/// high struggle or poor tracking stays sustained (the service drives visibility), fading ~1 s after
/// the condition clears. An app-owned window, NOT an OS notification — Windows notifications can be
/// silenced by Focus Assist / per-app settings, which is disqualifying for a caregiver safety channel
/// (owner decision, D1). Like <see cref="DriftNoticeWindow"/> it is click-through and
/// <see cref="OverlayWindow.ExcludeFromScreenCapture"/>=true, so it never intercepts gaze/mouse (no
/// dwell targets — zero mis-activation surface) and is not baked into the magnifier / action-menu
/// capture. The chime plays once per onset (D3).
/// </summary>
public partial class CaregiverAlertWindow : OverlayWindow, ICaregiverAlertPresenter
{
    private const double MarginDip = 48;
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));
    private bool _positioned;

    /// <summary>Creates the caregiver-alert window (click-through + excluded from screen capture, set before init).</summary>
    public CaregiverAlertWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void ShowCaregiverAlert(CaregiverAlertCondition condition)
    {
        Message.Text = MessageFor(StringTable.Current, condition);
        EnsurePositioned();
        // Cancel any in-flight fade and re-show at full opacity (the drift banner's re-show discipline:
        // a fade is started only by FadeOutCaregiverAlert, and removing a clock via BeginAnimation(dp, null)
        // does not raise the removed clock's Completed, so a re-show during a fade cannot leave a stale
        // Completed to hide the banner).
        Banner.BeginAnimation(UIElement.OpacityProperty, null);
        Banner.Opacity = 1.0;
        if (!IsVisible)
            base.Show();
        RaiseToTop();

        try
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Caregiver alert chime failed; the visual alert was shown.");
        }
    }

    /// <summary>The message shown for a reason, resolved from the given table. A table parameter
    /// rather than the process global so the mapping is pinnable without activating one (T06
    /// fixup-01 F1), like <see cref="TrackStatusWindow.QualityWord"/>.</summary>
    /// <param name="strings">The table to resolve from.</param>
    /// <param name="condition">The alert reason.</param>
    /// <returns>The banner message.</returns>
    internal static string MessageFor(StringTable strings, CaregiverAlertCondition condition) =>
        condition == CaregiverAlertCondition.SustainedPoorTracking
            ? strings.T("caregiver-alert.poor-tracking")
            : strings.T("caregiver-alert.struggling");

    /// <inheritdoc />
    public void FadeOutCaregiverAlert()
    {
        var fade = new DoubleAnimation(1.0, 0.0, FadeOut) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Hide();
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <inheritdoc />
    public void HideCaregiverAlert()
    {
        Banner.BeginAnimation(UIElement.OpacityProperty, null); // cancel any in-flight fade — down AT ONCE
        Hide();
    }

    private void EnsurePositioned()
    {
        if (_positioned)
            return;
        var area = SystemParameters.WorkArea; // DIPs, matching Window.Left/Top/Width/Height
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - MarginDip;
        _positioned = true;
    }
}
