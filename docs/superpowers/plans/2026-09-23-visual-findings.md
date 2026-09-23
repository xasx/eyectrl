# Visual Findings Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the three actionable findings of the 2026-09-22 visual assessment — surface model
integrity failure on the Models tab (with a working repair→reload path), keep the pause edge
strip visible while the toolbar band is summoned, and replace the fake `'─'` dividers with a
real full-width separator.

**Architecture:** A per-model verification ledger on `ModelRegistryService` (Core, framework-free)
is written by `PredictionStackFactory` at stack build (verdict derived from LM availability, not
cause enumeration) and by the download path (both hashes checked). `ModelsPageSource` renders the
failed state as a danger `ButtonSpec` row wired to the existing download flow, and rebuilds on a
new `VerificationChanged` event. A new snapshot term plus swap-time capture (fed the build's own
verdict) makes the repair actually reload the prediction stack. The pause indicator becomes a
pure function of paused state via `IToolbarVisibility.SetPauseIndicator`, with conditional
`RaiseToTop()` calls in the band show paths. A `SeparatorSpec` renders as a private nested
`IDwellComposite` so neither consumer switch changes.

**Tech Stack:** .NET 10 (net10.0 / net10.0-windows), WPF, xUnit v3 + NFluent, CommunityToolkit
patterns already in repo.

**Spec:** `docs/superpowers/specs/2026-09-23-visual-findings-design.md` (revision 5, SHIP).
Work in `.worktrees/visual-findings` on `fix/visual-findings-2026-09`.

## Global Constraints

- NFluent 3.1.0 assertions (`Check.That(...)`), xUnit v3 `[Fact]`; global usings `Xunit` + `NFluent`
  come from `tests/Directory.Build.props` — do not add per-file usings for them.
- No mocking library: real services over unique temp dirs (`ModelTestFakes` pattern) or
  hand-written fakes. Unique pipe/dir names per test (`AGENTS.md` pipe rule).
- File-scoped namespaces, Allman braces, `_camelCase` fields, `var` for obvious types, no
  `#region`.
- German-locale box: never parse formatted numbers in tests; `dotnet test` output is German
  (`Fehler!` = fail).
- Run ONE test project per `dotnet test` invocation (MSB1008 on this SDK).
- **GitNexus impact pass (`impact({target, direction: "upstream"})` or CLI) before editing each
  symbol the spec lists** — `ModelRegistryService`, `PredictionStackFactory`,
  `ModelManagementService`, `ModelsPageSource`, `KeyboardStackSnapshot`/`KeyboardCoordinator`,
  `ToolbarVisibilityAdapter`, `ToolbarLifecycleCoordinator`, `IToolbarVisibility`, the settings
  row renderer, `ButtonKind` consumers. Report risk; treat UNKNOWN as unresolved (text-search
  before proceeding).
- Never launch the app without the owner's explicit OK (input-channel rule). The live pass at
  the end is owner-gated.
- No push. Three commit groups as marked.
- The worktree was created with LFS checked out (verified 2026-09-23) — completions are real
  binaries.

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
  VerificationChanged` — raised only when a value *changes*. Later tasks consume all three.

- [ ] **Step 1: Run the GitNexus impact pass on `ModelRegistryService`** (upstream). Note
  consumers; expected risk LOW-MEDIUM (Core service, many readers). Do not proceed on UNKNOWN.

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
    public void ReportVerification_RoundTrips()
    {
        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        Check.That(_registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    [Fact]
    public void ReportVerification_Overwrites_AndRaisesChangedOnlyOnChange()
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

- [ ] **Step 4: Implement.** In `ModelRegistryService.cs`, add the enum just above the class and
  the ledger inside it (the class already has a `_lock` field; the concurrent dictionary makes
  the ledger lock-free — writers run on background/gaze threads, the reader on the UI thread):

```csharp
namespace EyeCtrl.Core.Prediction;

using System.Collections.Concurrent;
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

Inside the class, next to the other fields:

```csharp
    // Writers: PredictionStackFactory.Build (background/gaze thread) and
    // ModelManagementService download completion (worker thread). Reader: the settings UI
    // thread during page build. Verified/Failed semantics per ModelVerificationOutcome.
    private readonly ConcurrentDictionary<string, ModelVerificationOutcome> _verification =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the writer's thread whenever a model's verdict CHANGES (not on
    /// same-value writes). The Models page subscribes to rebuild while open.</summary>
    public event Action? VerificationChanged;

    /// <summary>Records the most recent verification outcome for a model. Same-value writes
    /// are no-ops and do not raise <see cref="VerificationChanged"/>.</summary>
    public void ReportVerification(string modelId, ModelVerificationOutcome outcome)
    {
        var previous = _verification.GetOrAdd(modelId, outcome);
        if (!_verification.TryUpdate(modelId, outcome, previous) && previous == outcome) return;
        if (previous != outcome) VerificationChanged?.Invoke();
    }

    /// <summary>The latest verification outcome reported for a model; the default for a model
    /// nobody reported on is <see cref="ModelVerificationOutcome.NotYetVerified"/>.</summary>
    public ModelVerificationOutcome GetVerification(string modelId) =>
        _verification.TryGetValue(modelId, out var outcome)
            ? outcome
            : ModelVerificationOutcome.NotYetVerified;
```

If `GetOrAdd`/`TryUpdate` feels heavier than needed, the simpler equivalent is a `lock
(_lock)` around a plain `Dictionary` get/set with a change check — prefer whichever reads
better; the contract is the tests above.

