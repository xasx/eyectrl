namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// B6's table-driven range-equality gate: every exit-dwell row this ticket adds, and every slider
/// it widens, spans EXACTLY its Core guard's bounds. The other span tests only assert a slider
/// stays INSIDE the guard, which is what let these five sliders stop short of the full range for
/// months. Equality is the claim the ticket makes, so it is the claim pinned here.
///
/// <para>
/// The table restates the guard bounds from <see cref="DwellSettings.Validate"/> and
/// <see cref="RepeatSettings.Validate"/> rather than reading them (the helpers do not expose
/// their bounds); a Core range change therefore reddens this table until it is updated on
/// purpose. The last assertion walks every value the stepper can reach and calls the row's own
/// Set, which is the crash path a slider past its guard takes on the UI thread
/// (<c>DwellSlider.Adjust</c> clamps to Min/Max then sets unguarded): a step that leaves the
/// allowed set — the 0-then-200 hole on the two new knobs, for example — fails here.
/// </para>
/// </summary>
public class SettingsDwellRangeEqualityTests
{
    private sealed record RowCase(
        string Source,
        string Id,
        double Min,
        double Max,
        Func<EyeCtrlSettings, double> Get,
        Action<EyeCtrlSettings, double> Set);

    private static ISettingsPageSource Source(SettingsPageContext ctx, string name) => name switch
    {
        "Dwell" => new DwellPageSource(ctx),
        "Advanced" => new AdvancedPageSource(ctx),
        "EyeCare" => new EyeCarePageSource(ctx),
        "General" => new GeneralPageSource(ctx),
        _ => throw new ArgumentException($"Unknown page source {name}", nameof(name)),
    };

    public static TheoryData<string> Cases() => new()
    {
        "d_fix", "d_act", "d_menu", "d_ManualSelection", "d_tool", "d_key", "d_pred", "d_rad",
        "d_cooldown", "d_resume", "d_manual_side", "av_drag_release", "av_drag_right_edge",
        "av_scroll_stop", "av_backdrop_hide", "av_repeat_stop", "ec_banner_dwell",
        "g_restart_notice",
    };

