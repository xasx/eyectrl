namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Prediction;
using NFluent;
using Xunit;

public class ModelsPageSourceTests
{
    // Smoke (Step 3): empty registry → one page, the "no models" label and the add-custom button.
    // The downloaded-state branch is covered by Build_DownloadedBundled_RendersActiveComboAndDeletePage.
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

    // Downloaded-state (Step 5): one bundled model on disk → active-model combo on page 1,
    // a manage button that navigates to page 2, and a delete button + back button on page 2.
    // The fake writes both model + tokenizer files so IsModelDownloaded/SelectableModels see them.
    [Fact]
    public void Build_DownloadedBundled_RendersActiveComboAndDeletePage()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithDownloadedBundled();
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
