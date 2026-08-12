namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class ModelsPageSourceTests
{
    // Smoke: empty registry → one page, the "no models" label and the add-custom button.
    [Fact]
    public void Build_EmptyRegistry_DoesNotThrow_AndHasAddButton()
    {
        var ctx = TestSettingsPageContext.Build(
            registry: ModelTestFakes.EmptyRegistry(),
            modelManagement: ModelTestFakes.NoOpManagement());
        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<LabelSpec>().First().Text()).Contains("No models downloaded");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("md_add");
    }
}
