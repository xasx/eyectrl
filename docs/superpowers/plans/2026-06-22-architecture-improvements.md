# Architecture Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply six safety-first architecture improvements to the EyeCtrl codebase: GitNexus index refresh, GitHub Actions CI, warning cleanup (~380 warnings), Platform `FrameworkReference` narrowing, `App.OnStartup` extraction, and ONNX/Tokenizers extraction to a new `EyeCtrl.LM` project.

**Architecture:** Four sequential phases ordered so each benefits from the previous one's safety net. Phase 1 lands CI + refreshed index. Phase 2 cleans all ~380 build warnings (38 nullable/test + 337 CS1591 XML docs). Phase 3 performs two low-risk refactors. Phase 4 extracts `SmallLMPredictor` + `LMTokenizer` to a new `EyeCtrl.LM` project with an `ILanguageModelPredictor` interface seam.

**Tech Stack:** .NET 10.0, xUnit v3 (3.2.2) + NFluent, CommunityToolkit.Mvvm v8.4.0, GitHub Actions `windows-latest`, WPF.

**Warning note:** `dotnet build` prints each warning twice (raw log ~810 lines for ~375 distinct warnings). All counts in this plan are *distinct* (deduped). Count warnings with `Select-String "warning CS..." | ForEach-Object { $_.Line } | Sort-Object -Unique`.

---

## File Map

| File | Phase | Action | Purpose |
|------|-------|--------|---------|
| `.github/workflows/ci.yml` | 1 | Create | GitHub Actions CI pipeline |
| `src/EyeCtrl.Core/**/*.cs` (multiple) | 2.1 | Modify | Nullable/test warning fixes |
| `src/EyeCtrl.Core/**/*.cs` (all public types) | 2.2 | Modify | XML doc comments on ~337 public members |
| `src/EyeCtrl.Core/EyeCtrl.Core.csproj` | 2.2 | Modify | Add `<WarningsAsErrors>CS1591</WarningsAsErrors>` ratchet |
| `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` | 3.1 | Modify | Narrow `FrameworkReference` → `UseWPF` |
| `src/EyeCtrl.App/App.xaml.cs` | 3.2 | Modify | Extract helpers from `OnStartup` |
| `src/EyeCtrl.Core/Prediction/ILanguageModelPredictor.cs` | 4 | Create | Interface for LM predictor seam |
| `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs` | 4 | Modify | Depend on `ILanguageModelPredictor?` |
| `src/EyeCtrl.LM/EyeCtrl.LM.csproj` | 4 | Create | New project for ONNX/Tokenizer code |
| `src/EyeCtrl.LM/SmallLMPredictor.cs` | 4 | Move | From Core/Prediction/ |
| `src/EyeCtrl.LM/LMTokenizer.cs` | 4 | Move | From Core/Prediction/ |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | 4 | Modify | Type the local to `ILanguageModelPredictor` |
| `EyeCtrl.slnx` | 4 | Modify | Add new projects |
| `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj` | 4 | Modify | Add ref to `EyeCtrl.LM` |
| `tests/EyeCtrl.LM.Tests/**` | 4 | Create | Tests for moved types |
| `tests/EyeCtrl.Core.Tests/Prediction/SmallLMPredictorTests.cs` | 4 | Delete | Moved to LM.Tests |
| `tests/EyeCtrl.Core.Tests/Prediction/LMTokenizerTests.cs` | 4 | Delete | Moved to LM.Tests |
| `ARCHITECTURE.md` | 4.1, 4.2 | Modify | MVVM invariant note + LM project entry |
| `AGENTS.md` | 1, 3.1, 4.1, 4.2 | Modify | CI section, Platform ref, MVVM note, LM project |
| `README.md` | 1, 4.2 | Modify | CI badge/note, project structure |

---

### Task 1: GitNexus Index Refresh (Phase 1.1)

**Files:** None (local-only operation)

- [ ] **Step 1: Refresh the index**

Run: `node .gitnexus/run.cjs analyze`
Expected: Completes successfully. Verify staleness is gone.

- [ ] **Step 2: Verify freshness**

Run: `git log -1 --format="%h"` to get HEAD commit.
Check that the GitNexus index is at HEAD (no commitsBehind).

---

### Task 2: Create GitHub Actions CI Workflow (Phase 1.2)

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Create the CI workflow file**

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build-and-test:
    name: Build & Unit Tests
    runs-on: windows-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', '**/Directory.Build.props', 'global.json') }}
          restore-keys: nuget-${{ runner.os }}-

      - name: Build
        run: dotnet build EyeCtrl.slnx

      - name: Run unit tests
        run: dotnet test --filter "Category!=UI" --no-build

  ui-tests:
    name: UI Tests
    runs-on: windows-latest
    continue-on-error: true
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', '**/Directory.Build.props', 'global.json') }}
          restore-keys: nuget-${{ runner.os }}-

      - name: Build
        run: dotnet build EyeCtrl.slnx

      - name: Run UI tests
        run: dotnet test --filter "Category=UI" --no-build
```

- [ ] **Step 2: Verify the workflow locally (YAML syntax)**

Run: (no local verification possible; push to GitHub to test)

- [ ] **Step 3: Update AGENTS.md — add CI section**

In `AGENTS.md`, after the "Test" section, add:

```markdown
## CI

GitHub Actions runs on `push` to `main` and `pull_request` targeting `main`:
- `build-and-test` — builds solution, runs `dotnet test --filter "Category!=UI"` (blocking)
- `ui-tests` — runs FlaUI tests with `Category=UI` (non-blocking, `continue-on-error: true`)

