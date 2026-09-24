# Visual Findings Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the three actionable findings of the 2026-09-22 visual assessment — surface model
integrity failure on the Models tab (with a working repair→reload path), keep the pause edge
strip visible while the toolbar band is summoned, and replace the fake `'─'` dividers with a
real full-width separator.

**Architecture:** A per-model verification ledger on `ModelRegistryService` (Core, framework-free)
is written by the stack-build swap continuation and by the download path — never by `Build`
itself, which only *classifies* and returns the verdict alongside the orchestrator, so
superseded builds publish nothing. `ModelsPageSource` renders the failed state as a danger
`ButtonSpec` row wired to the existing download flow and rebuilds on `VerificationChanged`.
A new snapshot term, patched (not re-captured) at swap time with the build's own verdict, makes
the repair actually reload the stack. The pause indicator is commanded exclusively via
`IToolbarVisibility.SetPauseIndicator`; band show paths conditionally `RaiseToTop()`. A
`SeparatorSpec` renders as a private nested `IDwellComposite` so neither consumer switch changes.

**Tech Stack:** .NET 10 (net10.0 / net10.0-windows), WPF, xUnit v3 + NFluent.

**Spec:** `docs/superpowers/specs/2026-09-23-visual-findings-design.md` (revision 5, SHIP).
Work in `.worktrees/visual-findings` on `fix/visual-findings-2026-09`.
Plan revision 4 — review round 1 (4C/4H/3M/7L) and round 2 (N1–N12) folded; round 3 verdict
**executable**, with its one test-teeth fix (R1) and five hardening items (R2–R6) folded here.

## Global Constraints

- NFluent 3.1.0 assertions (`Check.That(...)`), xUnit v3 `[Fact]`; global usings `Xunit` +
  `NFluent` come from `tests/Directory.Build.props` — do not add per-file usings for them.
- No mocking library: real services over unique temp dirs (`ModelTestFakes` pattern) or
  hand-written fakes/stubs. Unique pipe/dir names per test (`AGENTS.md` pipe rule).
- File-scoped namespaces, Allman braces, `_camelCase` fields, `var` for obvious types, no
  `#region`.
- German-locale box: never parse formatted numbers in tests; `dotnet test` output is German
  (`Fehler!` = fail).
- During tasks, run ONE test project per `dotnet test` invocation (MSB1008 on this SDK). The
  final verification may use the `EyeCtrl.slnx` form, but **against a recorded baseline, not an
  expectation of zero failures** — see the final task.
- **Before every commit step below (plan review N10):** run the GitNexus MCP `detect_changes`
  with `worktree` pointed at this worktree and `scope: "unstaged"`, and confirm the
  changed-symbol set matches the task's files. The CLI form (`node .gitnexus/run.cjs`) does NOT
  exist here — `.gitnexus/` lives only in the main checkout. Because the index has never seen
  this branch, ALSO run `git diff --stat` and compare against the task's file list; that diff
  is the check that counts. Investigate anything unexpected before committing (CLAUDE.md rule).
- **GitNexus impact pass before the first edit to each symbol the spec lists** — per-task Step 1
  names them. Report risk; treat UNKNOWN as unresolved (text-search before proceeding).
- Never launch the app without the owner's explicit OK (input-channel rule) — the live pass at
  the end needs TWO separate consents (launch; repair-download). No push.
- The worktree was created with LFS checked out (verified 2026-09-23).

---

## Commit group 1 — integrity ledger + Models surface + snapshot term

### Task 1: Verification ledger on `ModelRegistryService`

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/ModelRegistryService.cs`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/ModelRegistryServiceVerificationTests.cs` (create)

**Interfaces:**
- Produces: `enum ModelVerificationOutcome { NotYetVerified, Verified, Failed }` (namespace
  `EyeCtrl.Core.Prediction`); `void ReportVerification(string modelId, ModelVerificationOutcome
  outcome)`; `ModelVerificationOutcome GetVerification(string modelId)`; `event Action?
  VerificationChanged` — raised whenever the stored value CHANGES, including on the FIRST
  report (`NotYetVerified → Failed` is exactly the startup transition the Models page must
  rebuild on).

- [ ] **Step 1: GitNexus impact pass on `ModelRegistryService`** (upstream). Note consumers;
  expected risk LOW-MEDIUM. Do not proceed on UNKNOWN.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

