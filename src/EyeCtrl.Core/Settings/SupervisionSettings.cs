namespace EyeCtrl.Core.Settings;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Supervisor (watchdog) settings: crash-retry policy, hang thresholds, and fallback-AT ordering.</summary>
public partial class SupervisionSettings : ObservableObject, IValidatable
{
    /// <summary>Master switch for supervised process lifecycle (opt-in for v1).</summary>
    [ObservableProperty] private bool _enabled = false;
    /// <summary>Maximum restart attempts before giving up.</summary>
    [ObservableProperty] private int _maxRetries = 3;
    /// <summary>Rolling window in seconds over which crashes are counted toward MaxRetries.</summary>
    [ObservableProperty] private int _crashWindowSeconds = 120;
    /// <summary>Seconds without a heartbeat before the supervised process is considered hung.</summary>
    [ObservableProperty] private int _hangThresholdSeconds = 20;
    /// <summary>Grace period after launch during which hangs/crashes are not counted.</summary>
    [ObservableProperty] private int _startupGraceSeconds = 60;
    /// <summary>Preferred order in which assistive-technology fallbacks are attempted.</summary>
    [ObservableProperty] private List<string> _fallbackAtOrder = new() { "TDControl", "SnapCoreFirst", "WindowsEyeControl" };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(MaxRetries, 1, 20, nameof(MaxRetries)),
            ValidationHelper.ValidateRange(CrashWindowSeconds, 10, 3600, nameof(CrashWindowSeconds)),
            ValidationHelper.ValidateRange(HangThresholdSeconds, 5, 300, nameof(HangThresholdSeconds)),
            ValidationHelper.ValidateRange(StartupGraceSeconds, 10, 600, nameof(StartupGraceSeconds))
        );
    }
}
