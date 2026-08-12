namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class ModelsPageSourceTests
{
    // Smoke (Step 3): empty registry → one page, the "no models" label and the add-custom button.
    // The downloaded-state branch is covered by Build_DownloadedBundled_RendersActiveComboAndDeletePage.
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

    // Downloaded-state (Step 5): one bundled model on disk → active-model combo on page 1,
    // a manage button that navigates to page 2, and a delete button + back button on page 2.
    // The fake writes both model + tokenizer files so IsModelDownloaded/SelectableModels see them.
    [Fact]
    public void Build_DownloadedBundled_RendersActiveComboAndDeletePage()
    {
        var (registry, mgmt) = ModelTestFakes.WithDownloadedBundled();
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var pages = new ModelsPageSource(ctx).Build();
        Check.That(pages).HasSize(2);

        var p1 = pages[0].Rows;
        Check.That(p1.OfType<ComboBoxSpec>().Select(c => c.Id)).Contains(new[] { "md_active" });
        var p1Buttons = p1.OfType<ButtonSpec>().Select(b => b.Id);
        Check.That(p1Buttons).Contains(new[] { "md_add", "md_manage" });

        var p2 = pages[1].Rows;
        var p2Buttons = p2.OfType<ButtonSpec>().Select(b => b.Id);
        Check.That(p2Buttons).HasElementThatMatches(id => id.StartsWith("md_del_"));
        Check.That(p2Buttons).Contains(new[] { "md_back" });
    }
}