- [ ] **Step 5: Run to verify pass** — same filter as Step 3. Expected: 4/4 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/ModelRegistryService.cs tests/EyeCtrl.Core.Tests/Prediction/ModelRegistryServiceVerificationTests.cs
git commit -m "feat(core): per-model verification ledger on ModelRegistryService"
```

### Task 2: `PredictionStackFactory` reports the verdict it built under

**Files:**
- Modify: `src/EyeCtrl.App/Services/PredictionStackFactory.cs` (LM block ~lines 368–435)
- Test: `tests/EyeCtrl.App.Tests/Services/PredictionStackFactoryVerificationTests.cs` (create)
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/ModelTestFakes.cs` (add a hash-carrying fake)

**Interfaces:**
- Consumes: Task 1's `ReportVerification`/`GetVerification`/`ModelVerificationOutcome`.
- Produces: on the factory — `public (string ModelId, ModelVerificationOutcome Outcome)?
  LastBuildVerification { get; private set; }` — the verdict THE BUILD REPORTED, or null when
  the build verified nothing (prediction off, model off, model absent, custom/no-hash). Task 5's
  swap-time capture consumes this (final-review R1: the stored snapshot must take the build's
  own verdict, never a ledger re-read).

- [ ] **Step 1: GitNexus impact pass on `PredictionStackFactory`** (upstream). Expected: the
  keyboard reload path and LM consumers; no edits to callers.

- [ ] **Step 2: Extend `ModelTestFakes.WithDownloadedBundled` with a hash-carrying overload**
  (append below the existing method — the existing one stays, all its callers unchanged):

```csharp
    /// <summary>
    /// Same as <see cref="WithDownloadedBundled(string)"/> but the manifest entry carries REAL
    /// SHA-256 hashes of the dropped bytes, so the prediction stack's hash gate actually passes
    /// for them. (The 1-byte fake files cannot initialize ONNX, so the predictor still ends up
    /// unavailable — which is exactly the deterministic state the factory tests assert.)
    /// </summary>
    public (ModelRegistryService registry, ModelManagementService mgmt) WithDownloadedBundled(
        string id, string sha256, string tokenizerSha256)
    {
        var manifest = new ModelManifest
        {
            Models =
            {
                [id] = new ModelEntry
                {
                    DisplayName = "Test " + id,
                    Url = "https://example.invalid/model.onnx",
                    Sha256 = sha256,
                    SizeBytes = 1,
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                    TokenizerSha256 = tokenizerSha256,
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var onnx = ModelConstants.ModelFileNames.TryGetValue(id, out var named)
            ? named
            : $"{id.ToLowerInvariant()}.onnx";
        var modelBytes = new byte[] { 0x01 };
        File.WriteAllBytes(Path.Combine(Dir, onnx), modelBytes);
        File.WriteAllBytes(Path.Combine(Dir, onnx.Replace(".onnx", ".tokenizer.json")),
            new byte[] { 0x02 });

        var registry = BuildRegistry(manifest, settings);
        var mgmt = new ModelManagementService(registry, new ModelDownloadService(Dir), settings);
        return (registry, mgmt);
    }
```

Also add a static hash helper to `ModelTestScope`:

```csharp
    /// <summary>Lowercase hex SHA-256 of the given bytes — what a manifest entry must carry for
    /// the stack's hash gate to pass those bytes.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }
```

- [ ] **Step 3: Write the failing tests**

```csharp
namespace EyeCtrl.App.Tests.Services;

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

    [Fact]
    public void SyncBuild_PresentHashGatedModel_UnavailableLm_ReportsFailed()
    {
        var bytes = new byte[] { 0x01 };
        var tokBytes = new byte[] { 0x02 };
        var (registry, _) = _scope.WithDownloadedBundled(
            "135M", ModelTestScope.Sha256Hex(bytes), ModelTestScope.Sha256Hex(tokBytes));

        var factory = new PredictionStackFactory(); // sync mode: Build completes inside Rebuild
        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
        Check.That(factory.LastBuildVerification).IsNotNull();
        Check.That(factory.LastBuildVerification!.Value.ModelId).IsEqualTo("135M");
        Check.That(factory.LastBuildVerification!.Value.Outcome)
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    [Fact]
    public void SyncBuild_EmptyManifestHash_NoReport_LedgerStaysNotYetVerified()
    {
        var (registry, _) = _scope.WithDownloadedBundled(); // Sha256 = "" (exempt, custom-style)
        var factory = new PredictionStackFactory();
        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        Check.That(factory.LastBuildVerification).IsNull();
    }

    [Fact]
    public void SyncBuild_ModelFileAbsent_NoReport()
    {
        var bytes = new byte[] { 0x01 };
        var (registry, _) = _scope.WithDownloadedBundled(
            "135M", ModelTestScope.Sha256Hex(bytes), ModelTestScope.Sha256Hex(bytes));
        // Remove BOTH files: the model is named+hashed but not on disk -> the factory must not
        // claim anything (the Models tab shows the plain Download button for it).
        var onnx = Path.Combine(_scope.Dir, "smollm2-135m.onnx");
        var tok = Path.Combine(_scope.Dir, "smollm2-135m.tokenizer.json");
        if (File.Exists(onnx)) File.Delete(onnx);
        if (File.Exists(tok)) File.Delete(tok);

        var factory = new PredictionStackFactory();
        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        Check.That(factory.LastBuildVerification).IsNull();
    }
}
```

(If the fake file names differ from `smollm2-135m.onnx` on this manifest table, derive them
exactly as `ModelTestFakes` does — `ModelConstants.ModelFileNames` lookup with the lowercase
fallback — instead of hardcoding; delete-then-assert only the files the helper wrote.)

- [ ] **Step 4: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~PredictionStackFactoryVerification"`
  Expected: FAIL — `LastBuildVerification` missing (compile error).

- [ ] **Step 5: Implement.** In `PredictionStackFactory`:

  a) Add the property near `Current`:

