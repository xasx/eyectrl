namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using Xunit;

public class KeyboardCoordinatorTests
{
    // The ctor needs a ModalInteractionState, a QuickActionsCoordinator, and a
    // ToolbarLifecycleCoordinator. The lifecycle contract tests below never call Initialize;
    // the ONE exception is the round-2 swap-chain test further down, which runs the real
    // Initialize (and therefore does touch the ToolbarLifecycleCoordinator). Plain constructed
    // instances suffice for both — the same shape the sibling coordinator tests use.
    private static KeyboardCoordinator NewCoordinator() =>
        new(new ModalInteractionState(),
            new QuickActionsCoordinator(),
            new ToolbarLifecycleCoordinator(new ModalInteractionState()));

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = NewCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized coordinator
        // must still dispose cleanly (no NullRef on _context). The PredictionStackFactory is
        // field-initialized, so its Dispose must run even when Initialize never did.
        NewCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = NewCoordinator();
        c.Dispose();
        c.Dispose();
    }

    [Fact]
    public void WordPredictor_ReturnsNull_BeforeLoadKeyboard()
    {
        // The 8-member public surface (re-exposed on EventCoordinator) includes WordPredictor and
        // Orchestrator; both must read null until LoadKeyboard builds a prediction stack.
        var c = NewCoordinator();
        Check.That(c.WordPredictor).IsNull();
        Check.That(c.Orchestrator).IsNull();
    }

    [Fact]
    public void Ctor_RejectsNullCollaborators()
    {
        var modal = new ModalInteractionState();
        var quickActions = new QuickActionsCoordinator();
        var toolbar = new ToolbarLifecycleCoordinator(modal);

        Check.ThatCode(() => new KeyboardCoordinator(null!, quickActions, toolbar)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new KeyboardCoordinator(modal, null!, toolbar)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new KeyboardCoordinator(modal, quickActions, null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void ApplyModalKeyboardState_FansOutToQuickActionsSummon_AndPublishesTheSharedFlag()
    {
        // Spec B2 (ledger #9): showing the keyboard suspends ONLY the quick-actions hot-edge
        // summon and publishes the shared ModalInteractionState flag the gaze-thread early-out
        // reads. The toolbar auto-hide suspension is deliberately gone — the toolbar un-pins
        // while the keyboard is up, and the keyboard's own escape is the edge-bar Close.
        var modal = new ModalInteractionState();

        var summon = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var quickActions = new QuickActionsCoordinator();
        quickActions.InjectSummonForTesting(summon);

        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
            toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        toolbar.InjectAutoHideForTesting(autoHide);

        var keyboard = new KeyboardCoordinator(modal, quickActions, toolbar);

        keyboard.ApplyModalKeyboardState(true);
        Check.That(modal.KeyboardVisible).IsTrue();
        Check.That(quickActions.IsSummonSuspended).IsTrue();
        Check.That(autoHide.IsSuspended).IsFalse();

        keyboard.ApplyModalKeyboardState(false);
        Check.That(modal.KeyboardVisible).IsFalse();
        Check.That(quickActions.IsSummonSuspended).IsFalse();
        Check.That(autoHide.IsSuspended).IsFalse();
    }

    // ---- Emoji skin-tone persistence (issue #58) -------------------------------------------
    // EmojiGridManagerTests already covers the event-raising half (SkinToneChanged fires with a
    // clamped value). What was untested is what happens after: that the tone reaches settings, is
    // actually written to disk, and refreshes the reload snapshot.

    [Fact]
    public void ApplyEmojiSkinTone_WritesToneIntoSettings()
    {
        var settings = new EyeCtrlSettings();
        using var temp = new TempSettingsFile();

        KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 5, modelRegistry: null);

        Check.That(settings.Keyboard.EmojiSkinTone).IsEqualTo(5);
    }

    [Fact]
    public void ApplyEmojiSkinTone_ReturnsSnapshotCapturedAfterTheWrite()
    {
        // The ordering guard. ReloadIfChanged compares the stored snapshot against a fresh capture,
        // so a snapshot taken BEFORE the write would differ from the live settings and trigger a
        // full keyboard + prediction-stack rebuild on the next settings-apply.
        var settings = new EyeCtrlSettings();
        using var temp = new TempSettingsFile();
        var registry = NewRegistry(settings);

        var snapshot = KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 3, registry);

        Check.That(snapshot.EmojiSkinTone).IsEqualTo(3);
        Check.That(snapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings, registry));
    }

    [Fact]
    public async Task ApplyEmojiSkinTone_PersistsToneToDisk()
    {
        // The half issue #58 called out as untested: the tone must survive a restart. Drives the
        // real SettingsService against a temp file rather than asserting a call was made.
        var settings = new EyeCtrlSettings();
        using var temp = new TempSettingsFile();

        KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 4, modelRegistry: null);

        var loaded = await temp.WaitForToneAsync(4, TestContext.Current.CancellationToken);
        Check.That(loaded.Keyboard.EmojiSkinTone).IsEqualTo(4);
    }

    // ---- The snapshot's filesystem term (ticket 11) ------------------------------------------
    // KeyboardStackSnapshot carries whether the active model's files are on disk so a download of
    // the model MLModelSize already names (no setting changes) still moves the snapshot and
    // reloads the stack. The term is computed once, inside Capture; the registry is a REQUIRED
    // parameter so every capture site — including the two static helpers below — has to supply the
    // live one. These tests drive both halves: the decision, and the helpers that must not drift.

    [Fact]
    public void ShouldReload_IsFalseWhenNothingMoved_AndTrueWhenTheActiveModelAppears()
    {
        var settings = new EyeCtrlSettings();               // MLModelSize defaults to "360M"
        var registry = NewRegistry(settings);
        var stored = KeyboardStackSnapshot.Capture(settings, registry);   // startup: files absent

        // Negative control: an apply with nothing changed must not reload. This is the assertion
        // that refuses the worst implementation — a guard weakened to always reload would rebuild
        // the layout (resetting dwell under the user) and load the ONNX model on every slider step.
        Check.WithCustomMessage(
                "An unchanged apply must not reload: the guard is what keeps a slider step from " +
                "rebuilding the keyboard and loading the model.")
            .That(KeyboardCoordinator.ShouldReload(stored, KeyboardStackSnapshot.Capture(settings, registry)))
            .IsFalse();

        // Positive control, same run: the same settings, files now on disk (planted AFTER the
        // registry was built, so the cached entry.IsDownloaded stays false) -> the decision turns
        // true. This is the defect's transition: no setting changed.
        PutActiveModelOnDisk(registry);
        Check.WithCustomMessage(
                "Downloading the model MLModelSize already names moves the snapshot via its disk " +
                "term; the decision must see it or the LM stays off until restart.")
            .That(KeyboardCoordinator.ShouldReload(stored, KeyboardStackSnapshot.Capture(settings, registry)))
            .IsTrue();
    }

    [Fact]
    public void ApplyHelpers_CarryTheLiveDiskTerm_NotAConstant()
    {
        // The two static capture helpers have no registry of their own; the required parameter is
        // what makes each caller pass the live one. A helper that ignored it (null or a constant)
        // would refresh the stored snapshot with a term that disagrees with the next
        // ReloadIfChanged capture — a phantom diff that rebuilds the whole keyboard on the
        // following apply.
        var settings = new EyeCtrlSettings { Keyboard = { ScreenEdge = "Bottom" } };
        using var temp = new TempSettingsFile();
        var registry = NewRegistry(settings);
        PutActiveModelOnDisk(registry);   // planted AFTER construction: cached IsDownloaded stays false

        var toneSnapshot = KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 3, registry);
        Check.That(toneSnapshot.ActiveModelOnDisk).IsTrue();
        Check.That(toneSnapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings, registry));

        var (_, edgeSnapshot) = KeyboardCoordinator.ApplyKeyboardEdge(settings, temp.Service, registry);
        Check.That(edgeSnapshot.ActiveModelOnDisk).IsTrue();
        Check.That(edgeSnapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings, registry));
    }

    // ---- The snapshot's verification-verdict term ---------------------------------------------
    // The 17th snapshot member: the active model's ledger verdict, read live in Capture under
    // the same spelling rules as ActiveModelOnDisk (no active model / "Off" / files absent ->
    // the neutral term). These three drive the live read and every neutral spelling.

    [Fact]
    public void Capture_ReadsActiveModelVerdict_Live()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };

        var snap = KeyboardStackSnapshot.Capture(settings, registry);

        Check.That(snap.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    [Fact]
    public void Capture_NoModel_NeutralVerdictTerm()
    {
        using var scope = ModelTestFakes.Create();
        var snap = KeyboardStackSnapshot.Capture(new EyeCtrlSettings(), scope.EmptyRegistry());
        Check.That(snap.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }

    [Fact]
    public void Capture_OffOrEmptyModelSize_NeutralVerdictTerm()
    {
        using var scope = ModelTestFakes.Create();
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "Off" } };
        var snap = KeyboardStackSnapshot.Capture(settings, scope.EmptyRegistry());
        Check.That(snap.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }

    // ---- Swap-time snapshot PATCH (the repair reload path) ------------------------------------
    // When a background build swaps in, the handler PATCHES the stored snapshot's verdict term
    // with the verdict THAT build reported — never re-captures. These drive the full M2
    // startup-repair sequence over a real registry (each step changes real ledger state), and
    // the model-mismatch guard.

    [Fact]
    public void SwapSequence_StartupRepairCycle_MovesStoredExactlyAsTheSpecRequires()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };

        // 1. Seed (LoadKeyboard's :205 capture): no verdict yet.
        var stored = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(stored.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);

        // 2. Background build finishes corrupt -> swap publishes ("135M", Failed).
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        stored = KeyboardCoordinator.WithSwappedVerdict(
            stored, ("135M", ModelVerificationOutcome.Failed));
        Check.That(stored.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);

        // 3. First apply after startup: compare capture reads the same ledger state —
        //    NO reload (the N3 spurious-reload regression).
        Check.That(KeyboardCoordinator.ShouldReload(
            stored, KeyboardStackSnapshot.Capture(settings, registry))).IsFalse();

        // 4. Repair: the download reports Verified into the ledger; the next apply's compare
        //    capture sees Failed != Verified -> RELOAD (the C1 repair path).
        registry.ReportVerification("135M", ModelVerificationOutcome.Verified);
        Check.That(KeyboardCoordinator.ShouldReload(
            stored, KeyboardStackSnapshot.Capture(settings, registry))).IsTrue();

        // 5. A swap-to-null (prediction disabled) carries no verdict -> the stored Failed term
        //    is KEPT, not refreshed from the ledger (plan review C4a).
        var kept = KeyboardCoordinator.WithSwappedVerdict(stored, null);
        Check.That(kept.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    [Fact]
    public void WithSwappedVerdict_VerdictForADifferentModel_IsIgnored()
    {
        // The MODEL-ID check is the discriminating guard here: the active model's files ARE on
        // disk (so I1's !ActiveModelOnDisk guard passes) and only the id mismatch refuses the
        // patch. Over EmptyRegistry both guards would refuse, and the id check could be deleted
        // without this test noticing (re-review of the I1 wave).
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var stored = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(stored.ActiveModelOnDisk).IsTrue();   // the disk guard does NOT fire here

        var merged = KeyboardCoordinator.WithSwappedVerdict(
            stored, ("360M", ModelVerificationOutcome.Verified));

        Check.That(merged).IsEqualTo(stored);
    }

    // The DISK guard's own pin (the other half of the same early-return): the verdict's model
    // id MATCHES the stored active model, but the files are absent, so only !ActiveModelOnDisk
    // refuses the patch — I1's rule that a verdict the compare path cannot read back must
    // never be written.
    [Fact]
    public void WithSwappedVerdict_MatchingModelIdButNotOnDisk_IsIgnored()
    {
        using var scope = ModelTestFakes.Create();
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var stored = KeyboardStackSnapshot.Capture(settings, scope.EmptyRegistry());
        Check.That(stored.ActiveModelOnDisk).IsFalse();

        var merged = KeyboardCoordinator.WithSwappedVerdict(
            stored, ("135M", ModelVerificationOutcome.Failed));

        Check.That(merged).IsEqualTo(stored);
    }

    [Fact]
    public void BeforeFirstSwap_AnApplyAgainstTheSeedSnapshot_DoesNotReload()
    {
        // Spec O2a: "no reload for an apply that lands before the first swap". The seed from
        // LoadKeyboard is a plain Capture — it reads the ledger live at seed time — and nothing
        // patches the stored verdict term until the first swap lands. An apply in that window
        // therefore compares the seed's verdict against the same live ledger state and must see
        // NO diff (a spurious reload here would rebuild the layout and reload the model under
        // the user mid-session). Same arrange as the M2 sequence above, minus the swap step.
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };

        var stored = KeyboardStackSnapshot.Capture(settings, registry);   // the LoadKeyboard seed
        Check.That(stored.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);

        Check.That(KeyboardCoordinator.ShouldReload(
            stored, KeyboardStackSnapshot.Capture(settings, registry))).IsFalse();
    }

    // ---- I1: the tokenizer-absent perpetual reload loop ----------------------------------------
    // Build's REPORT gate must equal the snapshot's READ gate: ActiveModelVerificationOf reads
    // the ledger only when IsModelDownloaded(id) — BOTH files on disk. Pre-fix, a model file
    // present + hash-gated + tokenizer ABSENT classified Failed; the swap patched Failed into
    // the stored snapshot while every compare capture read NotYetVerified (IsModelDownloaded
    // false), so ShouldReload fired on EVERY settings apply — each one reloading the keyboard
    // under the user and re-hashing a multi-GB model in the background, forever. Reachable
    // when a download's tokenizer phase is interrupted (the model file is moved into place
    // first) or the tokenizer is deleted externally. Both halves are pinned: the report gate
    // (first assertion) and the patch guard (last two — driven with the Failed a
    // mis-reporting build would hand the coordinator on model-id match alone).

    [Fact]
    public void TokenizerAbsentModel_ReportsNothing_AndCannotReloadLoop()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        // The interrupted-download state: model in place, tokenizer not.
        File.Delete(registry.GetTokenizerPath("135M"));
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };

        // A sync Rebuild classifies through the same Build the background mode runs.
        new PredictionStackFactory().Rebuild(settings, AppDomain.CurrentDomain.BaseDirectory,
            scope.Dir, registry);

        // Half 1 — the report gate: nothing was reported (a tokenizer-absent model's Models
        // row is a Download row, not a repair row). PRE-FIX: Failed is reported and the loop
        // starts here.
        Check.WithCustomMessage(
                "A tokenizer-absent model must report NOTHING: the compare path reads the " +
                "ledger only when IsModelDownloaded, so a Failed here can never be read back.")
            .That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);

        // The loop-relevant sequence: the seed (LoadKeyboard's capture), the swap patch with
        // the verdict the build reported (null — it reported nothing), the apply's compare.
        var stored = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(stored.ActiveModelOnDisk).IsFalse();
        Check.That(stored.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        stored = KeyboardCoordinator.WithSwappedVerdict(stored, null);
        Check.WithCustomMessage(
                "An unchanged apply must not reload: pre-fix this was the loop — every apply " +
                "rebuilt the keyboard and re-hashed the model, forever.")
            .That(KeyboardCoordinator.ShouldReload(
                stored, KeyboardStackSnapshot.Capture(settings, registry)))
            .IsFalse();

        // Half 2 — the patch guard: even the Failed a mis-reporting build WOULD hand the
        // coordinator must not move the stored term while the model is not fully on disk.
        // PRE-FIX (patch half alone reverted): the term moves to Failed, the compare still
        // reads NotYetVerified, and ShouldReload turns true — the loop.
        var patched = KeyboardCoordinator.WithSwappedVerdict(
            stored, ("135M", ModelVerificationOutcome.Failed));
        Check.WithCustomMessage(
                "WithSwappedVerdict must never write a verdict term the compare path cannot " +
                "read back (ActiveModelOnDisk false -> the compare reads NotYetVerified).")
            .That(patched).IsEqualTo(stored);
        Check.That(KeyboardCoordinator.ShouldReload(
            patched, KeyboardStackSnapshot.Capture(settings, registry))).IsFalse();
    }

    // ---- Non-swap verdict preservation (plan review C4, the missed writers) --------------------
    // The swap handler patches and never re-captures, but two OTHER writers assign a FULL fresh
    // Capture to _keyboardSnapshot: the edge flip and the skin-tone change. Their own settings
    // terms genuinely moved, yet Capture also re-reads the verdict term from the ledger LIVE —
    // so a repair download's Verified landing between the worker's ReportVerification and the
    // ApplyAndSave continuation would be absorbed while the running stack is still the Failed
    // one: the next apply compares equal, skips the reload, and the LM stays dictionary-only
    // while the Models row shows the download verified. Both handlers now compose
    // WithPreservedVerdict(_keyboardSnapshot, <seam>); this drives each half exactly as its
    // handler drives it (the handlers themselves are Context-bound and not headless-reachable,
    // same accepted residual as the WithSwappedVerdict wiring).

    [Fact]
    public void NonSwapReCaptures_PreserveTheStoredVerdict_NotTheLedgersMidWindowState()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M", ScreenEdge = "Bottom" } };

        // Seed: what the swap patch would have stored — the running stack's own Failed verdict.
        var stored = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(stored.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);

        // The download writer lands mid-window: the ledger flips BEFORE the repair reload.
        registry.ReportVerification("135M", ModelVerificationOutcome.Verified);

        // The user changes skin tone / flips the keyboard edge inside that window — the two
        // handler seams, merged exactly as the handlers merge.
        using var temp = new TempSettingsFile();
        var toneSnapshot = KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 3, registry);
        var mergedTone = KeyboardCoordinator.WithPreservedVerdict(stored, toneSnapshot);
        Check.That(mergedTone.EmojiSkinTone).IsEqualTo(3);              // the moved term IS adopted
        Check.That(mergedTone.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);               // the verdict is NOT

        var (_, edgeSnapshot) = KeyboardCoordinator.ApplyKeyboardEdge(settings, temp.Service, registry);
        var mergedEdge = KeyboardCoordinator.WithPreservedVerdict(stored, edgeSnapshot);
        Check.That(mergedEdge.ScreenEdge).IsEqualTo("Top");             // the flip IS adopted
        Check.That(mergedEdge.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);

        // The payoff: with the verdict preserved, the next apply's live compare still sees
        // Failed != Verified and fires the repair reload (the compare path MUST keep reading
        // the ledger live — that liveness is what makes the repair reload fire).
        Check.That(KeyboardCoordinator.ShouldReload(
            mergedEdge, KeyboardStackSnapshot.Capture(settings, registry))).IsTrue();
    }

    // Round-2 N2: the on-disk half of WithPreservedVerdict — the same unreadable-term rule
    // WithSwappedVerdict enforces (see its disk guard above). If the tokenizer alone is
    // deleted after a Verified verdict, the non-swap writers' fresh capture is off-disk and
    // carries the neutral term by construction; carrying the STORED Verified onto it would
    // store a term ActiveModelVerificationOf can never read back (it reads the ledger only
    // when IsModelDownloaded), so every later apply compares unequal — one spurious reload
    // before the term settles. The merged snapshot must instead equal the fresh capture.
    [Fact]
    public void NonSwapReCaptures_OffDiskFreshCapture_TakesTheFreshNeutralTerm()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };

        // Seed while fully on disk: the ledger's Verified is readable, so the stored term is
        // genuinely Verified (the state the swap handler would have left behind).
        registry.ReportVerification("135M", ModelVerificationOutcome.Verified);
        var stored = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(stored.ActiveModelOnDisk).IsTrue();
        Check.That(stored.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Verified);

        // The tokenizer file alone is deleted after the verdict; the edge flip / skin-tone
        // writer then re-captures: OnDisk false, verdict term neutral.
        File.Delete(registry.GetTokenizerPath("135M"));
        var fresh = KeyboardStackSnapshot.Capture(settings, registry);
        Check.That(fresh.ActiveModelOnDisk).IsFalse();
        Check.That(fresh.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);

        var merged = KeyboardCoordinator.WithPreservedVerdict(stored, fresh);

        Check.That(merged).IsEqualTo(fresh); // no unreadable Verified carried onto the term
        Check.That(merged.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        // The payoff: the stored term is readable again — an unchanged apply compares equal
        // instead of reloading once over a term the compare path can never see.
        Check.That(KeyboardCoordinator.ShouldReload(
            merged, KeyboardStackSnapshot.Capture(settings, registry))).IsFalse();
    }

    /// <summary>A registry on a unique temp dir whose manifest lists "360M", with NO files on
    /// disk until <see cref="PutActiveModelOnDisk"/> plants them.</summary>
    private static ModelRegistryService NewRegistry(EyeCtrlSettings settings) =>
        new(TempTestDirectory.Create("keyboard-coordinator"),
            new ModelManifest
            {
                Models =
                {
                    ["360M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 360M",
                        Url = "https://example.invalid/model.onnx",
                        TokenizerUrl = "https://example.invalid/tokenizer.json",
                    },
                },
            },
            settings);

    /// <summary>Plants the active model's files through the registry's own paths, AFTER the
    /// registry exists — the discriminator against the stale <c>entry.IsDownloaded</c> flag.</summary>
    private static void PutActiveModelOnDisk(ModelRegistryService registry)
    {
        File.WriteAllText(registry.GetModelPath("360M"), "onnx");
        File.WriteAllText(registry.GetTokenizerPath("360M"), "{}");
    }

    /// <summary>
    /// A real <see cref="SettingsService"/> over a throwaway file. SaveDebounced is a 500ms
    /// debounce onto the thread pool, so the assertion polls for the value the way
    /// SettingsServiceDebounceTests does — a fixed delay flakes on a loaded runner.
    /// </summary>
    private sealed class TempSettingsFile : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), $"eyectrl_skintone_{Guid.NewGuid()}.json");

        public SettingsService Service { get; }

        public TempSettingsFile() => Service = new SettingsService(_path);

        public async Task<EyeCtrlSettings> WaitForToneAsync(int expected, CancellationToken ct)
        {
            for (var i = 0; i < 100; i++)
            {
                // Require the file to exist first: Load() returns defaults when it is missing, and
                // the default tone (0) could otherwise pass as a false positive.
                if (File.Exists(_path))
                {
                    var loaded = Service.Load();
                    if (loaded.Keyboard.EmojiSkinTone == expected) return loaded;
                }
                await Task.Delay(50, ct);
            }
            return Service.Load();
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    [Fact]
    public void ShouldShow_IsFalse_WhenAlreadyVisible_AndTrueOtherwise()
    {
        // The ring's Keyboard action is an idempotent SHOW (spec ledger #1): the guard is the
        // whole seam — the handler itself touches WPF windows and is not headless-reachable
        // (same reason ApplyEmojiSkinTone was extracted).
        Check.That(KeyboardCoordinator.ShouldShow(alreadyVisible: true)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldShow(alreadyVisible: false)).IsTrue();
    }

    // ---- Screen-edge flip (Keyboard ring action, Task 5) ------------------------------------
    // The flip core behind HandleKeyboardEdgeToggled: a pure case-normalizing flip plus a
    // persistable half that mutates settings, saves debounced, and refreshes the reload snapshot.

    [Fact]
    public void FlipScreenEdge_FlipsBothWays_AndNormalizesToCanonicalCase()
    {
        // Comparison is case-insensitive but the OUTPUT is canonical: validation is a
        // case-sensitive membership test and the unguarded exit-time save turns a stray
        // casing into an app crash (spec B1 2a).
        Check.That(KeyboardCoordinator.FlipScreenEdge("Top")).IsEqualTo("Bottom");
        Check.That(KeyboardCoordinator.FlipScreenEdge("Bottom")).IsEqualTo("Top");
        Check.That(KeyboardCoordinator.FlipScreenEdge("top")).IsEqualTo("Bottom");
        Check.That(KeyboardCoordinator.FlipScreenEdge("garbage")).IsEqualTo("Top");
    }

    [Fact]
    public void ApplyKeyboardEdge_MutatesSettings_AndReturnsFreshSnapshot()
    {
        var settings = new EyeCtrlSettings { Keyboard = { ScreenEdge = "Bottom" } };
        using var temp = new TempSettingsFile();
        var registry = NewRegistry(settings);

        var (flipped, snapshot) = KeyboardCoordinator.ApplyKeyboardEdge(settings, temp.Service, registry);

        Check.That(flipped).IsEqualTo("Top");
        Check.That(settings.Keyboard.ScreenEdge).IsEqualTo("Top");
        // The phantom-rebuild guard (spec C5): the snapshot must reflect the write, or the
        // next settings-apply rebuilds the whole keyboard + prediction stack for nothing.
        Check.That(snapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings, registry));
    }

    [Fact]
    public void ToolbarOccupancyFor_MapsVisibilityDockingAndExtent()
    {
        // The yield's input mapping (spec B3 #6/#7, ledger #8): the toolbar occupies the
        // keyboard's edge only when the adapter says visible AND the window is on screen,
        // and only a docked toolbar HAS an edge — undocked maps to "none", which no
        // keyboard edge matches in KeyboardYield.
        Check.That(KeyboardCoordinator.ToolbarOccupancyFor(false, true, true, "bottom", 40))
            .IsEqualTo((false, "none", 0.0));
        Check.That(KeyboardCoordinator.ToolbarOccupancyFor(true, false, true, "bottom", 40))
            .IsEqualTo((false, "none", 0.0));
        Check.That(KeyboardCoordinator.ToolbarOccupancyFor(true, true, false, "left", 132))
            .IsEqualTo((true, "none", 132.0));
        Check.That(KeyboardCoordinator.ToolbarOccupancyFor(true, true, true, "bottom", 132))
            .IsEqualTo((true, "bottom", 132.0));
    }

    // ---- Issue #166 M2: the verdict-term wiring is now exercised ----------------------------
    // The three snapshot writers (swap patch, edge flip, skin tone) previously assigned
    // WithSwappedVerdict/WithPreservedVerdict inline; deleting the assignments stayed green.
    // The writers now delegate to internal seams that tests drive; the event lambda's and the
    // handlers' remaining lines touch the real KeyboardWindow and stay source-pinned below.

    /// <summary>Builds the 17-term record directly — every test pins the terms it cares about
    /// and takes defaults for the rest.</summary>
    private static KeyboardStackSnapshot Snap(
        string layout = "QWERTY", string modelId = "pred_test_model",
        bool onDisk = true, ModelVerificationOutcome verification = ModelVerificationOutcome.NotYetVerified) =>
        new(layout, 1.0, true, "en", "Bottom", 0, true, true, 30, true,
            modelId, false, 800, 800, 400, onDisk, verification);

    [Fact]
    public void PatchSnapshotWithSwappedVerdict_MovesOnlyTheVerdictTerm_OfTheMatchingModel()
    {
        var c = NewCoordinator();
        var stored = Snap(verification: ModelVerificationOutcome.NotYetVerified);
        c.KeyboardSnapshotForTesting = stored;

        // The id compare is case-insensitive (production matches MLModelSize spellings), so the
        // test exercises that too by passing a differently-cased id.
        c.PatchSnapshotWithSwappedVerdict(("PRED_TEST_MODEL", ModelVerificationOutcome.Verified));

        var patched = c.KeyboardSnapshotForTesting!;
        Check.That(patched.ActiveModelVerification).IsEqualTo(ModelVerificationOutcome.Verified);
        // ONLY the verdict term may move: re-substituting the old verdict must reproduce the
        // stored snapshot term-for-term (a re-capture here would drift disk/ledger state).
        Check.That(patched with { ActiveModelVerification = stored.ActiveModelVerification })
            .IsEqualTo(stored);
    }

    [Fact]
    public void PatchSnapshotWithSwappedVerdict_IgnoresForeignModelIds_AndNullVerdicts()
    {
        var c = NewCoordinator();
        var stored = Snap(verification: ModelVerificationOutcome.Failed);
        c.KeyboardSnapshotForTesting = stored;

        c.PatchSnapshotWithSwappedVerdict(("some_other_model", ModelVerificationOutcome.Verified));
        Check.That(c.KeyboardSnapshotForTesting).IsSameReferenceAs(stored);

        c.PatchSnapshotWithSwappedVerdict(null);
        Check.That(c.KeyboardSnapshotForTesting).IsSameReferenceAs(stored);
    }

    [Fact]
    public void PatchSnapshotWithSwappedVerdict_WithNoSnapshotYet_IsANoOp()
    {
        var c = NewCoordinator();
        c.PatchSnapshotWithSwappedVerdict(("pred_test_model", ModelVerificationOutcome.Verified));
        Check.That(c.KeyboardSnapshotForTesting).IsNull();
    }

    [Fact]
    public void CapturePreservingVerdict_MovesFreshSettingsTerms_ButKeepsTheStoredVerdict()
    {
        var c = NewCoordinator();
        c.KeyboardSnapshotForTesting = Snap(layout: "QWERTY", verification: ModelVerificationOutcome.Failed);
        // A fresh capture whose live ledger read says Verified (the repair-landed-in-the-window
        // shape WithPreservedVerdict exists to refuse) while the running stack is still Failed.
        var fresh = Snap(layout: "AZERTY", verification: ModelVerificationOutcome.Verified);

        c.CapturePreservingVerdict(fresh);

        var merged = c.KeyboardSnapshotForTesting!;
        Check.That(merged.ActiveLayout).IsEqualTo("AZERTY");
        Check.That(merged.ActiveModelVerification).IsEqualTo(ModelVerificationOutcome.Failed);
    }

    [Fact]
    public void CapturePreservingVerdict_TakesTheFreshVerdict_WhenNothingIsStoredYet()
    {
        var c = NewCoordinator();
        var fresh = Snap(verification: ModelVerificationOutcome.Verified);

        c.CapturePreservingVerdict(fresh);

        Check.That(c.KeyboardSnapshotForTesting).IsSameReferenceAs(fresh);
    }

    [Fact]
    public void VerdictTermWiring_TheThreeSnapshotWritersDelegateToTheSeams()
    {
        // Source pins over COMMENT-STRIPPED, whitespace-collapsed code (the M3 discipline: a
        // pin over raw text passes on a commented-out call). Belt-and-braces around the
        // behavioural chain test below (round-2 I-1): the pins hold the wiring text in place
        // and are the only guard against an inline revert the behavioural test cannot see.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "Coordinators", "KeyboardCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "The StackSwapped event is no longer subscribed with OnStackSwapped — deleting "
                + "this subscription leaves the keyboard bound to the replaced stack, so word "
                + "prediction is dead for the whole session (round-2 X5/M7 measurement), on "
                + "top of the N3 spurious reload this pin originally guarded. The method-group "
                + "form is pinned (not an inline lambda) because a lambda body can be "
                + "dead-wrapped while its call text still satisfies a substring pin.")
            .That(source.Contains("_predictionFactory.StackSwapped+=OnStackSwapped;")).IsTrue();

        Check.WithCustomMessage(
                "OnStackSwapped no longer patches the snapshot verdict. Belt-and-braces only: " +
                "the behavioural chain test " +
                "StackSwapped_OnTheLiveFactory_RebindsTheKeyboard_AndPatchesTheSnapshot is the " +
                "primary catcher since round-2 I-1 (a dead-wrap inside OnStackSwapped fails " +
                "that test, not just this pin).")
            .That(source.Contains("PatchSnapshotWithSwappedVerdict(verification);")).IsTrue();

        Check.WithCustomMessage(
                "The keyboard-edge handler no longer stores its fresh capture with the verdict " +
                "preserved — a repair landing mid-flip would absorb Verified while the running " +
                "stack is still Failed (plan review C4).")
            .That(source.Contains("CapturePreservingVerdict(snapshot);")).IsTrue();

        Check.WithCustomMessage(
                "The emoji-skin-tone handler no longer stores its fresh capture with the " +
                "verdict preserved (same C4 discipline as the edge flip).")
            .That(source.Contains("CapturePreservingVerdict(ApplyEmojiSkinTone(")).IsTrue();
    }

    // ---- Issue #166 round-2 I-1: the event→re-bind→patch chain, behaviourally --------------
    // The seams test the patch in isolation and the pins hold the source text, but the CHAIN
    // (live factory's StackSwapped → OnStackSwapped → real KeyboardWindow re-bind + snapshot
    // patch) had no behavioural coverage: mutations X5 (subscribe the dead factory) and X9
    // (re-bind with null) survived the whole App suite green. This runs the REAL Initialize
    // against a REAL KeyboardWindow and fires the LIVE factory's event by reflecting the
    // field-like event's backing delegate — the cheap way to raise another assembly's event
    // from a test (driving the factory's own swap path is the alternative).

    private sealed class KeyboardOnlyOverlayManager : IOverlayManager
    {
        private readonly KeyboardWindow _keyboard;
        public KeyboardOnlyOverlayManager(KeyboardWindow keyboard) => _keyboard = keyboard;
        public KeyboardWindow Keyboard => _keyboard;
        public ICursorCircleOverlay CursorCircle => throw new NotImplementedException();
        public IActivationButtonOverlay ActivationButton => throw new NotImplementedException();
        public IManualSelectionOverlay ManualSelectionOverlay => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public IToolbarOverlay Toolbar => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator => throw new NotImplementedException();
        public IGlassBackdropOverlay GlassBackdrop => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();
        public IEdgeButtonBar EdgeButtonBar => throw new NotImplementedException();
        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            GazeProviderType providerType) => throw new NotImplementedException();
    }

    [StaFact]
    public void StackSwapped_OnTheLiveFactory_RebindsTheKeyboard_AndPatchesTheSnapshot()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var keyboard = new KeyboardWindow();
        keyboard.SetViewModel(vm);
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            OverlayManager = new KeyboardOnlyOverlayManager(keyboard),
            Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher,
            Messenger = new WeakReferenceMessenger(),
        };
        var c = NewCoordinator();
        c.Initialize(context);
        c.KeyboardSnapshotForTesting = Snap(verification: ModelVerificationOutcome.NotYetVerified);

        // The LIVE factory (round-2 X5: a subscription attached to the replaced factory would
        // leave this field-like event's backing delegate null).
        var factoryField = typeof(KeyboardCoordinator).GetField("_predictionFactory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Check.WithCustomMessage(
                "Reflection target '_predictionFactory' not found — the coordinator's field "
                + "was renamed; update this test, it is not a behaviour failure (round-3 R7).")
            .That(factoryField).IsNotNull();
        var factory = factoryField!.GetValue(c)!;
        var eventField = typeof(PredictionStackFactory).GetField("StackSwapped",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Check.WithCustomMessage(
                "Reflection target 'StackSwapped' not found — the factory's event was renamed "
                + "or gained custom accessors; update this test, it is not a behaviour failure.")
            .That(eventField).IsNotNull();
        var handler = (Delegate?)eventField!.GetValue(factory);
        Check.WithCustomMessage(
                "Nothing is subscribed to the LIVE factory's StackSwapped — the keyboard would "
                + "never re-bind on a swap (round-2 mutation X5).")
            .That(handler).IsNotNull();

        var swapped = new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(), "QWERTY"));
        try
        {
            handler!.DynamicInvoke(swapped,
                (object?)(("pred_test_model", ModelVerificationOutcome.Verified)));

            // The re-bind must land in the REAL window's VM — OnStackSwapped's first half,
            // which no pin covers (round-2 X9: SetOrchestrator(null) survived 1197 green).
            var vmField = typeof(KeyboardViewModel).GetField("_orchestrator",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check.WithCustomMessage(
                    "Reflection target '_orchestrator' not found — the VM's field was renamed; "
                    + "update this test, it is not a behaviour failure (round-3 R6).")
                .That(vmField).IsNotNull();
            var vmOrchestrator = vmField!.GetValue(vm);
            Check.WithCustomMessage(
                    "The swap did not re-bind the keyboard to the swapped orchestrator — word "
                    + "prediction would be dead for the whole session while the suite stays "
                    + "green (round-2 mutations X5/X9).")
                .That(vmOrchestrator).IsSameReferenceAs(swapped);

            // And the snapshot patch is part of the same chain (round-1 N5/M7 dead-wrap).
            Check.WithCustomMessage(
                    "The swap did not patch the stored snapshot's verdict term (round-1 "
                    + "mutations N5/M7).")
                .That(c.KeyboardSnapshotForTesting!.ActiveModelVerification)
                .IsEqualTo(ModelVerificationOutcome.Verified);
        }
        finally
        {
            c.Dispose();
            swapped.Dispose();
        }
    }

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new();
        public void Save(Dictionary<string, int> words) { }
    }
}
