namespace EyeCtrl.App.Views.Settings;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Localization;
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
                new SliderSpec("cal_points",
                    StringTable.Current.T("settings.calibration.points.label"), 3, 16, 1, 0,
                    () => cal.PointCount, v => { cal.PointCount = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.calibration.points.description") },
                new SliderSpec("cal_samples",
                    StringTable.Current.T("settings.calibration.samples.label"), 5, 100, 5, 0,
                    () => cal.SamplesPerPoint, v => { cal.SamplesPerPoint = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.calibration.samples.description") },
                new SliderSpec("cal_hold",
                    StringTable.Current.T("settings.calibration.hold.label"), 200, 3000, 100, 0,
                    () => cal.TargetHoldMs, v => { cal.TargetHoldMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.calibration.hold.description") },
                new SliderSpec("cal_settle",
                    StringTable.Current.T("settings.calibration.settle.label"), 0, 500, 25, 0,
                    () => cal.SettleWindowMs, v => { cal.SettleWindowMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = StringTable.Current.T("settings.calibration.settle.description") },
                new ColorPickerSpec("cal_color",
                    StringTable.Current.T("settings.calibration.color.label"),
                    () => cal.TargetColor, v => { cal.TargetColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = StringTable.Current.T("settings.calibration.color.description") },
            ]),
            new PageSpec([
                new LabelSpec(StatusText),
                new ButtonSpec("cal_run",
                    StringTable.Current.T("settings.calibration.run.label"),
                    () => { _ctx.Close(); _ctx.Messenger.Send(new CalibrationRequestedMessage()); },
                    ButtonKind.Default, Width: 300, Height: 100),
                new ButtonSpec("cal_probe",
                    StringTable.Current.T("settings.calibration.probe.label"),
                    () => { _ctx.Close(); _ctx.Messenger.Send(new MeasureOnlyCalibrationRequestedMessage()); },
                    ButtonKind.Default, Width: 300, Height: 100),
                new LabelSpec(() => StringTable.Current.T("settings.calibration.probe.note")),
                new ButtonSpec("cal_clear",
                    StringTable.Current.T("settings.calibration.clear.label"),
                    () => _ctx.ShowModal(StringTable.Current.T("settings.calibration.clear.message"), () =>
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
            // own "g". The numbers are formatted by T() with the rendered language's provider, so
            // the explicit provider here is only the date helper's, read off the surface id the
            // surface-reference gate can see.
            var date = CalibrationDateText.Format(
                data.CalibratedAt.LocalDateTime,
                StringTable.Current.FormatProviderFor("settings"));
            return StringTable.Current.T(
                "settings.calibration.status.ok",
                date, data.ComputeMeanErrorPixels(), data.ComputeQualityPercent());
        }
        // A calibration that exists on disk but was refused at startup is otherwise indistinguishable
        // from never having calibrated — both leave CurrentData null and gaze running raw. Surface the
        // rejection reason so drifting dwell has a visible explanation without reading the log.
        var rejection = _ctx.Calibration.LastLoadRejection;
        return rejection == null
            ? StringTable.Current.T("settings.calibration.status.none")
            : StringTable.Current.T("settings.calibration.status.rejected", rejection);
    }
}