public class ModelRegistryServiceVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "eyectrl_verify_test_" + Guid.NewGuid());
    private readonly ModelRegistryService _registry;

    public ModelRegistryServiceVerificationTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new ModelRegistryService(
            _dir, new ModelManifest(), new EyeCtrlSettings());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetVerification_DefaultsToNotYetVerified()
    {
        Check.That(_registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }

    [Fact]
    public void ReportVerification_FirstReport_Raises()
    {
        // NotYetVerified -> Failed is the startup transition the Models page must rebuild on:
        // the first report is a CHANGE, not a silent insert (plan review C2).
        var changes = 0;
        _registry.VerificationChanged += () => changes++;

        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);

        Check.That(changes).IsEqualTo(1);
    }

    [Fact]
    public void ReportVerification_RoundTrips_AndRaisesOnlyOnChange()
    {
        var changes = 0;
        _registry.VerificationChanged += () => changes++;

        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed); // no change
        _registry.ReportVerification("135M", ModelVerificationOutcome.Verified);

        Check.That(changes).IsEqualTo(2);
        Check.That(_registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Verified);
    }

    [Fact]
    public void ReportVerification_IsIndependentPerModel()
    {
        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        Check.That(_registry.GetVerification("360M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelRegistryServiceVerification"`
Expected: FAIL — `ModelVerificationOutcome` / `ReportVerification` do not exist (compile error).

- [ ] **Step 4: Implement.** Add the enum above the class; use the class's existing `_lock`
  for the ledger (plan review C2: the concurrent-dictionary sketch never raised on the first
  report — `GetOrAdd` returns the value it just inserted — and the two operations were not
  atomic; the lock makes read-modify-write one operation with the previous value defaulting to
  `NotYetVerified`):

```csharp
namespace EyeCtrl.Core.Prediction;

using Microsoft.Extensions.Logging;
using EyeCtrl.Core.Settings;

/// <summary>Outcome of the most recent verification of a model's files.</summary>
public enum ModelVerificationOutcome
{
    /// <summary>No verification has reported for this model.</summary>
    NotYetVerified,

    /// <summary>The model loaded and is serving (stack build), or its bytes matched both
    /// manifest hashes (download).</summary>
    Verified,

    /// <summary>The model is on disk and hash-gated but not serving — hash mismatch,
    /// unusable graph, session-init failure, or tokenizer missing/unparseable. The log line
    /// carries the cause.</summary>
    Failed,
}
```

Inside the class, next to the other members:

```csharp
    // Writers: the stack-build swap continuation (UI thread, via the factory's marshal) and
    // the download completion path (worker thread). Reader: the settings UI thread during
    // page build. VerificationChanged therefore fires on the UI thread for swaps and on a
    // worker thread for downloads — subscribers must marshal any UI work themselves (the
    // Models page routes through _ctx.Invoke). (Plan review N12.)
    private readonly Dictionary<string, ModelVerificationOutcome> _verification =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the writer's thread whenever a model's verdict CHANGES — including
    /// the first report (NotYetVerified -> something). The Models page subscribes to rebuild
    /// while open.</summary>
    public event Action? VerificationChanged;

    /// <summary>Records the most recent verification outcome for a model. Same-value writes
    /// are no-ops and do not raise <see cref="VerificationChanged"/>.</summary>
    public void ReportVerification(string modelId, ModelVerificationOutcome outcome)
    {
        bool changed;
        lock (_lock)
        {
            var previous = _verification.TryGetValue(modelId, out var value)
                ? value
                : ModelVerificationOutcome.NotYetVerified;
            changed = previous != outcome;
            _verification[modelId] = outcome;
        }

        // Raise OUTSIDE the lock: subscribers (the Models page rebuild) run UI work.
        if (changed) VerificationChanged?.Invoke();
    }

    /// <summary>The latest verification outcome reported for a model; the default for a model
    /// nobody reported on is <see cref="ModelVerificationOutcome.NotYetVerified"/>.</summary>
    public ModelVerificationOutcome GetVerification(string modelId)
    {
        lock (_lock)
        {
            return _verification.TryGetValue(modelId, out var outcome)
                ? outcome
                : ModelVerificationOutcome.NotYetVerified;
        }
    }
```

- [ ] **Step 5: Run to verify pass** — same filter as Step 3. Expected: 4/4 PASS.

- [ ] **Step 6: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.Core/Prediction/ModelRegistryService.cs tests/EyeCtrl.Core.Tests/Prediction/ModelRegistryServiceVerificationTests.cs
git commit -m "feat(core): per-model verification ledger on ModelRegistryService"
```

### Task 2: `Build` classifies and returns the verdict; publication happens at swap/sync only

**Files:**
- Modify: `src/EyeCtrl.App/Services/PredictionStackFactory.cs`
  (`Build` is `private static` at :323 — becomes an INSTANCE method, plan review N1; the LM
  block sits at :368–435; `Rebuild`'s disabled early-out :94–107; sync branch :111–121; swap
  continuation :148–181)
- Modify: `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs` (diagnostic accessor)
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs` (:120–121 — the
  `StackSwapped` subscriber gains its second argument NOW; leaving the one-arg lambda breaks
  the build for Tasks 2–4; plan review N2)
- Modify: `tests/EyeCtrl.App.Tests/Services/PredictionStackBackgroundTests.cs`
  (five one-argument subscriptions at :74, :149, :173, :190, :251 — same build break — plus
  the two new publication tests; plan review N2/N7)
- Modify: `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj` (link the tokenizer fixture;
  plan review N5)
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/ModelTestFakes.cs` (hash-carrying fake)
- Test: `tests/EyeCtrl.App.Tests/Services/PredictionStackFactoryVerificationTests.cs` (create)
- Linked, not modified: `tests/EyeCtrl.LM.Tests/TestTokenizerJson.cs` — MOVED to
  `tests/TestSupport/TestTokenizerJson.cs` and linked from both csproj files (see Step 2a;
  plan review R4)

**Interfaces:**
- Consumes: Task 1's ledger.
- Produces:
  - `Build` returns `(PredictionOrchestrator? Orchestrator, (string ModelId,
    ModelVerificationOutcome Outcome)? Verification)` — it CLASSIFIES (pure
    `ClassifyLmOutcome`) but writes NO ledger and NO shared state. `Rebuild`'s public
    signature is UNCHANGED (still returns `PredictionOrchestrator?`).
  - The **swap continuation** — after the generation check, on the UI thread — reports the
    verdict to the ledger and passes it to the swap event; the **sync branch** reports to the
    ledger immediately. Discarded builds publish nothing (plan review C3: an instance property
    written from thread-pool threads let a superseded build overwrite the winner and report
    `Failed` over a repair's `Verified`).
  - `StackSwapped` changes to `event Action<PredictionOrchestrator?,
    (string ModelId, ModelVerificationOutcome Outcome)?>? StackSwapped;` — Task 5's handler
    consumes the second argument. `LastBuildVerification` (revision 1) is GONE.
  - `PredictionOrchestrator` gains `public ILanguageModelPredictor? LanguageModel =>
    _lmPredictor;` (mirrors the existing public `WordPredictor` accessor) so the C1 regression
    test can assert the no-hash build still carries an LM.

- [ ] **Step 1: GitNexus impact pass on `PredictionStackFactory`** (upstream) — the reload
  path and `StackSwapped` subscribers (KeyboardCoordinator :120–121 today).

- [ ] **Step 2: Link the tokenizer fixture, then extend `ModelTestFakes.ModelTestScope`.**

  a) In `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`, link the existing valid-tokenizer
  fixture. It MOVES first (plan review R4): every existing cross-project link comes from the
  shared `tests/TestSupport/` folder, and linking directly into `EyeCtrl.LM.Tests` would break
  the App.Tests build without warning on any LM.Tests-side move or rename. So:
  `git mv tests/EyeCtrl.LM.Tests/TestTokenizerJson.cs tests/TestSupport/TestTokenizerJson.cs`,
  then link it from BOTH csproj files (keep the file's namespace — links compile
  cross-namespace):

```xml
    <!-- EyeCtrl.App.Tests.csproj -->
    <Compile Include="..\TestSupport\TestTokenizerJson.cs"
             Link="TestSupport\TestTokenizerJson.cs" />
```

```xml
    <!-- EyeCtrl.LM.Tests.csproj (unchanged path semantics, new location) -->
    <Compile Include="..\TestSupport\TestTokenizerJson.cs"
             Link="TestSupport\TestTokenizerJson.cs" />
```

  b) A hash-carrying downloaded fixture whose manifest hashes are COMPUTED from the exact
  bytes it writes — and whose tokenizer is the REAL valid fixture, not `{}` (plan review L4
  for the hash trap, N5 for the tokenizer; the 1-byte fake ONNX cannot initialize ONNX, so the
  predictor still ends up unavailable — the deterministic state the Failed tests assert):

```csharp
    /// <summary>
    /// One bundled model on disk whose manifest carries REAL SHA-256 hashes of the exact bytes
    /// written — including a REAL, parseable tokenizer (TestTokenizerJson), so the stack's
    /// tokenizer gate passes and only the ONNX bytes are fake. The 1-byte fake model cannot
    /// initialize ONNX, so the predictor still ends up unavailable — the deterministic state
    /// the Failed tests assert.
    /// </summary>
    public (ModelRegistryService registry, ModelManagementService mgmt)
        WithHashedDownloadedBundled(string id = "135M")
    {
        var modelBytes = new byte[] { 0x01 };
        var tokenizerPath = Path.Combine(Dir, "tokenizer-fixture.json");
        TestTokenizerJson.Write(tokenizerPath);
        var tokBytes = File.ReadAllBytes(tokenizerPath);

        var manifest = new ModelManifest
        {
            Models =
            {
                [id] = new ModelEntry
                {
                    DisplayName = "Test " + id,
                    Url = "https://example.invalid/model.onnx",
                    Sha256 = Sha256Hex(modelBytes),
                    SizeBytes = modelBytes.Length,
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                    TokenizerSha256 = Sha256Hex(tokBytes),
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var onnx = ModelConstants.ModelFileNames.TryGetValue(id, out var named)
            ? named
            : $"{id.ToLowerInvariant()}.onnx";
        File.WriteAllBytes(Path.Combine(Dir, onnx), modelBytes);
        File.WriteAllBytes(Path.Combine(Dir, onnx.Replace(".onnx", ".tokenizer.json")), tokBytes);

        var registry = BuildRegistry(manifest, settings);
        var mgmt = new ModelManagementService(registry, new ModelDownloadService(Dir), settings);
        return (registry, mgmt);
    }

    /// <summary>Lowercase hex SHA-256 of the given bytes — what a manifest entry must carry
    /// for the stack's hash gate to pass those bytes.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }
```

  (Add `using EyeCtrl.App.Tests.TestSupport;`-equivalent for the linked fixture's namespace —
  copy whatever namespace `TestTokenizerJson.cs` declares.)

- [ ] **Step 3: Write the failing tests**

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.App.Tests.Settings.Pages;
using NFluent;
using Xunit;

public class PredictionStackFactoryVerificationTests : IDisposable
{
    private readonly ModelTestScope _scope = ModelTestScope.Create();
    public void Dispose() => _scope.Dispose();

    private static EyeCtrlSettings SettingsFor(string modelSize) => new()
    {
        Keyboard = { PredictionEnabled = true, MLSuggestionsEnabled = true, MLModelSize = modelSize },
    };

    // The C2 classifier, table-tested (plan review M1): these six rows are the whole decision,
    // so a mutation that misreports any cause fails here.
    [Theory]
    [InlineData(false, true, true, true, true)]  // model absent        -> nothing reported
    [InlineData(true, false, true, true, true)]  // no manifest hash    -> nothing reported
    [InlineData(true, true, false, true, true)]  // hash mismatch       -> Failed (even though
                                                 //                        the PREDICTOR would be fine)
    [InlineData(true, true, true, false, true)]  // predictor null      -> Failed
    [InlineData(true, true, true, true, false)]  // tokenizer missing   -> Failed
    [InlineData(true, true, true, true, true)]   // everything live     -> Verified
    public void ClassifyLmOutcome_CoversTheWholeDecisionTable(
        bool modelFileExists, bool hashGated, bool hashOk,
        bool predictorAvailable, bool tokenizerLoaded)
    {
        var (reports, outcome) = PredictionStackFactory.ClassifyLmOutcome(
            modelFileExists, hashGated, hashOk, predictorAvailable, tokenizerLoaded);

        if (!modelFileExists || !hashGated)
        {
            Check.That(reports).IsFalse();
        }
        else if (!hashOk || !predictorAvailable || !tokenizerLoaded)
        {
            Check.That(reports).IsTrue();
            Check.That(outcome).IsEqualTo(ModelVerificationOutcome.Failed);
        }
        else
        {
            Check.That(reports).IsTrue();
            Check.That(outcome).IsEqualTo(ModelVerificationOutcome.Verified);
        }
    }

    private sealed class AvailableLmPredictor : ILanguageModelPredictor
    {
        public bool IsAvailable => true;
        public void Predict(string context, string? prefix = null) { }
        public IReadOnlyList<PredictionResult>? TryGetResults() => null;
        public void Cancel() { }
        public void Dispose() { }
    }

    // M1's integration arm: a live predictor + a REAL loaded tokenizer MUST classify Verified
    // through the sync build, so a mutation that hard-codes Failed cannot ship silently (it
    // would show the repair row on every healthy install and loop re-downloads). The tokenizer
    // is real (the fixture) and loaded by the production loader — only the ONNX predictor is
    // overridden.
    [Fact]
    public void SyncBuild_LivePredictorAndTokenizer_ReportsVerified()
    {
        var (registry, _) = _scope.WithHashedDownloadedBundled();
        var factory = new PredictionStackFactory
        {
            PredictorFactoryOverride = (_, _, _, _) => new AvailableLmPredictor(),
        };

        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Verified);
        Check.That(factory.Current!.LanguageModel).IsNotNull();
        Check.That(factory.Current!.LanguageModel!.IsAvailable).IsTrue();
    }

    [Fact]
    public void SyncBuild_PresentHashGatedModel_UnavailableLm_ReportsFailed()
    {
        var (registry, _) = _scope.WithHashedDownloadedBundled();

        var factory = new PredictionStackFactory(); // sync mode: Build completes inside Rebuild
        var orchestrator = factory.Rebuild(SettingsFor("135M"),
            AppDomain.CurrentDomain.BaseDirectory, _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    // Plan review C1: the original code CONSTRUCTS the predictor for every model that passes
    // the mismatch gate — including custom models, whose manifest hash is always empty. The
    // revision-1 plan dropped that construction in the no-report arm; this row pins it.
    [Fact]
    public void SyncBuild_EmptyManifestHash_StillConstructsTheLm()
    {
        var (registry, _) = _scope.WithDownloadedBundled(); // Sha256 = "" (custom-style)
        var factory = new PredictionStackFactory();

        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified); // nothing reported
        Check.That(factory.Current!.LanguageModel).IsNotNull();  // but the LM is built
    }

    [Fact]
    public void SyncBuild_HashMismatch_PredictorNotConstructed_ReportsFailed()
    {
        // Manifest hashes that do NOT match the on-disk bytes: mismatch arm — no predictor,
        // Failed reported (the SmallLMPredictor is deliberately not constructed today either).
        var (registry, _) = _scope.WithHashedDownloadedBundled();
        var settings = SettingsFor("135M");
        var tampered = new ModelManifest
        {
            Models =
            {
                ["135M"] = new ModelEntry
                {
                    DisplayName = "Test 135M",
                    Url = "https://example.invalid/model.onnx",
                    Sha256 = new string('a', 64), // wrong hash
                    SizeBytes = 1,
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                    TokenizerSha256 = new string('b', 64),
                },
            },
        };
        // Rebuild the registry over the same dir with the tampered manifest (same ctor shape
        // as ModelTestScope.BuildRegistry): the FILES stay, the EXPECTED hashes differ.
        registry = new ModelRegistryService(_scope.Dir, tampered, new EyeCtrlSettings());

        var factory = new PredictionStackFactory();
        factory.Rebuild(settings, AppDomain.CurrentDomain.BaseDirectory, _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
        Check.That(factory.Current!.LanguageModel).IsNull();
    }

    [Fact]
    public void SyncBuild_ModelFileAbsent_ReportsNothing()
    {
        var (registry, _) = _scope.WithHashedDownloadedBundled();
        var onnx = Path.Combine(_scope.Dir, "smollm2-135m.onnx");
        var tok = Path.Combine(_scope.Dir, "smollm2-135m.tokenizer.json");
        if (File.Exists(onnx)) File.Delete(onnx);
        if (File.Exists(tok)) File.Delete(tok);

        var factory = new PredictionStackFactory();
        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }
}
```

  Then, in `tests/EyeCtrl.App.Tests/Services/PredictionStackBackgroundTests.cs`, add the two
  publication tests (plan review N7 — the C3 fix, the headline of this revision, gets tests).
  The file's own harness is the point: `NewBackgroundFactory()` enqueues the continuation on a
  manual `_uiQueue`, and `Pump()`/`Drain()` control exactly when "the UI thread" runs — so
  superseded continuations are observably dropped:

```csharp
    // Plan review N7 + R1: only the WINNING swap publishes (round-1 C3), and the test must be
    // able to FAIL for exactly that regression. The two builds therefore DISAGREE, and the
    // loser is forced to finish LAST: call 1 of the predictor seam blocks on a gate, then
    // returns an AVAILABLE stub (classifies Verified — the dangerous verdict); call 2 returns
    // an UNAVAILABLE stub (classifies Failed) and wins the swap. The discarded orchestrator
    // disposes its LM (PredictionOrchestrator.cs:150), so the loser's stub signals "loser is
    // gone" from its own Dispose. A mutation that writes the ledger inside Build fails this:
    // the late loser would flip the ledger to Verified after the winner's Failed.
    [Fact]
    public void SupersededBuild_PublishesNothing_LoserFinishingLastWritesNothing()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();

        var loserMayFinish = new ManualResetEventSlim(false);
        var loserDisposed = new ManualResetEventSlim(false);
        var seamCalls = 0;
        var swaps = new List<(PredictionOrchestrator? Orchestrator,
            (string ModelId, ModelVerificationOutcome Outcome)? Verification)>();

        var factory = new PredictionStackFactory(work => _uiQueue.Enqueue(work))
        {
            PredictorFactoryOverride = (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref seamCalls) == 1)
                {
                    // Build #1: blocked so build #2 wins the race deterministically.
                    loserMayFinish.Wait();
                    loserDisposed.Set(); // read back from PredictionOrchestrator.Dispose
                    return new AvailableLmPredictor();   // classifies Verified
                }
                return new UnavailableLmPredictor();     // classifies Failed — the winner
            },
        };
        var swapsGuard = new object();
        factory.StackSwapped += (o, v) => { lock (swapsGuard) swaps.Add((o, v)); };

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";

        factory.Rebuild(settings, _baseDir, _appDataDir, registry); // blocks in the seam
        factory.Rebuild(settings, _baseDir, _appDataDir, registry); // wins, swaps, Failed
        Pump();

        lock (swapsGuard) Check.That(swaps.Count).IsEqualTo(1);
        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);

        // Release the loser, wait for its discard-dispose, then run its continuation: it must
        // pass the generation re-check's discard branch and publish NOTHING.
        loserMayFinish.Set();
        Check.That(loserDisposed.Wait(TimeSpan.FromSeconds(10))).IsTrue();
        Drain();

        lock (swapsGuard) Check.That(swaps.Count).IsEqualTo(1);
        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    private sealed class AvailableLmPredictor : ILanguageModelPredictor
    {
        public bool IsAvailable => true;
        public void Predict(string context, string? prefix = null) { }
        public IReadOnlyList<PredictionResult>? TryGetResults() => null;
        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed class UnavailableLmPredictor : ILanguageModelPredictor
    {
        public bool IsAvailable => false;
        public void Predict(string context, string? prefix = null) { }
        public IReadOnlyList<PredictionResult>? TryGetResults() => null;
        public void Cancel() { }
        public void Dispose() { }
    }

    // The winning swap's event argument equals the value written to the ledger — the two
    // publication channels cannot disagree.
    [Fact]
    public void WinningSwap_EventVerdict_MatchesLedger()
    {
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        var factory = NewBackgroundFactory();
        (string ModelId, ModelVerificationOutcome Outcome)? eventVerdict = null;
        factory.StackSwapped += (_, v) => eventVerdict = v;

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";
        factory.Rebuild(settings, _baseDir, _appDataDir, registry);
        Pump();

        // R3: NFluent-on-nullable-tuple is not a known overload — use HasValue.
        Check.That(eventVerdict.HasValue).IsTrue();
        Check.That(registry.GetVerification(eventVerdict!.Value.ModelId))
            .IsEqualTo(eventVerdict.Value.Outcome);
    }
```

  (Add `using EyeCtrl.App.Tests.Settings.Pages;` for `ModelTestFakes` — same assembly. If the
  superseded-interleaving proves flaky on the thread pool, pin it harder: enqueue a first
  rebuild, Pump it to completion, then a second — the invariant under test is that whichever
  continuation LOSES the generation re-check publishes nothing, which the at-rest assertion
  still exercises.)

- [ ] **Step 4: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~PredictionStackFactoryVerification"`
  Expected: FAIL — `ClassifyLmOutcome` / overrides / `LanguageModel` missing (compile error).

- [ ] **Step 5: Implement.**

  a) `PredictionOrchestrator` (Core): add the accessor beside the existing public
  `WordPredictor`:

