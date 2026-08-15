namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Prediction;
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

    // ── md_add: the three branches that only report a failure to the user ──────────────────────
    // SettingsPageContext documents ReadClipboard/ShowError/Invoke as existing so exactly these are
    // headlessly testable, but nothing exercised them: the recording context hard-wired showError to
    // a no-op and readClipboard to "", which made every branch past the URL guard unreachable.

    private static void DwellAddCustom(RecordingSettingsPageContext ctx)
    {
        var page = new ModelsPageSource(ctx.Context).Build().First();
        page.Rows.OfType<ButtonSpec>().First(b => b.Id == "md_add").OnActivate!();
    }

    [Fact]
    public void AddCustom_WithNonHttpsClipboard_ReportsError_AndShowsNoModal()
    {
        using var scope = ModelTestFakes.Create();
        var registry = scope.EmptyRegistry();
        var ctx = new RecordingSettingsPageContext(
            registry: registry, modelManagement: scope.NoOpManagement(registry),
            clipboard: "not-a-url");

        DwellAddCustom(ctx);

        Check.That(ctx.Errors).HasSize(1);
        Check.That(ctx.Errors[0]).Contains("HTTPS");
        // No confirm modal: the user is told what is wrong instead of being asked to confirm a
        // download that cannot work.
        Check.That(ctx.LastModalOnYes).IsNull();
    }

    [Fact]
    public void AddCustom_WhenADownloadIsAlreadyRunning_ReportsError_AndShowsNoModal()
    {
        using var scope = ModelTestFakes.Create();
        var registry = scope.EmptyRegistry();
        var busy = scope.ManagementThatIsBusy();
        var ctx = new RecordingSettingsPageContext(
            registry: registry, modelManagement: busy,
            clipboard: "https://example.invalid/model.onnx");

        DwellAddCustom(ctx);

        Check.That(ctx.Errors).HasSize(1);
        Check.That(ctx.Errors[0]).Contains("already in progress");
        Check.That(ctx.LastModalOnYes).IsNull();
    }

    [Fact]
    public async Task AddCustom_WhenTheDownloadThrows_ReportsTheFailure()
    {
        // The branch this PR adds. Without the try/catch the exception escapes a fire-and-forget
        // Task.Run and becomes UNOBSERVED: App's TaskScheduler.UnobservedTaskException handler logs
        // it and marks it observed, so the process lives and the user — who dwelled the button and
        // is waiting — is told nothing. Verified this test fails when the catch is removed.
        using var scope = ModelTestFakes.Create();
        var registry = scope.EmptyRegistry();
        var throwing = scope.ManagementThatThrows("disk on fire");
        var ctx = new RecordingSettingsPageContext(
            registry: registry, modelManagement: throwing,
            clipboard: "https://example.invalid/model.onnx");

        DwellAddCustom(ctx);

        // The modal is shown first; the download only starts once the user confirms.
        Check.That(ctx.LastModalOnYes).IsNotNull();
        ctx.LastModalOnYes!();

        // The download runs on a Task.Run, so give the continuation a bounded moment to land.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (ctx.Errors.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Check.That(ctx.Errors).HasSize(1);
        Check.That(ctx.Errors[0]).Contains("disk on fire");
    }
}