```csharp
    /// <summary>The verdict the most recent build REPORTED (model id + outcome), or null when
    /// that build verified nothing (prediction off, model off/absent, no manifest hash). Read
    /// by the swap-time snapshot capture so `stored` carries the running stack's own verdict
    /// rather than a ledger re-read (final-review R1).</summary>
    public (string ModelId, ModelVerificationOutcome Outcome)? LastBuildVerification { get; private set; }

    private void ReportBuildVerification(
        ModelRegistryService modelRegistry, string modelSize, ModelVerificationOutcome outcome)
    {
        modelRegistry.ReportVerification(modelSize, outcome);
        LastBuildVerification = (modelSize, outcome);
    }
```

  b) In `Build`, REPLACE the final model-hash check + predictor construction (from
  `var expectedModelHash = ...` through the `lmPredictor = new SmallLMPredictor(...)`) with
  this — the original mismatch gate is preserved verbatim as arm 1, arm 2 keeps constructing
  the predictor exactly as the original `else` did and adds the report, arm 3 is the
  absent/no-hash fall-through that previously constructed WITHOUT a hash (unchanged
  behaviour, no report):

```csharp
                var modelPath = modelRegistry.GetModelPath(modelSize);
                var expectedModelHash = modelRegistry.GetModelSha256(modelSize);

                if (expectedModelHash is not null && File.Exists(modelPath)
                    && !VerifyFileSha256(modelPath, expectedModelHash))
                {
                    Log.Error(
                        "Model at {Path} failed its SHA-256 integrity check; LM suggestions disabled for this build",
                        modelPath);
                    // Verdict is availability-derived (spec C2): a hash mismatch cannot serve.
                    ReportBuildVerification(modelRegistry, modelSize, ModelVerificationOutcome.Failed);
                }
                else if (File.Exists(modelPath) && expectedModelHash is not null)
                {
                    token.ThrowIfCancellationRequested();
                    lmPredictor = new SmallLMPredictor(
                        modelsDir,
                        modelSize,
                        settings.Keyboard.PreferGPU,
                        tokenizer: tokenizer,
                        modelPath: modelPath);
                    // Availability, NOT cause enumeration (spec C2): SmallLMPredictor swallows
                    // ONNX init failures into IsAvailable = false, and an unparseable tokenizer
                    // left tokenizer == null — a hash-clean model can still be unusable, so
                    // Verified requires BOTH. The log lines above keep which cause fired.
                    var lmLive = lmPredictor is not null && lmPredictor.IsAvailable
                                 && tokenizer is not null;
                    ReportBuildVerification(modelRegistry, modelSize,
                        lmLive ? ModelVerificationOutcome.Verified
                               : ModelVerificationOutcome.Failed);
                }
                else
                {
                    // Model absent or manifest has no hash: report nothing — the Models tab
                    // shows the plain Download button (absent) or the entry is exempt
                    // (custom/no-hash). Clear any stale verdict from an older build so this
                    // build's report is unambiguous.
                    LastBuildVerification = null;
                }
```

  c) In `Rebuild`, the prediction-disabled early-out (`!settings.Keyboard.PredictionEnabled`,
  ~line 94–107) sets `LastBuildVerification = null;` before returning — a swap-to-null then
  carries no verdict, and Task 5's merge keeps the captured (previous) term, exactly as the
  spec's null-swap edge names.

- [ ] **Step 6: Run to verify pass** — same filter. Expected: 3/3 PASS. Then run the full
  App.Tests service family:
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~PredictionStackFactory|FullyQualifiedName~KeyboardCoordinator"`
  Expected: no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/PredictionStackFactory.cs tests/EyeCtrl.App.Tests/Services/PredictionStackFactoryVerificationTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/ModelTestFakes.cs
git commit -m "feat(app): prediction stack reports per-model verification verdicts (availability-derived)"
```

### Task 3: Download path reports `Verified` (both-hash gate)

**Files:**
- Modify: `src/EyeCtrl.App/Services/ModelManagementService.cs` (~line 137, success path)
- Modify: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs` (:199–206 stale docstring)
- Test: extend `tests/EyeCtrl.App.Tests/Services/ModelManagementServiceTests.cs` (the file the
  `ModelTestFakes` doc cites for `BuildServices`)

**Interfaces:**
- Consumes: Task 1's `ReportVerification`.
- Produces: on bundled download success with BOTH `sha256` and `tokenizerSha256` non-empty,
  `registry.ReportVerification(modelId, Verified)` fires before `Complete(success)`.

- [ ] **Step 1: GitNexus impact pass on `ModelManagementService`** (upstream).

- [ ] **Step 2: Write the failing tests** (this file's transport pattern: `BuildServices(tempDir,
  settings, manifest, req => Task.FromResult(...))` with `Ok(bytes)`/`NotFound()` helpers and
  `TestContext.Current.CancellationToken`):

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

  (Uses `ModelTestScope.Sha256Hex` from Task 2 — same assembly, `InternalsVisibleTo` /
  internal access already established between these test files; if the helper's accessibility
  does not reach this file, move it to a shared spot under `tests/TestSupport/` and link it in
  both csproj `<Compile Include>` lists, matching the existing linked-helper pattern.)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModelManagementService"`
  Expected: the new tests FAIL (ledger stays NotYetVerified); existing tests PASS.

- [ ] **Step 4: Implement.** In `DownloadBundledModelAsync`, immediately before
  `ActivateDownloadedModelIfNothingRealIsActive(modelId);`:

```csharp
            // Spec M1+N4: Verified ONLY when both artifacts were hash-checked. An empty hash
            // means ModelDownloadService skipped the check (transport-trust), which is weaker
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

- [ ] **Step 6: Commit**

```bash
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
        var bytes = new byte[] { 0x01 };
        var (registry, _) = scope.WithDownloadedBundled(
            "135M", ModelTestScope.Sha256Hex(bytes), ModelTestScope.Sha256Hex(bytes));
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

  Fix every other construction site the impact pass found (expected: only tests).

- [ ] **Step 5: Run to verify pass** — same filter. Expected: PASS (the family's existing
  tests updated for the new positional argument where they construct the record directly).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/SettingsSnapshots.cs tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs
git commit -m "feat(app): keyboard stack snapshot carries the active model's verification verdict"
```

### Task 5: Swap-time capture (seed stays, swap overwrites, build's own verdict)

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs`
  (fields ~lines 38–46, `StackSwapped` wiring :118–125, `LoadKeyboard` :135/:205)
- Test: extend `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 2's `LastBuildVerification`; Task 4's term.
- Produces: `internal static KeyboardStackSnapshot WithSwappedVerdict(
  KeyboardStackSnapshot captured, (string ModelId, ModelVerificationOutcome Outcome)?
  lastBuild, string? activeModelSize)` — pure, headless-testable merge the swap handler uses.

- [ ] **Step 1: GitNexus impact pass on `KeyboardCoordinator`** (upstream). Expected HIGH-ish
  (the reload gate lives here) — read the affected processes before editing; do not proceed on
  UNKNOWN without a text-search confirmation.

- [ ] **Step 2: Write the failing tests** (pure-function tests + wiring is 2 lines covered by
  review):

```csharp
    [Fact]
    public void WithSwappedVerdict_SubstitutesOnlyForTheNamedActiveModel()
    {
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var captured = KeyboardStackSnapshot.Capture(settings, null); // ledger-neutral base
        var capturedFailed = captured with
            { ActiveModelVerification = ModelVerificationOutcome.Failed };

        var merged = KeyboardCoordinator.WithSwappedVerdict(
            capturedFailed, ("135M", ModelVerificationOutcome.Verified), "135M");

        Check.That(merged.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Verified);
    }

    [Fact]
    public void WithSwappedVerdict_DifferentModel_KeepsCapturedTerm()
    {
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var captured = KeyboardStackSnapshot.Capture(settings, null) with
            { ActiveModelVerification = ModelVerificationOutcome.Failed };

        var merged = KeyboardCoordinator.WithSwappedVerdict(
            captured, ("360M", ModelVerificationOutcome.Verified), "135M");

        Check.That(merged.ActiveModelVerification)
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    [Fact]
    public void WithSwappedVerdict_NoBuildVerdict_KeepsCapturedTerm()
    {
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var captured = KeyboardStackSnapshot.Capture(settings, null);

        var merged = KeyboardCoordinator.WithSwappedVerdict(captured, null, "135M");

        Check.That(merged).IsEqualTo(captured);
    }

    // The two reload rows the review named (N3 regression + O2a edge). ShouldReload is
    // record-equality, so these pin the BEHAVIOUR via the decision function:
    [Fact]
    public void Reload_DoesNotFire_ApplyAfterSwapWithSameVerdict()
    {
        // stored = swap-time (Verified, the build's own), current = ledger (Verified after the
        // build reported it) -> equal -> no reload.
        var settings = new EyeCtrlSettings { Keyboard = { MLModelSize = "135M" } };
        var stored = KeyboardStackSnapshot.Capture(settings, null) with
            { ActiveModelVerification = ModelVerificationOutcome.Verified };
        var current = stored; // swap capture == compare capture in the settled state
        Check.That(KeyboardCoordinator.ShouldReload(stored, current)).IsFalse();
    }

    [Fact]
    public void Reload_Fires_RepairDownloadFlipsVerdictBeforeNextApply()
    {
        // stored = swap-time Failed; the download then reports Verified; the next apply's
        // capture reads the ledger -> differs -> reload (the C1 repair path).
        var stored = KeyboardStackSnapshot.Capture(new EyeCtrlSettings(), null) with
            { ActiveModelVerification = ModelVerificationOutcome.Failed };
        var current = KeyboardStackSnapshot.Capture(new EyeCtrlSettings(), null) with
            { ActiveModelVerification = ModelVerificationOutcome.Verified };
        Check.That(KeyboardCoordinator.ShouldReload(stored, current)).IsTrue();
    }
```

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardCoordinatorTests"`
  Expected: FAIL — `WithSwappedVerdict` missing (compile error).

- [ ] **Step 4: Implement.** In `KeyboardCoordinator`:

  a) New field near `_modelRegistry` (final-review R2: the swap capture must use the LIVE
  settings instance `LoadKeyboard` was given — the same object the compare path reads — never
  a fresh `SettingsService.Load()`):

```csharp
    // The live settings instance of the most recent LoadKeyboard. The StackSwapped handler
    // re-captures the stored snapshot from THIS object (R2): a fresh SettingsService.Load()
    // could disagree on unrelated terms and fabricate a phantom diff — the exact failure the
    // CaptureSnapshot doc (:222-231) exists to prevent.
    private EyeCtrlSettings? _lastKeyboardSettings;
```

  Set it as the first line of `LoadKeyboard` (after the null guards):
  `_lastKeyboardSettings = settings;`

  b) The pure merge (extracted so it is headless-testable — same rationale as
  `ShouldReload`):