```csharp
    /// <summary>The language model this orchestrator was built with, or null. Diagnostic/
    /// test surface, mirroring <see cref="WordPredictor"/>: it lets tests assert an LM was
    /// actually constructed without poking at prediction behaviour.</summary>
    public ILanguageModelPredictor? LanguageModel => _lmPredictor;
```

  b) `PredictionStackFactory`:

  - Seams — ONE override (plan review N5: the revision-2 `TokenizerLoaderOverride` is dropped —
    `LMTokenizer` is `sealed` (LMTokenizer.cs:7) and rejects `{}` ("contains no vocabulary"),
    so the stub subclass could not compile; the Verified-path test loads a REAL valid tokenizer
    via the existing `TestTokenizerJson.Write(path)` fixture instead):

```csharp
    /// <summary>Test override for predictor construction. Production: null — the real
    /// SmallLMPredictor(modelsDir, modelSize, preferGPU, tokenizer, modelPath) runs inside
    /// Build.</summary>
    internal Func<string, bool, LMTokenizer?, string, ILanguageModelPredictor?>?
        PredictorFactoryOverride { get; init; }
```

  - The pure classifier (static, on the class):

```csharp
    /// <summary>
    /// The whole C2 decision in one pure function (plan review M1 — table-tested, so a
    /// mutation of any cause fails). Returns whether the verdict should be REPORTED at all:
    /// a model that is absent or not hash-gated is exempt (custom models never carry a hash).
    /// </summary>
    internal static (bool Reports, ModelVerificationOutcome Outcome) ClassifyLmOutcome(
        bool modelFileExists, bool hashGated, bool hashOk,
        bool predictorAvailable, bool tokenizerLoaded)
    {
        if (!modelFileExists || !hashGated)
            return (false, ModelVerificationOutcome.NotYetVerified);
        if (!hashOk || !predictorAvailable || !tokenizerLoaded)
            return (true, ModelVerificationOutcome.Failed); // N3: Failed IS published
        return (true, ModelVerificationOutcome.Verified);
    }
```

    (A Failed verdict IS published — `Reports: true` — otherwise the repair row could never
    appear. Revision 2's snippet had `(false, Failed)` here, contradicting its own comment.)

  - `Build` becomes:

```csharp
    private (PredictionOrchestrator? Orchestrator, (string ModelId, ModelVerificationOutcome Outcome)? Verification) Build(
        EyeCtrlSettings settings, string baseDir, string appDataDir,
        ModelRegistryService? modelRegistry, CancellationToken token)
```

  INSTANCE, not static (plan review N1 — the body reads the instance
  `PredictorFactoryOverride` seam; a static method cannot). That is safe now, because this
  method writes no shared state: the verdict travels out through the return value, and the
  callers decide publication.

    Inside the LM block, load the tokenizer with the PRODUCTION loader (the tokenizer seam was
    removed in this revision — only the predictor seam remains; plan review R6), construct the
    predictor through that seam, and classify — keeping the ORIGINAL mismatch gate's
    construction semantics EXACTLY (plan review C1: the mismatch arm constructs nothing; every
    other arm constructs, including empty-hash custom models):

```csharp
                LMTokenizer? tokenizer = null;
                var tokenizerPath = modelRegistry.GetTokenizerPath(modelSize);
                try
                {
                    if (File.Exists(tokenizerPath))
                    {
                        var expectedTokenizerHash = modelRegistry.GetTokenizerSha256(modelSize);
                        if (expectedTokenizerHash is null
                            || VerifyFileSha256(tokenizerPath, expectedTokenizerHash))
                        {
                            tokenizer = new LMTokenizer(tokenizerPath);
                        }
                        else
                        {
                            Log.Error(
                                "Tokenizer at {Path} failed its SHA-256 integrity check; LM suggestions disabled for this build",
                                tokenizerPath);
                        }
                    }
                    else
                    {
                        Log.Warning("Tokenizer file not found at {Path}; LM suggestions disabled", tokenizerPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load tokenizer from {Path}; LM suggestions disabled", tokenizerPath);
                }

                var modelPath = modelRegistry.GetModelPath(modelSize);
                var expectedModelHash = modelRegistry.GetModelSha256(modelSize);

                (bool Reports, ModelVerificationOutcome Outcome) verdict =
                    (false, ModelVerificationOutcome.NotYetVerified);

                var hashMatches = expectedModelHash is not null
                    && File.Exists(modelPath)
                    && VerifyFileSha256(modelPath, expectedModelHash);

                if (expectedModelHash is not null && File.Exists(modelPath) && !hashMatches)
                {
                    // Mismatch arm: unchanged behaviour — the predictor is NOT constructed.
                    Log.Error(
                        "Model at {Path} failed its SHA-256 integrity check; LM suggestions disabled for this build",
                        modelPath);
                }
                else
                {
                    // Unchanged arm: hash matches, OR no hash (custom), OR file absent —
                    // construct exactly as today (plan review C1).
                    token.ThrowIfCancellationRequested();
                    lmPredictor = PredictorFactoryOverride is not null
                        ? PredictorFactoryOverride(modelsDir, modelSize, settings.Keyboard.PreferGPU, tokenizer, modelPath)
                        : new SmallLMPredictor(
                            modelsDir, modelSize, settings.Keyboard.PreferGPU,
                            tokenizer: tokenizer, modelPath: modelPath);
                }

                verdict = ClassifyLmOutcome(
                    modelFileExists: File.Exists(modelPath),
                    hashGated: expectedModelHash is not null,
                    hashOk: hashMatches,
                    predictorAvailable: lmPredictor is not null && lmPredictor.IsAvailable,
                    tokenizerLoaded: tokenizer is not null);

                // N6: assigned HERE, inside the LM block, where `modelSize` is in scope.
                if (verdict.Reports)
                    verification = (ModelId: modelSize, Outcome: verdict.Outcome);
```

    and `verification` itself is a METHOD-SCOPED local declared at the top of `Build`, with an
    explicit nullable-tuple type — a ternary between a named tuple and `null` has no natural
    type and will not compile (plan review N6):

```csharp
        // Declared BEFORE the prediction-disabled early-outs and the LM block; stays null for
        // every path that verifies nothing.
        (string ModelId, ModelVerificationOutcome Outcome)? verification = null;
```

    and the end of `Build` returns it unchanged:

```csharp
        return (new PredictionOrchestrator(
            wordPredictor, trieIndex, lmPredictor, settings.Keyboard.MLSuggestionsEnabled),
            verification);
```

  - `Rebuild` — public signature unchanged. Disabled early-out: raise
    `StackSwapped?.Invoke(null, null)` (swap-to-null carries no verdict, spec O2). Sync branch:
    keep the `_current = orchestrator;` assignment (plan review N4 — revision 2's snippet
    dropped it, leaving `Current` null), report the verdict to the ledger immediately
    (single-threaded — no discard window), then return the orchestrator:

```csharp
            var (orchestrator, verification) = Build(settings, baseDir, appDataDir, modelRegistry, CancellationToken.None);
            _current = orchestrator;
            if (verification is not null && modelRegistry is not null)
                modelRegistry.ReportVerification(
                    verification.Value.ModelId, verification.Value.Outcome);
            return orchestrator;
```

  - Background continuation: after the EXISTING generation re-check inside
    `_postToUi(() => { ... })`, alongside `SwapInWithLearnedTail(built, before, generation)`:

```csharp
                        SwapInWithLearnedTail(built, before, generation);

                        // Publication point (plan review C3): the build CLASSIFIED; only the
                        // winning swap reports — to the ledger and to the swap event — so a
                        // superseded build can never write Failed over a repair's Verified.
                        var swapVerdict = built.Verification;
                        if (swapVerdict is not null && modelRegistry is not null)
                            modelRegistry.ReportVerification(
                                swapVerdict.Value.ModelId, swapVerdict.Value.Outcome);
                        StackSwapped?.Invoke(_current, swapVerdict);
```

    (`built.Verification` requires the continuation's `t.Result` to carry the tuple — the
    `Task.Run` body returns `Build(...)`'s tuple, so thread `built.Orchestrator` through
    `SwapInWithLearnedTail` and rename the local accordingly. Superseded/discarded builds
    dispose `built.Orchestrator` exactly as today and publish nothing.)

  - Event declaration:

```csharp
    /// <summary>
    /// Raised on the UI THREAD (through the constructor's marshal) after a background build
    /// swapped a new stack in — including a swap to <c>null</c>. The second argument is the
    /// verdict THE SWAPPED BUILD reported, or null when it verified nothing (prediction off,
    /// model off/absent, no manifest hash, swap-to-null). Subscribers re-bind their consumer
    /// and patch the stored snapshot (KeyboardCoordinator). Not raised in synchronous mode:
    /// the caller already holds the returned stack, and the sync branch reports to the ledger
    /// directly.
    /// </summary>
    public event Action<PredictionOrchestrator?, (string ModelId, ModelVerificationOutcome Outcome)?>? StackSwapped;
```

  - Subscriber arity (plan review N2 — the tree must BUILD at the end of this task): update
    the production subscriber, KeyboardCoordinator.cs :120–121, to

```csharp
        _predictionFactory.StackSwapped +=
            (orchestrator, _) => context.OverlayManager.Keyboard.SetOrchestrator(orchestrator);
```

    (Task 5 replaces the discard with the snapshot patch.) Update the FIVE one-argument
    subscriptions in `PredictionStackBackgroundTests.cs` (:74, :149, :173, :190, :251) to
    two-argument lambdas — e.g. `factory.StackSwapped += (o, _) => swappedSeen.Add(o);` —
    keeping each test's existing recording body.

- [ ] **Step 6: Run to verify pass** — same filter; then
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinator"` (the
  snapshot family is untouched so far — arity change comes in Task 4).