    private static RowCase CaseFor(string id) => id switch
    {
        // Widened to their Validate ranges (the table in the ticket).
        "d_fix" => new("Dwell", id, 100, 1000,
            s => s.Dwell.FixationDetectionMs, (s, v) => s.Dwell.FixationDetectionMs = (int)v),
        "d_act" => new("Dwell", id, 200, 2000,
            s => s.Dwell.ActivationButtonDwellMs, (s, v) => s.Dwell.ActivationButtonDwellMs = (int)v),
        "d_menu" => new("Dwell", id, 200, 2000,
            s => s.Dwell.ActionMenuDwellMs, (s, v) => s.Dwell.ActionMenuDwellMs = (int)v),
        "d_ManualSelection" => new("Dwell", id, 200, 2000,
            s => s.Dwell.ManualSelectionActionMenuDwellMs,
            (s, v) => s.Dwell.ManualSelectionActionMenuDwellMs = (int)v),
        "d_tool" => new("Dwell", id, 200, 2000,
            s => s.Dwell.ToolbarButtonDwellMs, (s, v) => s.Dwell.ToolbarButtonDwellMs = (int)v),
        "d_key" => new("Dwell", id, 300, 1500,
            s => s.Dwell.KeyboardKeyDwellMs, (s, v) => s.Dwell.KeyboardKeyDwellMs = (int)v),
        "d_pred" => new("Dwell", id, 300, 1500,
            s => s.Dwell.KeyboardPredictionDwellMs, (s, v) => s.Dwell.KeyboardPredictionDwellMs = (int)v),
        "d_rad" => new("Dwell", id, 10, 200,
            s => s.Dwell.FixationRadiusPixels, (s, v) => s.Dwell.FixationRadiusPixels = v),
        "d_cooldown" => new("Dwell", id, 100, 5000,
            s => s.Dwell.DwellCooldownMs, (s, v) => s.Dwell.DwellCooldownMs = (int)v),
        // NEW rows.
        "d_resume" => new("Dwell", id, 500, 3000,
            s => s.Dwell.ResumeDwellMs, (s, v) => s.Dwell.ResumeDwellMs = (int)v),
        "d_manual_side" => new("Dwell", id, 200, 2000,
            s => s.Dwell.ManualSelectionSideButtonDwellMs,
            (s, v) => s.Dwell.ManualSelectionSideButtonDwellMs = (int)v),
        "av_drag_release" => new("Advanced", id, 200, 2000,
            s => s.Dwell.DragReleaseButtonDwellMs, (s, v) => s.Dwell.DragReleaseButtonDwellMs = (int)v),
        "av_drag_right_edge" => new("Advanced", id, 200, 2000,
            s => s.Dwell.DragRightEdgeButtonDwellMs, (s, v) => s.Dwell.DragRightEdgeButtonDwellMs = (int)v),
        "av_scroll_stop" => new("Advanced", id, 200, 2000,
            s => s.Dwell.ScrollStopButtonDwellMs, (s, v) => s.Dwell.ScrollStopButtonDwellMs = (int)v),
        "av_backdrop_hide" => new("Advanced", id, 200, 2000,
            s => s.Dwell.BackdropHideButtonDwellMs, (s, v) => s.Dwell.BackdropHideButtonDwellMs = (int)v),
        "av_repeat_stop" => new("Advanced", id, 200, 2000,
            s => s.Repeat.StopButtonDwellMs, (s, v) => s.Repeat.StopButtonDwellMs = (int)v),
        // NEW knobs: 0 = follow the activation dwell, else 200–3000. The bounds cover the whole
        // allowed set; the walk below proves every 200 ms stop from 0 is accepted.
        "ec_banner_dwell" => new("EyeCare", id, 0, 3000,
            s => s.Dwell.EyeCareBannerDwellMs, (s, v) => s.Dwell.EyeCareBannerDwellMs = (int)v),
        "g_restart_notice" => new("General", id, 0, 3000,
            s => s.Dwell.RestartNoticeDwellMs, (s, v) => s.Dwell.RestartNoticeDwellMs = (int)v),
        _ => throw new ArgumentException($"No case for {id}", nameof(id)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheRow_SpansExactlyTheGuard_AndEveryStepperStopIsAccepted(string id)
    {
        var c = CaseFor(id);
        var settings = new EyeCtrlSettings();
        var slider = Source(TestSettingsPageContext.Build(settings), c.Source)
            .Build().SelectMany(p => p.Rows).OfType<SliderSpec>().Single(s => s.Id == id);

        Check.WithCustomMessage($"{id}: slider Min {slider.Min} must equal the guard's {c.Min}")
            .That(slider.Min).IsEqualTo(c.Min);
        Check.WithCustomMessage($"{id}: slider Max {slider.Max} must equal the guard's {c.Max}")
            .That(slider.Max).IsEqualTo(c.Max);

        slider.Set(c.Max);
        Check.That(c.Get(settings)).IsEqualTo(c.Max);
        slider.Set(c.Min);
        Check.That(c.Get(settings)).IsEqualTo(c.Min);

        for (var v = c.Min; v <= c.Max + 1e-9; v += slider.Step)
        {
            var value = v;
            Check.WithCustomMessage(
                    $"{id}: the stepper can reach {value}, which the Core guard rejects — the "
                    + "dwell slider clamps to its own Min/Max and then sets unguarded, so this "
                    + "crashes on the UI thread.")
                .ThatCode(() => slider.Set(value)).DoesNotThrow();
        }
    }

    /// <summary>
    /// F2 (review 01): <c>DwellSlider.Adjust</c> clamps but never snaps, so a stored value off the
    /// step grid (a hand edit — valid, because the guard accepts every 200–3000) minus one step
    /// can land in the 1–199 hole the two knobs' guard rejects. The row setter snaps anything
    /// below 200 to 0, so the stepper's own arithmetic must never throw from 300 or 250 either.
    /// </summary>
    [Theory]
    [InlineData("General", "g_restart_notice", 300)]
    [InlineData("General", "g_restart_notice", 250)]
    [InlineData("EyeCare", "ec_banner_dwell", 300)]
    [InlineData("EyeCare", "ec_banner_dwell", 250)]
    public void TheKnobRow_MinusFromAnOffGridStoredValue_SnapsToZero_AndNeverThrows(
        string source, string id, int start)
    {
        var settings = new EyeCtrlSettings();
        if (id == "g_restart_notice")
            settings.Dwell.RestartNoticeDwellMs = start;
        else
            settings.Dwell.EyeCareBannerDwellMs = start;
        var slider = Source(TestSettingsPageContext.Build(settings), source)
            .Build().SelectMany(p => p.Rows).OfType<SliderSpec>().Single(s => s.Id == id);

        // The exact arithmetic DwellSlider.Adjust runs, through the row's own Set.
        var minus = Math.Round(Math.Clamp(slider.Get() - slider.Step, slider.Min, slider.Max), slider.Decimals);

        Check.ThatCode(() => slider.Set(minus)).DoesNotThrow();
        Check.That(slider.Get()).IsEqualTo(0);
        Check.That(id == "g_restart_notice"
                ? settings.Dwell.EffectiveRestartNoticeDwellMs
                : settings.Dwell.EffectiveEyeCareBannerDwellMs)
            .IsEqualTo(settings.Dwell.ActivationButtonDwellMs);
    }

    /// <summary>
    /// F2's sweep, and the answer to "no other new or widened slider can produce a guard-rejected
    /// value": from EVERY stored value in the row's span — on the grid or a hand-edited one —
    /// one minus step must land on a value the setter accepts. For a contiguous guard this holds
    /// by construction; for the two 0-then-200 knobs it is exactly what the setter snap buys.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void FromEveryStoredValue_OneMinusStepNeverThrows(string id)
    {
        var c = CaseFor(id);
        var slider = Source(TestSettingsPageContext.Build(new EyeCtrlSettings()), c.Source)
            .Build().SelectMany(p => p.Rows).OfType<SliderSpec>().Single(s => s.Id == id);

        for (var stored = c.Min; stored <= c.Max; stored += 1)
        {
            slider.Set(stored);
            var minus = Math.Round(
                Math.Clamp(slider.Get() - slider.Step, slider.Min, slider.Max), slider.Decimals);
            var at = stored;
            Check.WithCustomMessage(
                    $"{id}: from stored value {at} the minus stepper lands on {minus}, which the "
                    + "row's Set rejects — the dispatcher catches it only as an unhandled crash.")
                .ThatCode(() => slider.Set(minus)).DoesNotThrow();
        }
    }
}
