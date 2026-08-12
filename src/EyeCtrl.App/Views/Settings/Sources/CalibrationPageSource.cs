namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels.Messages;

internal sealed class CalibrationPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public CalibrationPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public IReadOnlyList<PageSpec> Build()
    {
        var cal = _ctx.Settings.Calibration;
        return
        [
            new PageSpec([
                new SliderSpec("cal_points", "Calibration Points", 3, 16, 1, 0,
                    () => cal.PointCount, v => { cal.PointCount = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("cal_samples", "Samples per Point", 5, 100, 5, 0,
                    () => cal.SamplesPerPoint, v => { cal.SamplesPerPoint = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("cal_hold", "Target Hold (ms)", 200, 3000, 100, 0,
                    () => cal.TargetHoldMs, v => { cal.TargetHoldMs = (int)v; _ctx.ApplyAndSave(); }),
                new SliderSpec("cal_settle", "Settle Window (ms)", 0, 500, 25, 0,
                    () => cal.SettleWindowMs, v => { cal.SettleWindowMs = (int)v; _ctx.ApplyAndSave(); }),
            ]),
            new PageSpec([
                new LabelSpec(StatusText),
                new ButtonSpec("cal_run", "Calibrate Now",
                    () => { _ctx.Close(); _ctx.Messenger.Send(new CalibrationRequestedMessage()); },
                    ButtonKind.Default, Width: 300, Height: 80),
                new ButtonSpec("cal_clear", "Clear Calibration",
                    () => _ctx.ShowModal("Clear saved calibration?", () =>
                    {
                        _ctx.Messenger.Send(new CalibrationClearRequestedMessage());
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 80),
            ]),
        ];
    }

    private string StatusText()
    {
        var data = _ctx.Calibration.CurrentData;
        if (data != null)
        {
            return $"Last calibration: {data.CalibratedAt.LocalDateTime:g}  \u00B7  "
                + $"{data.ComputeMeanErrorPixels():F1} px ({data.ComputeQualityPercent():F1}%)";
        }
        var rejection = _ctx.Calibration.LastLoadRejection;
        return rejection == null ? "Last calibration: none" : $"Last calibration: {rejection}";
    }
}