- [ ] **Step 7: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Services/PredictionStackFactory.cs src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs tests/EyeCtrl.App.Tests/Services/PredictionStackFactoryVerificationTests.cs tests/EyeCtrl.App.Tests/Services/PredictionStackBackgroundTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/ModelTestFakes.cs tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj
git commit -m "feat(app): stack build classifies LM verdict; swap publishes it (superseded builds stay silent)"
```

### Task 3: Download path reports `Verified` (both-hash gate)

**Files:**
- Modify: `src/EyeCtrl.App/Services/ModelManagementService.cs` (~line 137, success path)
- Modify: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs` (:199–206 stale docstring)
- Test: extend `tests/EyeCtrl.App.Tests/Services/ModelManagementServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's ledger.
- Produces: on bundled download success with BOTH `sha256` and `tokenizerSha256` non-empty,
  `registry.ReportVerification(modelId, Verified)` fires before `Complete(success)`.

- [ ] **Step 1: GitNexus impact pass on `ModelManagementService`** (upstream).

- [ ] **Step 2: Write the failing tests** (this file's transport pattern:
  `BuildServices(tempDir, settings, manifest, req => Task.FromResult(...))` with
  `Ok(bytes)`/`NotFound()` and `TestContext.Current.CancellationToken`):

```csharp
    [Fact]
    public async Task DownloadBundledModelAsync_Success_WithBothHashes_ReportsVerified()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var modelBytes = new byte[] { 0x05 };
            var tokBytes = new byte[] { 0x06 };
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/onnx/model.onnx",
                        Sha256 = ModelTestScope.Sha256Hex(modelBytes),
                        SizeBytes = modelBytes.Length,
                        TokenizerUrl = "https://example.com/onnx/tokenizer.json",
                        TokenizerSha256 = ModelTestScope.Sha256Hex(tokBytes),
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest,
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(tokBytes)
                    : Ok(modelBytes)));

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/onnx/model.onnx", manifest.Models["135M"].Sha256,
                "https://example.com/onnx/tokenizer.json", manifest.Models["135M"].TokenizerSha256,
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(registry.GetVerification("135M"))
                .IsEqualTo(ModelVerificationOutcome.Verified);
        }
        finally { Cleanup(tempDir); }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_Success_WithEmptyHashes_DoesNotReportVerified()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/onnx/model.onnx",
                        TokenizerUrl = "https://example.com/onnx/tokenizer.json",
                        // Sha256 / TokenizerSha256 left empty: the download service skips the
                        // check (transport trust) — weaker than the stack writer's Verified.
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest,
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[] { 0x06 })
                    : Ok(new byte[] { 0x05 })));

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/onnx/model.onnx", "",
                "https://example.com/onnx/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(registry.GetVerification("135M"))
                .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        }
        finally { Cleanup(tempDir); }
    }
```

  (`ModelTestScope.Sha256Hex` is `public static` on an `internal` class in the same test
  project — usable directly. Add `using EyeCtrl.Core.Prediction;` if absent.)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModelManagementService"`
  Expected: the new tests FAIL (ledger stays NotYetVerified); existing tests PASS.

- [ ] **Step 4: Implement.** In `DownloadBundledModelAsync`, immediately before
  `ActivateDownloadedModelIfNothingRealIsActive(modelId);`:

```csharp
            // Spec M1+N4: Verified ONLY when both artifacts were hash-checked. An empty hash
            // means ModelDownloadService skipped the check (transport trust), which is weaker
            // than the stack writer's Verified — report nothing there.
            if (!string.IsNullOrEmpty(sha256) && !string.IsNullOrEmpty(tokenizerSha256))
                _registryService.ReportVerification(modelId, ModelVerificationOutcome.Verified);
```

  (Add `using EyeCtrl.Core.Prediction;` if not present.) Fix the stale docstring in
  `ModelDownloadService.DownloadModelAsync` (:199–206): replace "and to bundled models whose
  manifest omits a hash (the shipped models.json currently leaves these empty)" with
  "and to any caller that passes an empty hash — the shipped models.json populates every
  hash, and `PredictionStackFactory` enforces them at load".

- [ ] **Step 5: Run to verify pass** — same filter. Expected: all PASS.

- [ ] **Step 6: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Services/ModelManagementService.cs src/EyeCtrl.Core/Prediction/ModelDownloadService.cs tests/EyeCtrl.App.Tests/Services/ModelManagementServiceTests.cs
git commit -m "feat(app): bundled download reports Verified when both hashes were checked"
```

### Task 4: `KeyboardStackSnapshot` verdict term

**Files:**
- Modify: `src/EyeCtrl.App/Services/SettingsSnapshots.cs`
- Test: extend `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 1's `ModelVerificationOutcome`.
- Produces: `KeyboardStackSnapshot` gains a 17th member
  `ModelVerificationOutcome ActiveModelVerification` (last positional parameter). Capture
  reads it live from the ledger (compare path). Task 5 overwrites it at swap time via
  `with { ActiveModelVerification = ... }`.