```csharp
    /// <summary>
    /// Overwrites the captured snapshot's verdict term with the verdict THE BUILD reported
    /// (final-review R1), so `stored` equals the running stack's own state by construction. A
    /// download report that lands in the dispatcher-hop window between the build's report and
    /// this swap capture cannot be absorbed: it stays in the LEDGER, which only the
    /// compare-time capture reads — so the next apply still sees it and the repair reload
    /// fires. Substitutes only when the build's model is the settings-named one.
    /// </summary>
    internal static KeyboardStackSnapshot WithSwappedVerdict(
        KeyboardStackSnapshot captured,
        (string ModelId, ModelVerificationOutcome Outcome)? lastBuild,
        string? activeModelSize)
    {
        if (lastBuild is not { } build
            || string.IsNullOrEmpty(activeModelSize)
            || !string.Equals(build.ModelId, activeModelSize, StringComparison.OrdinalIgnoreCase))
            return captured;
        return captured with { ActiveModelVerification = build.Outcome };
    }
```

  c) Extend the `StackSwapped` subscription (:120–121):

```csharp
        _predictionFactory.StackSwapped +=
            orchestrator =>
            {
                context.OverlayManager.Keyboard.SetOrchestrator(orchestrator);

                // Swap-time capture (spec N3/O2/R1): the seed from LoadKeyboard stays until the
                // first swap; each swap overwrites stored with the LIVE settings and the build's
                // own verdict. A swap-to-null (prediction disabled) carries no verdict — the
                // merge keeps the captured term, which is the previous verdict, as the spec
                // names.
                var settings = _lastKeyboardSettings;
                if (settings is null) return;
                var captured = CaptureSnapshot(settings);
                _keyboardSnapshot = WithSwappedVerdict(
                    captured, _predictionFactory.LastBuildVerification, settings.Keyboard.MLModelSize);
            };
```

  `LoadKeyboard`'s `:205` seed capture stays exactly as it is (sync mode never raises
  `StackSwapped`, and there the seed already reads the settled verdict).

- [ ] **Step 5: Run to verify pass** — same filter; then
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsSnapshots|FullyQualifiedName~KeyboardCoordinator"`.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs
git commit -m "feat(app): swap-time snapshot capture carries the build's own verdict (repair reload path)"
```

### Task 6: `ButtonKind.Danger` + `FillFor` arm + render pin

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs` (:44 enum)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (`FillFor` :1020–1025)
- Test: `tests/EyeCtrl.App.Tests/Settings/SettingsRenderMappingTests.cs`

**Interfaces:**
- Produces: `ButtonKind.Danger`; `FillFor(ButtonKind.Danger)` returns
  `(DeleteBase, Tokens.BrushSurfaceDark)` — the same `AccentWarning` family as Delete (the
  contrast-gated token; `AccentDanger` is deliberately excluded from
  `DwellButtonProgressBarContrastTests`).

- [ ] **Step 1: Write the failing test** (in `SettingsRenderMappingTests`, matching its two
  existing kind-pins):

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
        Check.That(fill).IsEqualTo(SettingsWindow.DeleteBase);
        Check.That(text).IsNotNull();
    }
```

  (`DeleteBase` is the file-internal name the existing pins use; if it is private, reference
  the expected color the way the existing tests do — copy their assertion form exactly.)

- [ ] **Step 2: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsRenderMappingTests"`.
  Expected: FAIL — `ButtonKind.Danger` missing (compile error).

- [ ] **Step 3: Implement.** `public enum ButtonKind { Default, Reset, Delete, Danger }` with a
  doc line ("non-destructive danger — repair flows; same visual family as Delete"). `FillFor`
  gains the arm `ButtonKind.Danger => (DeleteBase, Tokens.BrushSurfaceDark),`.

- [ ] **Step 4: Run to verify pass.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Settings/SettingsRenderMappingTests.cs
git commit -m "feat(app): ButtonKind.Danger — non-destructive danger styling for the repair row"
```

### Task 7: ModelsPageSource — live refresh, in-flight restructure, failed row

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs`
- Test: extend `tests/EyeCtrl.App.Tests/Settings/Pages/ModelsPageSourceTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 (ledger, event), Task 6 (`ButtonKind.Danger`).
- Produces: page rows — for a downloaded, hash-gated entry with
  `GetVerification(id) == Failed`: a `ButtonSpec` with `Id = "md_fix_" + entry.Id`,
  `Kind = ButtonKind.Danger`, label
  `⚠ {DisplayName} — model unusable · predictions fall back to dictionary · dwell to re-download`,
  activation opening the same confirm modal + `DownloadBundledAsync` as the download button.
  `VerificationChanged` triggers `RebuildContent` while the Models tab is current.

- [ ] **Step 1: Write the failing tests** (harness: `ModelTestFakes` + `TestSettingsPageContext`
  exactly like the existing tests; `TestSettingsPageContext.Build` exposes an
  `Invoke`/`RebuildContent` recording surface — read its parameters before use; if it records
  rebuilds, assert on that, otherwise assert the rows only):

```csharp
    [Fact]
    public void Build_FailedVerdict_RendersDangerRepairRow()
    {
        using var scope = ModelTestFakes.Create();
        var bytes = new byte[] { 0x01 };
        var (registry, mgmt) = scope.WithDownloadedBundled(
            "135M", ModelTestScope.Sha256Hex(bytes), ModelTestScope.Sha256Hex(bytes));
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
        var bytes = new byte[] { 0x01 };
        var (registry, mgmt) = scope.WithDownloadedBundled(
            "135M", ModelTestScope.Sha256Hex(bytes), ModelTestScope.Sha256Hex(bytes));
        registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        // Simulate in-flight: the page reads ModelManagement.CurrentDownloadId, which has an
        // internal setter (ModelManagementService.cs:38) — settable from App.Tests.
        mgmt.CurrentDownloadId = "135M";
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<ButtonSpec>().Where(b => b.Id == "md_fix_135M")).IsEmpty();
        // and the progress label is present (the existing in-flight branch's shape):
        Check.That(rows.OfType<LabelSpec>().Select(l => l.Text()))
            .HasElementThatMatches(t => t().Contains("downloading"));
    }

    [Fact]
    public void Build_VerifiedOrUnverified_KeepsTodayRows()
    {
        using var scope = ModelTestFakes.Create();
        var bytes = new byte[] { 0x01 };
        var (registry, mgmt) = scope.WithDownloadedBundled(
            "135M", ModelTestScope.Sha256Hex(bytes), ModelTestScope.Sha256Hex(bytes));
        registry.ReportVerification("135M", ModelVerificationOutcome.Verified);
        var ctx = TestSettingsPageContext.Build(registry: registry, modelManagement: mgmt);

        var rows = new ModelsPageSource(ctx).Build().Single().Rows;

        Check.That(rows.OfType<ButtonSpec>().Where(b => b.Id.StartsWith("md_fix_"))).IsEmpty();
        // "✓ Downloaded" label present for the downloaded entry:
        Check.That(rows.OfType<LabelSpec>().Select(l => l.Text()))
            .HasElementThatMatches(t => t().Contains("✓ Downloaded"));
    }
