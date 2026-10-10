namespace EyeCtrl.App.Overlays;

using System;
using System.Windows;
using System.Windows.Media;
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
    /// <summary>The gap between the banner's bottom edge and the work area's bottom, in DIP.</summary>
    internal const double MarginDip = 48;
    private static readonly Duration FadeOut = new(TimeSpan.FromSeconds(1));

    /// <summary>Creates the caregiver-alert window (click-through + excluded from screen capture, set before init).</summary>
    public CaregiverAlertWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true;
        InitializeComponent();
        // The backstop for any resize after show (a DPI change, a later layout): the BOTTOM edge
        // is what stays put, so a taller window grows upward, never past the 48 DIP margin.
        SizeChanged += (_, e) => Anchor(SystemParameters.WorkArea, e.NewSize.Height);
    }

    /// <inheritdoc />
    public void ShowCaregiverAlert(CaregiverAlertCondition condition)
    {
        Prepare(MessageFor(StringTable.Current, condition), SystemParameters.WorkArea);
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

    /// <summary>
    /// Sets the message, sizes the window to it and anchors it — on EVERY show, because the
    /// message differs per condition. Class-C growth (T09): the window is 720 DIP wide and grows
    /// in HEIGHT only, with today's 120 as the minimum (<c>MinHeight</c> in the XAML), so English
    /// changes nothing and a longer translation is never clipped. Sized here, before
    /// <c>Show</c>, rather than by <c>SizeToContent</c>: the window is placed at its final height
    /// with no frame at the old one, and the sizing is headless-testable.
    /// </summary>
    /// <param name="message">The banner message.</param>
    /// <param name="workArea">The work area to anchor in, in DIP (<see cref="SystemParameters.WorkArea"/>).</param>
    internal void Prepare(string message, Rect workArea)
    {
        Message.Text = message;

        // A text change marks only the TextBlock dirty; its ancestors learn of it in the next
        // layout pass, which a hidden window never runs. Measure() early-returns on an element
        // that is not dirty and is asked for the same size again, so without this the content
        // would report the PREVIOUS message's height (a long message followed by a short one
        // kept the tall window). Invalidate the whole chain up to this window.
        for (DependencyObject? node = Message;
             node is not null && !ReferenceEquals(node, this);
             node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            (node as UIElement)?.InvalidateMeasure();
        }

        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Max(MinHeight, content.DesiredSize.Height);
        Anchor(workArea, Height);
    }

    /// <summary>
    /// Centres the window horizontally and puts its BOTTOM edge <see cref="MarginDip"/> above the
    /// work area's bottom: <c>Top = Bottom − height − 48</c> from the height actually in force,
    /// so a grown window rises instead of sliding under the taskbar.
    /// </summary>
    /// <param name="area">The work area, in DIP (matching <c>Left</c>/<c>Top</c>).</param>
    /// <param name="height">The window's height in DIP.</param>
    internal void Anchor(Rect area, double height)
    {
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - height - MarginDip;
    }
}
