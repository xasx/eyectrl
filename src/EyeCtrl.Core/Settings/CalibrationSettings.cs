namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class CalibrationSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _pointCount = 5;
    [ObservableProperty] private int _samplesPerPoint = 30;
    [ObservableProperty] private int _targetHoldMs = 1000;
    [ObservableProperty] private int _settleWindowMs = 200;
    [ObservableProperty] private int _targetRadiusPixels = 20;
    [ObservableProperty] private string _targetColor = "#00FF00";

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