```

  If `CurrentDownloadId` has no public setter, add `internal set` — the project already uses
  `InternalsVisibleTo` for `EyeCtrl.App.Tests`.

- [ ] **Step 2: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModelsPageSourceTests"`.
  Expected: new tests FAIL (no `md_fix_` row; in-flight check unreachable for downloaded
  entries).

- [ ] **Step 3: Implement.** In `ModelsPageSource.cs`:

  a) Subscribe like the progress handler (ctor + `Detach`):

```csharp
        if (_ctx.Registry is not null)
            _ctx.Registry.VerificationChanged += OnVerificationChanged;

    private void OnVerificationChanged()
    {
        if (_ctx.ViewModel?.Settings.CurrentTabName == "Models")
            _ctx.Invoke(_ctx.RebuildContent);
    }
```

  and unsubscribe in `Detach` (mirror `DownloadProgressChanged`; guard null `_ctx.Registry`).
  Check how the existing ctor guards (`_ctx.ModelManagement is not null`) and match it —
  `Registry` may be non-null in every real context; keep the guard symmetric with the
  progress handler's.

  b) In `Build()`, restructure the per-entry loop so the IN-FLIGHT check moves ABOVE the
  downloaded/failed split (review L3 — `IsModelDownloaded` stays true throughout a repair,
  so the old placement made the progress line unreachable):

```csharp
        foreach (var entry in _ctx.Registry.Registry.Values.OrderBy(e => e.SizeBytes))
        {
            // In-flight takes precedence over BOTH branches: a repair re-download keeps the
            // files present throughout (File.Move(overwrite:true) at the end), so gating the
            // progress line on !IsModelDownloaded made it unreachable exactly when a repair
            // runs (review L3).
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
                    () => { /* unchanged download button body */ },
                    ButtonKind.Default, Width: 544, Height: 100));
                continue;
            }

            var failed = _ctx.Registry.GetVerification(entry.Id) == ModelVerificationOutcome.Failed;
            if (!failed)
            {
                var e1 = entry;
                page1.Add(new LabelSpec(() =>
                    $"  {e1.DisplayName}  {FormatSize(e1.SizeBytes)}  ✓ Downloaded"));
                continue;
            }

            // Spec O3: cause-neutral copy — the verdict is availability-derived and includes
            // non-integrity failures, so the row must not claim "integrity" nor promise that a
            // re-download fixes every cause; it is the right FIRST remedy for the common case.
            var e3 = entry;
            page1.Add(new ButtonSpec($"md_fix_{entry.Id}",
                $"⚠ {e3.DisplayName} — model unusable · predictions fall back to dictionary · dwell to re-download",
                () => _ctx.ShowModal(
                    $"Re-download {e3.DisplayName}? This replaces the files on disk.",
                    () => _ = DownloadBundledAsync(e3.Id, e3.Url, e3.Sha256, e3.TokenizerUrl, e3.TokenizerSha256)),
                ButtonKind.Danger, Width: 544, Height: 100));
        }
```

  (Carry over the original download-button body verbatim — the busy-guard + confirm modal —
  into the `md_dl_` arm; it is unchanged from today's code.)

  c) Keep the section dividers as-is in this task (Task 10 replaces them with `SeparatorSpec`).

- [ ] **Step 4: Run to verify pass** — same filter. Expected: new tests PASS, existing
  download/e2e tests PASS.

- [ ] **Step 5: Fit assertion for the new label (spec M5).** Add to
  `tests/EyeCtrl.App.Tests/Settings/SettingsContentFitTests.cs` a `[StaFact]` in the file's
  established style (reuse its `BuildContentTree`/`WindowWidthFor` helpers):

```csharp
    [StaFact]
    public void TheRepairRowLabel_WrapsInsideIts544Button_AndDoesNotClip()
    {
        // The longest real label this change introduces (~95 chars for Qwen2.5 1.5B (q4f16)).
        // The DwellButton text wraps (no clipping), so the requirement is: the rendered text
        // height at width 544 stays within the button's Height, and the label's longest WORD
        // run is narrower than 544. Build the real label and measure.
        var label = "⚠ Qwen2.5 1.5B (q4f16) — model unusable · predictions fall back to dictionary · dwell to re-download";
        var host = new StackPanel();
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        var block = new TextBlock
        {
            Text = label,
            FontSize = Tokens.FontSizeBody, // the size DwellButton renders labels at — copy it
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 544 - 16, // button's own padding, same as production
        };
        host.Children.Add(block);
        host.Measure(new Size(544, double.PositiveInfinity));

        Check.That(block.DesiredSize.Height).IsStrictlyGreaterThan(0);
        // Two lines at body size ≈ 2×line height; if this ever exceeds the 100 DIP button, the
        // row height must grow in ModelsPageSource, not clip here.
        Check.That(block.DesiredSize.Height).IsStrictlyLessThan(100);
    }
```

  Match the actual font-size token `DwellButton` uses for its label (read it before writing);
  if the measured height exceeds the button height, raise the repair row's `Height` to the
  measured need (e.g. 120) in `ModelsPageSource` and note it in the commit.