No linter, formatter, or code coverage configured.
```

- [ ] **Step 4: Update README.md — add CI badge**

At top of README.md, after the title, add:

```markdown
[![CI](https://github.com/xasx/eyectrl/actions/workflows/ci.yml/badge.svg)](https://github.com/xasx/eyectrl/actions/workflows/ci.yml)
```

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml AGENTS.md README.md
git commit -m "ci: add GitHub Actions workflow (build + test, UI non-blocking)"
```

---

### Task 3: Fix Nullable & Test Warnings (Phase 2.1)

**Files:** Multiple test files + 1 production file. ~38 distinct warnings.

**Warning breakdown (distinct):**
| Code | Count | Pattern |
|------|-------|---------|
| CS8602 | 28 | Null-forgiving `!` after `Check.That(x).IsNotNull()` |
| CS8629 | 6 | Explicit `!.Value` / `!` on nullable value types |
| CS8604 | 2 | Null-forgiving on nullable ref → non-nullable param |
| xUnit1051 | 2 | Pass `TestContext.Current.CancellationToken` |

- [ ] **Step 1: Find all nullable/test warnings to fix**

Run:
```powershell
dotnet build EyeCtrl.slnx 2>&1 | Select-String "warning CS8602|warning CS8629|warning CS8604|warning xUnit1051" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

Capture output. This is the fix list.

- [ ] **Step 2: Fix CS8602 warnings in tests**

Pattern — after `Check.That(x).IsNotNull()`, dereference with `x!.Property`:

```csharp
// Before:
Check.That(result).IsNotNull();
var value = result.Property;  // CS8602

// After:
Check.That(result).IsNotNull();
var value = result!.Property;
```

Apply to each CS8602 location. The files are:
- `tests/EyeCtrl.Core.Tests/Settings/SettingsPropertyValidationTests.cs`
- `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs`
- `tests/EyeCtrl.Core.Tests/Prediction/FileDictionaryProviderTests.cs`
- `tests/EyeCtrl.App.Tests/Services/ManualSelectionDwellServiceTests.cs`

- [ ] **Step 3: Fix CS8629 warnings**

In production: `src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs` — explicit `!.Value` on nullable value types.

In tests: same pattern — null-forgiving after assertion guards.

- [ ] **Step 4: Fix CS8604 warnings**

Same pattern as CS8602 — add `!` where the nullable is guarded by an assertion.

- [ ] **Step 5: Fix xUnit1051 warnings**

In `tests/EyeCtrl.ViewModels.Tests/CalibrationViewModelTests.cs` (lines ~93, ~104):

```csharp
// Before:
await Task.Delay(100);

// After:
await Task.Delay(100, TestContext.Current.CancellationToken);
```

- [ ] **Step 6: Verify fixes**

Run:
```powershell
dotnet build EyeCtrl.slnx 2>&1 | Select-String "warning CS8602|warning CS8629|warning CS8604|warning xUnit1051" | ForEach-Object { $_.Line } | Sort-Object -Unique
```
Expected: Empty output (0 distinct warnings).

- [ ] **Step 7: Run tests to confirm no regressions**

```bash
dotnet test --filter "Category!=UI"
```
Expected: Same pass count as before.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "fix: suppress nullable and test warnings (~38 distinct)"
```

---

### Task 4: XML Doc Comments — Models/, Enums/, MathConstants.cs, LayoutConstants.cs, BackdropType.cs (Phase 2.2 batch 1)

**Files:** ~13 files in `src/EyeCtrl.Core/Models/`, `src/EyeCtrl.Core/Enums/`, plus `MathConstants.cs`, `LayoutConstants.cs`, `BackdropType.cs`.

**Convention:**

| Member type | Doc content |
|---|---|
| Class/record | One-sentence purpose. Note invariants (e.g., coordinates in physical screen pixels). |
| Method | Purpose, parameter semantics, return value. Note side effects. |
| Property | What it represents, units (px, µs, ms), valid ranges if constrained. |
| Enum | The concept; each member gets a `<summary>`. |

- [ ] **Step 1: Document Models/**

Every public class, record, property, and method in `src/EyeCtrl.Core/Models/*.cs` that lacks `///` gets a `<summary>`. Key files:

- `PhysicalPoint.cs` — already partially documented; fill gaps
- `GazePoint.cs`, `LogicalPoint.cs`, `NormalizedPoint.cs` — coordinate types
- `PhysicalRect.cs`, `ScreenBounds.cs` — spatial types
- `ControlInfo.cs`, `InteractionState.cs`, `InteractionAction.cs` — UI metadata
- `GazeFilterMode.cs`, `ViewPosition.cs` — enums in Models/
- `NoteData.cs` — note persistence model
- `PositionIndicatorStyle.cs`, `SelectionMode.cs` — enums

For each type, write a one-sentence summary. For each property, describe what it holds and units.

- [ ] **Step 2: Document Enums/**

Document `src/EyeCtrl.Core/Enums/GazeProviderType.cs` — describe each enum member.

- [ ] **Step 3: Document standalone constants/types**

- `src/EyeCtrl.Core/MathConstants.cs` — document the class and each constant
- `src/EyeCtrl.Core/LayoutConstants.cs` — document the class and each constant
- `src/EyeCtrl.Core/BackdropType.cs` — document the enum

- [ ] **Step 4: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```
Count should decrease proportional to files documented in this batch.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/ src/EyeCtrl.Core/Enums/ src/EyeCtrl.Core/MathConstants.cs src/EyeCtrl.Core/LayoutConstants.cs src/EyeCtrl.Core/BackdropType.cs
git commit -m "docs: add XML docs to Core Models, Enums, and constant types"
```

---

### Task 5: XML Doc Comments — Interfaces/ (Phase 2.2 batch 2)

**Files:** `src/EyeCtrl.Core/Interfaces/*.cs`

- [ ] **Step 1: Document all public interfaces**

Files:
- `IGazeFilter.cs` — document the interface and `Filter` method
- `IControlFinder.cs` — document interface and `FindTarget`/`FindNearestInteractiveControl`
- `IGazeProvider.cs` — document interface, `Start`/`Stop`, and `GazePointReceived` event
- `IInputSimulator.cs` — document interface and each method's side effects (keyboard/mouse input synthesized)

Each interface gets a contract summary. Each method gets `<param>` and `<returns>` where applicable.

- [ ] **Step 2: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/
git commit -m "docs: add XML docs to Core Interfaces"
```

---

### Task 6: XML Doc Comments — Settings/ (Phase 2.2 batch 3)

**Files:** All 13 files in `src/EyeCtrl.Core/Settings/`

- [ ] **Step 1: Document settings classes**

Key files (top contributors):
- `KeyboardSettings.cs` (9 undocumented members) — document each property with units/ranges
- `ActionMenuSettings.cs`, `AutoSelectionSettings.cs`, `BackdropSettings.cs`, `CalibrationSettings.cs`, `CursorSettings.cs`, `DwellSettings.cs`, `ExternalDwellSettings.cs`, `GeneralSettings.cs`, `ManualSelectionSettings.cs`, `ThemeSettings.cs`, `ToolbarSettings.cs`
- `EyeCtrlSettings.cs` — the root aggregate
- `ValidationHelper.cs` — document validation methods
- `IValidatable.cs` — document the validation contract
- `SettingsService.cs` — already partially documented; fill gaps

For properties: describe what the setting controls, units (ms, px, etc.), valid ranges if constrained.
For `[ObservableProperty]` source fields: document the field; the generated property inherits it (CS1591 does not fire on generated code).

- [ ] **Step 2: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/
git commit -m "docs: add XML docs to Core Settings classes"
```

---

### Task 7: XML Doc Comments — Processing/ (Phase 2.2 batch 4)

**Files:** `src/EyeCtrl.Core/Processing/*.cs`

- [ ] **Step 1: Document processing classes**

Key files:
- `DwellDetector.cs` — document `AddTarget`, `RemoveTarget`, `Update`, `SetCooldown`, `ResetAllState`
- `DwellTarget.cs` — document properties (position, radius, timing)
- `FixationDetector.cs`, `FixationCentroidTracker.cs` — document fixation algorithms
- `GapInterpolator.cs` — document gap-filling behavior
- `OneEuroFilter.cs`, `DirectFilter.cs`, `SmoothedFilter.cs` — document filter algorithms
- `GazeFilterFactory.cs` — document factory method and filter selection
- `InteractionManager.cs` — document state machine, methods, events
- `IDwellRegistrar.cs` — document registration contract

- [ ] **Step 2: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Processing/
git commit -m "docs: add XML docs to Core Processing classes"
```

---

### Task 8: XML Doc Comments — Calibration/ (Phase 2.2 batch 5)

**Files:** `src/EyeCtrl.Core/Calibration/*.cs`

- [ ] **Step 1: Document calibration classes**

Files:
- `CalibrationData.cs` (10 members) — document data shape, coordinate systems
- `CalibrationService.cs` (15 members) — document Load/Save/transform lifecycle
- `CalibrationPoint.cs`, `CalibrationPointCollector.cs`, `CalibrationState.cs`
- `AffineTransform.cs`, `TransformType.cs`, `ICalibrationTransform.cs`
- `CalibrationValidator.cs`, `CalibrationTargetPositionHelper.cs`

Note coordinate system (physical pixels) in class summaries. Math-heavy methods need equation descriptions.

- [ ] **Step 2: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/
git commit -m "docs: add XML docs to Core Calibration classes"
```

---

### Task 9: XML Doc Comments — Prediction/ (non-ML) (Phase 2.2 batch 6)

**Files:** `src/EyeCtrl.Core/Prediction/*.cs` except `SmallLMPredictor.cs` and `LMTokenizer.cs`

- [ ] **Step 1: Document prediction classes (excluding ML)**

Files:
- `WordPredictor.cs` — document prediction pipeline, n-gram models, dictionary management
- `BigramModel.cs`, `PhraseModel.cs` — document statistical model methods
- `SentenceCompletionIndex.cs` — document trie-based completion
- `FuzzyEditDistance.cs` — document algorithm and parameters
- `CapitalizationHelper.cs`, `CasingHelper.cs`, `CasingPreferenceTracker.cs` — document text formatting
- `KeyboardAdjacencyMap.cs` — document spatial keyboard model
- `LanguageDetector.cs` — document language detection
- `PredictionResult.cs` — document fields (Text, Score, Confidence, Type)
- `FileDictionaryProvider.cs`, `IDictionaryProvider.cs` — document dictionary loading
- `ModelConstants.cs` — document model file name constants
- `ModelManifest.cs` — document manifest schema
- `ModelRegistry.cs` — document registry structure
- `ModelDownloadService.cs` (19 members) — document download lifecycle
- `ModelRegistryService.cs` (16 members) — document registry operations

- [ ] **Step 2: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/WordPredictor.cs src/EyeCtrl.Core/Prediction/BigramModel.cs src/EyeCtrl.Core/Prediction/PhraseModel.cs src/EyeCtrl.Core/Prediction/SentenceCompletionIndex.cs src/EyeCtrl.Core/Prediction/FuzzyEditDistance.cs src/EyeCtrl.Core/Prediction/CapitalizationHelper.cs src/EyeCtrl.Core/Prediction/CasingHelper.cs src/EyeCtrl.Core/Prediction/CasingPreferenceTracker.cs src/EyeCtrl.Core/Prediction/KeyboardAdjacencyMap.cs src/EyeCtrl.Core/Prediction/LanguageDetector.cs src/EyeCtrl.Core/Prediction/PredictionResult.cs src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs src/EyeCtrl.Core/Prediction/IDictionaryProvider.cs src/EyeCtrl.Core/Prediction/ModelConstants.cs src/EyeCtrl.Core/Prediction/ModelManifest.cs src/EyeCtrl.Core/Prediction/ModelRegistry.cs src/EyeCtrl.Core/Prediction/ModelDownloadService.cs src/EyeCtrl.Core/Prediction/ModelRegistryService.cs
git commit -m "docs: add XML docs to Core Prediction classes (non-ML)"
```

---

### Task 10: XML Doc Comments — Prediction/ (ML) + Keyboard/ + Services/ (Phase 2.2 batch 7)

**Files:** `SmallLMPredictor.cs`, `LMTokenizer.cs`, `Keyboard/*.cs`, `Services/DpiHelper.cs`

- [ ] **Step 1: Document ML prediction classes**

- `SmallLMPredictor.cs` (8 members) — document Predict, TryGetResults, Cancel, IsAvailable, constructor
- `LMTokenizer.cs` — document Encode, Decode, VocabSize, BosTokenId, EosTokenId

- [ ] **Step 2: Document Keyboard/ classes**

Files: `KeyboardLayout.cs`, `KeyboardLayoutLoader.cs`, `KeyDefinition.cs`, `ModifierKey.cs`, `SubLayout.cs`, `EmojiData.cs`, `EmojiCategory.cs`, `EmojiDataLoader.cs`, `RecentEmojiStore.cs`

Document: layout structure, key properties, loader behavior, emoji data model.

- [ ] **Step 3: Document Services/**

- `DpiHelper.cs` — document `Initialize`, `LogicalToPhysicalX/Y`, `PhysicalToLogicalX/Y`, `ScaleX/Y`

- [ ] **Step 4: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs src/EyeCtrl.Core/Prediction/LMTokenizer.cs src/EyeCtrl.Core/Keyboard/ src/EyeCtrl.Core/Services/DpiHelper.cs
git commit -m "docs: add XML docs to ML predictors, Keyboard, and DpiHelper"
```

---

### Task 11: XML Doc Comments — GazeEngine.cs + PredictionOrchestrator.cs (Phase 2.2 batch 8)

**Files:** `src/EyeCtrl.Core/GazeEngine.cs`, `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs`

- [ ] **Step 1: Complete GazeEngine.cs docs**

`GazeEngine.cs` already has some docs — extend to cover:
- `OnGazePoint` — the central pipeline entry point
- `Start`/`Stop` — lifecycle
- All events: `CursorMoved`, `FixationStarted`, `FixationEnded`, `DwellProgressed`, `DwellActivated`

- [ ] **Step 2: Document PredictionOrchestrator.cs**

`PredictionOrchestrator.cs` (14 members) — document:
- Constructor parameters (`wordPredictor`, `sentenceIndex`, `lmPredictor`, `mlEnabled`)
- `GetSuggestions` — merging strategy (word predictor → sentence completions → LM)
- `LearnWord`, `LearnSequence`, `SaveUserDictionary` — learning methods
- `WordPredictor` property — exposed for dictionary save
- `Dispose` — disposes LM predictor

- [ ] **Step 3: Verify batch**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs
git commit -m "docs: complete XML docs for GazeEngine and PredictionOrchestrator"
```

---

### Task 12: Lock In the CS1591 Ratchet (Phase 2.2 final)

**Files:** `src/EyeCtrl.Core/EyeCtrl.Core.csproj`

- [ ] **Step 1: Confirm zero CS1591 warnings**

```powershell
dotnet build src/EyeCtrl.Core --no-incremental 2>&1 | Select-String "warning CS1591" | ForEach-Object { $_.Line } | Sort-Object -Unique
```
Expected: Empty output.

- [ ] **Step 2: Add WarningsAsErrors for CS1591**

In `src/EyeCtrl.Core/EyeCtrl.Core.csproj`, after the `<PropertyGroup>` containing `<GenerateDocumentationFile>true</GenerateDocumentationFile>`:

```xml
<WarningsAsErrors>CS1591</WarningsAsErrors>
```

The full PropertyGroup becomes:

```xml
<PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <WarningsAsErrors>CS1591</WarningsAsErrors>
</PropertyGroup>
```

- [ ] **Step 3: Verify build still passes**

```bash
dotnet build src/EyeCtrl.Core
```
Expected: 0 errors, 0 CS1591 warnings.

- [ ] **Step 4: Verify full solution build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Clean build (other projects don't have `<GenerateDocumentationFile>` so they won't produce CS1591, and `<WarningsAsErrors>` is scoped to Core only).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/EyeCtrl.Core.csproj
git commit -m "build: promote CS1591 to error in Core (ratchet)"
```

---

### Task 13: Narrow Platform's `FrameworkReference` (Phase 3.1)

**Files:** `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`

- [ ] **Step 1: Run GitNexus impact analysis**

```
impact({target: "EyeCtrl.Platform", direction: "upstream"})
```
Report blast radius. Expected: only App references Platform (no other consumers rely on the broad FrameworkReference).

- [ ] **Step 2: Verify no WinForms usage in Platform**

```powershell
Select-String -Path "src/EyeCtrl.Platform/**/*.cs" -Pattern "System\.Windows\.Forms" -SimpleMatch
```
Expected: No matches (Platform only uses `System.Windows.Point` and `System.Windows.Automation`, both WPF subset).

- [ ] **Step 3: Replace FrameworkReference with UseWPF**

In `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`, replace:

```xml
    <FrameworkReference Include="Microsoft.WindowsDesktop.App" />
```

with:

```xml
    <UseWPF>true</UseWPF>
```

The `<PackageReference>` line stays. The `UseWPF` goes inside `<PropertyGroup>` alongside the existing properties:

```xml
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
```

And remove the `<ItemGroup>` containing `<FrameworkReference>`.

- [ ] **Step 4: Verify build and tests**

```bash
dotnet build EyeCtrl.slnx
```

Expected: Clean build.

```bash
dotnet test --filter "Category!=UI"
```

Expected: All pass (same count as before).

- [ ] **Step 5: Run detect_changes**

```
detect_changes()
```

Verify only `EyeCtrl.Platform.csproj` is affected.

- [ ] **Step 6: Update AGENTS.md**

In `AGENTS.md` "Dependencies & Quirks", add a note:

```markdown
- **Platform uses `UseWPF` not `FrameworkReference`** — Platform only needs `System.Windows.Point` and `System.Windows.Automation` (WPF subset, no WinForms). Narrowed from the broader `Microsoft.WindowsDesktop.App`.
```

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Platform/EyeCtrl.Platform.csproj AGENTS.md
git commit -m "refactor: narrow Platform FrameworkReference to UseWPF"
```

---

### Task 14: Extract `App.OnStartup` Helpers (Phase 3.2)

**Files:** `src/EyeCtrl.App/App.xaml.cs`

**Current state:** `OnStartup` is ~150 lines (lines 50-199). Two helpers will be extracted as private methods.

- [ ] **Step 1: Run GitNexus impact analysis**

```
impact({target: "App.OnStartup", direction: "upstream"})
```
Expected: Single call site (Application infrastructure calls OnStartup). LOW risk.

- [ ] **Step 2: Extract `ResolveViewModels` helper**

Extract lines 124-133 (the 10 ViewModel resolutions) plus the `manualSelectionIndicatorVm.Configure(settings)` call at line 142 into a private method. The adapter and dwell-service resolutions (lines 136, 139) stay in `OnStartup`.

In `App.xaml.cs`, add this private method:

```csharp
private (CursorCircleViewModel CursorVm,
         ActivationButtonViewModel ActivationVm,
         ManualSelectionOverlayViewModel ManualSelectionVm,
         ActionMenuViewModel ActionMenuVm,
         ToolbarViewModel ToolbarVm,
         KeyboardViewModel KeyboardVm,
         ManualSelectionGazeIndicatorViewModel ManualSelectionIndicatorVm,
         GlassBackdropViewModel BackdropVm,
         CalibrationViewModel CalibrationVm,
         TargetHighlightOverlayViewModel TargetHighlightVm) ResolveViewModels(EyeCtrlSettings settings)
{
    var cursorVm = _serviceProvider.GetRequiredService<CursorCircleViewModel>();
    var activationVm = _serviceProvider.GetRequiredService<ActivationButtonViewModel>();
    var manualSelectionVm = _serviceProvider.GetRequiredService<ManualSelectionOverlayViewModel>();
    var actionMenuVm = _serviceProvider.GetRequiredService<ActionMenuViewModel>();
    var toolbarVm = _serviceProvider.GetRequiredService<ToolbarViewModel>();
    var keyboardVm = _serviceProvider.GetRequiredService<KeyboardViewModel>();
    var manualSelectionIndicatorVm = _serviceProvider.GetRequiredService<ManualSelectionGazeIndicatorViewModel>();
    var backdropVm = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
    var calibrationVm = _serviceProvider.GetRequiredService<CalibrationViewModel>();
    var targetHighlightVm = _serviceProvider.GetRequiredService<TargetHighlightOverlayViewModel>();

    manualSelectionIndicatorVm.Configure(settings);

    return (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm,
            keyboardVm, manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm);
}
```

- [ ] **Step 3: Extract `InitializeOverlays` and `ShowOverlays` helpers**

`OnStartup` *initialises* the overlay windows early (lines 145-147) — so `overlayManager.Keyboard` is ready for `LoadKeyboard` — but *shows* them last (lines 184-190), only after calibration, event coordination, keyboard load, and tray setup are wired. **Preserve that split with two helpers.** Do NOT merge them: showing/binding the toolbar before `EventCoordinator.Initialize` reorders startup, and dropping `splash?.Close()` leaves the splash on screen forever.

In `App.xaml.cs`, add these private methods:

```csharp
private (CursorCircleOverlayWindow CursorCircle, ToolbarOverlayWindow Toolbar) InitializeOverlays(
    IOverlayManager overlayManager,
    OverlayViewModels viewModels,
    EyeCtrlSettings settings,
    string providerType)
{
    overlayManager.Initialize(viewModels, settings, providerType);
    return (overlayManager.CursorCircle, overlayManager.Toolbar);
}

private static void ShowOverlays(
    SplashWindow? splash,
    CursorCircleOverlayWindow cursorCircle,
    ToolbarOverlayWindow toolbar,
    EyeCtrlSettings settings)
{
    splash?.Close();
    cursorCircle.Show();
    toolbar.Show();
    toolbar.SetMode(settings.Toolbar.IsDocked, settings.Toolbar.DockedEdge);
    toolbar.SetSelectionModeState(settings.SelectionMode);
    toolbar.BindToViewModel();
}
```

`ShowOverlays` keeps the original intra-block order (`SetMode` before `BindToViewModel`), its original position (after tray setup), **and** the splash close — none of which may be dropped.

- [ ] **Step 4: Rewrite `OnStartup` to use the helpers**

Replace lines 124-190 of the existing `OnStartup` method with calls to the helpers. The new `OnStartup` body:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);

    // ── Splash screen ──
    SplashWindow? splash = null;
    if (!IsTestMode)
    {
        splash = new SplashWindow();
        splash.Show();
    }

    // ── DPI initialization ──
    if (splash != null)
    {
        var source = PresentationSource.FromVisual(splash);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformToDevice;
            DpiHelper.Initialize(m.M11, m.M22);
        }
    }
    if (IsTestMode)
    {
        var dpi = GetDpiForSystem();
        var scale = dpi / (double)DpiStandardScale;
        DpiHelper.Initialize(scale, scale);
    }

    // ── Settings ──
    _appDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
    var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
    var tempSettingsService = new SettingsService(settingsFilePath);
    var settings = tempSettingsService.Load();

    // Migrate settings for newly-introduced features
    settings.ActionMenu.EnsureDefaults();

    // ── Logging ──
    var logsDir = Path.Combine(_appDataDir, "logs");
    LoggingInitializer.Initialize(logsDir);

    AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    {
        Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
            args.IsTerminating);
        Log.CloseAndFlush();
    };

    DispatcherUnhandledException += (_, args) =>
    {
        Log.Error(args.Exception, "Unhandled dispatcher exception");
    };

    // ── Build DI container ──
    _serviceProvider = AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode);

    // ── Resolve services ──
    var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
    var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
    var messenger = _serviceProvider.GetRequiredService<IMessenger>();
    var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
    var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();
    var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
    var version = System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString() ?? "unknown";
    Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
        version, activeProviderType,
        Path.Combine(_appDataDir, "settings.json"));
    var im = gazeEngine.InteractionManager;

    // ── Resolve ViewModels ──
    var (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm,
         manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm) = ResolveViewModels(settings);

    // Resolve adapter (triggers Core event subscriptions)
    _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

    // Resolve ManualSelectionDwellService (triggers message subscriptions)
    _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

    // ── Initialize overlay windows via OverlayManager ──
    var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
    var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm);
    var (cursorCircle, toolbar) = InitializeOverlays(overlayManager, viewModels, settings, activeProviderType);

    // Load calibration. Screen size must be in PHYSICAL pixels.
    var calibrationPath = Path.Combine(_appDataDir, "calibration.json");
    var screen = new PhysicalPoint(
        SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
        SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
    var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
    _serviceProvider.GetRequiredService<CalibrationService>().Load(calibrationPath, screen, monitorName);

    // ── Event Coordination ──
    _eventCoordinator = new EventCoordinator();
    var noteManager = _serviceProvider.GetRequiredService<INoteManager>();
    var coordinationContext = new EventCoordinationContext(
        cursorVm, activationVm, manualSelectionVm, manualSelectionIndicatorVm, actionMenuVm, toolbarVm, keyboardVm, backdropVm,
        overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, targetHighlightVm, inputSimulator,
        _serviceProvider.GetRequiredService<CalibrationService>(),
        noteManager);
    _eventCoordinator.Initialize(coordinationContext);

    var modelRegistry = _serviceProvider.GetRequiredService<ModelRegistryService>();
    var modelDownload = _serviceProvider.GetRequiredService<ModelDownloadService>();
    var modelManagement = _serviceProvider.GetRequiredService<ModelManagementService>();
    _eventCoordinator.SetModelServices(modelRegistry, modelDownload, modelManagement);

    // Load keyboard using EventCoordinator
    _eventCoordinator.LoadKeyboard(overlayManager.Keyboard, settings, inputSimulator, settingsService);

    // ── System tray ──
    _trayIconManager = new TrayIconManager(im, Dispatcher, () => Shutdown());

    // ── Close splash and show persistent overlays (last, after all wiring) ──
    ShowOverlays(splash, cursorCircle, toolbar, settings);

    if (settings.General.StartPaused)
        im.TogglePause();

    gazeEngine.Start();

    // External dwell pipe server (cooperating-app control activation).
    _serviceProvider.GetRequiredService<ExternalDwellService>().Start();
}
```

Key changes:
- `InitializeOverlays` creates the windows early (so `overlayManager.Keyboard` is ready for `LoadKeyboard`); `ShowOverlays` closes the splash and shows/binds them last — preserving the original ordering (overlays appear only after calibration, event coordination, keyboard, and tray are wired).
- Removed PascalCase locals (`ManualSelectionVM` → `manualSelectionVm`, `ManualSelectionIndicatorVM` → `manualSelectionIndicatorVm`)
- `cursorCircle` and `toolbar` come from the tuple return of `InitializeOverlays` and are passed to `ShowOverlays`
- The splash is still closed (inside `ShowOverlays`, at its original late position) — this must not be dropped.

- [ ] **Step 5: Verify build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Clean.

- [ ] **Step 6: Verify unit tests**

```bash
dotnet test --filter "Category!=UI"
```
Expected: All pass.

- [ ] **Step 7: Manual smoke test**

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```
Expected: App launches and quits cleanly.

