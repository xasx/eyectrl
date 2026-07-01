namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class DragSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _settleDwellMs = 500;
    [ObservableProperty] private int _settleRadiusPixels = 40;
    [ObservableProperty] private string _trailColor = "#ff9800";
    [ObservableProperty] private double _trailOpacity = 0.7;

    partial void OnSettleDwellMsChanging(int value)
    {
        if (value < 200 || value > 2000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(SettleDwellMs)} must be between 200 and 2000");
    }

    partial void OnSettleRadiusPixelsChanging(int value)
    {
        if (value < 10 || value > 200)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(SettleRadiusPixels)} must be between 10 and 200");
    }

    partial void OnTrailOpacityChanging(double value)
    {
        if (value < 0.1 || value > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{nameof(TrailOpacity)} must be between 0.1 and 1.0");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(SettleDwellMs, 200, 2000, nameof(SettleDwellMs)),
            ValidationHelper.ValidateRange(SettleRadiusPixels, 10, 200, nameof(SettleRadiusPixels)),
            ValidationHelper.ValidateHexColor(TrailColor, nameof(TrailColor)),
            ValidationHelper.ValidateRange(TrailOpacity, 0.1, 1.0, nameof(TrailOpacity))
        );
    }
}