- [ ] **Step 1: GitNexus impact pass on `KeyboardStackSnapshot`** (upstream). EVERY
  construction site must be found (record positional arity!). Expected sites:
  `SettingsSnapshots.Capture` and tests.

- [ ] **Step 2: Write the failing tests** (in `KeyboardCoordinatorTests`, beside the existing
  snapshot family):

```csharp
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
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
  Expected: FAIL — no `ActiveModelVerification` member (compile error across construction
  sites).

- [ ] **Step 4: Implement.** In `KeyboardStackSnapshot`, add the parameter after
  `bool ActiveModelOnDisk` and the doc:

```csharp
    bool ActiveModelOnDisk,
    ModelVerificationOutcome ActiveModelVerification)
```

  Extend the XML doc: the new term "is the active model's most recent verification outcome —
  the ledger value the running stack was built under. A repair re-download flips it
  (Failed → Verified), which is what makes ShouldReload fire where ActiveModelOnDisk cannot
  (present → present)."

  In `Capture`, append the argument via a helper mirroring `IsActiveModelOnDisk`:

```csharp
        IsActiveModelOnDisk(s, models),
        ActiveModelVerificationOf(s, models));

    private static ModelVerificationOutcome ActiveModelVerificationOf(
        EyeCtrlSettings s, ModelRegistryService? models)
    {
        // Same spelling rules as IsActiveModelOnDisk: no active model means the neutral term.
        var id = s.Keyboard.MLModelSize;
        return models is not null
            && !string.IsNullOrEmpty(id)
            && !string.Equals(id, "Off", StringComparison.OrdinalIgnoreCase)
            && models.IsModelDownloaded(id)
                ? models.GetVerification(id)
                : ModelVerificationOutcome.NotYetVerified;
    }
```

  Fix every other construction site the impact pass found (expected: only tests). Add
  `using EyeCtrl.Core.Prediction;` if absent.

- [ ] **Step 5: Run to verify pass** — same filter. Expected: PASS (the family's existing
  tests updated for the new positional argument where they construct the record directly).

- [ ] **Step 6: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Services/SettingsSnapshots.cs tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs
git commit -m "feat(app): keyboard stack snapshot carries the active model's verification verdict"
```

### Task 5: Swap-time snapshot PATCH (seed stays, swap overwrites with the build's own verdict)

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`
  (`StackSwapped` wiring :118–125 — signature changed in Task 2)
- Test: extend `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 2's `StackSwapped(orchestrator, verification)` second argument.
- Produces: `internal static KeyboardStackSnapshot WithSwappedVerdict(
  KeyboardStackSnapshot stored, (string ModelId, ModelVerificationOutcome Outcome)?
  verification)` — pure merge that PATCHES the stored snapshot's verdict term only. NO
  re-capture happens here (plan review C4: a fresh `CaptureSnapshot` would re-read the ledger
  AND `ActiveModelOnDisk` live — absorbing a download report that landed after the build
  checked disk, and storing `OnDisk = true` for a stack built without the model, which
  resurrects the ticket-11 bug). The R2 "which settings instance" concern dissolves: nothing
  is re-read, so `_lastKeyboardSettings` is never added.

- [ ] **Step 1: GitNexus impact pass on `KeyboardCoordinator`** (upstream; HIGH-ish — the
  reload gate lives here). Read the affected processes; do not proceed on UNKNOWN without a
  text-search confirmation.

- [ ] **Step 2: Write the failing tests** — the M2 sequence, over a REAL registry so the
  ledger is live (these are not tautologies: each step changes real state):

```csharp
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
        using var scope = ModelTestFakes.Create();
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var stored = KeyboardStackSnapshot.Capture(settings, scope.EmptyRegistry());

        var merged = KeyboardCoordinator.WithSwappedVerdict(
            stored, ("360M", ModelVerificationOutcome.Verified));

        Check.That(merged).IsEqualTo(stored);
    }
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
  Expected: FAIL — the two-argument `WithSwappedVerdict` (and the new event arity) missing.

- [ ] **Step 4: Implement.** In `KeyboardCoordinator`:

  a) The pure merge (now two parameters — the verdict comes from the event, so no active-model
  lookup is needed: the stored snapshot already carries `MLModelSize`):

```csharp
    /// <summary>
    /// PATCHES the stored snapshot's verdict term with the verdict the swapped build reported
    /// (plan review C4): never re-captures. A fresh CaptureSnapshot here would re-read the
    /// ledger AND ActiveModelOnDisk live — absorbing a download report that landed after the
    /// build checked disk (silently un-fixing the repair reload) and storing on-disk state the
    /// running stack was never built against (the ticket-11 bug, back). A null verdict
    /// (swap-to-null) keeps the stored term, i.e. the previous verdict. A verdict for a model
    /// other than the stored active model is ignored.
    /// </summary>
    internal static KeyboardStackSnapshot WithSwappedVerdict(
        KeyboardStackSnapshot stored,
        (string ModelId, ModelVerificationOutcome Outcome)? verification)
    {
        if (verification is not { } swap
            || !string.Equals(swap.ModelId, stored.MLModelSize, StringComparison.OrdinalIgnoreCase))
            return stored;
        return stored with { ActiveModelVerification = swap.Outcome };
    }
```

  b) The handler (:120–121):

```csharp
        _predictionFactory.StackSwapped += (orchestrator, verification) =>
        {
            context.OverlayManager.Keyboard.SetOrchestrator(orchestrator);

            // Swap-time snapshot PATCH (spec N3/O2/R1 + plan review C4): the seed from
            // LoadKeyboard stays until the first swap; each swap moves ONLY the verdict term,
            // with the verdict the swapped build itself reported. No re-capture — a fresh one
            // would re-read live disk/ledger state the running stack was never built against.
            if (_keyboardSnapshot is not null)
                _keyboardSnapshot = WithSwappedVerdict(_keyboardSnapshot, verification);
        };
```

  Do NOT add a `_lastKeyboardSettings` field (revision 1 had one; C4's patch shape removes the
  need — `WithSwappedVerdict` reads `stored.MLModelSize`).

- [ ] **Step 5: Run to verify pass** — same filter; then
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsSnapshots|FullyQualifiedName~PredictionStackFactory"`.

- [ ] **Step 6: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs
git commit -m "feat(app): swap patches the stored snapshot with the build's own verdict (no re-capture)"
```

### Task 6: `ButtonKind.Danger` + `FillFor` arm + render pin

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs` (:44 enum)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`FillFor` :1020–1025)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRenderMappingTests.cs` (NOTE the
  `Pages/` segment — the file is NOT at the project root; plan review H1)

**Interfaces:**
- Produces: `ButtonKind.Danger`; `FillFor(ButtonKind.Danger)` returns
  `(Tokens.AccentWarning, Tokens.BrushSurfaceDark)` — the same family as Reset/Delete (the
  contrast-gated token; `AccentDanger` is deliberately excluded from
  `DwellButtonProgressBarContrastTests`).

- [ ] **Step 1: GitNexus impact pass on `ButtonKind`** (upstream: every `FillFor` caller and
  switch over the enum — including `SettingsAutoPagination` if it kinds anything).

- [ ] **Step 2: Write the failing test** (in `SettingsRenderMappingTests`, matching its two
  existing kind-pins; `DeleteBase` is `private static` on `SettingsWindow`
  (SettingsWindow.xaml.cs:176) and NOT reachable — assert the token directly; plan review H4):

```csharp
    [Fact]
    public void FillFor_Danger_UsesTheSharedWarningFamily_WithDarkLabel()
    {
        // Danger is the non-destructive danger kind (repair/download): same fill family as
        // Reset/Delete (Tokens.AccentWarning) so the DwellButtonProgressBarContrastTests gate
        // (which deliberately excludes AccentDanger) still covers it. Pinned by name because
        // nothing enumerates ButtonKind: the `_` arm would silently style Danger as a plain
        // button.
        var (fill, text) = SettingsWindow.FillFor(ButtonKind.Danger);
        Check.That(fill).IsEqualTo(Tokens.AccentWarning);
        Check.That(text).IsEqualTo(Tokens.BrushSurfaceDark);
    }
```

  (Add the `Tokens` using the file's siblings use — `EyeCtrl.Controls.Theme`.)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRenderMappingTests"`.
  Expected: FAIL — `ButtonKind.Danger` missing (compile error).

- [ ] **Step 4: Implement.** `public enum ButtonKind { Default, Reset, Delete, Danger }` with a
  doc line ("non-destructive danger — repair flows; same visual family as Delete"). `FillFor`
  gains the arm `ButtonKind.Danger => (DeleteBase, Tokens.BrushSurfaceDark),`.

- [ ] **Step 5: Run to verify pass.** Expected: PASS.

- [ ] **Step 6: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRenderMappingTests.cs
git commit -m "feat(app): ButtonKind.Danger — non-destructive danger styling for the repair row"
```

### Task 7: ModelsPageSource — live refresh, in-flight restructure, failed row

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs`
- Test: extend `tests/EyeCtrl.App.Tests/Settings/Pages/ModelsPageSourceTests.cs`
- Test: extend `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs` (NOTE: project ROOT, not
  `Settings/` — plan review H1)

**Interfaces:**
- Consumes: Tasks 1–3 (ledger, event), Task 6 (`ButtonKind.Danger`).
- Produces: page rows — for a downloaded, hash-gated entry with
  `GetVerification(id) == Failed`: a `ButtonSpec` with `Id = "md_fix_" + entry.Id`,
  `Kind = ButtonKind.Danger`, label
  `⚠ {DisplayName} — model unusable · predictions fall back to dictionary · dwell to re-download`,
  activation carrying the same busy-guard + confirm modal + download flow as the download
  button (plan review L3). `VerificationChanged` triggers `RebuildContent` while the Models
  tab is current.

- [ ] **Step 1: GitNexus impact pass on `ModelsPageSource`** (upstream: the settings shell
  rebuild paths).

- [ ] **Step 2: Write the failing tests** (harness: `ModelTestFakes` + `TestSettingsPageContext`
  exactly like the existing tests; `CurrentDownloadId` has an internal setter,
  ModelManagementService.cs:38; predicates receive STRINGS after the Select — plan review H3):