- [ ] **Step 8: Run detect_changes**

```
detect_changes()
```

Verify only `App.xaml.cs` is affected.

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "refactor: extract ResolveViewModels and InitializeOverlays from App.OnStartup"
```

---

### Task 15: Document MVVM in Core (Phase 4.1)

**Files:** `ARCHITECTURE.md`, `AGENTS.md`

No code change — document the architectural decision.

- [ ] **Step 1: Add invariant to ARCHITECTURE.md §7**

In `ARCHITECTURE.md`, after invariant #6 (Settings drive behaviour), add:

```markdown
7. **CommunityToolkit.Mvvm in Core is a source-generator utility, not a UI dependency.** `ObservableObject` + `[ObservableProperty]` provide `INotifyPropertyChanged` for settings objects (13 classes, ~80 properties). `GazeEngine` subscribes to `CursorSettings.PropertyChanged` to rebuild the filter chain live. The library emits C# source only — no runtime UI dependency. This is distinct from the `WeakReferenceMessenger` used in ViewModels (which Core does *not* use).
```

- [ ] **Step 2: Add cross-reference to AGENTS.md**

In `AGENTS.md` "Dependencies & Quirks", add:

```markdown
- **CommunityToolkit.Mvvm in Core** — `ObservableObject` is used solely for `INotifyPropertyChanged` on settings objects (source-generator utility). The `WeakReferenceMessenger` is NOT used in Core (only in ViewModels). This is documented as architectural invariant #7.
```

- [ ] **Step 3: Commit**

```bash
git add ARCHITECTURE.md AGENTS.md
git commit -m "docs: document MVVM usage in Core as architectural invariant"
```

---

### Task 16: Add `ILanguageModelPredictor` Interface (Phase 4.2 — Commit 1 of 5)

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/ILanguageModelPredictor.cs`
- Modify: `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs`
- Modify: `src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs`

