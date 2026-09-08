namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for locked repeat-click mode (gap #4, TD Control Continuous/RepeatClick).</summary>
public partial class RepeatSettings : ObservableObject, IValidatable
{
    private const int IntervalMinMs = 250;
    private const int IntervalMaxMs = 5000;
    private const int IntervalDefaultMs = 1000;
    private const int StopDwellMinMs = 200;
    private const int StopDwellMaxMs = 2000;
    private const int StopDwellDefaultMs = 600;

    /// <summary>Milliseconds between repeat ticks while repeat mode is active. Valid range: 250–5000.</summary>
    [ObservableProperty] private int _intervalMs = IntervalDefaultMs;

    /// <summary>Dwell time in milliseconds for the right-edge stop button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _stopButtonDwellMs = StopDwellDefaultMs;

    partial void OnIntervalMsChanging(int value)
    {
        if (value < IntervalMinMs || value > IntervalMaxMs)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(IntervalMs)} must be between {IntervalMinMs} and {IntervalMaxMs}");
    }

    partial void OnStopButtonDwellMsChanging(int value)
    {
        if (value < StopDwellMinMs || value > StopDwellMaxMs)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(StopButtonDwellMs)} must be between {StopDwellMinMs} and {StopDwellMaxMs}");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(IntervalMs, IntervalMinMs, IntervalMaxMs, nameof(IntervalMs)),
            ValidationHelper.ValidateRange(StopButtonDwellMs, StopDwellMinMs, StopDwellMaxMs, nameof(StopButtonDwellMs))
        );
    }
}