```csharp
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
```

  (`BusyManagement` and the `showModal` recorder parameter already exist — ModelTestFakes.cs:151
  and TestSettingsPageContext.cs:26. `showError` is hard-coded to a no-op
  (TestSettingsPageContext.cs:36); asserting the error message would mean widening that
  parameter — optional, not required by this task.)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModelsPageSourceTests"`.
  Expected: new tests FAIL (no `md_fix_` row; in-flight check unreachable for downloaded
  entries).

- [ ] **Step 4: Implement.** In `ModelsPageSource.cs`:

  a) Subscribe like the progress handler (ctor + `Detach`), guarding null exactly as the
  progress subscription does:

```csharp
        if (_ctx.Registry is not null)
            _ctx.Registry.VerificationChanged += OnVerificationChanged;

    private void OnVerificationChanged()
    {
        if (_ctx.ViewModel?.Settings.CurrentTabName == "Models")
            _ctx.Invoke(_ctx.RebuildContent);
    }
```

  and `_ctx.Registry?.VerificationChanged -= OnVerificationChanged;` in `Detach`.

  b) In `Build()`, restructure the per-entry loop so the IN-FLIGHT check moves ABOVE the
  downloaded/failed split (review L3 of the spec — `IsModelDownloaded` stays true throughout a
  repair, so the old placement made the progress line unreachable):

```csharp
        foreach (var entry in _ctx.Registry.Registry.Values.OrderBy(e => e.SizeBytes))
        {
            // In-flight takes precedence over BOTH branches: a repair re-download keeps the
            // files present throughout (File.Move(overwrite:true) at the end), so gating the
            // progress line on !IsModelDownloaded made it unreachable exactly when a repair
            // runs (spec review L3).
            if (_ctx.ModelManagement is not null && entry.Id == _ctx.ModelManagement.CurrentDownloadId)
            {
                var eInFlight = entry;
                page1.Add(new LabelSpec(() =>
                    $"⇣ {eInFlight.DisplayName}  downloading… {_ctx.ModelManagement.CurrentDownloadProgress:P0}"));
                continue;
            }

            if (!_ctx.Registry.IsModelDownloaded(entry.Id))
            {
                var e2 = entry;
                page1.Add(new ButtonSpec($"md_dl_{entry.Id}",
                    $"{entry.DisplayName}  {FormatSize(entry.SizeBytes)}  [Download]",
                    () =>
                    {
                        // Not silent: a busy workflow that swallows the dwell is
                        // indistinguishable from a broken button. (original body, unchanged)
                        if (_ctx.ModelManagement.IsDownloading)
                        {
                            _ctx.ShowError("Another download is already in progress.");
                            return;
                        }
                        _ctx.ShowModal($"Download {e2.DisplayName} ({FormatSize(e2.SizeBytes)})?",
                            () => _ = DownloadBundledAsync(e2.Id, e2.Url, e2.Sha256, e2.TokenizerUrl, e2.TokenizerSha256));
                    },
                    ButtonKind.Default, Width: 544, Height: 100));
                continue;
            }

            if (_ctx.Registry.GetVerification(entry.Id) != ModelVerificationOutcome.Failed)
            {
                var e1 = entry;
                page1.Add(new LabelSpec(() =>
                    $"  {e1.DisplayName}  {FormatSize(e1.SizeBytes)}  ✓ Downloaded"));
                continue;
            }

            // Spec O3: cause-neutral copy — the verdict is availability-derived and includes
            // non-integrity failures, so the row must not claim "integrity" nor promise that a
            // re-download fixes every cause; it is the right FIRST remedy for the common case.
            // Known and accepted (spec review L7): a hash-clean-but-unusable model will come
            // back Failed after a re-download; the log line distinguishes the cause and the
            // spec declined a cause bit.
            var e3 = entry;
            page1.Add(new ButtonSpec($"md_fix_{entry.Id}",
                $"⚠ {e3.DisplayName} — model unusable · predictions fall back to dictionary · dwell to re-download",
                () =>
                {
                    // L3: same busy guard as the download arm — refuse BEFORE the modal.
                    if (_ctx.ModelManagement.IsDownloading)
                    {
                        _ctx.ShowError("Another download is already in progress.");
                        return;
                    }
                    _ctx.ShowModal(
                        $"Re-download {e3.DisplayName}? This replaces the files on disk.",
                        () => _ = DownloadBundledAsync(e3.Id, e3.Url, e3.Sha256, e3.TokenizerUrl, e3.TokenizerSha256));
                },
                ButtonKind.Danger, Width: 544, Height: 100));
        }
```

  c) Keep the section dividers as-is in this task (Task 10 replaces them with `SeparatorSpec`).

- [ ] **Step 5: Run to verify pass** — same filter. Expected: new tests PASS, existing
  download/e2e tests PASS.

- [ ] **Step 6: Fit assertion for the new label (spec M5 + plan review M3).** Add to
  `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs` (project ROOT) a `[StaFact]` that
  measures the REAL control, not a free-standing TextBlock:

```csharp
    [StaFact]
    public void TheRepairRow_WrapsItsLabel_InsideTheRealDangerButton()
    {
        // M3+N8: measure through SettingsWindow.CreateControl so production changes to
        // DwellButton's font, padding or wrap behaviour make THIS test fail, not a probe.
        // AND re-measure the inner TextBlock unclamped: UIElement.Measure clamps DesiredSize
        // to the available size, so a height read off an arranged child inside a fixed-100
        // button can never exceed 100 and the assertion would be vacuous.
        const string repairLabel = "⚠ Qwen2.5 1.5B (q4f16) — model unusable · predictions fall " +
            "back to dictionary · dwell to re-download";
        var control = (DwellControl)SettingsWindow.CreateControl(
            new ButtonSpec("md_fix_fit", repairLabel, () => { },
                ButtonKind.Danger, Width: 544, Height: 100),
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, WideColumnWidth);

        var host = new System.Windows.Controls.Grid();
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        control.Attach(host);
        host.Measure(new System.Windows.Size(544, double.PositiveInfinity));
        host.Arrange(new System.Windows.Rect(0, 0, 544, host.DesiredSize.Height));
        host.UpdateLayout();

        var label = FindTextBlocks(control.Root)
            .OrderByDescending(t => t.Text?.Length ?? 0).First();

        // Unclamped re-measure at the width the button actually gives the text:
        label.Measure(new System.Windows.Size(label.ActualWidth, double.PositiveInfinity));
        var wrappedHeight = label.DesiredSize.Height;

        // R5: compare against the label's OWN row height (it sits in the layout Grid's label
        // row, above the progress-bar row) — comparing to the button's outer 100 DIP would
        // pass a label that clips inside its row.
        Check.That(wrappedHeight).IsStrictlyGreaterThan(0);
        if (label.Parent is System.Windows.Controls.Grid layoutGrid
            && System.Windows.Controls.Grid.GetRow(label) < layoutGrid.RowDefinitions.Count)
        {
            var row = System.Windows.Controls.Grid.GetRow(label);
            var rowHeight = layoutGrid.RowDefinitions[row].ActualHeight;
            Check.That(wrappedHeight).IsLessOrEqualThan(rowHeight + 0.5);
        }
        else
        {
            Check.That(wrappedHeight).IsStrictlyLessThan(100); // fallback: outer height
        }
    }

    private static IEnumerable<System.Windows.Controls.TextBlock> FindTextBlocks(
        System.Windows.Media.Visual root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(root, i)
                is not System.Windows.Media.Visual child)
                continue;
            if (child is System.Windows.Controls.TextBlock tb)
                yield return tb;
            foreach (var nested in FindTextBlocks(child))
                yield return nested;
        }
    }
```

  (`RadiusPhysical`, `DwellMs`, `WideColumnWidth` — the file's existing constants, as used by
  `ALabelledRow_ForwardsItsInnerCompositesCachedChildren`; `FindTextBlocks` is a standard
  `VisualTreeHelper` walk. If the DwellButton label is not a `TextBlock` but a `ContentPresenter`
  chain, widen the walk to `FrameworkElement` and pick the widest element — the invariant
  being pinned is "the wrapped label fits the 100 DIP row at 544 width, measured through the
  real control".)

- [ ] **Step 7: Run the page + fit suites** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModelsPageSourceTests|FullyQualifiedName~SettingsContentFitTests|FullyQualifiedName~SettingsRowDescriptionCompleteness"`.

- [ ] **Step 8: detect_changes, then commit (completes commit group 1)**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/ModelsPageSourceTests.cs tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs
git commit -m "feat(app): Models tab renders a repair row for failed-integrity models (live refresh, in-flight precedence)"
```

---

## Commit group 2 — pause edge strip

### Task 8: `RaiseToTop` + `SetPauseIndicator` seam + adapter rework

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`IPauseIndicatorOverlay`, :185)
- Modify: `src/EyeCtrl.App/Services/IToolbarVisibility.cs`
- Modify: `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`
- Modify: `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterHeadlessTests.cs`
  (FakePauseIndicator + **four existing tests assert the OLD contract and are rewritten or
  deleted — `HideToolbar_WhilePaused_RaisesTheIndicator`,
  `HideToolbar_WhileRunning_LeavesTheIndicatorDown`,
  `ShowToolbar_ShowsTheToolbarAndClearsTheIndicator`,
  `ForceShow_ShowsTheToolbarAndClearsTheIndicator` (:158–215); plan review H2**)
- Modify: `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs`
  (`FakeVisibility` :~596 implements `IToolbarVisibility` — gains the new member)
- Modify: `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideGeometryTests.cs`
  (`RecordingVisibility` :~37 — same)
- Modify: `tests/EyeCtrl.App.Tests/Tests/ToolbarTests.cs` (:~47 — the comment "The indicator
  shows only after the toolbar's slide-out ANIMATION completes" is stale after this task;
  reword to "The indicator is commanded on the pause flip; wait for it" — plan review L6)

**Interfaces:**
- Produces: `IPauseIndicatorOverlay.RaiseToTop()`; `IToolbarVisibility.SetPauseIndicator(bool
  visible)`. Contract: the strip's visibility is commanded ONLY through
  `SetPauseIndicator`; `ShowToolbar`/`ForceShow` conditionally raise (iff commanded visible)
  after showing the band; `HideToolbar` touches nothing.

- [ ] **Step 1: GitNexus impact pass on `ToolbarVisibilityAdapter` + `IToolbarVisibility`**
  (upstream: `ToolbarAutoHideController` callers, every implementer of the interface — the two
  test fakes above are in that set).

- [ ] **Step 2: Write the failing tests.** The file's arrange is
  `private static (ToolbarVisibilityAdapter Adapter, List<string> Log,
  ExposedDwellToolbarViewModel Vm, FakePauseIndicator Indicator) Build()` (line ~108) with
  `Pump()` to run queued dispatcher work; `FakeToolbar` logs literals like
  `"Toolbar.ShowThenSlideIn"`. First extend `FakePauseIndicator` (line ~95):

```csharp
        public bool Visible { get; private set; }

        public void Show() { Visible = true; _log.Add("Indicator.Show"); }
        public void Hide() { Visible = false; _log.Add("Indicator.Hide"); }
        public void RaiseToTop() => _log.Add("Indicator.RaiseToTop");
```

  Rewrite/delete the four old-contract tests listed above (they assert HideToolbar/ShowToolbar
  move the indicator — the new contract is that they do NOT), then add:

```csharp
    [Fact]
    public void SetPauseIndicator_True_ShowsAndRaises()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        Pump();
        Check.That(log).Contains("Indicator.Show");
        Check.That(log).Contains("Indicator.RaiseToTop");
    }

    [Fact]
    public void SetPauseIndicator_IsEdgeGuarded()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        adapter.SetPauseIndicator(true);
        Pump();
        Check.That(log.Count(t => t == "Indicator.Show")).IsEqualTo(1);
    }

    [Fact]
    public void Unpause_WhileBandVisible_HidesStrip()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        adapter.SetPauseIndicator(false); // the H1 row: nothing else would dismiss the strip
        Pump();
        Check.That(log).Contains("Indicator.Hide");
        Check.That(log.Count(t => t == "Indicator.Hide")).IsEqualTo(1);
    }

    [Fact]
    public void Summon_WhileStripCommanded_RaisesIndicatorAfterTheBandShow()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        log.Clear();
        adapter.ShowToolbar();
        Pump();
        var bandIndex = log.IndexOf("Toolbar.ShowThenSlideIn");
        var raiseIndex = log.IndexOf("Indicator.RaiseToTop");
        Check.That(raiseIndex).IsStrictlyGreaterThan(bandIndex);
    }

    [Fact]
    public void Summon_WithoutStrip_DoesNotRaise()
    {
        var (adapter, log, _, _) = Build();
        adapter.ShowToolbar();
        Pump();
        Check.That(log).Not.Contains("Indicator.RaiseToTop");
    }

    [Fact]
    public void HideToolbar_NeitherShowsNorHidesNorRaisesTheStrip()
    {
        var (adapter, log, _, _) = Build();
        adapter.SetPauseIndicator(true);
        log.Clear();
        adapter.HideToolbar(paused: true);
        Pump();
        Check.That(log.Where(t => t.StartsWith("Indicator."))).IsEmpty();
    }
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterHeadlessTests"`.
  Expected: FAIL — `SetPauseIndicator`/`RaiseToTop` missing; the four old tests fail compile
  until rewritten.