- [ ] **Step 1: Run GitNexus context on SmallLMPredictor**

```
context({name: "SmallLMPredictor"})
```

Enumerate all public methods. The interface must cover every method `PredictionOrchestrator` calls.

From the source code, `PredictionOrchestrator` calls on `_lmPredictor`:
- `_lmPredictor.IsAvailable` (property get)
- `_lmPredictor.TryGetResults()` → `IReadOnlyList<PredictionResult>?`
- `_lmPredictor.Predict(string context, string? prefix)` → void
- `Dispose()` (via `_lmPredictor?.Dispose()`)

- [ ] **Step 2: Run impact analysis**

```
impact({target: "SmallLMPredictor", direction: "upstream"})
```

Expected callers: `PredictionOrchestrator` constructor, `EventCoordinator.LoadKeyboard`. Report blast radius.

- [ ] **Step 3: Create the interface**

```csharp
// src/EyeCtrl.Core/Prediction/ILanguageModelPredictor.cs
namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Predicts text completions using a language model (e.g., ONNX neural LM).
/// Implementations are optional — the orchestrator functions correctly when
/// the predictor is null or <see cref="IsAvailable"/> is false.
/// </summary>
public interface ILanguageModelPredictor : IDisposable
{
    /// <summary>
    /// Whether the underlying model is loaded and ready to produce predictions.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Begins an asynchronous prediction for the given context and prefix.
    /// Results are retrieved later via <see cref="TryGetResults"/>.
    /// </summary>
    /// <param name="context">The text preceding the prefix (may be null or empty).</param>
    /// <param name="prefix">The text to complete (may be null or empty).</param>
    void Predict(string context, string? prefix = null);

    /// <summary>
    /// Returns completed predictions from the most recent <see cref="Predict"/> call,
    /// or null if the inference is still in progress.
    /// </summary>
    IReadOnlyList<PredictionResult>? TryGetResults();

    /// <summary>
    /// Cancels any pending inference and releases resources.
    /// </summary>
    void Cancel();
}
```

