namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Calibration settings for the eye tracker calibration routine.</summary>
public partial class CalibrationSettings : ObservableObject, IValidatable
{
    /// <summary>Number of calibration points displayed on screen. Valid range: 3–16.</summary>
    [ObservableProperty] private int _pointCount = 5;
    /// <summary>
    /// Number of IN-BAND gaze samples collected per calibration point. Valid range: 5–100.
    /// <para>
    /// Lowered from 30 on 2026-09-13. The number means something different now: samples only count
    /// while gaze is inside the band, and they accumulate across separate holds rather than needing
    /// one unbroken window. Measured on this user, sustained still-holds last a median 0.24 s — about
    /// 8 samples at 33 Hz — so 30 demanded roughly four successful holds on every target, while 15 is
    /// about two. The cost is a mean built from fewer samples; that is affordable precisely because
    /// they are now all on target, whereas 30 unbounded ones were not.
    /// </para>
    /// </summary>
    [ObservableProperty] private int _samplesPerPoint = 15;
    /// <summary>
    /// Minimum time in milliseconds a calibration target stays on screen before the session may
    /// advance. The real pacer is the user: a target advances once gaze has arrived and enough
    /// in-band post-settle samples are collected (see CalibrationPointCollector), so this is a
    /// visible floor, not a timer. Valid range: 200–3000.
    /// <para>
    /// Deliberately NOT changed for the jittery-user retune. It bounds how fast a target can leave
    /// once it is already finished, so it only ever affects a user who is succeeding; a user who
    /// struggles is gated by in-band sample count, never by this. Lowering it would have looked like
    /// part of the fix while changing nothing for the person it was meant to help.
    /// </para>
    /// </summary>
    [ObservableProperty] private int _targetHoldMs = 1000;
    /// <summary>
    /// Time in milliseconds to discard samples after gaze first enters the target area (settle period).
    /// Valid range: 0–500.
    /// <para>
    /// Raised from 200 on 2026-09-13: 200 ms is shorter than a saccade typically takes to settle, so
    /// the tail of the landing movement was being measured as if it were fixation. It costs nothing
    /// now that collection is paced by in-band sample count rather than by a clock — discarding more
    /// of the landing does not shorten the time available to collect.
    /// </para>
    /// </summary>
    [ObservableProperty] private int _settleWindowMs = 300;
    /// <summary>
    /// Radius of the calibration target DOT — the thing to aim at — in physical pixels. Valid range: 8–80.
    /// <para>
    /// This is not the tolerance. The tolerance is <see cref="EyeCtrl.Core.Calibration.CalibrationGeometry.BandRadiusPx"/>,
    /// drawn around the dot as a ring, and it is a great deal larger (240 px on a 3840×2400 display).
    /// Conflating the two is what the 2026-09-12 session suffered from: the user aimed at a 20 px dot
    /// with no indication that anything within 240 px counted, so "I cannot hit it" was a problem they
    /// did not actually have.
    /// </para>
    /// <para>
    /// Until 2026-09-13 this setting was read by nothing at all — the dot was a hard-coded 40×40
    /// ellipse in CalibrationWindow.xaml — so changing it had no effect.
    /// </para>
    /// </summary>
    [ObservableProperty] private int _targetRadiusPixels = 20;
    /// <summary>Hex color of the calibration target (e.g., "#A5F3FC").</summary>
    [ObservableProperty] private string _targetColor = SettingsDefaults.Accent;

    partial void OnPointCountChanging(int value)
    {
        if (value < 3 || value > 16)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(PointCount)} must be between 3 and 16");
    }

    partial void OnSamplesPerPointChanging(int value)
    {
        if (value < 5 || value > 100)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(SamplesPerPoint)} must be between 5 and 100");
    }

    partial void OnTargetHoldMsChanging(int value)
    {
        if (value < 200 || value > 3000)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(TargetHoldMs)} must be between 200 and 3000");
    }

    partial void OnSettleWindowMsChanging(int value)
    {
        if (value < 0 || value > 500)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(SettleWindowMs)} must be between 0 and 500");
    }

    partial void OnTargetRadiusPixelsChanging(int value)
    {
        if (value < 8 || value > 80)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(TargetRadiusPixels)} must be between 8 and 80");
    }

    partial void OnTargetColorChanging(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}$"))
            throw new ArgumentException(
                $"{nameof(TargetColor)} must be a valid hex color (e.g., #00FF00)",
                nameof(value));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(PointCount, 3, 16, nameof(PointCount)),
            ValidationHelper.ValidateRange(SamplesPerPoint, 5, 100, nameof(SamplesPerPoint)),
            ValidationHelper.ValidateRange(TargetHoldMs, 200, 3000, nameof(TargetHoldMs)),
            ValidationHelper.ValidateRange(SettleWindowMs, 0, 500, nameof(SettleWindowMs)),
            ValidationHelper.ValidateRange(TargetRadiusPixels, 8, 80, nameof(TargetRadiusPixels)),
            ValidationHelper.ValidateHexColor(TargetColor, nameof(TargetColor)));
    }
}