- [ ] **Step 6: Run the page + fit suites** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ModelsPageSourceTests|FullyQualifiedName~SettingsContentFitTests|FullyQualifiedName~SettingsRowDescriptionCompleteness"`.

- [ ] **Step 7: Commit (completes commit group 1)**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/ModelsPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/SettingsContentFitTests.cs
git commit -m "feat(app): Models tab renders a repair row for failed-integrity models (live refresh, in-flight precedence)"
```

---

## Commit group 2 — pause edge strip

### Task 8: `RaiseToTop` + `SetPauseIndicator` seam + adapter rework

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayWindows.cs` (`IPauseIndicatorOverlay`, :185)
- Modify: `src/EyeCtrl.App/Services/IToolbarVisibility.cs`
- Modify: `src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterHeadlessTests.cs`

**Interfaces:**
- Produces: `IPauseIndicatorOverlay.RaiseToTop()`; `IToolbarVisibility.SetPauseIndicator(bool
  visible)`. Contract: the strip's visibility is commanded ONLY through
  `SetPauseIndicator`; `ShowToolbar`/`ForceShow` conditionally raise (iff commanded visible)
  after showing the band; `HideToolbar` touches nothing.

- [ ] **Step 1: GitNexus impact pass** on `ToolbarVisibilityAdapter` + `IToolbarVisibility`
  (upstream: `ToolbarAutoHideController` callers, fakes).

- [ ] **Step 2: Write the failing tests.** The file's arrange is
  `private static (ToolbarVisibilityAdapter Adapter, List<string> Log, ExposedDwellToolbarViewModel
  Vm, FakePauseIndicator Indicator) Build()` (line ~108) with `Pump()` to run the queued
  dispatcher work, and the log strings are exactly what `FakePauseIndicator`/`FakeToolbar`
  append. First extend `FakePauseIndicator` (line ~95):

```csharp
        public bool Visible { get; private set; }

        public void Show() { Visible = true; _log.Add("Indicator.Show"); }
        public void Hide() { Visible = false; _log.Add("Indicator.Hide"); }
        public void RaiseToTop() => _log.Add("Indicator.RaiseToTop");
```

  Then the tests (same style as `HideToolbar_DeactivatesDwellBeforeHiding`):

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
        Check.That(log).Contains("Toolbar.ShowThenSlideIn"); // band shown (FakeToolbar's entry)
        var bandIndex = log.IndexOf(log.First(t => t.StartsWith("Toolbar.")));
        var raiseIndex = log.IndexOf("Indicator.RaiseToTop");
        Check.That(raiseIndex).IsGreaterOrEqualThan(0);
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

  (`IsGreaterOrEqualThan` / `IsStrictlyGreaterThan` are the NFluent names — see the
  reference-nfluent-assertions note in the repo memory; do not guess `OrEqualTo` variants.)

- [ ] **Step 3: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ToolbarVisibilityAdapterHeadlessTests"`.
  Expected: FAIL — `SetPauseIndicator`/`RaiseToTop` missing.

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

- [ ] **Step 5: Run to verify pass** — same filter. Then the whole file's suite.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/IOverlayWindows.cs src/EyeCtrl.App/Services/IToolbarVisibility.cs src/EyeCtrl.App/Services/ToolbarVisibilityAdapter.cs tests/EyeCtrl.App.Tests/Services/ToolbarVisibilityAdapterHeadlessTests.cs
git commit -m "feat(app): pause strip commanded via SetPauseIndicator; band shows raise it above"
```

### Task 9: Driver wiring + startup ordering pin

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs`
  (mirror at :57–60)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (ordering comment at the `TogglePause` call ~:284)

**Interfaces:**
- Consumes: Task 8's `SetPauseIndicator`. The existing `StateChangedMessage` mirror gains the
  strip command; the startup push arrives via `im.TogglePause()` at App.xaml.cs:284, which
  already runs AFTER `ShowOverlays` (:281) — the O4 ordering, pinned by comment + test.

- [ ] **Step 1: Implement the mirror** (one line):

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

- [ ] **Step 3: Wiring visibility.** This is the codebase's accepted untestable-seam shape
  (the `CaptureSnapshot` doc precedent: wiring is not unit-reachable). The behavioural rows
  live in Task 8's tests; add ONE source-read test so the wiring cannot silently vanish AND
  the startup ordering stays pinned (O4), following the
  `TheAppTestsCaseCoveringLabelledSettingsRow_StillExists` content-assertion pattern:

```csharp
    [Fact]
    public void PauseIndicatorWiring_StateChangedMirror_CommandsStrip_AfterStartupBandShow()
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

  (Guard `IndexOf` results against -1 implicitly: `-1 > -1` is false, so either marker going
  missing fails the check — the message tells the reader which invariant broke.)

- [ ] **Step 4: Run** `dotnet test tests/EyeCtrl.App.Tests --filter
  "FullyQualifiedName~ToolbarVisibility|FullyQualifiedName~PauseIndicatorWiring"`.

- [ ] **Step 5: Commit (completes commit group 2)**

```bash
git add src/EyeCtrl.App/Services/Coordinators/ToolbarLifecycleCoordinator.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/
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
- Test: redirect the three dash probes in `tests/EyeCtrl.App.Tests/Settings/SettingsContentFitTests.cs` (:455–515 region)