- [ ] **Step 4: Implement.**

  a) `IPauseIndicatorOverlay` += `/// <summary>Re-asserts this window above sibling topmost
  overlays shown after it (OverlayWindow.RaiseToTop: SetWindowPos HWND_TOPMOST |
  SWP_NOACTIVATE).</summary> void RaiseToTop();` — `PauseIndicatorWindow : OverlayWindow`
  already has the implementation; no window code changes.

  b) `IToolbarVisibility` +=

```csharp
    /// <summary>
    /// Commands the pause edge strip's visibility. The strip's state lives HERE (edge-guarded),
    /// not in band show/hide paths: every pause flip produces exactly one command (spec H1 —
    /// unpause with the band up must dismiss it; nothing else will).
    /// </summary>
    void SetPauseIndicator(bool visible);
```

  c) `ToolbarVisibilityAdapter`: add `private bool _pauseIndicatorVisible;`; implement

```csharp
    public void SetPauseIndicator(bool visible) => _dispatcher.BeginInvoke(() =>
    {
        if (_pauseIndicatorVisible == visible) return; // StateChangedMessage fires per gaze flip
        _pauseIndicatorVisible = visible;
        if (visible)
        {
            _overlays.PauseIndicator.Show();
            _overlays.PauseIndicator.RaiseToTop();
        }
        else
        {
            _overlays.PauseIndicator.Hide();
        }
    });
```

  In `ShowToolbar`: DELETE the `_overlays.PauseIndicator.Hide();` line and its comment; after
  the `Log.Information(...)` line add

```csharp
        // The strip stays above a band shown after it (spec N2/H3): RaiseToTop is uncontested
        // for the whole slide — the slide animates a TranslateTransform (render transform), so
        // the band's HWND z-order never changes during it (review O6).
        if (_pauseIndicatorVisible)
            _overlays.PauseIndicator.RaiseToTop();
```

  In `ForceShow`: same removal + same conditional raise after `ShowAtRest()`. In
  `HideToolbar`'s slide-out callback: delete the `if (paused) ... else ...` indicator block,
  keep `OnVisibilityChanged(false);` in the callback. Keep the `paused` parameter (the log
  line uses it); update the `IToolbarVisibility` doc comments that say "hides the pause
  indicator" — those sentences are now false.

  d) The two other fakes: `FakeVisibility` gains
  `public void SetPauseIndicator(bool visible) { }` (plus whatever recording style the class
  already uses); `RecordingVisibility` gains the recording member in its own style.

  e) The four old-contract tests: delete
  `HideToolbar_WhilePaused_RaisesTheIndicator` and
  `HideToolbar_WhileRunning_LeavesTheIndicatorDown` (their content is superseded by
  `HideToolbar_NeitherShowsNorHidesNorRaisesTheStrip`); rewrite
  `ShowToolbar_ShowsTheToolbarAndClearsTheIndicator` and
  `ForceShow_ShowsTheToolbarAndClearsTheIndicator` to the new contract (band shows; strip
  raised only when commanded).

- [ ] **Step 5: Run to verify pass** — same filter. Then the whole file's suite plus
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarAutoHide"`.

- [ ] **Step 6: detect_changes, then commit**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Services/IOverlayWindows.cs src/EyeCtrl.App/Services/IToolbarVisibility.cs src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterHeadlessTests.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideGeometryTests.cs tests/EyeCtrl.App.Tests/Tests/ToolbarTests.cs
git commit -m "feat(app): pause strip commanded via SetPauseIndicator; band shows raise it above"
```

### Task 9: Driver wiring + startup ordering pin

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`
  (mirror at :57–60)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (ordering comment at the `TogglePause` call ~:284)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/ToolbarLifecycleCoordinatorTests.cs`
  (EXISTS — host the source-read pin there; plan review N11)

**Interfaces:**
- Consumes: Task 8's `SetPauseIndicator`. The existing `StateChangedMessage` mirror gains the
  strip command; the startup push arrives via `im.TogglePause()` at App.xaml.cs:284, which
  already runs AFTER `ShowOverlays` (:281) — the O4 ordering, pinned by comment + test.

- [ ] **Step 1: Implement the mirror** (the `_visibility` field is set later in
  `SetupToolbarAutoHide` (:269) — the null-conditional is what makes pre-init messages safe):

```csharp
        context.Messenger.Register<ToolbarLifecycleCoordinator, StateChangedMessage>(this, (_, msg) =>
        {
            var paused = msg.NewState == InteractionState.Paused;
            _autoHide?.SetPaused(paused);
            // The strip is a pure function of paused state (spec H1/N2): one command per flip.
            // The startup-paused push arrives here from App.OnStartup's TogglePause, which runs
            // AFTER ShowOverlays — that ordering is load-bearing for the strip's z-order (O4);
            // see the comment at App.xaml.cs.
            _visibility?.SetPauseIndicator(paused);
        });
```

- [ ] **Step 2: Pin the App ordering.** At App.xaml.cs ~:283, extend the existing lines with:

```csharp
            // ORDER LOAD-BEARING (spec O4): this pause push must stay AFTER ShowOverlays — the
            // startup band show bypasses ToolbarVisibilityAdapter, so on --paused/startPaused
            // launches the strip's show+raise lands here, on top of the already-shown band.
            // Moving this above ShowOverlays hides the strip under the band until the next
            // band transition.
            if (settings.General.StartPaused || IsPausedLaunch)
                im.TogglePause();
```

- [ ] **Step 3: Wiring visibility + ordering pin (source-read).** This is the codebase's
  accepted untestable-seam shape (the `CaptureSnapshot` doc precedent: wiring is not
  unit-reachable). The behavioural rows live in Task 8's tests; add ONE source-read test to
  `tests/EyeCtrl.App.Tests/Services/Coordinators/ToolbarLifecycleCoordinatorTests.cs` (it
  exists) so the wiring cannot silently vanish AND the startup ordering stays pinned (O4),
  following the `TheAppTestsCaseCoveringLabelledSettingsRow_StillExists` content-assertion
  pattern:

```csharp
    [Fact]
    public void PauseIndicatorWiring_MirrorCommandsStrip_AndStartupPushStaysAfterShowOverlays()
    {
        var root = SourceScanner.RepoRoot().FullName;

        var coordinator = File.ReadAllText(Path.Combine(root,
            "src", "EyeCtrl.App", "Services", "Coordinators", "ToolbarLifecycleCoordinator.cs"));
        Check.WithCustomMessage(
                "The StateChangedMessage mirror no longer commands the pause strip. Without it, "
                + "an unpause with the band visible never dismisses the strip (spec H1).")
            .That(coordinator.Contains("SetPauseIndicator(paused);")).IsTrue();

        var app = File.ReadAllText(Path.Combine(root, "src", "EyeCtrl.App", "App.xaml.cs"));
        var showOverlays = app.IndexOf("ShowOverlays(splash, cursorCircle, toolbar, settings);",
            StringComparison.Ordinal);
        var togglePause = app.IndexOf("im.TogglePause();", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "The startup pause push must stay AFTER ShowOverlays (spec O4): the startup band "
                + "show bypasses the adapter, so on --paused/startPaused launches the strip's "
                + "show+raise lands in the TogglePause call — moving it before ShowOverlays "
                + "hides the strip under the band.")
            .That(togglePause).IsStrictlyGreaterThan(showOverlays);
    }
