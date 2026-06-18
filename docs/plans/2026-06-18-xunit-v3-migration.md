# xUnit v2 → v3 Migration

## Background

Current state across all 6 test projects:
- `xunit` **2.9.3** (v2 core)
- `xunit.runner.visualstudio` **3.1.5** — the 3.x runner supports **both** v2 and v3 (it is a dual-mode VSTest adapter, not a "v3-only" runner), so no version change is needed
- `Xunit.StaFact` **1.2.69** (v2-only — the main blocker; used on **15** `[StaFact]` methods in `EyeCtrl.App.Tests`: 14 in `NoteManagerTests.cs`, 1 in `GlassBackdropWindowTests.cs`)
- `coverlet.collector` **8.0.0** on 5 of 6 projects (`EyeCtrl.Sdk.Abstractions.Tests` lacks it) — centralized in Shared changes, which also adds it to that project for parity
- `Microsoft.NET.Test.Sdk` **18.3.0** (exceeds the 17.12 minimum xUnit v3 requires; no change needed)

In xUnit v3 the `xunit` meta-package is replaced by `xunit.v3`. The runner is already current — only the core package needs replacing, plus the StaFact bump and one `IAsyncLifetime` signature change (Task 7).

### Verified versions (live-checked 2026-06-18 via `dotnet package search`)

| Package | Current latest | Action |
|---------|----------------|--------|
| `xunit.v3` | **3.2.2** | replace `xunit` 2.9.3 |
| `Xunit.StaFact` | **3.0.13** | replace 1.2.69 (StaFact 3.x targets `xunit.v3` 3.x; StaFact 2.x targets `xunit.v3` 2.x — confirmed on NuGet) |
| `xunit.runner.visualstudio` | **3.1.5** | none (already current) |
| `xunit.analyzers` | **1.27.0** | add **early** — see Shared changes |
| `Microsoft.NET.Test.Sdk` | **18.3.0** | none (17.12+ required) |

`xunit.v3` 3.2.2 already pulls in `xunit.analyzers` transitively, but we add it explicitly and early (Shared changes) to pin the version and surface migration warnings up front.

---

## Decision — stay on VSTest mode (not Microsoft.Testing.Platform)