**Interfaces:**
- Produces: `sealed record SeparatorSpec() : SettingsRowSpec("", "");` and a private nested
  `SeparatorRow : IDwellComposite` (cached empty `Children` — a readonly FIELD, never `=> []`).

- [ ] **Step 1: Write the failing tests.**

  a) `SettingsRowLayoutTests` (follow the `ALabelledRow_ForwardsItsInnerCompositesCachedChildren`
  pattern exactly — `SettingsWindow.CreateControl(...)` + `SettingsAutoPagination.NoopDwellRegistrar()`):

```csharp
    [Fact]
    public void ASeparatorRow_HasCachedEmptyChildren_AndSpansTheColumn()
    {
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            new SeparatorSpec(), new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, WideColumnWidth);

        var first = composite.Children;
        var second = composite.Children;
        Check.That(first).IsEmpty();
        Check.That(ReferenceEquals(first, second)).IsTrue();

        // The rule spans the content column: a Grid/Border given the finite width it needs
        // (LabelledSettingsRow's doc explains why a StackPanel would measure infinite).
        var host = new System.Windows.Controls.Grid();
        composite.Attach(host);
        host.Measure(new System.Windows.Size(WideColumnWidth, double.PositiveInfinity));
        Check.That(host.Children.Count).IsEqualTo(1);
    }
```

  b) `AdvancedPageSourceTests.Build_HasSeventeenRows_FourSeparators_ResetLast`: change
  `Check.That(rows.OfType<LabelSpec>()).HasSize(4);` to
  `Check.That(rows.OfType<SeparatorSpec>()).HasSize(4);` (rename the test
  `..._FourSeparators_...` stays accurate). `HasSize(17)` and the button asserts are unchanged.

  c) `DwellCompositeChildrenStabilityTests`: add `"SeparatorRow"` to the `covered` array of
  `NoProjectOutsideControls_ImplementsIDwellComposite` (with a remark line pointing at the
  App.Tests pin), and extend the still-exists guard to also require the new pin:

```csharp
        const string SeparatorTestName = "ASeparatorRow_HasCachedEmptyChildren_AndSpansTheColumn";
        Check.WithCustomMessage(
                "SeparatorRow's Children-stability pin is gone from SettingsRowLayoutTests. The"
                + " census above vouches for it; restore the test or remove the name here.")
            .That(File.ReadAllText(path).Contains(SeparatorTestName)).IsTrue();
```

  (`EveryCompositeInTheAssembly_IsCoveredByACaseAbove` is untouched — an `EyeCtrl.App`-nested
  type is invisible to `typeof(IDwellComposite).Assembly` reflection.)

  d) `SettingsContentFitTests`: in `TheWidestSeparatorLabel_FitsTheContentColumn`, DELETE the
  source-regex dash-run scan (:462) and both `DwellLabel('\u2500', …)` probes (:479, :502);
  replace with a span assertion on the real composite: build via
  `SettingsWindow.CreateControl(new SeparatorSpec(), ...)`, attach to the content-tree host,
  measure+arrange as the old test did, and assert the border's `ActualWidth` equals the
  content column width (the star-column bound), not the old clipped-label behaviour.

- [ ] **Step 2: Run to verify failure** —
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdvancedPageSourceTests|FullyQualifiedName~SettingsRowLayoutTests"` and
  `dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~DwellCompositeChildrenStabilityTests"`.
  Expected: FAIL (no `SeparatorSpec`/`SeparatorRow`; census missing the name).

- [ ] **Step 3: Implement.**

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

- [ ] **Step 4: Run to verify pass** — both filters from Step 2, plus
  `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsAutoPagination|FullyQualifiedName~SettingsTabDrift|FullyQualifiedName~SettingsRowDescriptionCompleteness"`
  (all three should be unaffected and green).

- [ ] **Step 5: Commit (completes commit group 3)**

```bash
git add src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.App/Views/Settings/Sources/ModelsPageSource.cs src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs src/EyeCtrl.Controls/DwellLabel.cs tests/EyeCtrl.App.Tests/Settings/SettingsRowLayoutTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdvancedPageSourceTests.cs tests/EyeCtrl.Controls.Tests/DwellCompositeChildrenStabilityTests.cs tests/EyeCtrl.App.Tests/Settings/SettingsContentFitTests.cs
git commit -m "feat(app): full-width SeparatorSpec replaces the dash-label rules in Models and Advanced"
```

---

## Final verification

- [ ] **Full non-UI suite** (CI parity, one project per invocation is unnecessary here — the
  solution form works on this box):
  `dotnet test EyeCtrl.slnx --filter "Category!=UI"` — expect green (German output:
  `Fehler!` = fail).
- [ ] **`dotnet build EyeCtrl.slnx`** clean.
- [ ] **Live pass — OWNER GATED: ask before launching.** With explicit OK: launch
  `src/EyeCtrl.App/bin/Debug/net10.0-windows/EyeCtrl.App.exe --test-mode` (never touches the
  real tracker), capped run per the box's standard drill, and eyeball:
  1. Models tab shows the repair row against the owner's REAL corrupt
     `qwen2.5-1.5b.onnx` (log line `failed its SHA-256 integrity check` at startup), and —
     after dwelling the repair row and confirming — a subsequent rebuild logs the LM loading
     (C1 end-to-end: the re-download actually re-enables the LM).
  2. With the app paused: summoning the band shows the strip ABOVE it; unpausing dismisses
     it; on a `--paused` launch the strip is above the band from startup (O4).
  3. Models + Advanced tabs: rules span the content column.
- [ ] **Report**: summarize per commit group; note anything the live pass contradicted.