- [ ] **Step 4: Make `SmallLMPredictor` implement the interface**

In `src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs`, change the class declaration:

```csharp
public class SmallLMPredictor : IDisposable
```

to:

```csharp
public class SmallLMPredictor : ILanguageModelPredictor
```

Note: `IDisposable` is already in the interface chain through `ILanguageModelPredictor : IDisposable`, so we remove the explicit `IDisposable` from the class declaration.

The `Cancel()` method is already public — no change needed.

- [ ] **Step 5: Change `PredictionOrchestrator` to depend on the interface**

In `src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs`:

Change field type:
```csharp
// Before:
private readonly SmallLMPredictor? _lmPredictor;
// After:
private readonly ILanguageModelPredictor? _lmPredictor;
```

Change constructor parameter type:
```csharp
// Before:
SmallLMPredictor? lmPredictor = null,
// After:
ILanguageModelPredictor? lmPredictor = null,
```

The `_mlEnabled` check and all usages (`_lmPredictor.IsAvailable`, `_lmPredictor.TryGetResults()`, `_lmPredictor.Predict(...)`, `_lmPredictor?.Dispose()`) remain identical — no code change needed beyond the type.

- [ ] **Step 6: Verify build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Clean. Core builds with `ILanguageModelPredictor`; `SmallLMPredictor` still in Core implements it.