```

  (Guard `IndexOf` results implicitly: `-1 > -1` is false, so either marker going missing
  fails the check — the message tells the reader which invariant broke.)

- [ ] **Step 4: Run** `dotnet test tests/EyeCtrl.App.Tests --filter
  "FullyQualifiedName~ToolbarVisibility|FullyQualifiedName~PauseIndicatorWiring"`.

- [ ] **Step 5: detect_changes, then commit (completes commit group 2)**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/Services/Coordinators/ToolbarLifecycleCoordinatorTests.cs
git commit -m "feat(app): paused-state mirror commands the edge strip; startup ordering pinned for --paused"
```

---

## Commit group 3 — separator

### Task 10: `SeparatorSpec` + the `SeparatorRow` composite + six replacements

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs` (add record; rewrite the
  `LabelSpec` wrap doc's "Models separator rule" sentence :52–55)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`CreateControl` case +
  private nested `SeparatorRow` beside `LabelledSettingsRow` :1166)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs` (:110, :153)
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs` (:33, :48, :53, :58)
- Modify: `src/EyeCtrl.Controls/DwellLabel.cs` (rewrite the `wrap` param doc :26–32)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs` (new pin)
- Test: update `tests/EyeCtrl.App.Tests/Settings/Pages/AdvancedPageSourceTests.cs` (:12–19)
- Test: `tests/EyeCtrl.Controls.Tests/DwellCompositeChildrenStabilityTests.cs` (census + still-exists)
- Test: redirect the three dash probes in `tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs`
  (project ROOT — plan review H1; the region is `TheWidestSeparatorLabel_FitsTheContentColumn`,
  :455–515)

**Interfaces:**
- Produces: `sealed record SeparatorSpec() : SettingsRowSpec("", "");` and a private nested
  `SeparatorRow : IDwellComposite` (cached empty `Children` — a readonly FIELD, never `=> []`).

- [ ] **Step 1: GitNexus impact pass** on the settings row renderer
  (`SettingsWindow.CreateControl`/`RenderSpec`) and `IDwellComposite` implementers.

- [ ] **Step 2: Write the failing tests.**

  a) `SettingsRowLayoutTests` (follow the `ALabelledRow_ForwardsItsInnerCompositesCachedChildren`
  pattern exactly — `SettingsWindow.CreateControl(...)` + `SettingsAutoPagination.NoopDwellRegistrar()`):

```csharp
    [Fact]
    public void ASeparatorRow_HasCachedEmptyChildren_AndAttaches()
    {
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            new SeparatorSpec(), new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, WideColumnWidth);

        var first = composite.Children;
        var second = composite.Children;
        Check.That(first).IsEmpty();
        Check.That(ReferenceEquals(first, second)).IsTrue();

        var host = new System.Windows.Controls.Grid();
        composite.Attach(host);
        Check.That(host.Children.Count).IsEqualTo(1);
    }
```

  b) `AdvancedPageSourceTests.Build_HasSeventeenRows_FourSeparators_ResetLast`: change
  `Check.That(rows.OfType<LabelSpec>()).HasSize(4);` to
  `Check.That(rows.OfType<SeparatorSpec>()).HasSize(4);`. `HasSize(17)` and the button asserts
  are unchanged.

  c) `DwellCompositeChildrenStabilityTests`: add `"SeparatorRow"` to the `covered` array of
  `NoProjectOutsideControls_ImplementsIDwellComposite` (after `"LabelledSettingsRow"` — the
  census sorts ordinal, so appending satisfies `ContainsExactly`), with a remark line pointing
  at the App.Tests pin, and extend the still-exists guard:

```csharp
        const string SeparatorTestName = "ASeparatorRow_HasCachedEmptyChildren_AndAttaches";
        Check.WithCustomMessage(
                "SeparatorRow's Children-stability pin is gone from SettingsRowLayoutTests. The"
                + " census above vouches for it; restore the test or remove the name here.")
            .That(File.ReadAllText(path).Contains(SeparatorTestName)).IsTrue();
```

  (`EveryCompositeInTheAssembly_IsCoveredByACaseAbove` is untouched — an `EyeCtrl.App`-nested
  type is invisible to `typeof(IDwellComposite).Assembly` reflection.)

  d) `SettingsContentFitTests.TheWidestSeparatorLabel_FitsTheContentColumn`: DELETE the
  source-regex dash-run scan (:462) and both `DwellLabel('\u2500', …)` probes (:479, :502);
  replace with a span assertion on the real composite: build via
  `SettingsWindow.CreateControl(new SeparatorSpec(), ...)`, attach to the content-tree host,
  measure+arrange as the old test did, and assert the border's `ActualWidth` equals the
  content column width (the star-column bound), not the old clipped-label behaviour.

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdvancedPageSourceTests|FullyQualifiedName~SettingsRowLayoutTests"` and
  `dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~DwellCompositeChildrenStabilityTests"`.
  Expected: FAIL (no `SeparatorSpec`/`SeparatorRow`; census missing the name).

- [ ] **Step 4: Implement.**

  a) `SettingsRowSpec.cs`:

```csharp
/// <summary>
/// A full-width hairline rule separating sections within one tab's content. Renders as a
/// 1 px BrushBorderSubtle border stretching the content column (the '─'-character label it
/// replaced rendered at its text width and stopped mid-column).
/// </summary>
public sealed record SeparatorSpec() : SettingsRowSpec("", "");
```

  Rewrite the `LabelSpec` `wrap` doc's last sentence to: "Off by default: rule-like labels
  (a run of one glyph) must clip at the column edge, not stack into a block of strokes." —
  no longer naming the Models rule (it is gone).

  b) `SettingsWindow.xaml.cs` — nested beside `LabelledSettingsRow`:

```csharp
    /// <summary>
    /// The SeparatorSpec's visual: a 1 px BrushBorderSubtle rule stretching the content
    /// column, in a one-child Grid (a StackPanel would measure infinite width and the border
    /// would not stretch — the same reason LabelledSettingsRow hosts its control in a Grid).
    /// An IDwellComposite with an EMPTY cached Children list: it registers no dwell target —
    /// a rule is not one (the LabelledSettingsRow precedent).
    /// </summary>
    private sealed class SeparatorRow : IDwellComposite
    {
        private static readonly IReadOnlyList<DwellControl> NoChildren = Array.Empty<DwellControl>();
        private readonly Grid _root;

        internal SeparatorRow()
        {
            _root = new Grid();
            _root.Children.Add(new System.Windows.Controls.Border
            {
                Height = 1,
                Background = Tokens.BrushBorderSubtle,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 10, 0, 10),
            });
        }

        public void Attach(Panel parent) => parent.Children.Add(_root);
        public void Detach(Panel parent) => parent.Children.Remove(_root);
        public IReadOnlyList<DwellControl> Children => NoChildren;
    }
```

  and in `CreateControl`, before `case LabelSpec`:

```csharp
            case SeparatorSpec:
                return new SeparatorRow();
```

  c) Replace the six dash sites: ModelsPageSource (:110, :153) and AdvancedPageSource
  (:33, :48, :53, :58) — each `new LabelSpec(() => new string('\u2500', 50), Wrap: false)`
  becomes `new SeparatorSpec()`.

  d) `DwellLabel.cs` `wrap` doc (:26–32): rewrite to the neutral wording (no U+2500 rule
  reference — see the SettingsRowSpec doc; keep the clip-vs-wrap rationale sentence).

- [ ] **Step 5: Run to verify pass** — both filters from Step 3, plus
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsAutoPagination|FullyQualifiedName~SettingsTabDrift|FullyQualifiedName~SettingsRowDescriptionCompleteness"`
  (all three should be unaffected and green).

- [ ] **Step 6: detect_changes, then commit (completes commit group 3)**

```bash
# Pre-commit checks first (Global Constraints): MCP detect_changes (worktree param) +
# git diff --stat vs this task's file list.
git add src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs src/EyeCtrl.Controls/DwellLabel.cs tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdvancedPageSourceTests.cs tests/EyeCtrl.Controls.Tests/DwellCompositeChildrenStabilityTests.cs tests/EyeCtrl.App.Tests/SettingsContentFitTests.cs
git commit -m "feat(app): full-width SeparatorSpec replaces the dash-label rules in Models and Advanced"
```

---

## Final verification

- [ ] **Baseline first** (plan review L1): on `main` (or the merge-base), record
  `dotnet test EyeCtrl.slnx --filter "Category!=UI"` — this box has ~13 known
  environmentally-failing App.Tests headless; the number is the baseline, NOT zero.
- [ ] **Full non-UI suite on the branch:**
  `dotnet test EyeCtrl.slnx --filter "Category!=UI"` — require: no NEW failures vs the
  baseline, all new/updated tests green.
- [ ] **`dotnet build EyeCtrl.slnx`** clean.
- [ ] **Live pass — OWNER GATED, TWO SEPARATE CONSENTS (plan review L5):**
  1. *Consent to launch*: `src/EyeCtrl.App/bin/Debug/net10.0-windows/EyeCtrl.App.exe
     --test-mode` (never touches the real tracker), capped run per the box's standard drill.
  2. *Consent to repair-download*: dwelling the repair row fetches 1.67 GB and makes the Qwen
     LM live on the owner's only text surface — the owner decides this separately.
  Then eyeball:
  - Models tab shows the repair row against the owner's REAL corrupt `qwen2.5-1.5b.onnx`
    (log line `failed its SHA-256 integrity check` at startup); after a consented repair, a
    subsequent rebuild logs the LM loading (C1 end-to-end).
  - With the app paused: summoning the band shows the strip ABOVE it; unpausing dismisses it;
    on a `--paused` launch the strip is above the band from startup (O4).
  - Models + Advanced tabs: rules span the content column.
- [ ] **Report**: summarize per commit group; note anything the live pass contradicted.
