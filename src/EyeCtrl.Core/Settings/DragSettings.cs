namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class DragSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _settleDwellMs = 500;
    [ObservableProperty] private int _settleRadiusPixels = 40;
    [ObservableProperty] private string _trailColor = "#ff9800";
    [ObservableProperty] private double _trailOpacity = 0.7;

    partial void OnSettleDwellMsChanged(int value)
    {
        if (value < 200)
            SettleDwellMs = 200;
        else if (value > 2000)
            SettleDwellMs = 2000;
    }

    partial void OnSettleRadiusPixelsChanged(int value)
    {
        if (value < 10)
            SettleRadiusPixels = 10;
        else if (value > 200)
            SettleRadiusPixels = 200;
    }

    partial void OnTrailOpacityChanged(double value)
    {
        if (value < 0.1)
            TrailOpacity = 0.1;
        else if (value > 1.0)
            TrailOpacity = 1.0;
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