- [ ] **Step 7: Verify tests**

```bash
dotnet test --filter "Category!=UI"
```
Expected: All pass. `PredictionOrchestratorTests` passes `lmPredictor: null` as before.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/ILanguageModelPredictor.cs src/EyeCtrl.Core/Prediction/PredictionOrchestrator.cs src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs
git commit -m "refactor: add ILanguageModelPredictor interface, wire into PredictionOrchestrator"
```

---

### Task 17: Create `EyeCtrl.LM` Project and Move Files (Phase 4.2 — Commit 2 of 5)

**Files:**
- Create: `src/EyeCtrl.LM/EyeCtrl.LM.csproj`
- Move: `SmallLMPredictor.cs`, `LMTokenizer.cs` from `Core/Prediction/` → `EyeCtrl.LM/`
- Modify: `src/EyeCtrl.Core/EyeCtrl.Core.csproj` (remove 3 PackageReferences)
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj` + `Services/EventCoordinator.cs`, and `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj` + `SmallLMBenchmarks.cs` (add LM reference)
- Modify: `EyeCtrl.slnx` (add new project)

- [ ] **Step 1: Create the LM project directory and csproj**

Create file `src/EyeCtrl.LM/EyeCtrl.LM.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="EyeCtrl.LM.Tests" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\EyeCtrl.Core\EyeCtrl.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.21.0" />
    <PackageReference Include="Tokenizers.DotNet" Version="1.4.1" />
    <PackageReference Include="Tokenizers.DotNet.runtime.win-x64" Version="1.4.1" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Move SmallLMPredictor.cs and LMTokenizer.cs**

Move files:
- `src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs` → `src/EyeCtrl.LM/SmallLMPredictor.cs`
- `src/EyeCtrl.Core/Prediction/LMTokenizer.cs` → `src/EyeCtrl.LM/LMTokenizer.cs`

Update namespace in both files from `EyeCtrl.Core.Prediction` to `EyeCtrl.LM`:

`SmallLMPredictor.cs`:
```csharp
namespace EyeCtrl.LM;
```

`LMTokenizer.cs`:
```csharp
namespace EyeCtrl.LM;
```

Add `using EyeCtrl.Core.Prediction;` to `SmallLMPredictor.cs` (for `PredictionResult`, `PredictionType`, `ModelConstants` references to Core types).

- [ ] **Step 3: Remove ONNX/Tokenizer PackageReferences from Core**

In `src/EyeCtrl.Core/EyeCtrl.Core.csproj`, remove these 3 lines:

```xml
    <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.21.0" />
    <PackageReference Include="Tokenizers.DotNet" Version="1.4.1" />
    <PackageReference Include="Tokenizers.DotNet.runtime.win-x64" Version="1.4.1" />
