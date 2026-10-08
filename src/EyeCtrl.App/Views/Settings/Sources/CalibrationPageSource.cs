namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.ViewModels.Messages;

internal sealed class CalibrationPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public CalibrationPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var cal = _ctx.Settings.Calibration;
        return
        [
            new PageSpec([
                new SliderSpec("cal_points", "Calibration Points", 3, 16, 1, 0,
                    () => cal.PointCount, v => { cal.PointCount = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How many points the calibration run visits." },
                new SliderSpec("cal_samples", "Samples per Point", 5, 100, 5, 0,
                    () => cal.SamplesPerPoint, v => { cal.SamplesPerPoint = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Gaze samples gathered at each calibration point." },
                new SliderSpec("cal_hold", "Target Hold (ms)", 200, 3000, 100, 0,
                    () => cal.TargetHoldMs, v => { cal.TargetHoldMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Each target stays on screen at least this long." },
                new SliderSpec("cal_settle", "Settle Window (ms)", 0, 500, 25, 0,
                    () => cal.SettleWindowMs, v => { cal.SettleWindowMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Time at a target before its samples start counting." },
                new ColorPickerSpec("cal_color", "Target Color",
                    () => cal.TargetColor, v => { cal.TargetColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = "Color of the calibration target. Applies after a restart." },
            ]),
            new PageSpec([
                new LabelSpec(StatusText),
                new ButtonSpec("cal_run", "Calibrate Now",
                    () => { _ctx.Close(); _ctx.Messenger.Send(new CalibrationRequestedMessage()); },
                    ButtonKind.Default, Width: 300, Height: 100),
                new ButtonSpec("cal_probe", "Measure Gaze Offset",
                    () => { _ctx.Close(); _ctx.Messenger.Send(new MeasureOnlyCalibrationRequestedMessage()); },
                    ButtonKind.Default, Width: 300, Height: 100),
                new LabelSpec(() => "Measure gaze offset records where your gaze lands at five points. "
                    + "It changes nothing and always ends with \u201Crecorded\u201D; run it when you are rested."),
                new ButtonSpec("cal_clear", "Clear Calibration",
                    () => _ctx.ShowModal("Clear saved calibration?", () =>
                    {
                        _ctx.Messenger.Send(new CalibrationClearRequestedMessage());
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }

    private string StatusText()
    {
        var data = _ctx.Calibration.CurrentData;
        if (data != null)
        {
            // The date and both numbers follow the SURFACE they are drawn on — the Settings
            // window's Calibration tab — not the calibration overlay (T02 fixup-01 F2): T07 keys
            // this sentence as a settings.* key. The date itself goes through the pure helper
            // (fixup-02): English shows the unambiguous "2026-10-08 14:03", German the provider's
            // own "g". The provider is inline here too, so the surface-reference gate can see it.
            var date = CalibrationDateText.Format(
                data.CalibratedAt.LocalDateTime,
                EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("settings"));
            return string.Create(
                EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("settings"),
                $"Last calibration: {date}  \u00B7  {data.ComputeMeanErrorPixels():F1} px ({data.ComputeQualityPercent():F1}%)");
        }
        // A calibration that exists on disk but was refused at startup is otherwise indistinguishable
        // from never having calibrated — both leave CurrentData null and gaze running raw. Surface the
        // rejection reason so drifting dwell has a visible explanation without reading the log.
        var rejection = _ctx.Calibration.LastLoadRejection;
        return rejection == null ? "Last calibration: none" : $"Last calibration: {rejection}";
    }
}
