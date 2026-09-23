namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using System.Net.Http;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
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

    // End-to-end repro of the user-visible flow "dwell Download → confirm the modal → model
    // lands on disk", against a fake HttpClient so nothing touches the network: the reported
    // symptom (2026-08-17, round 3) was "models do not download" with NO download-path log
    // lines at all — the service logs every failure it reaches, so a break anywhere between
    // the button and the service is silent. This pins the whole plumbing chain.
    [Fact]
    public async Task DwellDownload_ThenConfirm_DownloadsBothFiles_EndToEnd()
    {
        using var scope = ModelTestFakes.Create();
        var manifest = new ModelManifest
        {
            Models =
            {
                ["135M"] = new ModelEntry
                {
                    DisplayName = "SmolLM2 135M", Url = "https://example.invalid/m.onnx",
                    Sha256 = "",   // empty hash: verification skipped, so the fake bytes pass
                    SizeBytes = 10,
                    TokenizerUrl = "https://example.invalid/t.json", TokenizerSha256 = "",
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var registry = new ModelRegistryService(scope.Dir, manifest, settings);
        var download = new ModelDownloadService(scope.Dir, new HttpClient(new FakeOkHandler()));
        var mgmt = new ModelManagementService(registry, download, settings);
        var rec = new RecordingSettingsPageContext(registry: registry, modelManagement: mgmt);
        var src = new ModelsPageSource(rec.Context);

        var button = src.Build().Single().Rows.OfType<ButtonSpec>().Single(b => b.Id == "md_dl_135M");
        button.OnActivate();

        Check.That(rec.LastModalMessage).IsNotNull();
        Check.That(rec.LastModalMessage!).Contains("SmolLM2 135M");

        rec.LastModalOnYes!();   // the user confirms the modal

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline
               && rec.RebuildContentCount == 0 && rec.Errors.Count == 0)
            await Task.Delay(50);

        Check.WithCustomMessage(
                "The download never completed: rebuild=" + rec.RebuildContentCount
                + ", errors=" + (rec.Errors.Count == 0 ? "<none>" : string.Join(" | ", rec.Errors)))
            .That(rec.RebuildContentCount).IsStrictlyGreaterThan(0);
        Check.That(rec.Errors).IsEmpty();
        Check.That(mgmt.CurrentDownloadId).IsNull();   // terminal path clears the in-flight state
        Check.That(File.Exists(Path.Combine(scope.Dir, "smollm2-135m.onnx"))).IsTrue();
        Check.That(File.Exists(Path.Combine(scope.Dir, "smollm2-135m.tokenizer.json"))).IsTrue();

        // The service activation is worthless unless the PAGE applies it: ApplyAndSave persists
        // and raises SettingsChanged -> ReloadIfChanged, which rebuilds only when the settings
        // snapshot changed; RebuildContent alone redraws the list. Before this, a bundled
        // download activated the model in the service and nothing else happened — green service
        // test, dead feature.
        //
        // The property is "a successful bundled download reaches the save chain"; == 1 is
        // tighter and holds because one download makes exactly one save. If this ever fires on
        // an honest second save, the answer is IsStrictlyGreaterThan(0) — do not delete the pin.
        // These assertions read the LOCAL `settings`, the object the service writes; the
        // recording context builds its own Settings when none is passed, so `_ctx.Settings` is a
        // different instance and a future page-level assertion must not read it instead.
        Check.WithCustomMessage(
                "A successful bundled download must call ApplyAndSave (got "
                + rec.ApplyAndSaveCount + "): the persistence and the reload trigger for a "
                + "changed snapshot. RebuildContent alone changes nothing.")
            .That(rec.ApplyAndSaveCount).IsEqualTo(1);
        Check.That(settings.Keyboard.MLModelSize).IsEqualTo("135M");
        Check.That(registry.ActiveModelId).IsEqualTo("135M");
    }

    private sealed class FakeOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
            });
    }

    // Ticket 11, end to end at the level the reload actually consumes: a default install names
    // "360M" with no 360M on disk, and the user downloads exactly that model on the page. No
    // setting changes, so the old settings-only snapshot stayed equal and ReloadIfChanged
    // suppressed the rebuild — the LM stayed off until restart. The snapshot now carries the
    // disk term, so the same completion moves it and the decision fires. The negative control
    // is in the same run: a second capture with nothing changed is equal, which is what refuses
    // a guard weakened to always reload.
    [Fact]
    public async Task DownloadOfTheModelTheSettingNames_MovesTheSnapshot_WithoutASettingChanging()
    {
        using var scope = ModelTestFakes.Create();
        var manifest = new ModelManifest
        {
            Models =
            {
                ["360M"] = new ModelEntry
                {
                    DisplayName = "SmolLM2 360M", Url = "https://example.invalid/model.onnx",
                    Sha256 = "", SizeBytes = 10,
                    TokenizerUrl = "https://example.invalid/tokenizer.json", TokenizerSha256 = "",
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var registry = new ModelRegistryService(scope.Dir, manifest, settings);
        var download = new ModelDownloadService(scope.Dir, new HttpClient(new FakeOkHandler()));
        var mgmt = new ModelManagementService(registry, download, settings);
        var rec = new RecordingSettingsPageContext(registry: registry, modelManagement: mgmt);
        var src = new ModelsPageSource(rec.Context);

        // What LoadKeyboard stored at startup: the setting names 360M and its files are absent.
        var stored = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(stored.ActiveModelOnDisk).IsFalse();

        await DwellDownloadAndConfirmAsync(src, rec, "md_dl_360M");

        // The transition, end to end: no setting moved, the files are on disk, and the decision
        // the settings apply runs sees the difference.
        Check.That(settings.Keyboard.MLModelSize).IsEqualTo("360M");
        var current = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(current.ActiveModelOnDisk).IsTrue();
        // "Without a setting changing" made exact: only the disk term may differ from what
        // LoadKeyboard stored — every setting must be identical.
        Check.WithCustomMessage(
                "Only the disk term may differ after the download; every setting must be " +
                "identical to what LoadKeyboard stored.")
            .That(current with { ActiveModelOnDisk = false }).IsEqualTo(stored);
        Check.WithCustomMessage(
                "The page's ApplyAndSave must leave a snapshot the reload decision can see move; " +
                "otherwise the LM stays off until restart — the defect.")
            .That(KeyboardCoordinator.ShouldReload(stored, current)).IsTrue();

        // Negative control in the same run: nothing changed since that apply, so a reload now
        // would be a gratuitous rebuild.
        Check.WithCustomMessage("An unchanged apply must not reload.")
            .That(KeyboardCoordinator.ShouldReload(current, KeyboardStackSnapshot.Capture(settings, registry)))
            .IsFalse();
    }

    /// <summary>Dwells a model's download button, confirms the modal, and waits for the page's
    /// completion callback (RebuildContent). The download runs on the thread pool, so this is
    /// the same bounded poll the end-to-end test above uses.</summary>
    private static async Task DwellDownloadAndConfirmAsync(
        ModelsPageSource src, RecordingSettingsPageContext rec, string buttonId)
    {
        var button = src.Build().Single().Rows.OfType<ButtonSpec>().Single(b => b.Id == buttonId);
        button.OnActivate();
        Check.That(rec.LastModalOnYes).IsNotNull();
        rec.LastModalOnYes!();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && rec.RebuildContentCount == 0 && rec.Errors.Count == 0)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        Check.WithCustomMessage(
                "The download never completed: rebuild=" + rec.RebuildContentCount
                + ", errors=" + (rec.Errors.Count == 0 ? "<none>" : string.Join(" | ", rec.Errors)))
            .That(rec.RebuildContentCount).IsStrictlyGreaterThan(0);
        Check.That(rec.Errors).IsEmpty();
    }

    // The 2026-08-17 round-3 finding: downloads ran invisibly — the button kept saying
    // "[Download]", nothing showed progress, and a busy guard silently swallowed dwells —
    // which read to the user as "models do not download". The in-flight model now renders as
    // a live progress line, and dwelling another model's button while busy says WHY it no-ops.
    [Fact]
    public void WhileDownloading_InFlightModelShowsProgressLine_AndBusyIsLoud()
    {
        using var scope = ModelTestFakes.Create();
        var manifest = new ModelManifest
        {
            Models =
            {
                ["135M"] = new ModelEntry
                {
                    DisplayName = "SmolLM2 135M", Url = "https://example.invalid/a.onnx",
                    Sha256 = "", SizeBytes = 1024,
                    TokenizerUrl = "https://example.invalid/a.json", TokenizerSha256 = "",
                },
                ["other"] = new ModelEntry
                {
                    DisplayName = "Other One", Url = "https://example.invalid/b.onnx",
                    Sha256 = "", SizeBytes = 2048,
                    TokenizerUrl = "https://example.invalid/b.json", TokenizerSha256 = "",
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var registry = new ModelRegistryService(scope.Dir, manifest, settings);
        var mgmt = new BusyManagement(registry, new ModelDownloadService(scope.Dir), settings);
        mgmt.CurrentDownloadId = "135M";          // internal seam: inject a mid-download state
        mgmt.CurrentDownloadProgress = 0.42;
        var rec = new RecordingSettingsPageContext(registry: registry, modelManagement: mgmt);
        var src = new ModelsPageSource(rec.Context);

        var rows = src.Build().Single().Rows.ToList();

        var progressLine = rows.OfType<LabelSpec>()
            .Single(l => l.Text().Contains("⇣")).Text();
        Check.That(progressLine).Contains("SmolLM2 135M");
        Check.That(progressLine).Contains("42");   // 0.42 as a locale percentage
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Not.Contains("md_dl_135M");

        // The OTHER model still has its button; dwelling it while busy must SAY so.
        var other = rows.OfType<ButtonSpec>().Single(b => b.Id == "md_dl_other");
        other.OnActivate();
        Check.That(rec.Errors).HasElementThatMatches(e => e.Contains("already in progress"));
        Check.That(rec.LastModalMessage).IsNull();
    }

    // WYSIWYG dwell floor: no Models button may be declared shorter than the 100 DIP hit circle
    // every ButtonSpec registers (DwellButton's principle: callers may draw bigger, never
    // smaller). At 60 DIP the circle stuck out 20 DIP past the visual on both sides, and the
    // flush stack's circles overlapped — what the user dwelled was not what they saw. Covers
    // all five declaration sites: md_dl_* and md_add and md_manage (list view), md_del_* and
    // md_back (manage view).
    [Fact]
    public void EveryModelsButton_MeetsTheDwellFloor()
    {
        using var scope = ModelTestFakes.Create();
        var manifest = new ModelManifest
        {
            Models =
            {
                // Downloaded (files on disk): drives the combo + md_manage + the manage view.
                ["135M"] = new ModelEntry
                {
                    DisplayName = "Downloaded One", Url = "https://example.invalid/a.onnx",
                    Sha256 = "", SizeBytes = 1024,
                    TokenizerUrl = "https://example.invalid/a.json", TokenizerSha256 = "",
                },
                // Not downloaded: drives the md_dl_* download button.
                ["other"] = new ModelEntry
                {
                    DisplayName = "Other One", Url = "https://example.invalid/b.onnx",
                    Sha256 = "", SizeBytes = 2048,
                    TokenizerUrl = "https://example.invalid/b.json", TokenizerSha256 = "",
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var onnx = ModelConstants.ModelFileNames["135M"];
        File.WriteAllBytes(Path.Combine(scope.Dir, onnx), new byte[1]);
        File.WriteAllBytes(Path.Combine(scope.Dir, onnx.Replace(".onnx", ".tokenizer.json")), new byte[1]);
        var registry = new ModelRegistryService(scope.Dir, manifest, settings);
        var mgmt = new ModelManagementService(registry, new ModelDownloadService(scope.Dir), settings);
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);
        var src = new ModelsPageSource(ctx);

        var listRows = src.Build().Single().Rows.OfType<ButtonSpec>().ToList();
        Check.That(listRows.Select(b => b.Id))
            .Contains(new[] { "md_dl_other", "md_add", "md_manage" });

        src.Build().Single().Rows.OfType<ButtonSpec>().Single(b => b.Id == "md_manage").OnActivate();
        var manageRows = src.Build().Single().Rows.OfType<ButtonSpec>().ToList();

        foreach (var b in listRows.Concat(manageRows))
        {
            Check.WithCustomMessage(
                    $"Button {b.Id} is declared {b.Height} DIP tall — below the 100 DIP WYSIWYG "
                    + "dwell floor every settings dwell control must meet.")
                .That(b.Height).IsGreaterOrEqualThan(100);
        }
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

    // ── Failed-verdict repair row (verification ledger, Task 1) ────────────────────────────
    // A downloaded, hash-gated entry whose ledger verdict is Failed is UNUSABLE: the stack fell
    // back to the dictionary and the user sees no sign of it on this tab. The row below is the
    // tab's only signal, and its activation is the first remedy.

    [Fact]
    public void Build_FailedVerdict_RendersDangerRepairRow()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var rows = new ModelsPageSource(ctx).Build().Single().Rows;
        var repair = rows.OfType<ButtonSpec>().SingleOrDefault(b => b.Id == "md_fix_135M");

        Check.That(repair).IsNotNull();
        Check.That(repair!.Kind).IsEqualTo(ButtonKind.Danger);
        Check.That(repair.Label).Contains("model unusable");
        Check.That(repair.Label).Contains("dwell to re-download");
    }

    [Fact]
    public void Build_FailedRow_InFlightTakesPrecedence()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        mgmt.CurrentDownloadId = "135M";
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<ButtonSpec>().Where(b => b.Id == "md_fix_135M")).IsEmpty();
        Check.That(rows.OfType<LabelSpec>().Select(l => l.Text()))
            .HasElementThatMatches(t => t.Contains("downloading"));
    }

    [Fact]
    public void Build_VerifiedOrUnverified_KeepsTodayRows()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Verified);
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<ButtonSpec>().Where(b => b.Id.StartsWith("md_fix_"))).IsEmpty();
        Check.That(rows.OfType<LabelSpec>().Select(l => l.Text()))
            .HasElementThatMatches(t => t.Contains("✓ Downloaded"));
    }

    [Fact]
    public void RepairRow_Activation_WhileDownloading_RefusesBeforeTheModal()
    {
        // L3+N9: the repair arm must refuse a second download exactly like the download arm —
        // BEFORE showing the modal, with an error — not with an error after the user confirmed.
        // The busy fake is constructed over the SAME registry that carries the Failed verdict
        // (ManagementThatIsBusy builds its own empty registry, which would render no row).
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        var busy = new BusyManagement(registry, new ModelDownloadService(scope.Dir),
            new EyeCtrlSettings()); // IsDownloading => true (ModelTestFakes.cs:151)

        var modals = new List<string>();
        var ctx = TestSettingsPageContext.Build(
            registry: registry, modelManagement: busy,
            showModal: (title, _) => modals.Add(title)); // Build already takes this (cs :26)

        var repair = new ModelsPageSource(ctx).Build().Single().Rows
            .OfType<ButtonSpec>().Single(b => b.Id == "md_fix_135M");
        repair.OnActivate();

        Check.That(modals).IsEmpty(); // the guard fired before the modal
    }

    // R2: positive control for the guard test above — with an IDLE management the activation
    // records exactly one modal, titled as a re-download. This also pins the repair row's
    // modal wiring, which no other test covers.
    [Fact]
    public void RepairRow_Activation_IdleManagement_OpensExactlyOneReDownloadModal()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, mgmt) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);

        var modals = new List<string>();
        var ctx = TestSettingsPageContext.Build(
            registry: registry, modelManagement: mgmt,
            showModal: (title, _) => modals.Add(title));

        var repair = new ModelsPageSource(ctx).Build().Single().Rows
            .OfType<ButtonSpec>().Single(b => b.Id == "md_fix_135M");
        repair.OnActivate();

        Check.That(modals).HasSize(1);
        Check.That(modals[0]).Contains("Re-download");
    }
}
