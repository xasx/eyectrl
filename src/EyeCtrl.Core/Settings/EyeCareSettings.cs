namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Eye-care break reminder and exercise settings. See design spec §Settings.</summary>
public partial class EyeCareSettings : ObservableObject, IValidatable
{
    /// <summary>Master switch (opt-in for v1).</summary>
    [ObservableProperty] private bool _enabled = false;
    /// <summary>Cadence in minutes (20-20-20 default).</summary>
    [ObservableProperty] private int _intervalMinutes = 20;
    /// <summary>Natural-break suppression threshold in seconds.</summary>
    [ObservableProperty] private int _naturalBreakSeconds = 30;
    /// <summary>Per-exercise length in seconds.</summary>
    [ObservableProperty] private int _exerciseDurationSeconds = 45;
    /// <summary>Seconds the banner waits before auto-snoozing when ignored.</summary>
    [ObservableProperty] private int _bannerAutoSnoozeSeconds = 15;
    /// <summary>Postpone duration in minutes after a dismiss/snooze.</summary>
    [ObservableProperty] private int _snoozeMinutes = 5;
    /// <summary>Whether fatigue escalation may fire breaks early (gated off in v1).</summary>
    [ObservableProperty] private bool _fatigueMonitorEnabled = false;
    /// <summary>Fatigue score at which escalation fires (0.0–1.0).</summary>
    [ObservableProperty] private double _fatigueThreshold = 0.75;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(IntervalMinutes, 1, 120, nameof(IntervalMinutes)),
            ValidationHelper.ValidateRange(NaturalBreakSeconds, 5, 600, nameof(NaturalBreakSeconds)),
            ValidationHelper.ValidateRange(ExerciseDurationSeconds, 10, 180, nameof(ExerciseDurationSeconds)),
            ValidationHelper.ValidateRange(BannerAutoSnoozeSeconds, 5, 120, nameof(BannerAutoSnoozeSeconds)),
            ValidationHelper.ValidateRange(SnoozeMinutes, 1, 60, nameof(SnoozeMinutes)),
            ValidationHelper.ValidateRange(FatigueThreshold, 0.0, 1.0, nameof(FatigueThreshold))
        );
    }
}