xUnit v3 can run under either classic **VSTest** (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`) or the newer **Microsoft.Testing.Platform (MTP)**. This migration **keeps VSTest mode** — it is the smallest change and `dotnet test` keeps working unchanged. Do **not** adopt MTP as part of this migration; ignore MTP-specific guidance in the v3 docs.

---

## Task 0 — Capture a baseline (do this first)

Migration regressions must be distinguishable from pre-existing failures. Per project history, ~13 `Category=UI` / `AppLaunch` tests in `EyeCtrl.App.Tests` already fail in a headless session on clean `main` (environmental — "Toolbar_Window did not appear within 15s" — not regressions).

On current `main`, **before any edit**, record pass/fail counts:
```
dotnet test --filter "Category!=UI"     # headless-safe subset — this is the real gate
dotnet test                              # full run — note exactly which UI tests already fail
```
Save both numbers. Every later "all green" claim is measured against this baseline, not against zero failures.

---

## Task 1 — Re-confirm package versions

The versions above were live-checked on 2026-06-18. Re-confirm immediately before editing in case a newer stable has shipped:
```
dotnet package search xunit.v3 --exact-match
dotnet package search Xunit.StaFact --exact-match
```
Use `3.2.2` / `3.0.13` unless a newer stable release exists.

---

## Shared changes — centralize common test config in `tests/Directory.Build.props`

Every test project shares the same test-infrastructure packages and properties. Centralize them in a new **`tests/Directory.Build.props`** (none exists today) so the migration touches one file instead of six and versions can't drift:

```xml
<Project>
  <PropertyGroup>
    <!-- xUnit v3 test projects are stand-alone executables, not class libraries -->
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <!-- Surfaces v2 -> v3 API breakages at compile time -->
    <PackageReference Include="xunit.analyzers" Version="1.27.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <!-- Was on 5 of 6 projects; centralizing adds it to Sdk.Abstractions.Tests too (parity, harmless) -->
    <PackageReference Include="coverlet.collector" Version="8.0.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

Then **strip the now-centralized lines from all 6 `.csproj` files.** Remove from each: the old `xunit` 2.9.3 reference, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `coverlet.collector` (the 5 that have it), `<Using Include="Xunit" />`, and the `<IsPackable>` / `<IsTestProject>` properties. What remains in each `.csproj` is only its TFM, language settings, and project-specific references.

Example — `EyeCtrl.Core.Tests.csproj` shrinks from ~35 lines to:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Tokenizers.DotNet" Version="1.4.1" />
    <PackageReference Include="Tokenizers.DotNet.runtime.win-x64" Version="1.4.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Core\EyeCtrl.Core.csproj" />
  </ItemGroup>
</Project>
```

Notes:
- `tests/Directory.Build.props` is scoped to `tests/` only — it won't affect `src/` or the standalone `benchmarks/` project.
- The only project-specific package references that remain are: `FlaUI.UIA3` + `Xunit.StaFact` (App.Tests), `Microsoft.Extensions.Logging.Abstractions` (Platform.Tests), and `Tokenizers.DotNet` (+ runtime) (Core.Tests). `TargetFramework` (`net10.0` vs `net10.0-windows`), `UseWPF`, and the `TOBII_AVAILABLE` block also stay per-project.
- `Xunit.StaFact` is **not** centralized — only App.Tests uses it, and it still needs the 1.2.69 → 3.0.13 swap (Task 7).
- Leftover duplicates are the likely gotcha: if any `.csproj` still declares a package now in props, NuGet reports duplicate `PackageReference` items. Strip all 6.

**After this step, build all test projects.** The analyzer warnings are your migration to-do list — resolve them before the per-project test runs in Tasks 2–7.

> **WPF caveat (affects `EyeCtrl.App.Tests` only):** that project sets `<UseWPF>true</UseWPF>`. Combining `OutputType=Exe` with xUnit v3's auto-generated `Main` entry point is a known sharp edge — verify the project still builds without a duplicate/conflicting entry-point error. It has no `App.xaml` (`ApplicationDefinition`), so it should be fine, but confirm explicitly.

---

## Task 2 — `EyeCtrl.Sdk.Abstractions.Tests` (simplest, no extras)

**File:** `tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj`

After Shared changes this `.csproj` has no test packages of its own — only the `ProjectReference`. It also gains `coverlet.collector` via the centralized props (new coverage for this project; intended). Build and run:
```
dotnet build tests/EyeCtrl.Sdk.Abstractions.Tests
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests
```

---

## Task 3 — `EyeCtrl.Sdk.WinUI.Tests`

**File:** `tests/EyeCtrl.Sdk.WinUI.Tests/EyeCtrl.Sdk.WinUI.Tests.csproj`

No package references remain after Shared changes (the `Compile` links and `ProjectReference` stay). Build and run tests.

---

## Task 4 — `EyeCtrl.Platform.Tests`

**File:** `tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj`

After Shared changes, only `Microsoft.Extensions.Logging.Abstractions` remains as a package reference; the `TOBII_AVAILABLE` block stays. No xUnit-specific code changes. Build and run tests.

---

## Task 5 — `EyeCtrl.ViewModels.Tests`

**File:** `tests/EyeCtrl.ViewModels.Tests/EyeCtrl.ViewModels.Tests.csproj`

No package references remain after Shared changes. Contains `IDisposable` test classes (e.g. `GlassBackdropViewModelTests`) — fully compatible with v3, no code changes needed. Build and run tests.

---

## Task 6 — `EyeCtrl.Core.Tests`

**File:** `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj`

After Shared changes, only `Tokenizers.DotNet` (+ `Tokenizers.DotNet.runtime.win-x64`) remain as package references (this is the example shown in Shared changes). Contains:
- Multiple `IDisposable` test classes (`SettingsServiceValidationTests`, `SettingsServiceDebounceTests`, `SettingsServiceResourceLeakTests`, `FileDictionaryProviderTests`, `LMTokenizerTests`, `EmojiDataLoaderTests`) — all compatible
- `[CollectionDefinition("DpiHelper Tests", DisableParallelization = true)]` — unchanged in v3

Build and run:
```
dotnet build tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.Core.Tests
```

---

## Task 7 — Resolve `Xunit.StaFact` and update `EyeCtrl.App.Tests` (blocker)

**File:** `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`

This project uses `[StaFact]` on 15 test methods (`NoteManagerTests.cs`, `GlassBackdropWindowTests.cs`). The v1.x `Xunit.StaFact` package is incompatible with xUnit v3.

The common test packages were already centralized (Shared changes), so the only package edit left here is the StaFact bump. After Shared changes this `.csproj` keeps just `FlaUI.UIA3` and `Xunit.StaFact`:

```xml
<!-- Before (StaFact only — xunit was removed in Shared changes) -->
<PackageReference Include="Xunit.StaFact" Version="1.2.69" />

<!-- After -->
<PackageReference Include="Xunit.StaFact" Version="3.0.13" />
```

### IAsyncLifetime in AppFixture

`AppFixture` (`tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs`) is the only `IAsyncLifetime` implementation in the suite. It implements `InitializeAsync()` and `DisposeAsync()` returning `Task`. In xUnit v3, `IAsyncLifetime` now inherits from `IAsyncDisposable` and both methods return **`ValueTask`** instead of `Task`. Update the signatures:

```csharp
// Before
public async Task InitializeAsync() { ... }
public Task DisposeAsync() { ... return Task.CompletedTask; }

// After
public async ValueTask InitializeAsync() { ... }
public ValueTask DisposeAsync()
{
    ...
    return ValueTask.CompletedTask;
}
```

The current `DisposeAsync` body is synchronous (`_app?.Kill()`, `_automation?.Dispose()`), so returning `ValueTask.CompletedTask` is a direct swap.

### ICollectionFixture / CollectionDefinition

`AppCollection`, `[Collection("App")]`, `[CollectionDefinition("DpiHelper Tests (App)")]` — all unchanged in v3, no code edits needed.

### WPF entry point

Confirm the WPF caveat from Shared changes: `EyeCtrl.App.Tests` builds cleanly with `OutputType=Exe` + xUnit v3's generated entry point.

Build and run, comparing to the Task 0 baseline:
```
dotnet build tests/EyeCtrl.App.Tests
dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"   # must match/exceed baseline
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"    # informational — expect the same ~13 environmental failures
```

---

## Task 8 — Full solution build and test run

```
dotnet build EyeCtrl.slnx
dotnet test EyeCtrl.slnx --filter "Category!=UI"   # primary gate — compare to Task 0 baseline
dotnet test EyeCtrl.slnx --filter "Category=UI"    # informational — expect no NEW failures beyond baseline
```

**Success criteria:** the `Category!=UI` subset matches or exceeds the Task 0 baseline, and `Category=UI` shows no failures beyond the pre-existing environmental set. The solution excludes the benchmarks project (`EyeCtrl.Benchmarks` is standalone, uses BenchmarkDotNet, not xUnit) — no changes needed there.

---

## Out of scope (track separately)

These are **not** part of this framework-version migration and must not gate it:

- **The `GlassBackdropWindow.InitializeComponent()` NRE** (`src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs`) — a separate WPF bug, unrelated to the xUnit version. The `Category=UI` tests drive a separately-built `EyeCtrl.App.exe` via FlaUI, so the test-framework migration cannot change the app's runtime behavior. File its own ticket if reproducible.
- **Making `Category=UI` / `AppLaunch` tests pass headless** — these are environmental (require a logged-in, non-headless Windows session) and already fail on clean `main`. Out of scope here.

---

## Known Risks

| Risk | Mitigation |
|------|-----------|
| Mistaking pre-existing UI failures for migration regressions | Capture the Task 0 baseline first; compare every run against it, not against zero failures |
| `Xunit.StaFact` 3.0.13 restore against `xunit.v3` 3.2.2 / .NET 10 | 3.0.13 confirmed on NuGet and targets `xunit.v3` 3.x; verify it restores cleanly at Task 7. If it fails, keep `EyeCtrl.App.Tests` on v2 temporarily and migrate the other 5 |
| `IAsyncLifetime` return-type mismatch | Update `AppFixture.cs` signatures from `Task` to `ValueTask`, return `ValueTask.CompletedTask` (Task 7 snippet) |
| WPF + `OutputType=Exe` entry-point conflict in `EyeCtrl.App.Tests` | Verify the project builds without a duplicate-`Main` error; it has no `ApplicationDefinition`, so it should be fine |
| Duplicate `PackageReference` after centralizing | If a `.csproj` still declares a package now in `tests/Directory.Build.props`, NuGet reports duplicate items — strip all 6 csprojs as described in Shared changes |
| `OutputType=Exe` missing on a project | Centralized in `tests/Directory.Build.props`, so it applies to all 6 automatically; `dotnet test` fails with VSTest errors if a project somehow lacks it |
| `coverlet.collector` v8 compatibility | v8 works with xUnit v3 in VSTest mode; no change needed |
| Global `<Using Include="Xunit" />` | Namespace is still `Xunit` in v3 — centralized in props; no per-project change needed |