```

After removal, Core.csproj `<ItemGroup>` should be:

```xml
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>
```

- [ ] **Step 4: Add `EyeCtrl.LM` to the solution file**

The `.slnx` format is XML. Add a `<Project>` entry inside the `<Folder Name="/src/">` element — existing entries have **no** `Type` attribute:

```xml
    <Project Path="src/EyeCtrl.LM/EyeCtrl.LM.csproj" />
```

This goes alongside the existing `src/EyeCtrl.Core` entry, inside the `/src/` folder.

- [ ] **Step 5: Add `EyeCtrl.LM` references to App and benchmarks**

In `src/EyeCtrl.App/EyeCtrl.App.csproj`, add:

```xml
    <ProjectReference Include="..\EyeCtrl.LM\EyeCtrl.LM.csproj" />
```

(Next to the existing `EyeCtrl.Core` and `EyeCtrl.Platform` references.)

Add `using EyeCtrl.LM;` to `EventCoordinator.cs` (for the moved `SmallLMPredictor` and `LMTokenizer` types).

`benchmarks/EyeCtrl.Benchmarks/SmallLMBenchmarks.cs` also references the moved types, and the benchmarks project is **not** in `EyeCtrl.slnx` — so the per-commit `dotnet build EyeCtrl.slnx` won't catch its breakage. Fix it in this same commit so benchmarks never builds broken: add to `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj`:

```xml
    <ProjectReference Include="..\..\src\EyeCtrl.LM\EyeCtrl.LM.csproj" />
```

and add `using EyeCtrl.LM;` to `SmallLMBenchmarks.cs`.

- [ ] **Step 6: Verify build**

```bash
dotnet build EyeCtrl.slnx
dotnet build benchmarks/EyeCtrl.Benchmarks
```
Expected: Clean. Core no longer has ONNX dependencies. LM project builds. App and benchmarks reference LM. (Benchmarks is built separately — it is not in the solution.)

- [ ] **Step 7: Verify publish still works**

```bash
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
Expected: Completes. LM natives are included via App → LM transitively.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.LM/ src/EyeCtrl.Core/EyeCtrl.Core.csproj src/EyeCtrl.App/EyeCtrl.App.csproj src/EyeCtrl.App/Services/EventCoordinator.cs benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj benchmarks/EyeCtrl.Benchmarks/SmallLMBenchmarks.cs EyeCtrl.slnx
git rm src/EyeCtrl.Core/Prediction/SmallLMPredictor.cs src/EyeCtrl.Core/Prediction/LMTokenizer.cs
git commit -m "refactor: extract SmallLMPredictor and LMTokenizer to EyeCtrl.LM project"
```

---

### Task 18: Update EventCoordinator to Use `ILanguageModelPredictor` (Phase 4.2 — Commit 3 of 5)

**Files:** `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Change the static type of the local in LoadKeyboard**

In `EventCoordinator.cs`, line 876, change:

```csharp
SmallLMPredictor? lmPredictor = null;
```

to:

```csharp
ILanguageModelPredictor? lmPredictor = null;
```

The construction on lines 901-906 (`new SmallLMPredictor(...)`) remains identical — `SmallLMPredictor` implements `ILanguageModelPredictor`, so assignment is valid.

The `_orchestrator` construction on line 911 already takes `ILanguageModelPredictor?` (from Task 16) — no change needed.

- [ ] **Step 2: Remove the now-unused `using EyeCtrl.Core.Prediction` for SmallLMPredictor?**

Check if `SmallLMPredictor` is still used by name elsewhere in `EventCoordinator.cs`. The construction still references `SmallLMPredictor` and `LMTokenizer` — but these are now in `EyeCtrl.LM` namespace. The `using EyeCtrl.LM;` added in Task 17 covers them. Remove `using EyeCtrl.Core.Prediction;` if only used for these types.

Actually, keep `using EyeCtrl.Core.Prediction;` — it's needed for `PredictionOrchestrator`, `WordPredictor`, `SentenceCompletionIndex`, `PredictionResult`, `FileDictionaryProvider`, `IDictionaryProvider`, and `PredictionType` which are all still in Core.

- [ ] **Step 3: Verify build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Clean.

- [ ] **Step 4: Verify tests**

```bash
dotnet test --filter "Category!=UI"
```
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "refactor: type EventCoordinator lmPredictor local as ILanguageModelPredictor"
```

---

### Task 19: Move Tests + Update Solution (Phase 4.2 — Commit 4 of 5)

**Files:**
- Create: `tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj`
- Move: `SmallLMPredictorTests.cs`, `LMTokenizerTests.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Read existing test project structure for conventions**

Read `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj` and `tests/Directory.Build.props` to replicate conventions.

- [ ] **Step 2: Create `tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.LM\EyeCtrl.LM.csproj" />
  </ItemGroup>

</Project>
```

Note: `tests/Directory.Build.props` provides global usings (xUnit, NFluent), so no explicit package references needed for test framework.

- [ ] **Step 3: Move test files**

- `tests/EyeCtrl.Core.Tests/Prediction/SmallLMPredictorTests.cs` → `tests/EyeCtrl.LM.Tests/SmallLMPredictorTests.cs`
- `tests/EyeCtrl.Core.Tests/Prediction/LMTokenizerTests.cs` → `tests/EyeCtrl.LM.Tests/LMTokenizerTests.cs`

Update namespace in both from `EyeCtrl.Core.Tests.Prediction` to `EyeCtrl.LM.Tests`.

Update usings: add `using EyeCtrl.LM;` instead of `using EyeCtrl.Core.Prediction;` for `SmallLMPredictor` and `LMTokenizer` types. Keep `using EyeCtrl.Core.Prediction;` for `PredictionResult`, `PredictionType`, etc.

- [ ] **Step 4: Add test project to solution**

In `EyeCtrl.slnx`, add inside the `<Folder Name="/tests/">` element (no `Type` attribute):

```xml
    <Project Path="tests/EyeCtrl.LM.Tests/EyeCtrl.LM.Tests.csproj" />
```

- [ ] **Step 5: Confirm benchmarks reference (added in Task 17)**

The `benchmarks/EyeCtrl.Benchmarks` → `EyeCtrl.LM` reference and the `using EyeCtrl.LM;` in `SmallLMBenchmarks.cs` were added in Task 17 (the move commit), so benchmarks never built broken. No change needed here — Step 7 re-verifies it compiles.

