namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class LayoutActionMenuPageSourceTests
{
    [Fact]
    public void Layout_OnePageWithPositionComboAndReset()
    {
        var rows = new LayoutPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows.OfType<ComboBoxSpec>().Single().Id).IsEqualTo("lay_pos");
        // NFluent 3.1.0 has no ContainsOnly on IEnumerable<T>; the repo idiom is ContainsExactly.
        // The Layout page has exactly one button (the Reset), so this also pins the count.
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Kind).ToArray()).ContainsExactly(ButtonKind.Reset);
    }

    // The Action Menu's one real setting is VisibleActions (which actions appear in the ring);
    // until 2026-08-17 the tab exposed only the reset, so the tab looked empty. One toggle per
    // InteractionAction, reflecting and editing the live list (the ring reads it at menu-open,
    // so a toggle applies on the next open — no restart), then the reset.
    [Fact]
    public void ActionMenu_OneTogglePerAction_ThenReset()
    {
        var settings = new EyeCtrl.Core.Settings.EyeCtrlSettings();
        settings.ActionMenu.VisibleActions.Remove(EyeCtrl.Core.Models.InteractionAction.PinNote);
        var rows = new ActionMenuPageSource(TestSettingsPageContext.Build(settings: settings))
            .Build().Single().Rows;

        var toggles = rows.OfType<ToggleSpec>().ToList();
        var expected = EyeCtrl.Core.Models.InteractionAction.GetValues<EyeCtrl.Core.Models.InteractionAction>();
        // Two rows per action since 2026-08-26: whether it is on at all, and whether it sits on
        // the ring first layer rather than behind More.
        Check.That(toggles.Select(t => t.Id))
            .ContainsExactly(expected.Select(a => "am_" + a)
                .Concat(expected.Select(a => "am_primary_" + a)).ToArray());
        Check.That(toggles.All(t => t.Label.Length > 0)).IsTrue();

        var pin = toggles.Single(t => t.Id == "am_" + EyeCtrl.Core.Models.InteractionAction.PinNote);
        Check.That(pin.Get()).IsFalse();
        pin.Set(true);
        Check.That(settings.ActionMenu.VisibleActions).Contains(EyeCtrl.Core.Models.InteractionAction.PinNote);
        pin.Set(false);
        Check.That(settings.ActionMenu.VisibleActions.Contains(EyeCtrl.Core.Models.InteractionAction.PinNote)).IsFalse();

        var buttons = rows.OfType<ButtonSpec>().ToList();
        Check.That(buttons.Select(b => b.Id)).ContainsExactly("am_reset");
    }
}
