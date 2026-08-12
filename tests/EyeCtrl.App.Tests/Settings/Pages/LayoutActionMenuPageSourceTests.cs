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

    [Fact]
    public void ActionMenu_OneResetButton()
    {
        var rows = new ActionMenuPageSource(TestSettingsPageContext.Build()).Build().Single().Rows;
        Check.That(rows).HasSize(1);
        Check.That(rows[0]).IsInstanceOf<ButtonSpec>();
    }
}
