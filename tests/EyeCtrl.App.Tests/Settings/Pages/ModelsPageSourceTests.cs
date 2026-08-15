namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class ModelsPageSourceTests
{
    // Smoke (Step 3): empty registry → one page, the "no models" label and the add-custom button.
    // The downloaded-state branch is covered by Build_DownloadedBundled_RendersListRows.
    [Fact]
    public void Build_EmptyRegistry_DoesNotThrow_AndHasAddButton()
    {
        using var scope = ModelTestFakes.Create();
        var registry = scope.EmptyRegistry();
        var ctx = TestSettingsPageContext.Build(
            registry: registry,
            modelManagement: scope.NoOpManagement(registry));
        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<LabelSpec>().First().Text()).Contains("No models downloaded");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("md_add");
    }

    // Downloaded-state (Step 5): one bundled model on disk → the list is a SINGLE page (one view =
    // one page; height re-pagination happens later, in the shell) holding the active-model combo,
    // the add-custom button, and the manage button that switches to the Manage view. The fake
    // writes both model + tokenizer files so IsModelDownloaded/SelectableModels see them.
    [Fact]
    public void Build_DownloadedBundled_RendersListRows()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled();
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var pages = new ModelsPageSource(ctx).Build();
        Check.That(pages).HasSize(1);

        var rows = pages[0].Rows;
        Check.That(rows.OfType<ComboBoxSpec>().Select(c => c.Id)).Contains(new[] { "md_active" });
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains(new[] { "md_add", "md_manage" });
    }

    // View machine: dwelling md_manage switches to the Manage view (and asks the shell to rebuild);
    // Build() then returns only delete buttons + md_back — never the list's md_add — and md_back
    // returns to the list rows. This is what lets Models opt into height-based auto-pagination:
    // navigation is view state, not page boundaries.
    [Fact]
    public void ManageView_RoundTrips_ThroughMdManageAndMdBack()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled();
        var rec = new RecordingSettingsPageContext(registry: registry, modelManagement: mgmt);
        var src = new ModelsPageSource(rec.Context);

        var manage = src.Build().Single().Rows.OfType<ButtonSpec>().Single(b => b.Id == "md_manage");
        manage.OnActivate();
        Check.That(rec.RebuildContentCount).IsEqualTo(1);   // GoTo rebuilds the whole content

        var manageRows = src.Build().Single().Rows.OfType<ButtonSpec>().Select(b => b.Id).ToList();
        Check.That(manageRows).HasElementThatMatches(id => id.StartsWith("md_del_"));
        Check.That(manageRows).Contains(new[] { "md_back" });
        Check.That(manageRows).Not.Contains("md_add");

        var back = src.Build().Single().Rows.OfType<ButtonSpec>().Single(b => b.Id == "md_back");
        back.OnActivate();
        Check.That(rec.RebuildContentCount).IsEqualTo(2);

        var listRows = src.Build().Single().Rows.OfType<ButtonSpec>().Select(b => b.Id).ToList();
        Check.That(listRows).Contains(new[] { "md_add", "md_manage" });
        Check.That(listRows).Not.Contains("md_back");
    }
}