- [ ] **Step 6: Verify build and tests**

```bash
dotnet build EyeCtrl.slnx
dotnet test
```
Expected: All tests pass, including moved LM tests.

- [ ] **Step 7: Verify benchmarks compile**

```bash
dotnet build benchmarks/EyeCtrl.Benchmarks
```
Expected: Clean build.

- [ ] **Step 8: Commit**

```bash
git add tests/EyeCtrl.LM.Tests/ EyeCtrl.slnx
git rm tests/EyeCtrl.Core.Tests/Prediction/SmallLMPredictorTests.cs tests/EyeCtrl.Core.Tests/Prediction/LMTokenizerTests.cs
git commit -m "test: move LM tests to EyeCtrl.LM.Tests project"
```

---

### Task 20: Update Architecture Documentation (Phase 4.2 — Commit 5 of 5)

**Files:** `ARCHITECTURE.md`, `AGENTS.md`, `README.md`

- [ ] **Step 1: Update ARCHITECTURE.md §2 — add EyeCtrl.LM to project table**

Update the section heading `## 2. The Eight Projects` → `## 2. The Nine Projects` (the count is hardcoded).

In the project table, add:

```markdown
| `EyeCtrl.LM` | `net10.0` | ONNX neural language model predictor (`SmallLMPredictor`) and tokenizer (`LMTokenizer`). Depends on `EyeCtrl.Core`. |
```

- [ ] **Step 2: Update ARCHITECTURE.md §10 — add EyeCtrl.LM to directory structure**

In the directory structure, add:

```
│   ├── EyeCtrl.LM/              # ONNX neural LM (SmallLMPredictor, LMTokenizer)
│   │   ├── SmallLMPredictor.cs  # ONNX inference via Microsoft.ML.OnnxRuntime
│   │   └── LMTokenizer.cs       # Tokenizer wrapper via Tokenizers.DotNet
```

And in the tests section:

```
│   ├── EyeCtrl.LM.Tests/         # Tests for EyeCtrl.LM
```

- [ ] **Step 3: Update ARCHITECTURE.md §4.6 — update keyboard prediction flow**

In the Keyboard Prediction mermaid, note that `SmallLMPredictor` now lives in `EyeCtrl.LM` and is accessed via `ILanguageModelPredictor`.

- [ ] **Step 4: Update AGENTS.md**

In "Dependencies & Quirks", add:

```markdown
- **EyeCtrl.LM** is a leaf project for ONNX/Tokenizer natives. Core is free of native ML dependencies — the `ILanguageModelPredictor` interface provides the seam. App references LM, constructs `SmallLMPredictor` in `EventCoordinator.LoadKeyboard`, and passes it to `PredictionOrchestrator` as `ILanguageModelPredictor`.
```

In the project table, add `EyeCtrl.LM` row:

```markdown
| `EyeCtrl.LM` | ONNX LM | `net10.0` leaf project. `SmallLMPredictor` + `LMTokenizer`. References Core. |
```

- [ ] **Step 5: Update README.md**

In the project structure section, add `EyeCtrl.LM` alongside the other projects.

- [ ] **Step 6: Run detect_changes**

```
detect_changes()
```

Verify only doc files were changed.

- [ ] **Step 7: Commit**

```bash
git add ARCHITECTURE.md AGENTS.md README.md
git commit -m "docs: document LM extraction and ILanguageModelPredictor seam"
```

---

## Verification Checklist (per phase)

### Phase 1
- [ ] GitNexus index is at HEAD (0 commits behind)
- [ ] CI workflow file exists at `.github/workflows/ci.yml`
- [ ] `dotnet build EyeCtrl.slnx` clean
- [ ] `dotnet test --filter "Category!=UI"` green

### Phase 2
- [ ] 0 distinct CS8602/CS8629/CS8604/xUnit1051 warnings
- [ ] 0 distinct CS1591 warnings in Core (before and after ratchet)
- [ ] `<WarningsAsErrors>CS1591</WarningsAsErrors>` makes CI fail on new undocumented public members
- [ ] `dotnet test --filter "Category!=UI"` green

### Phase 3
- [ ] Platform builds with `UseWPF` and no `FrameworkReference`
- [ ] `App.OnStartup` is ~70 lines with well-named helper calls
- [ ] No PascalCase local variables in OnStartup
- [ ] `dotnet build EyeCtrl.slnx` clean
- [ ] `dotnet test --filter "Category!=UI"` green
- [ ] `dotnet run --project src/EyeCtrl.App -- --test-mode` launches/quits cleanly

### Phase 4
- [ ] Core.csproj has 0 ONNX/Tokenizer PackageReferences
- [ ] `EyeCtrl.LM` project exists with `SmallLMPredictor` + `LMTokenizer`
- [ ] `ILanguageModelPredictor` interface in Core
- [ ] `PredictionOrchestrator` depends on `ILanguageModelPredictor?` (not `SmallLMPredictor?`)
- [ ] `EventCoordinator.LoadKeyboard` types local as `ILanguageModelPredictor?`
- [ ] `dotnet build EyeCtrl.slnx` clean
- [ ] `dotnet test` green (including LM.Tests)
- [ ] `dotnet build benchmarks/EyeCtrl.Benchmarks` clean
- [ ] `dotnet publish src/EyeCtrl.App` includes LM natives (expected — App → LM transitively)
- [ ] Docs updated: `ARCHITECTURE.md`, `AGENTS.md`, `README.md`

---

## Summary

| Task | Phase | Commits | Key files changed |
|------|-------|---------|-------------------|
| 1 | 1.1 | 0 (local) | None |
| 2 | 1.2 | 1 | `.github/workflows/ci.yml`, `AGENTS.md`, `README.md` |
| 3 | 2.1 | 1 | Test files + `ManualSelectionOverlayWindow.xaml.cs` |
| 4–11 | 2.2 | 8 | ~337 doc comments across Core subdirectories |
| 12 | 2.2 | 1 | `EyeCtrl.Core.csproj` (ratchet) |
| 13 | 3.1 | 1 | `EyeCtrl.Platform.csproj`, `AGENTS.md` |
| 14 | 3.2 | 1 | `App.xaml.cs` |
| 15 | 4.1 | 1 | `ARCHITECTURE.md`, `AGENTS.md` |
| 16 | 4.2.1 | 1 | `ILanguageModelPredictor.cs`, `PredictionOrchestrator.cs`, `SmallLMPredictor.cs` |
| 17 | 4.2.2 | 1 | New `EyeCtrl.LM` project, moved files, Core.csproj, App + benchmarks refs |
| 18 | 4.2.3 | 1 | `EventCoordinator.cs` |
| 19 | 4.2.4 | 1 | Test files, solution |
| 20 | 4.2.5 | 1 | Docs |

**Total: ~20 commits across 20 tasks.**
