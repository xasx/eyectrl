namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for locked-action dwell-to-act mode (gap #4; TD Control Continuous analog, reworked D23–D28).</summary>
public partial class RepeatSettings : ObservableObject, IValidatable
{
    private const int DwellMinMs = 200;
    private const int DwellMaxMs = 2000;
    private const int DwellDefaultMs = 600;
    private const int StopDwellMinMs = 200;
    private const int StopDwellMaxMs = 2000;
    private const int StopDwellDefaultMs = 600;

    /// <summary>Milliseconds gaze must hold a target before the locked action fires there. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _dwellMs = DwellDefaultMs;

    /// <summary>Dwell time in milliseconds for the right-edge stop button. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _stopButtonDwellMs = StopDwellDefaultMs;

    partial void OnDwellMsChanging(int value)
    {
        if (value < DwellMinMs || value > DwellMaxMs)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(DwellMs)} must be between {DwellMinMs} and {DwellMaxMs}");
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
            ValidationHelper.ValidateRange(DwellMs, DwellMinMs, DwellMaxMs, nameof(DwellMs)),
            ValidationHelper.ValidateRange(StopButtonDwellMs, StopDwellMinMs, StopDwellMaxMs, nameof(StopButtonDwellMs))
        );
    }
}
