# Instance Scoping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `EYECTRL_INSTANCE_ID` scope every process-external name a self-contained `EyeCtrl.App` instance binds, so a second instance cannot touch the first, and so a harness can prove it did not.

**Architecture:** One environment variable, validated once in the composition root, which derives five scoped names and hands them to receivers as plain strings — no component learns that instance ids exist. Two subsystems (supervision, self-restart) are refused rather than scoped, which removes the supervisor process and the restart handshake from the problem. Startup fails closed in two phases: a permission check before anything binds, and a read-back off the constructed objects before anything can act on input.

**Tech stack:** .NET 10 (Contracts and the SDK are `net8.0`), xUnit v3 + NFluent, `Microsoft.CodeAnalysis.CSharp` for the drift guard, Win32 named mutexes/events/pipes and `HKCU` registry.

**Source spec:** [2026-08-12-instance-scoping-design.md](../specs/2026-08-12-instance-scoping-design.md) (v5)

**Verified against:** `main` @ `385211f3`. `EventCoordinator` was decomposed at that commit; the third `%APPDATA%` resolver now lives in `Coordinators/KeyboardCoordinator.cs:54`.

**Unblocks:** [2026-08-10-core-e2e-test-plan.md](2026-08-10-core-e2e-test-plan.md) v2, which names this a hard prerequisite and whose Task 1 this supersedes.

---

## Status — read before starting

**Tasks 1-3 are implemented, committed and green** (`2cff15cf`). Their steps are ticked below. Do
not redo them.

- `InstanceScope` in Contracts, `AppPaths` in Core, and effective-name getters plus injectable names
  on the four receivers.
- Verified: solution builds with 0 errors; full headless suite 1962 passed / 1 skipped /
  `Fehler: 0` across 12 assemblies; 40 new tests (23 + 9 + 5 in Core, 3 in Platform).

**Tasks 4-12 are corrected but NOT implemented.** They were revised in `dd0128b8` against a plan
review plus what implementing Tasks 1-3 established. Nothing in them has met a compiler.

Start at **Task 4**. Re-run the baseline first so you can attribute any failure:

```bash
dotnet test --filter "Category!=UI"
```

Expected: `Fehler: 0` on all 12 assemblies, 1962 passed, 1 skipped.

**Task 8 is the one to be most careful with.** It reorders `OnStartup`, moves two constructions,
changes `AppBootstrapper`'s signature and touches five test call sites. Its corrections are the
least verified in the plan because none of its code has been compiled yet.

---

## Read this before Task 1

Three things about this codebase will bite an engineer who does not know them.

**Test output is German.** Per-test failures appear as `Fehler <FQN>`, and the per-assembly summary is `Fehler: 0, erfolgreich: N`. Grepping for "Failed" or "Passed" reports green on a red run. Assert on `Fehler:` counts.

**Assertions are NFluent, not xUnit.** `Check.That(x).IsEqualTo(y)`, `Check.That(c).HasSize(n)`, `Check.That(s).Contains(sub)`. `IsCloseTo` takes an **absolute** delta. Nullable structs use `.HasValue` rather than `IsNotNull`. xUnit remains the runner. See `AGENTS.md`.

**Named pipes and mutexes are machine-global, and this repo routinely has a dozen worktrees checked out.** Any test that binds a production name will be stolen by a sibling `dotnet test`, and the symptom is a *different* test failing each run. Every test in this plan that touches a kernel object binds a GUID-suffixed name. Before debugging any such failure:

```bash
Get-Process -Name "dotnet","testhost*","EyeCtrl*" -ErrorAction SilentlyContinue
```

**Baseline.** `dotnet test --filter "Category!=UI"` on a quiet box. Record the pass count before Task 1 and compare after each task; the plan states the expected delta per task.

## File structure

**`EyeCtrl.Contracts` (net8.0) — the shared rule.** The SDK targets `net8.0` and references only Contracts, so it cannot see Core. This is the one assembly both the app and the SDK can read.

| File | Responsibility |
|---|---|
| Create `src/EyeCtrl.Contracts/InstanceScope.cs` | `EnvVar`, `TryParseId`, `Scope` — pure string rule, no I/O |

**`EyeCtrl.Core` — the profile path and two receivers.**

| File | Responsibility |
|---|---|
| Create `src/EyeCtrl.Core/Services/AppPaths.cs` | pure profile-directory resolution taking the id as a parameter |
| Modify `src/EyeCtrl.Core/Services/SingleInstanceGuard.cs` | expose `EffectiveName` |
| Modify `src/EyeCtrl.Core/Settings/SettingsService.cs` | injectable writer-mutex name + `EffectiveWriterMutexName` |

**`EyeCtrl.Platform` — the registry receiver.**

| File | Responsibility |
|---|---|
| Modify `src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs` | injectable value name + `EffectiveValueName` |

**`EyeCtrl.App` — the composition root, the policy, the manifest.**

| File | Responsibility |
|---|---|
| Create `src/EyeCtrl.App/Startup/InstanceStartupPolicy.cs` | pure phase-1 decision: may this instance run? |
| Create `src/EyeCtrl.App/Startup/InstanceResource.cs` | one resource's reported name + scoped flag |
| Create `src/EyeCtrl.App/Startup/InstanceScopeVerifier.cs` | pure phase-2 read-back check |
| Create `src/EyeCtrl.App/Startup/InstanceManifest.cs` | manifest record + writer |
| Modify `src/EyeCtrl.App/App.xaml.cs` | startup reordering, phase 1, force supervision off, phase 2, manifest |
| Modify `src/EyeCtrl.App/Services/AppBootstrapper.cs` | pass scoped names into DI |
| Modify `src/EyeCtrl.App/Services/HeartbeatPublisher.cs` | `IsPublishing`; guard the send in `SignalShuttingDown` |
| Modify `src/EyeCtrl.App/Services/ExternalDwellService.cs` | expose `EffectivePipeName` |
| Modify `src/EyeCtrl.App/Services/AppRestartCoordinator.cs` | `isScoped` gate + `IsScopeGated` |
| Modify `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs:54` | use the injected profile dir |

**`EyeCtrl.ViewModels`.**

| File | Responsibility |
|---|---|
| Modify `src/EyeCtrl.ViewModels/CalibrationViewModel.cs:63-65` | drop the self-derived `%APPDATA%` fallback |

**`EyeCtrl.Sdk.Abstractions` (net8.0, shipped).**

| File | Responsibility |
|---|---|
| Create `src/EyeCtrl.Sdk.Abstractions/DwellHead.cs` | `PipeName` + first-use latch |
| Modify `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs` | take the name; read the latched field, never the property |
| Modify `src/EyeCtrl.Sdk.Abstractions/DwellHeadCore.cs` | construct `Client` inside `EnsureStarted` |

**`EyeCtrl.Supervisor`.**

| File | Responsibility |
|---|---|
| Modify `src/EyeCtrl.Supervisor/Supervision/ProcessController.cs` | strip the variable from the child environment |

**Tests.**

| File | Responsibility |
|---|---|
| Create `tests/EyeCtrl.Core.Tests/Services/AppPathsTests.cs` | profile resolution |
| Create `tests/EyeCtrl.Core.Tests/Services/InstanceScopeTests.cs` | the shared rule |
| Create `tests/EyeCtrl.App.Tests/Startup/InstanceStartupPolicyTests.cs` | phase 1 |
| Create `tests/EyeCtrl.App.Tests/Startup/InstanceScopeVerifierTests.cs` | phase 2 |
| Create `tests/EyeCtrl.App.Tests/Startup/InstanceManifestTests.cs` | manifest round-trip |
| Create `tests/EyeCtrl.App.Tests/Startup/InstanceScopeDriftTests.cs` | Roslyn guard |
| Modify `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs` | the one test that pins the bug |
| Create `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellHeadTests.cs` | latch semantics |

`InstanceScopeTests` lives in `Core.Tests` rather than a new `Contracts.Tests` project: `EyeCtrl.Core.Tests` already references Contracts transitively through App? **It does not** — verify with the command in Task 1 Step 2 and add the reference if absent. There is no `EyeCtrl.Contracts.Tests` project and adding one for two pure functions is not worth a csproj.

---

## Task 1: `InstanceScope` — the shared rule

The one thing the app and the shipped SDK both need. Pure string handling so it can live in `net8.0` Contracts and be tested without touching process state.

**Files:**
- Create: `src/EyeCtrl.Contracts/InstanceScope.cs`
- Test: `tests/EyeCtrl.Core.Tests/Services/InstanceScopeTests.cs`
- Modify: `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj` (only if Contracts is not already referenced)

- [x] **Step 1: Record the baseline**

```bash
dotnet test --filter "Category!=UI"
```

Write down the `erfolgreich:` total per assembly. Expected at `385211f3`: exit code 0. Every later step compares against this.

- [x] **Step 2: Check whether Core.Tests can see Contracts**

```bash
grep -n "ProjectReference" tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj
```

`EyeCtrl.Contracts.csproj` **is** absent from this project as of `385211f3` — verified. Add it inside the existing `ItemGroup`:

```xml
    <ProjectReference Include="..\..\src\EyeCtrl.Contracts\EyeCtrl.Contracts.csproj" />
```

- [x] **Step 3: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Services/InstanceScopeTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Contracts;

public class InstanceScopeTests
{
    [Fact]
    public void EnvVar_IsTheDocumentedName()
    {
        Check.That(InstanceScope.EnvVar).IsEqualTo("EYECTRL_INSTANCE_ID");
    }

    [Theory]
    [InlineData("e2e")]
    [InlineData("abc123")]
    [InlineData("with-dash")]
    [InlineData("with_underscore")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void TryParseId_AcceptsLegalIds(string raw)
    {
        Check.That(InstanceScope.TryParseId(raw, out var id)).IsTrue();
        Check.That(id).IsEqualTo(raw);
    }

    [Fact]
    public void TryParseId_TrimsSurroundingWhitespace()
    {
        Check.That(InstanceScope.TryParseId("  run7  ", out var id)).IsTrue();
        Check.That(id).IsEqualTo("run7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseId_RejectsAbsent(string? raw)
    {
        Check.That(InstanceScope.TryParseId(raw, out var id)).IsFalse();
        Check.That(id).IsEqualTo(string.Empty);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("back\\slash")]      // would smuggle a segment into Local\...
    [InlineData("for/ward")]         // would smuggle a path segment
    [InlineData("dot.dot")]          // not in the allowed set
    [InlineData("semi;colon")]
    [InlineData("pipe|char")]
    [InlineData("colon:char")]
    public void TryParseId_RejectsCharactersIllegalInPipeOrMutexNames(string raw)
    {
        Check.That(InstanceScope.TryParseId(raw, out _)).IsFalse();
    }

    [Fact]
    public void TryParseId_RejectsOverLongIds()
    {
        Check.That(InstanceScope.TryParseId(new string('a', 64), out _)).IsTrue();
        Check.That(InstanceScope.TryParseId(new string('a', 65), out _)).IsFalse();
    }

    [Fact]
    public void Scope_AppendsTheSuffix()
    {
        Check.That(InstanceScope.Scope("eyectrl_dwell", "run7")).IsEqualTo("eyectrl_dwell_run7");
        Check.That(InstanceScope.Scope(@"Local\EyeCtrl.App.Singleton", "run7"))
             .IsEqualTo(@"Local\EyeCtrl.App.Singleton_run7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Scope_ReturnsTheDefaultWhenThereIsNoId(string? id)
    {
        Check.That(InstanceScope.Scope("eyectrl_dwell", id)).IsEqualTo("eyectrl_dwell");
    }

    [Fact]
    public void Scope_RejectsAMalformedIdRatherThanBuildingAnUnbindableName()
    {
        // A caller that skipped TryParseId must not get a name with a backslash in the middle.
        ArgumentException? ex = null;
        try { InstanceScope.Scope("eyectrl_dwell", "bad\\id"); }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
    }
}
```

- [x] **Step 4: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InstanceScopeTests"
```

Expected: **compile error** — `InstanceScope` does not exist (`Fehler CS0246`).

- [x] **Step 5: Write the implementation**

Create `src/EyeCtrl.Contracts/InstanceScope.cs`:

```csharp
namespace EyeCtrl.Contracts;

/// <summary>
/// The rule that scopes a process-external name to one app instance.
/// <para>
/// Lives here rather than in <c>EyeCtrl.Core</c> because
/// <c>EyeCtrl.Sdk.Abstractions</c> targets <c>net8.0</c> and references only this assembly — it
/// cannot see Core (<c>net10.0</c>). Server and client deriving the same name from one place is the
/// same rationale <see cref="DwellPipe"/> already documents.
/// </para>
/// <para>
/// Pure string handling, no I/O and no environment reads, so it is testable without touching
/// process state — which matters because <c>Environment.SetEnvironmentVariable</c> races every
/// sibling test class in the same xUnit host.
/// </para>
/// </summary>
public static class InstanceScope
{
    /// <summary>Environment variable naming the instance. Absence means the default instance.</summary>
    public const string EnvVar = "EYECTRL_INSTANCE_ID";

    /// <summary>Maximum id length. Long enough for a GUID in "N" format plus a prefix.</summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// Validates and trims an instance id.
    /// <para>
    /// The allowed set is letters, digits, <c>-</c> and <c>_</c>. It is deliberately narrow: an id
    /// ends up inside a <c>Local\…</c> mutex name, a pipe name and a directory segment, so a
    /// backslash, a forward slash or a colon would either produce an unbindable name or redirect a
    /// path. Rejecting them here is the only place that has to know.
    /// </para>
    /// </summary>
    /// <param name="raw">Raw value, typically from the environment. May be <c>null</c>.</param>
    /// <param name="id">Trimmed id when valid; <see cref="string.Empty"/> otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="raw"/> is a well-formed id.</returns>
    public static bool TryParseId(string? raw, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxIdLength) return false;

        foreach (var c in trimmed)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '-' || c == '_';
            if (!ok) return false;
        }

        id = trimmed;
        return true;
    }

    /// <summary>
    /// Returns <paramref name="defaultName"/> scoped to <paramref name="instanceId"/>, or unchanged
    /// when there is no id.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="instanceId"/> is non-blank but malformed. Throwing rather than silently
    /// sanitising: a caller that skipped <see cref="TryParseId"/> has a bug, and an unbindable or
    /// path-redirecting name is the worst possible way to find out.
    /// </exception>
    public static string Scope(string defaultName, string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return defaultName;

        if (!TryParseId(instanceId, out var id))
            throw new ArgumentException(
                $"'{instanceId}' is not a well-formed instance id.", nameof(instanceId));

        return defaultName + "_" + id;
    }
}
```

- [x] **Step 6: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InstanceScopeTests"
```

Expected: `Fehler: 0, erfolgreich: 23` (nine test methods, but `[Theory]` cases count individually: 5 + 3 + 7 inline cases plus six single facts).

- [x] **Step 7: Commit**

```bash
git add src/EyeCtrl.Contracts/InstanceScope.cs tests/EyeCtrl.Core.Tests
git commit -m "feat(contracts): add the InstanceScope naming rule"
```

---

## Task 2: `AppPaths` — the profile directory

The one resource that is a path rather than a name. Kept a pure function taking the id as a **parameter** so Core needs no reference to Contracts and the test needs no environment mutation.

**Files:**
- Create: `src/EyeCtrl.Core/Services/AppPaths.cs`
- Test: `tests/EyeCtrl.Core.Tests/Services/AppPathsTests.cs`

- [x] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Services/AppPathsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Services;

using System.IO;
using EyeCtrl.Core.Services;

public class AppPathsTests
{
    private const string Roaming = @"C:\Users\someone\AppData\Roaming";

    [Fact]
    public void Resolve_WithNoIdAndNoOverride_IsTheLiveProfile()
    {
        var result = AppPaths.Resolve(overrideDir: null, Roaming, instanceId: null);

        Check.That(result).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void Resolve_WithAnId_NestsUnderInstances()
    {
        var result = AppPaths.Resolve(overrideDir: null, Roaming, instanceId: "run7");

        Check.That(result).IsEqualTo(Path.Combine(Roaming, "EyeCtrl", "instances", "run7"));
    }

    [Fact]
    public void Resolve_ScopedPathIsNestedInsideTheLiveProfile_NotBesideIt()
    {
        // The E2E harness deletes its own profile directory. It must be provably BELOW the live
        // one so a recursive delete of the wrong level is impossible to write by accident.
        var live = AppPaths.Resolve(null, Roaming, null);
        var scoped = AppPaths.Resolve(null, Roaming, "run7");

        Check.That(scoped.StartsWith(live, StringComparison.Ordinal)).IsTrue();
        Check.That(scoped).IsNotEqualTo(live);
    }

    [Fact]
    public void Resolve_OverrideWins_OverBothDefaultAndId()
    {
        var result = AppPaths.Resolve(@"C:\scratch\here", Roaming, instanceId: "run7");

        Check.That(result).IsEqualTo(@"C:\scratch\here");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOverrideIsTreatedAsAbsent(string blank)
    {
        var result = AppPaths.Resolve(blank, Roaming, instanceId: null);

        Check.That(result).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void Resolve_TrimsAPaddedOverride()
    {
        var result = AppPaths.Resolve("  C:\\scratch  ", Roaming, null);

        Check.That(result).IsEqualTo(@"C:\scratch");
    }

    [Fact]
    public void Resolve_RejectsAMalformedId()
    {
        ArgumentException? ex = null;
        try { AppPaths.Resolve(null, Roaming, "bad\\id"); }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
    }

    [Fact]
    public void OverrideEnvVar_IsTheDocumentedName()
    {
        Check.That(AppPaths.OverrideEnvVar).IsEqualTo("EYECTRL_APPDATA_DIR");
    }
}
```

- [x] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AppPathsTests"
```

Expected: **compile error** — `AppPaths` does not exist.

- [x] **Step 3: Write the implementation**

Create `src/EyeCtrl.Core/Services/AppPaths.cs`:

```csharp
namespace EyeCtrl.Core.Services;

using System.IO;

/// <summary>
/// Single source of truth for the per-user data directory holding <c>settings.json</c>,
/// <c>calibration.json</c>, <c>actions.json</c>, <c>recent_emojis.json</c>, the user dictionary,
/// downloaded models and the Serilog logs.
/// <para>
/// A scoped instance nests under <c>instances\&lt;id&gt;</c> rather than sitting in a temp directory,
/// so a scoped profile is provably *inside* the live profile. That is deliberate: the end-to-end
/// harness deletes its own profile on teardown, and "below the live one" makes a delete of the
/// wrong level impossible to write by accident.
/// </para>
/// <para>
/// The id arrives as a parameter, not from the environment, because Core cannot reference
/// <c>EyeCtrl.Contracts</c> where the rule lives — and because a pure function is testable without
/// mutating process state.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>Explicit path override. Wins over both the default and the instance id.</summary>
    public const string OverrideEnvVar = "EYECTRL_APPDATA_DIR";

    /// <summary>Directory name under the roaming profile.</summary>
    private const string RootFolder = "EyeCtrl";

    /// <summary>Subdirectory holding scoped instances.</summary>
    private const string InstancesFolder = "instances";

    /// <summary>
    /// Resolves the profile directory. Precedence: explicit override, then instance id, then the
    /// live profile.
    /// </summary>
    /// <param name="overrideDir">Value of <see cref="OverrideEnvVar"/>, or <c>null</c>.</param>
    /// <param name="roamingAppData">Resolved <c>ApplicationData</c> special folder.</param>
    /// <param name="instanceId">Validated instance id, or <c>null</c> for the default instance.</param>
    /// <exception cref="ArgumentException"><paramref name="instanceId"/> is non-blank but malformed.</exception>
    public static string Resolve(string? overrideDir, string roamingAppData, string? instanceId)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir.Trim();

        var root = Path.Combine(roamingAppData, RootFolder);
        if (string.IsNullOrWhiteSpace(instanceId)) return root;

        // Validate the same way InstanceScope does. Duplicated rather than referenced because Core
        // cannot see Contracts; InstanceScopeDriftTests (Task 10) asserts the two agree.
        var id = instanceId.Trim();
        if (id.Length > 64 || !IsLegalId(id))
            throw new ArgumentException($"'{instanceId}' is not a well-formed instance id.", nameof(instanceId));

        return Path.Combine(root, InstancesFolder, id);
    }

    private static bool IsLegalId(string id)
    {
        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return false;
        }
        return id.Length > 0;
    }
}
```

- [x] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AppPathsTests"
```

Expected: `Fehler: 0, erfolgreich: 9` (eight methods; one `[Theory]` contributes two cases).

- [x] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Services/AppPaths.cs tests/EyeCtrl.Core.Tests
git commit -m "feat(core): add AppPaths profile resolution with instance nesting"
```

---

## Task 3: Effective-name surface on the four receivers

Phase 2 reads names back off constructed objects rather than re-deriving them, because re-deriving would compute the same expected value twice and agree with itself. That needs a getter on each receiver, and two of them need an injectable name first.

**Files:**
- Modify: `src/EyeCtrl.Core/Services/SingleInstanceGuard.cs`
- Modify: `src/EyeCtrl.Core/Settings/SettingsService.cs:178`
- Modify: `src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs`
- Modify: `src/EyeCtrl.App/Services/ExternalDwellService.cs`
- Test: `tests/EyeCtrl.Core.Tests/Services/ReceiverNameTests.cs`
- Test: `tests/EyeCtrl.Platform.Tests/Services/WindowsStartupRegistrarNameTests.cs`

- [x] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Services/ReceiverNameTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Services;

using System.IO;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

public class ReceiverNameTests
{
    [Fact]
    public void SingleInstanceGuard_ReportsItsDefaultName()
    {
        using var guard = new SingleInstanceGuard();

        Check.That(guard.EffectiveName).IsEqualTo(SingleInstanceGuard.DefaultMutexName);
    }

    [Fact]
    public void SingleInstanceGuard_ReportsAnInjectedName()
    {
        var name = @"Local\EyeCtrl.App.Singleton_" + Guid.NewGuid().ToString("N");
        using var guard = new SingleInstanceGuard(name);

        Check.That(guard.EffectiveName).IsEqualTo(name);
    }

    [Fact]
    public void SettingsService_ReportsItsDefaultWriterMutexName()
    {
        var svc = new SettingsService(Path.Combine(Path.GetTempPath(), "unused.json"), SettingsWriter.App);

        Check.That(svc.EffectiveWriterMutexName).IsEqualTo(SettingsService.DefaultWriterMutexName);
        Check.That(SettingsService.DefaultWriterMutexName).IsEqualTo(@"Local\EyeCtrl.Settings.Writer");
    }

    [Fact]
    public void SettingsService_ReportsAnInjectedWriterMutexName()
    {
        var name = @"Local\EyeCtrl.Settings.Writer_" + Guid.NewGuid().ToString("N");
        var svc = new SettingsService(
            Path.Combine(Path.GetTempPath(), "unused.json"), SettingsWriter.App,
            logger: null, writerMutexName: name);

        Check.That(svc.EffectiveWriterMutexName).IsEqualTo(name);
    }

    [Fact]
    public void SettingsService_UsesTheInjectedMutexWhenSaving()
    {
        // Proves the name is not merely reported but actually bound: hold the injected mutex and
        // confirm Save blocks on it. A name that is reported but unused would pass phase 2 while
        // still contending on the production mutex.
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var name = @"Local\EyeCtrl.Settings.Writer_" + Guid.NewGuid().ToString("N");
        var svc = new SettingsService(
            Path.Combine(dir, "settings.json"), SettingsWriter.App, logger: null, writerMutexName: name);

        using var held = new System.Threading.Mutex(initiallyOwned: true, name: name);
        var saveFinished = System.Threading.Tasks.Task.Run(() => svc.Save(new EyeCtrlSettings()));

        Check.That(saveFinished.Wait(TimeSpan.FromMilliseconds(400))).IsFalse();  // blocked

        held.ReleaseMutex();

        Check.That(saveFinished.Wait(TimeSpan.FromSeconds(5))).IsTrue();          // released
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
```

Create `tests/EyeCtrl.Platform.Tests/Services/WindowsStartupRegistrarNameTests.cs`:

```csharp
namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;

public class WindowsStartupRegistrarNameTests
{
    [Fact]
    public void ReportsItsDefaultValueName()
    {
        var registrar = new WindowsStartupRegistrar();

        Check.That(registrar.EffectiveValueName).IsEqualTo("EyeCtrl");
    }

    [Fact]
    public void ReportsAnInjectedValueName()
    {
        var registrar = new WindowsStartupRegistrar("EyeCtrl_run7");

        Check.That(registrar.EffectiveValueName).IsEqualTo("EyeCtrl_run7");
    }

    [Fact]
    public void IsRegistered_ReadsTheInjectedValueName_NotTheDefault()
    {
        // The live "EyeCtrl" value may or may not exist on the dev box. A GUID-suffixed name
        // certainly does not, so this asserts the read honours the injected name without
        // touching the real autostart entry.
        var registrar = new WindowsStartupRegistrar("EyeCtrl_absent_" + Guid.NewGuid().ToString("N"));

        Check.That(registrar.IsRegistered).IsFalse();
    }
}
```

- [x] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ReceiverNameTests"
dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~WindowsStartupRegistrarNameTests"
```

Expected: **compile errors** — `EffectiveName`, `DefaultWriterMutexName`, `EffectiveWriterMutexName`, the `writerMutexName` parameter and `EffectiveValueName` do not exist.

- [x] **Step 3: Add `EffectiveName` to `SingleInstanceGuard`**

In `src/EyeCtrl.Core/Services/SingleInstanceGuard.cs`, after the `_mutexName` field:

```csharp
    /// <summary>
    /// The mutex name this guard binds. Read back by the app's phase-2 instance-scope check, which
    /// verifies what was constructed rather than re-deriving what should have been.
    /// </summary>
    public string EffectiveName => _mutexName;
```

- [x] **Step 4: Make the settings writer mutex injectable**

In `src/EyeCtrl.Core/Settings/SettingsService.cs`, add near the other constants:

```csharp
    /// <summary>
    /// Production name of the cross-process mutex serialising the read-merge-write in <see cref="Save"/>.
    /// Was an inline literal; hoisted so it can be scoped per instance and reported to the app's
    /// phase-2 check.
    /// </summary>
    public const string DefaultWriterMutexName = @"Local\EyeCtrl.Settings.Writer";
```

Add a field beside `_writer`:

```csharp
    private readonly string _writerMutexName;
```

Add the parameter to the constructor — append it so every existing call site keeps compiling:

```csharp
    /// <param name="writerMutexName">
    /// Cross-process writer mutex name. Defaults to <see cref="DefaultWriterMutexName"/>; a scoped
    /// instance passes a suffixed name so two instances with separate profiles do not serialise
    /// against each other, and neither blocks the default instance.
    /// </param>
```

and in the body:

```csharp
        _writerMutexName = writerMutexName ?? DefaultWriterMutexName;
```

Expose it:

```csharp
    /// <summary>The writer mutex name this instance binds. Read back by the phase-2 check.</summary>
    public string EffectiveWriterMutexName => _writerMutexName;
```

Then replace the literal at line 178:

```csharp
        using var mutex = new Mutex(false, _writerMutexName);
```

- [x] **Step 5: Make the Run value name injectable**

Replace the top of `src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs` — the `ValueName` constant becomes a default plus an instance field:

```csharp
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Production value name under the Run key.</summary>
    public const string DefaultValueName = "EyeCtrl";

    private readonly string _exePath;
    private readonly string _valueName;

    /// <summary>Registrar for the production value name.</summary>
    public WindowsStartupRegistrar() : this(null) { }

    /// <summary>
    /// Registrar for a specific value name.
    /// </summary>
    /// <param name="valueName">
    /// Value name under the Run key. Defaults to <see cref="DefaultValueName"/>. A scoped instance
    /// passes a suffixed name, which is what stops a test run from deleting the default instance's
    /// autostart entry — the bug that produced this whole design.
    /// </param>
    public WindowsStartupRegistrar(string? valueName)
    {
        _exePath = Environment.ProcessPath ?? string.Empty;
        _valueName = valueName ?? DefaultValueName;
    }

    /// <summary>The Run value name this registrar reads and writes. Read back by the phase-2 check.</summary>
    public string EffectiveValueName => _valueName;
```

Then replace every use of `ValueName` in the file with `_valueName` — there are three, at the `SetValue`, `GetValue`/`DeleteValue` pair, and the `IsRegistered` read.

- [x] **Step 6: Expose the dwell pipe name**

In `src/EyeCtrl.App/Services/ExternalDwellService.cs`, after the `_pipeName` field:

```csharp
    /// <summary>The pipe name this service serves on. Read back by the phase-2 check.</summary>
    public string EffectivePipeName => _pipeName;
```

- [x] **Step 7: Run to verify they pass**

```bash
dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ReceiverNameTests"
dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~WindowsStartupRegistrarNameTests"
```

Expected: `Fehler: 0, erfolgreich: 5` in Core.Tests and `Fehler: 0, erfolgreich: 3` in Platform.Tests.

- [x] **Step 8: Confirm nothing else broke**

```bash
dotnet test --filter "Category!=UI"
```

Expected: `Fehler: 0` everywhere. Cumulative new tests after this task: 23 (Task 1) + 9 (Task 2) + 8 (this task: 5 in Core, 3 in Platform) = 40 above the baseline recorded in Task 1 Step 1.

- [x] **Step 9: Commit**

```bash
git add src/EyeCtrl.Core src/EyeCtrl.Platform src/EyeCtrl.App/Services/ExternalDwellService.cs tests
git commit -m "feat: make receiver names injectable and readable for scope verification"
```

---

## Task 4: The heartbeat exit path

The spec's round-2 BLOCKER. `SignalShuttingDown` dials the pipe with no publishing guard, and today the single-instance mutex is the only thing stopping a second instance from reaching it — so scoping the mutex is what makes this reachable. One existing test pins the bug.

**Files:**
- Modify: `src/EyeCtrl.App/Services/HeartbeatPublisher.cs:138-151`
- Modify: `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs:46-95`

- [ ] **Step 1: Write the failing regression test**

Add to `tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs`:

```csharp
    [Fact]
    public async Task SignalShuttingDown_SendsNothing_WhenNotPublishing()
    {
        // Regression for the instance-scoping design's round-2 BLOCKER. An unsupervised instance's
        // graceful exit used to dial eyectrl_watch anyway. A supervisor in adoption mode
        // authenticates on exe path + session and skips the creation-time comparison, so that
        // connection binds as VERIFIED; CleanExitRequested carries no PID, so the supervisor applies
        // the clean-exit to whatever it is supervising, and the live app gets force-killed when it
        // does not exit inside the 5s deadline.
        using var server = new NamedPipeServerStream(
            _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        pub.SignalShuttingDown();

        // Nothing should ever connect. Bounded wait: a pass is "no connection within the budget".
        var finished = await Task.WhenAny(connected, Task.Delay(1500));

        Check.That(finished).Not.IsSameReferenceAs(connected);
        Check.That(pub.IsPublishing).IsFalse();
    }

    [Fact]
    public void IsPublishing_IsFalseWhenNotSupervised_AndTrueAfterEnable()
    {
        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        Check.That(pub.IsPublishing).IsFalse();

        pub.EnablePublishing();
        Check.That(pub.IsPublishing).IsTrue();
    }
```

- [ ] **Step 2: Add `IsPublishing` only, then run to verify the new test fails**

Adding the property and the guard together would mean never observing the regression test red,
which is the point of writing it. So add **only** the property first, beside the `_publishing` field
in `src/EyeCtrl.App/Services/HeartbeatPublisher.cs`:

```csharp
    /// <summary>
    /// Whether this publisher is permitted to put anything on the wire. Exposed for the app's
    /// phase-2 instance-scope check, which asserts a scoped instance never publishes.
    /// </summary>
    public bool IsPublishing => Volatile.Read(ref _publishing) != 0;
```

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatPublisherTests"
```

Expected: `SignalShuttingDown_SendsNothing_WhenNotPublishing` **fails** — a connection does arrive,
which is the bug. `IsPublishing_IsFalseWhenNotSupervised_AndTrueAfterEnable` passes.

- [ ] **Step 3: Guard the send**

In `SignalShuttingDown`, guard **only the send**:

```csharp
    public void SignalShuttingDown(bool restartRequested = false)
    {
        _shuttingDown = true;
        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);
        var final = new HeartbeatPayload(
            _pid, _startedAtUtc, false, false, lastFrame,
            ShuttingDown: true, RestartRequested: restartRequested);
        _pending = final;

        // Guard the SEND, not the method. An early return here would skip _shuttingDown and
        // _pending above and silently empty CurrentPayload, which exists for diagnostics and is
        // asserted by SignalShuttingDown_StampsFinalPayload.
        //
        // The guard itself: this method's doc comment claimed "if not supervised (no server), this
        // is a bounded no-op", which holds only when no server exists. When the user's own
        // supervisor IS listening, an unsupervised instance's exit reaches it. TryReachSupervisor
        // has always had this check (HeartbeatPublisher.cs:179); this method never did.
        if (!IsPublishing) return;

        try { SendOneAsync(final, CancellationToken.None).Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort — never block OnExit on the final send */ }
    }
```

- [ ] **Step 4: Fix the one existing test that pins the bug**

`SignalShuttingDown_DeliversFinalPayload_EvenUnderBlockingSyncContext` (`:46-95`) constructs with `supervised: false` and asserts delivery. It is the F2 regression guard for a dispatcher-strand bug, so its delivery assertion must survive. Add one line before `SignalShuttingDown`:

```csharp
        using var pub = new HeartbeatPublisher(supervised: false, pipeName: _pipeName);
        pub.EnablePublishing();   // the send is now gated on this; the F2 assertion still stands
```

The writer loop this starts parks on `_sendSignal.WaitAsync` with nothing stamped and nothing released, so the synchronous flush remains the only connection — which matters because the test's server is `maxInstances: 1`.

**Do not** weaken the guard to keep this green. And do not touch the two
`HeartbeatRestartPayloadTests.SignalShuttingDown_*` tests: they already use `supervised: true`
(`:174`, `:188`) and are unaffected.

- [ ] **Step 5: Run to verify**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatPublisherTests"
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~HeartbeatRestartPayloadTests"
```

Expected: `Fehler: 0` for both. The second should be unchanged from baseline.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/HeartbeatPublisher.cs tests/EyeCtrl.App.Tests/Services/HeartbeatPublisherTests.cs
git commit -m "fix(heartbeat): stop SignalShuttingDown dialling the pipe when not publishing"
```

---

## Task 5: Refuse self-restart when scoped

`HandleRestart` is the load-bearing gate. Suppressing the offer is cosmetic: `ShouldOfferSupervisor` evaluates to "is a supervisor exe present" once `Enabled` is forced false, and both the settings window and the view model send `RestartAppRequestedMessage` regardless. `HandleRestart` → `TryBringUpSupervisor` calls `_enablePublishing()` **before** flipping `Enabled`, which is the one mid-session route onto the heartbeat pipe.

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppRestartCoordinator.cs:77-106,140-141`
- Test: `tests/EyeCtrl.App.Tests/Services/AppRestartCoordinatorScopeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Services/AppRestartCoordinatorScopeTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

public class AppRestartCoordinatorScopeTests
{
    private static AppRestartCoordinator Build(
        bool isScoped, EyeCtrlSettings settings,
        Action? onShutdown = null, Action? onEnablePublishing = null,
        Func<bool>? onLaunchSupervisor = null, Action<string>? onUnavailable = null)
        => new(
            new WeakReferenceMessenger(),
            settings,
            tryReachSupervisor: () => false,
            selfRestart: () => RestartOutcome.Started,
            shutdown: () => onShutdown?.Invoke(),
            reportInProgress: () => { },
            reportUnavailable: m => onUnavailable?.Invoke(m),
            runInBackground: a => a(),          // synchronous, so assertions need no waiting
            postToUi: a => a(),
            supervisorInstalled: () => true,
            launchSupervisor: () => onLaunchSupervisor?.Invoke() ?? true,
            enablePublishing: () => onEnablePublishing?.Invoke(),
            persistSettings: () => { },
            isScoped: isScoped);

    [Fact]
    public void Unscoped_IsNotGated()
    {
        var c = Build(isScoped: false, new EyeCtrlSettings());

        Check.That(c.IsScopeGated).IsFalse();
    }

    [Fact]
    public void Scoped_ReportsItselfGated()
    {
        var c = Build(isScoped: true, new EyeCtrlSettings());

        Check.That(c.IsScopeGated).IsTrue();
    }

    [Fact]
    public void Scoped_HandleRestart_DoesNotShutDown_AndSaysWhy()
    {
        var shutdowns = 0;
        string? message = null;
        var c = Build(isScoped: true, new EyeCtrlSettings(),
            onShutdown: () => shutdowns++, onUnavailable: m => message = m);

        c.HandleRestart(startSupervisor: false);

        Check.That(shutdowns).IsEqualTo(0);
        Check.That(message).IsNotNull();
        Check.That(message!).Contains("scoped");
    }

    [Fact]
    public void Scoped_HandleRestart_NeverEnablesPublishing()
    {
        // The specific escape this gate closes: TryBringUpSupervisor enables publishing BEFORE
        // flipping Supervision.Enabled, so a mid-session restart-with-supervisor would put a scoped
        // instance on the heartbeat pipe despite supervision being forced off at startup.
        var enables = 0;
        var launches = 0;
        var c = Build(isScoped: true, new EyeCtrlSettings(),
            onEnablePublishing: () => enables++, onLaunchSupervisor: () => { launches++; return true; });

        c.HandleRestart(startSupervisor: true);

        Check.That(enables).IsEqualTo(0);
        Check.That(launches).IsEqualTo(0);
    }

    [Fact]
    public void Scoped_ShouldOfferSupervisor_IsFalse_EvenThoughOneIsInstalled()
    {
        var c = Build(isScoped: true, new EyeCtrlSettings());

        Check.That(c.ShouldOfferSupervisor()).IsFalse();
    }

    [Fact]
    public void Unscoped_ShouldOfferSupervisor_StillTrue_WhenInstalledAndDisabled()
    {
        // Guards against the gate being applied unconditionally: the unscoped behaviour is
        // "offer when one is installed and none is answering".
        var c = Build(isScoped: false, new EyeCtrlSettings());

        Check.That(c.ShouldOfferSupervisor()).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppRestartCoordinatorScopeTests"
```

Expected: **compile error** — no `isScoped` parameter and no `IsScopeGated`.

- [ ] **Step 3: Add the gate**

In `src/EyeCtrl.App/Services/AppRestartCoordinator.cs`, add a field and append the constructor parameter (appending keeps existing call sites compiling):

```csharp
    private readonly bool _isScoped;
```

```csharp
        Action persistSettings,
        bool isScoped = false)
```

```csharp
        _isScoped = isScoped;
```

Expose it for phase 2:

```csharp
    /// <summary>
    /// True when this instance is scoped and every restart route is therefore refused. Read back by
    /// the app's phase-2 instance-scope check.
    /// </summary>
    public bool IsScopeGated => _isScoped;
```

Gate the offer:

```csharp
    public bool ShouldOfferSupervisor() =>
        !_isScoped
        && _supervisorInstalled()
        && !(_settings.Supervision.Enabled && _tryReachSupervisor());
```

And gate `HandleRestart` at the very top, before the in-flight guard so a refusal never consumes the single attempt:

```csharp
    public void HandleRestart(bool startSupervisor)
    {
        // A scoped instance runs alone: it has no supervisor to hand off to, and a self-restart
        // child would inherit EYECTRL_INSTANCE_ID and re-enter this state. Refusing here rather
        // than only hiding the offer is what matters — ShouldOfferSupervisor removes a button, but
        // both SettingsWindow and SettingsViewModel send RestartAppRequestedMessage regardless, and
        // TryBringUpSupervisor calls _enablePublishing() before flipping Enabled.
        if (_isScoped)
        {
            _reportUnavailable(
                "Restart is unavailable: this is a scoped instance (EYECTRL_INSTANCE_ID is set), "
                + "which runs alone and refuses supervision and self-restart.");
            return;
        }

        if (Interlocked.Exchange(ref _restartInFlight, 1) != 0)
        {
            return;
        }
        // ... existing body unchanged
```

- [ ] **Step 4: Add the second line of defence in `SelfRestartStrategy`, and WIRE it**

`Begin` is the only place `Local\EyeCtrl.App.RestartReady` is created
(`SelfRestartStrategy.cs:125-126`), so refusing there is what actually removes that name from a
scoped instance's surface. Three edits, and the third is the one an earlier draft of this plan
omitted — leaving the gate implemented but dead in production.

**(a) A new outcome.** `RestartOutcome` is `Started | NoExePath | LaunchFailed | ChildExitedEarly
| ChildNotReady` (`SelfRestartStrategy.cs:17-33`) — verified. None means "refused", so add one:

```csharp
    /// <summary>This instance is scoped and refuses self-restart. Never attempt, never exit.</summary>
    RefusedScoped,
```

Confirm no consumer switches exhaustively over the enum in a way a new member breaks:

```bash
grep -rn "RestartOutcome\." src tests --include=*.cs
```

Every consumer should compare against `Started` (or against a failure set); if any has a
`switch` expression with no default arm, add one rather than reusing a misleading member.

**(b) The gate.** Add `bool isScoped = false` as the last constructor parameter, store it, and at
the top of `Begin`:

```csharp
        // Second line of defence behind AppRestartCoordinator.HandleRestart.
        if (_isScoped) return RestartOutcome.RefusedScoped;
```

**(c) The wiring — do not skip this.** `CreateDefault()` is a static factory with no scoped
argument, and `App.xaml.cs:275` passes `SelfRestartStrategy.CreateDefault().Begin` as the
coordinator's `selfRestart` delegate. Give the factory the flag and thread it through:

```csharp
    /// <param name="isScoped">
    /// True when EYECTRL_INSTANCE_ID is set. A scoped instance refuses self-restart, so the ready
    /// event is never created.
    /// </param>
    public static SelfRestartStrategy CreateDefault(bool isScoped = false) =>
```

pass `isScoped` into the constructor call inside it, and update `App.xaml.cs:275` to
`SelfRestartStrategy.CreateDefault(isScoped: _instanceId != null).Begin`.

**(d) A test that drives `Begin`, not just the coordinator.** Add to
`tests/EyeCtrl.App.Tests/Services/` (the existing `SelfRestartStrategy` test file, or a new
`SelfRestartStrategyScopeTests.cs`):

```csharp
    [Fact]
    public void Begin_RefusesWhenScoped_AndNeverLaunchesOrCreatesTheReadyEvent()
    {
        var launches = 0;
        var strategy = new SelfRestartStrategy(
            resolveExePath: () => @"C:ake\EyeCtrl.App.exe",
            launch: (_, _) => { launches++; throw new InvalidOperationException("must not launch"); },
            waitForReady: (_, _) => true,
            readyWait: TimeSpan.FromSeconds(1),
            isScoped: true);

        var outcome = strategy.Begin();

        Check.That(outcome).IsEqualTo(RestartOutcome.RefusedScoped);
        Check.That(launches).IsEqualTo(0);
    }
```

Without this the gate is untested — and an untested gate on the only creator of the ready event
is exactly the shape of the defect this plan's review found.

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AppRestartCoordinator"
```

Expected: `Fehler: 0`; the six new tests pass and the existing `AppRestartCoordinator` tests are unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/AppRestartCoordinator.cs src/EyeCtrl.App/Services/SelfRestartStrategy.cs tests/EyeCtrl.App.Tests/Services/AppRestartCoordinatorScopeTests.cs
git commit -m "feat(restart): refuse restart and supervisor bring-up on a scoped instance"
```

---

## Task 6: Phase 1 — may this instance run?

Pure decision logic, so the ordering constraints are testable without launching WPF. Note the asymmetry the spec pins: the malformed-id row cannot log, because a malformed id yields no legal `instances\<id>` directory and logging would open the **live** `%APPDATA%\EyeCtrl\logs` — writing to the profile the instance was supposed to stay out of, on the one path where the id cannot be trusted.

**Files:**
- Create: `src/EyeCtrl.App/Startup/InstanceStartupPolicy.cs`
- Test: `tests/EyeCtrl.App.Tests/Startup/InstanceStartupPolicyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Startup/InstanceStartupPolicyTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class InstanceStartupPolicyTests
{
    [Fact]
    public void NoId_RunsAsTheDefaultInstance()
    {
        var d = InstanceStartupPolicy.Evaluate(rawId: null, isTestMode: false, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunDefault);
        Check.That(d.InstanceId).IsNull();
        Check.That(d.CanLog).IsTrue();
    }

    [Fact]
    public void BlankId_RunsAsTheDefaultInstance()
    {
        var d = InstanceStartupPolicy.Evaluate("   ", isTestMode: false, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunDefault);
    }

    [Fact]
    public void ValidId_WithTestMode_RunsScoped()
    {
        var d = InstanceStartupPolicy.Evaluate("run7", isTestMode: true, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunScoped);
        Check.That(d.InstanceId).IsEqualTo("run7");
    }

    [Fact]
    public void MalformedId_Refuses_AndCannotLog()
    {
        var d = InstanceStartupPolicy.Evaluate("bad id", isTestMode: true, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsFalse();      // no legal profile dir => no scoped log dir
        Check.That(d.Reason!).Contains("EYECTRL_INSTANCE_ID");
    }

    [Fact]
    public void ValidId_WithoutTestMode_Refuses_AndCanLog()
    {
        // The concurrency boundary: --test-mode forces MouseGazeProvider, so scoping cannot be used
        // to put two instances on one eye tracker.
        var d = InstanceStartupPolicy.Evaluate("run7", isTestMode: false, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsTrue();       // the id is valid, so a scoped log dir exists
        Check.That(d.Reason!).Contains("--test-mode");
    }

    [Fact]
    public void ValidId_WithRestartHandoff_Refuses()
    {
        var d = InstanceStartupPolicy.Evaluate("run7", isTestMode: true, isRestartHandoff: true);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsTrue();
        Check.That(d.Reason!).Contains("restart");
    }

    [Fact]
    public void MalformedId_TakesPrecedenceOverTheOtherRefusals()
    {
        // Order matters: a malformed id must be reported as such, because it is the only refusal
        // that must not initialise Serilog.
        var d = InstanceStartupPolicy.Evaluate("bad id", isTestMode: false, isRestartHandoff: true);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsFalse();
    }

    [Fact]
    public void RefusalExitCode_IsEXCONFIG()
    {
        Check.That(InstanceStartupPolicy.RefusalExitCode).IsEqualTo(78);
    }

    [Fact]
    public void UnscopedHandoff_IsAllowed()
    {
        // A restart handoff on the DEFAULT instance is legitimate and must not be refused.
        var d = InstanceStartupPolicy.Evaluate(null, isTestMode: false, isRestartHandoff: true);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunDefault);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~InstanceStartupPolicyTests"
```

Expected: **compile error** — the policy does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/EyeCtrl.App/Startup/InstanceStartupPolicy.cs`:

```csharp
namespace EyeCtrl.App.Startup;

using EyeCtrl.Contracts;

/// <summary>What startup should do about the instance id it was given.</summary>
public enum InstanceStartupOutcome
{
    /// <summary>No id: behave exactly as before this feature existed.</summary>
    RunDefault,

    /// <summary>Valid id and every precondition met: run scoped.</summary>
    RunScoped,

    /// <summary>Refuse to start. Half-scoped is the one state that must be impossible.</summary>
    Refuse
}

/// <summary>The phase-1 decision, and whether it can be logged.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="InstanceId">Validated id when scoped; <c>null</c> otherwise.</param>
/// <param name="Reason">Human-readable refusal reason; <c>null</c> unless refusing.</param>
/// <param name="CanLog">
/// Whether a scoped log directory can be derived. <c>false</c> only for a malformed id — see
/// <see cref="InstanceStartupPolicy"/>.
/// </param>
public sealed record InstanceStartupDecision(
    InstanceStartupOutcome Outcome,
    string? InstanceId,
    string? Reason,
    bool CanLog);

/// <summary>
/// Pure phase-1 gate: may this instance run at all? Kept free of I/O so the ordering rules are
/// testable without launching WPF, and so the one branch that must not initialise logging is
/// explicit rather than implied by statement order.
/// </summary>
public static class InstanceStartupPolicy
{
    /// <summary>
    /// Exit code for an isolation refusal. <c>EX_CONFIG</c>, chosen to be distinguishable from the
    /// <c>0</c> a rejected second instance already returns via <c>Shutdown()</c>.
    /// </summary>
    public const int RefusalExitCode = 78;

    /// <summary>
    /// Decides. Evaluation order is load-bearing: the malformed-id branch comes first because it is
    /// the only one that cannot be logged, and a refusal reported as something else would send the
    /// caller down the logging path.
    /// </summary>
    /// <param name="rawId">Raw <see cref="InstanceScope.EnvVar"/> value, or <c>null</c>.</param>
    /// <param name="isTestMode"><c>--test-mode</c> was passed.</param>
    /// <param name="isRestartHandoff"><c>--restart-handoff</c> was passed.</param>
    public static InstanceStartupDecision Evaluate(string? rawId, bool isTestMode, bool isRestartHandoff)
    {
        // Absent is not an error: the default instance keeps today's behaviour byte for byte.
        if (string.IsNullOrWhiteSpace(rawId))
            return new(InstanceStartupOutcome.RunDefault, null, null, CanLog: true);

        if (!InstanceScope.TryParseId(rawId, out var id))
            return new(
                InstanceStartupOutcome.Refuse, null,
                $"{InstanceScope.EnvVar} is set to '{rawId}', which is not a well-formed instance id "
                + $"(letters, digits, '-' and '_' only, at most {InstanceScope.MaxIdLength} characters).",
                CanLog: false);

        // Scoping separates NAMES. It does not make the runtime concurrent: one eye tracker, one
        // tray area, screen-wide overlays. --test-mode forces MouseGazeProvider, which is what turns
        // "concurrency is unsupported" from a comment into something enforced.
        if (!isTestMode)
            return new(
                InstanceStartupOutcome.Refuse, id,
                $"{InstanceScope.EnvVar} requires --test-mode. Instance scoping separates names, not "
                + "the eye tracker, the tray or the overlays, so a scoped instance may not run as a "
                + "real gaze-driven app.",
                CanLog: true);

        // A scoped instance refuses self-restart, so it can never be a handoff replacement.
        if (isRestartHandoff)
            return new(
                InstanceStartupOutcome.Refuse, id,
                "A scoped instance refuses self-restart, so it cannot be a restart handoff "
                + "replacement.",
                CanLog: true);

        return new(InstanceStartupOutcome.RunScoped, id, null, CanLog: true);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~InstanceStartupPolicyTests"
```

Expected: `Fehler: 0, erfolgreich: 9`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Startup/InstanceStartupPolicy.cs tests/EyeCtrl.App.Tests/Startup
git commit -m "feat(app): add the phase-1 instance startup policy"
```

---

## Task 7: Phase 2 and the manifest

Phase 2 reads names back off the constructed objects. It is the only check that can catch a receiver which accepts an injectable name and is never passed one — as `ExternalDwellService` is today (`AppBootstrapper.cs:236-242` constructs it with no `pipeName`).

**Files:**
- Create: `src/EyeCtrl.App/Startup/InstanceResource.cs`
- Create: `src/EyeCtrl.App/Startup/InstanceScopeVerifier.cs`
- Create: `src/EyeCtrl.App/Startup/InstanceManifest.cs`
- Test: `tests/EyeCtrl.App.Tests/Startup/InstanceScopeVerifierTests.cs`
- Test: `tests/EyeCtrl.App.Tests/Startup/InstanceManifestTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Startup/InstanceScopeVerifierTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class InstanceScopeVerifierTests
{
    private static InstanceResource Scoped(string name) => new(name, Scoped: true, name + "_run7");
    private static InstanceResource Unscoped(string name) => new(name, Scoped: false, name);

    private static readonly string[] Expected =
    [
        "ProfileDirectory", "AppSingletonMutex", "SettingsWriterMutex", "DwellPipe", "StartupRunValue"
    ];

    [Fact]
    public void Passes_WhenEveryExpectedResourceIsScoped()
    {
        var result = InstanceScopeVerifier.Verify(Expected.Select(Scoped).ToArray(), Expected);

        Check.That(result.IsSatisfied).IsTrue();
        Check.That(result.Failures).IsEmpty();
    }

    [Fact]
    public void Fails_AndNamesTheResource_WhenOneIsUnscoped()
    {
        var reported = Expected.Select(n => n == "DwellPipe" ? Unscoped(n) : Scoped(n)).ToArray();

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.IsSatisfied).IsFalse();
        Check.That(result.Failures).HasSize(1);
        Check.That(result.Failures[0]).Contains("DwellPipe");
    }

    [Fact]
    public void Fails_WhenAnExpectedResourceIsNotReportedAtAll()
    {
        // The failure a missed wiring change produces: the resource exists but nobody collected it.
        var reported = Expected.Where(n => n != "SettingsWriterMutex").Select(Scoped).ToArray();

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.IsSatisfied).IsFalse();
        Check.That(result.Failures[0]).Contains("SettingsWriterMutex");
        Check.That(result.Failures[0]).Contains("not reported");
    }

    [Fact]
    public void Fails_WhenAScopedResourceReportsTheDefaultValue()
    {
        // ExternalDwellService accepts a pipeName and is currently constructed without one. The
        // effective value equalling the default is exactly what that looks like from here.
        var reported = Expected.Select(n =>
            n == "DwellPipe" ? new InstanceResource(n, Scoped: true, "eyectrl_dwell") : Scoped(n)).ToArray();

        var result = InstanceScopeVerifier.Verify(
            reported, Expected, defaults: new Dictionary<string, string> { ["DwellPipe"] = "eyectrl_dwell" });

        Check.That(result.IsSatisfied).IsFalse();
        Check.That(result.Failures[0]).Contains("eyectrl_dwell");
    }

    [Fact]
    public void ReportsEveryFailure_NotJustTheFirst()
    {
        var reported = new[] { Unscoped("ProfileDirectory"), Unscoped("DwellPipe") };

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.Failures.Count).IsStrictlyGreaterThan(1);
    }
}
```

Create `tests/EyeCtrl.App.Tests/Startup/InstanceManifestTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Startup;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Startup;

public class InstanceManifestTests
{
    private static InstanceManifest Sample() => new(
        InstanceId: "run7",
        ProcessId: 4242,
        StartedAtUtc: new DateTime(2026, 8, 13, 9, 14, 22, DateTimeKind.Utc),
        TestMode: true,
        Resources:
        [
            new InstanceResource("ProfileDirectory", true, @"C:\p\instances\run7"),
            new InstanceResource("DwellPipe", true, "eyectrl_dwell_run7")
        ],
        Refused: ["Supervision", "SelfRestart"]);

    [Fact]
    public void SerializesCamelCase_SoTheHarnessCanReadIt()
    {
        var json = JsonSerializer.Serialize(Sample(), InstanceManifest.JsonOptions);

        Check.That(json).Contains("\"instanceId\"");
        Check.That(json).Contains("\"effectiveValue\"");
        Check.That(json).Contains("\"refused\"");
        Check.That(json).Not.Contains("\"InstanceId\"");
    }

    [Fact]
    public void RoundTrips()
    {
        var json = JsonSerializer.Serialize(Sample(), InstanceManifest.JsonOptions);
        var back = JsonSerializer.Deserialize<InstanceManifest>(json, InstanceManifest.JsonOptions);

        Check.That(back).IsNotNull();
        Check.That(back!.InstanceId).IsEqualTo("run7");
        Check.That(back.Resources).HasSize(2);
        Check.That(back.Refused).ContainsExactly("Supervision", "SelfRestart");
    }

    [Fact]
    public void Write_CreatesInstanceJsonInTheProfileDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            InstanceManifest.Write(dir, Sample());

            var path = Path.Combine(dir, InstanceManifest.FileName);
            Check.That(File.Exists(path)).IsTrue();
            Check.That(File.ReadAllText(path)).Contains("run7");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Write_CreatesMissingParentDirectories()
    {
        // The profile directory may not exist on a first scoped launch, so Write creates the tree
        // rather than failing. What it does NOT do is swallow other failures - see the next test.
        var nested = Path.Combine(Path.GetTempPath(), "eyectrl-missing-" + Guid.NewGuid().ToString("N"),
                                  "nested", "deeper");
        try
        {
            InstanceManifest.Write(nested, Sample());

            Check.That(File.Exists(Path.Combine(nested, InstanceManifest.FileName))).IsTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(nested))!, true); } catch { }
        }
    }

    [Fact]
    public void Write_PropagatesFailures_SoAHarnessCannotProceedUnverified()
    {
        // A "directory" path that is actually an existing FILE cannot be created, and the failure
        // must surface: a harness that cannot read instance.json must not drive real input.
        var file = Path.Combine(Path.GetTempPath(), "eyectrl-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a directory");
        try
        {
            Exception? ex = null;
            try { InstanceManifest.Write(file, Sample()); } catch (Exception e) { ex = e; }

            Check.That(ex).IsNotNull();
        }
        finally { try { File.Delete(file); } catch { } }
    }

    [Fact]
    public void FileName_IsTheDocumentedName()
    {
        Check.That(InstanceManifest.FileName).IsEqualTo("instance.json");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~InstanceScopeVerifierTests|FullyQualifiedName~InstanceManifestTests"
```

Expected: **compile errors** — none of the three types exist.

- [ ] **Step 3: Write `InstanceResource`**

Create `src/EyeCtrl.App/Startup/InstanceResource.cs`:

```csharp
namespace EyeCtrl.App.Startup;

/// <summary>
/// One process-external resource as reported by the object that owns it.
/// <para>
/// <paramref name="EffectiveValue"/> is the name the resource is <b>configured</b> with, not proof
/// it is bound: the settings-writer mutex is created per <c>Save</c> call and may not bind until
/// exit. Genuinely bound at manifest time are the app singleton mutex, the dwell pipe and the Run
/// value.
/// </para>
/// </summary>
/// <param name="Name">Stable identifier, e.g. <c>DwellPipe</c>.</param>
/// <param name="Scoped">Whether this resource took a scoped name.</param>
/// <param name="EffectiveValue">The name or path actually configured.</param>
public sealed record InstanceResource(string Name, bool Scoped, string EffectiveValue);
```

- [ ] **Step 4: Write `InstanceScopeVerifier`**

Create `src/EyeCtrl.App/Startup/InstanceScopeVerifier.cs`:

```csharp
namespace EyeCtrl.App.Startup;

/// <summary>Outcome of the phase-2 read-back.</summary>
/// <param name="IsSatisfied">True when every expected resource reported a scoped name.</param>
/// <param name="Failures">One human-readable line per problem; empty when satisfied.</param>
public sealed record InstanceScopeResult(bool IsSatisfied, IReadOnlyList<string> Failures);

/// <summary>
/// Phase 2: after wiring, before anything can act on input, confirm every resource actually took
/// its scoped name.
/// <para>
/// Deliberately a <b>read-back</b> rather than a re-derivation. Re-deriving would compute the same
/// expected value twice and agree with itself; reading back off the constructed objects is what
/// catches a receiver that accepts an injectable name and is never passed one.
/// </para>
/// <para>
/// Pure, so the failure modes are testable without launching WPF.
/// </para>
/// </summary>
public static class InstanceScopeVerifier
{
    /// <summary>
    /// Verifies reported resources against the expected set.
    /// </summary>
    /// <param name="reported">What the constructed objects said about themselves.</param>
    /// <param name="expectedNames">Every resource that must appear. A missing entry is a failure.</param>
    /// <param name="defaults">
    /// Optional default value per resource name. When supplied, a resource claiming
    /// <c>Scoped: true</c> while reporting its default value is a failure — the shape a missed
    /// wiring change takes when the reporter derives its flag independently of its name.
    /// </param>
    public static InstanceScopeResult Verify(
        IReadOnlyList<InstanceResource> reported,
        IReadOnlyList<string> expectedNames,
        IReadOnlyDictionary<string, string>? defaults = null)
    {
        var failures = new List<string>();
        var byName = reported.ToDictionary(r => r.Name, StringComparer.Ordinal);

        foreach (var name in expectedNames)
        {
            if (!byName.TryGetValue(name, out var resource))
            {
                failures.Add(
                    $"{name} was not reported. A resource that exists but is not collected cannot be "
                    + "verified, so this is treated as unscoped.");
                continue;
            }

            if (!resource.Scoped)
            {
                failures.Add($"{name} is unscoped (effective value '{resource.EffectiveValue}').");
                continue;
            }

            if (defaults != null
                && defaults.TryGetValue(name, out var defaultValue)
                && string.Equals(resource.EffectiveValue, defaultValue, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{name} claims to be scoped but reports its default value '{defaultValue}'. "
                    + "Its owner probably accepts an injectable name and was never passed one.");
            }
        }

        return new InstanceScopeResult(failures.Count == 0, failures);
    }
}
```

- [ ] **Step 5: Write `InstanceManifest`**

Create `src/EyeCtrl.App/Startup/InstanceManifest.cs`:

```csharp
namespace EyeCtrl.App.Startup;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// What a scoped instance recorded about itself, written to the profile directory before anything
/// can act on input so a harness can read it and abort while nothing is on screen.
/// <para>
/// Never written when there is no instance id: a default launch must leave no new artefact.
/// </para>
/// </summary>
public sealed record InstanceManifest(
    string InstanceId,
    int ProcessId,
    DateTime StartedAtUtc,
    bool TestMode,
    IReadOnlyList<InstanceResource> Resources,
    IReadOnlyList<string> Refused)
{
    /// <summary>Manifest file name inside the profile directory.</summary>
    public const string FileName = "instance.json";

    /// <summary>camelCase, matching every other JSON emitter in this codebase.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Writes the manifest. Creates the directory tree if needed and lets any other failure
    /// propagate: a harness that cannot verify scoping must not go on to drive real input, so this
    /// is fatal by design rather than best-effort.
    /// </summary>
    public static void Write(string profileDirectory, InstanceManifest manifest)
    {
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(
            Path.Combine(profileDirectory, FileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }
}
```

- [ ] **Step 6: Run to verify they pass**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~InstanceScopeVerifierTests|FullyQualifiedName~InstanceManifestTests"
```

Expected: `Fehler: 0, erfolgreich: 11` (five verifier facts plus six manifest facts).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Startup tests/EyeCtrl.App.Tests/Startup
git commit -m "feat(app): add phase-2 scope verification and the instance manifest"
```

---

## Task 8: Wire it into startup

The reordering. `OnStartup` currently acquires the singleton mutex at `:94-95` and does not initialise Serilog until `:111` — which the code itself flags at `:101-103` as why the rejection is "deliberately unlogged". Phase 1 must explain itself and the mutex's *name* depends on the id, so resolution has to come first.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:71-111,150-157,232-265,500-512`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:51,71-72,236-242`
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs:54`
- Modify: `src/EyeCtrl.ViewModels/CalibrationViewModel.cs:63-65`

- [ ] **Step 1: Read the current startup ordering end to end**

```bash
sed -n '71,135p' src/EyeCtrl.App/App.xaml.cs
sed -n '495,515p' src/EyeCtrl.App/App.xaml.cs
```

Note three constraints that must survive: `RestartHandoff.SignalReady()` precedes the mutex (`:79-85` documents that reversing it deadlocks); `ThemeStartup.Apply` precedes any window construction (`:119-122`); and the splash is shown at `:125-129` only when not in test mode — so a scoped instance, which requires `--test-mode`, never shows one.

- [ ] **Step 2: Split settings-and-logging in two**

Replace `InitializeSettingsAndLogging()` with two methods. The first resolves the instance and starts logging; the second loads settings. Both are private to `App`.

```csharp
    /// <summary>
    /// Resolves the instance, derives the profile directory (stored in <see cref="_appDataDir"/>),
    /// and initialises Serilog. Split out of settings loading so phase 1 can log its refusals and
    /// so a rejected instance no longer loads settings — which matters because
    /// <c>SettingsService.Load</c> can write a migrated file back.
    /// </summary>
    private void InitializeInstanceAndLogging(string? instanceId)
    {
        _instanceId = instanceId;
        _appDataDir = AppPaths.Resolve(
            Environment.GetEnvironmentVariable(AppPaths.OverrideEnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            instanceId);

        LoggingInitializer.Initialize(Path.Combine(_appDataDir, "logs"));

        // Move ALL THREE global handlers across, not just the AppDomain one. The original method
        // also registered DispatcherUnhandledException (App.xaml.cs:524-527) and
        // TaskScheduler.UnobservedTaskException (:533-537); the latter exists specifically to keep a
        // faulted heartbeat writer from tearing down the process. Dropping either compiles cleanly
        // and regresses silently, which is why they are spelled out here rather than left to
        // "the rest of the old method".
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
                args.IsTerminating);
            Log.CloseAndFlush();
        };

        // Copy the existing DispatcherUnhandledException and TaskScheduler.UnobservedTaskException
        // registrations verbatim from the old InitializeSettingsAndLogging body. Read them first:
        //   sed -n '520,540p' src/EyeCtrl.App/App.xaml.cs
    }

    /// <summary>Loads settings from the resolved profile, migrating newly-introduced defaults.</summary>
    private (EyeCtrlSettings Settings, string SettingsFilePath) LoadSettings()
    {
        var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
        var tempSettingsService = new SettingsService(
            settingsFilePath, SettingsWriter.App, logger: null,
            writerMutexName: InstanceScope.Scope(SettingsService.DefaultWriterMutexName, _instanceId));
        var settings = tempSettingsService.Load();

        settings.ActionMenu.EnsureDefaults();

        // Supervision is FORCED off on a scoped instance rather than refused, because the refusal
        // could not be ordered: the flag lives only in settings, which load here — after phase 1.
        // Forcing deletes the ordering question. MergeForWriter keeps this in-memory flip off disk
        // when the file already has a supervision section, so the live profile is unaffected either
        // way; the scoped profile is ours to write.
        if (_instanceId != null && settings.Supervision.Enabled)
        {
            Log.Warning(
                "Scoped instance {InstanceId}: forcing Supervision.Enabled off. A scoped instance "
                + "runs alone and refuses supervision.", _instanceId);
            settings.Supervision.Enabled = false;
        }

        return (settings, settingsFilePath);
    }
```

Add the field beside `_appDataDir`:

```csharp
    private string? _instanceId;
```

Add the usings `EyeCtrl.Contracts` and `EyeCtrl.Core.Services` if absent.

- [ ] **Step 3: Rewrite the top of `OnStartup`**

Replace `App.xaml.cs:71-106` with:

```csharp
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        IsPausedLaunch = e.Args.Contains("--paused", StringComparer.OrdinalIgnoreCase);
        var isRestartHandoff = e.Args.Contains(RestartHandoff.Argument, StringComparer.OrdinalIgnoreCase);

        // ── Phase 1: may this instance run at all? ──
        // Before the mutex, because the mutex's NAME depends on the id. Before the handoff signal,
        // so a replacement that is going to be refused never tells its parent it is ready.
        var decision = InstanceStartupPolicy.Evaluate(
            Environment.GetEnvironmentVariable(InstanceScope.EnvVar), IsTestMode, isRestartHandoff);

        if (decision.Outcome == InstanceStartupOutcome.Refuse && !decision.CanLog)
        {
            // A malformed id yields no legal instances\<id> directory, so there is no scoped log
            // path. Initialising Serilog anyway would open the LIVE %APPDATA%\EyeCtrl\logs — writing
            // to the profile this instance was supposed to stay out of, on the one path where the id
            // cannot be trusted. stderr instead, and out.
            Console.Error.WriteLine(decision.Reason);
            Shutdown(InstanceStartupPolicy.RefusalExitCode);
            return;
        }

        InitializeInstanceAndLogging(decision.InstanceId);

        if (decision.Outcome == InstanceStartupOutcome.Refuse)
        {
            Log.Fatal("Refusing to start: {Reason}", decision.Reason);
            Log.CloseAndFlush();
            Shutdown(InstanceStartupPolicy.RefusalExitCode);
            return;
        }

        // A restart replacement announces itself BEFORE queueing for the mutex: the outgoing
        // instance is waiting for exactly this before it exits, and it still holds the mutex while
        // it waits. Reversing the order deadlocks. Unreachable while scoped — phase 1 refuses
        // --restart-handoff — so it is never reached by an instance that might still fail validation.
        if (isRestartHandoff)
        {
            RestartHandoff.SignalReady();
        }

        // Single-instance guard, now under its scoped name. Local\ (not Global\): multi-user/RDP is
        // a non-goal. A restart replacement is the one case that must WAIT rather than fail fast.
        _singleInstance = new SingleInstanceGuard(
            InstanceScope.Scope(SingleInstanceGuard.DefaultMutexName, decision.InstanceId));
        if (!_singleInstance.TryAcquire(isRestartHandoff ? RestartHandoff.MutexWait : TimeSpan.Zero))
        {
            // Now loggable, because logging is initialised above — closing the wart the old comment
            // here described.
            Log.Information("Another instance already holds {MutexName}; exiting.",
                _singleInstance.EffectiveName);
            Log.CloseAndFlush();
            Shutdown();
            return;
        }
```

Then, immediately after, replace the old `InitializeSettingsAndLogging()` call inside the `try` with:

```csharp
            var (settings, settingsFilePath) = LoadSettings();
```

- [ ] **Step 4: Pass scoped names into DI**

`AppBootstrapper.BuildServiceProvider` needs the profile directory and the instance id rather than deriving its own. Change its signature to accept them and replace `AppBootstrapper.cs:51`:

```csharp
    // Was: Path.Combine(Environment.GetFolderPath(ApplicationData), "EyeCtrl")
    // The composition root resolves this once; deriving it again here is how three call sites
    // drifted apart in the first place.
    var appDataDir = profileDirectory;
```

**The DI `SettingsService` singleton must get the scoped mutex too.** This is the defect the
plan's own review found, and it is the spec's canonical failure — a receiver that accepts a name
and is never passed one — committed on a *different* receiver than the one Task 3 fixed. Phase 2
reads `_serviceProvider.GetRequiredService<SettingsService>()`, not the temp loader from
`LoadSettings`, so leaving this alone makes **every scoped launch exit 78**. Worse, `OnExit`
(`App.xaml.cs:395-402`) saves through this singleton first, on the **production** writer mutex,
against the live instance.

Replace `AppBootstrapper.cs:62-65`:

```csharp
        services.AddSingleton(sp => new SettingsService(
            settingsFilePath,
            SettingsWriter.App,
            sp.GetRequiredService<ILogger<SettingsService>>(),
            writerMutexName: InstanceScope.Scope(SettingsService.DefaultWriterMutexName, instanceId)));
```

Replace the registrar registration at `:71-72`:

```csharp
        services.AddSingleton<IStartupRegistrar>(_ => new WindowsStartupRegistrar(
            InstanceScope.Scope(WindowsStartupRegistrar.DefaultValueName, instanceId)));
```

Replace the `ExternalDwellService` registration at `:236-242` — adding the `pipeName` it already accepts and has never been passed:

```csharp
        services.AddSingleton(sp => new ExternalDwellService(
            sp.GetRequiredService<EyeCtrlSettings>().ExternalDwell,
            sp.GetRequiredService<GazeEngine>(),
            dispatcher,
            sp.GetRequiredService<ILogger<ExternalDwellService>>(),
            sp.GetRequiredService<IMessenger>(),
            sink: sp.GetRequiredService<IStruggleTelemetrySink>(),
            pipeName: InstanceScope.Scope(ExternalDwellService.DefaultPipeName, instanceId)));
```

The new signature, and every caller:

```csharp
    public static ServiceProvider BuildServiceProvider(
        EyeCtrlSettings settings,
        string settingsFilePath,
        Dispatcher dispatcher,
        bool isTestMode,
        string profileDirectory,
        string? instanceId)
```

Callers to update — the second is a test the plan would otherwise leave to "fix call sites":

- `src/EyeCtrl.App/App.xaml.cs:134` — pass `_appDataDir` and `_instanceId`.
- `tests/EyeCtrl.App.Tests/Services/TelemetrySinkRegistrationTests.cs:32-36` — pass a temp
  directory and `null`.

Also pass `isScoped: _instanceId != null` when constructing `AppRestartCoordinator`
(`App.xaml.cs:271`) and `SelfRestartStrategy.CreateDefault(isScoped: _instanceId != null)` at
`:275`.

Two usings are needed in `App.xaml.cs` and are not currently present: `EyeCtrl.Contracts` (for
`InstanceScope`) and `EyeCtrl.Platform.Services` (for the concrete `WindowsStartupRegistrar` cast in
phase 2). The csproj already references both assemblies.

- [ ] **Step 5: Remove the two remaining independent resolvers**

`src/EyeCtrl.App/Services/Coordinators/KeyboardCoordinator.cs:54` derives its own path inside
`Initialize`. Add a constructor parameter and **delete the derivation** — leaving `Initialize:54` in
place would overwrite whatever was injected with the live `%APPDATA%\EyeCtrl`.

The hop this needs, which an earlier draft left unstated: the only production construction is
`EventCoordinator.cs:110` (`new KeyboardCoordinator(_modal, _quickActions, _toolbar)`), and
`EventCoordinator` has no profile directory to pass — nor does `EventCoordinationContext`
(`IEventCoordinator.cs:51-86`). So the path has to reach it. Add a `string AppDataDir` member to
`EventCoordinationContext`, populate it in `AppBootstrapper` from `profileDirectory`, and pass
`_context.AppDataDir` at the construction site.

Test call sites this breaks, all of which construct the coordinator directly:

- `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs:60-62,87`
- `tests/EyeCtrl.App.Tests/Services/Coordinators/SettingsUiCoordinatorTests.cs:21,55`

Give each a temp directory. Making the parameter required rather than optional is deliberate: an
optional one would silently keep the live profile on any path that forgot to pass it.

`src/EyeCtrl.ViewModels/CalibrationViewModel.cs:63-65` has a self-derived fallback. The App's DI already injects a resolved calibration path (`AppBootstrapper.cs:203`), so delete the fallback and make the path a required constructor argument.

Verify only one derivation remains in the app:

```bash
grep -rn "SpecialFolder.ApplicationData" src/ --include=*.cs
```

Expected: `AppPaths.cs`, plus `Supervisor/Program.cs:116` (a separate process, out of scope — a scoped instance never launches one).

- [ ] **Step 6: Add phase 2 and the manifest**

In `App.xaml.cs`, after the DI container is built and services wired but **before** overlays are shown and before `gazeEngine.Start()`:

```csharp
            // ── Phase 2: did every resource actually take its scoped name? ──
            // Read back off the CONSTRUCTED objects. This is what catches a receiver that accepts an
            // injectable name and is never passed one. Runs before any overlay is shown and before
            // the gaze engine starts, i.e. before anything can act on input. (A window OBJECT
            // already exists by now — ExternalDwellService's constructor builds a
            // DwellFeedbackOverlay — but nothing is shown and no input is processed.)
            if (_instanceId != null)
            {
                var dwell = _serviceProvider.GetRequiredService<ExternalDwellService>();
                var registrar = (WindowsStartupRegistrar)_serviceProvider.GetRequiredService<IStartupRegistrar>();
                var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

                var reported = new List<InstanceResource>
                {
                    new("ProfileDirectory",    _appDataDir != AppPaths.Resolve(null, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), null), _appDataDir),
                    new("AppSingletonMutex",   _singleInstance!.EffectiveName != SingleInstanceGuard.DefaultMutexName, _singleInstance.EffectiveName),
                    new("SettingsWriterMutex", settingsService.EffectiveWriterMutexName != SettingsService.DefaultWriterMutexName, settingsService.EffectiveWriterMutexName),
                    new("DwellPipe",           dwell.EffectivePipeName != ExternalDwellService.DefaultPipeName, dwell.EffectivePipeName),
                    new("StartupRunValue",     registrar.EffectiveValueName != WindowsStartupRegistrar.DefaultValueName, registrar.EffectiveValueName)
                };

                var expected = new[]
                {
                    "ProfileDirectory", "AppSingletonMutex", "SettingsWriterMutex", "DwellPipe", "StartupRunValue"
                };
                var defaults = new Dictionary<string, string>
                {
                    ["AppSingletonMutex"]   = SingleInstanceGuard.DefaultMutexName,
                    ["SettingsWriterMutex"] = SettingsService.DefaultWriterMutexName,
                    ["DwellPipe"]           = ExternalDwellService.DefaultPipeName,
                    ["StartupRunValue"]     = WindowsStartupRegistrar.DefaultValueName
                };

                var verdict = InstanceScopeVerifier.Verify(reported, expected, defaults);
                if (!verdict.IsSatisfied)
                {
                    foreach (var failure in verdict.Failures)
                        Log.Fatal("Instance scoping failed: {Failure}", failure);
                    Log.CloseAndFlush();
                    Shutdown(InstanceStartupPolicy.RefusalExitCode);
                    return;
                }

                // Explicit patterns, not `?.X == true`: a null publisher makes that expression false,
                // which would report a subsystem as refused when it was merely never built.
                var refusedFailures = new List<string>();
                if (_heartbeatPublisher is null)
                    refusedFailures.Add("HeartbeatPublisher was never constructed, so it cannot be verified.");
                else if (_heartbeatPublisher.IsPublishing)
                    refusedFailures.Add("HeartbeatPublisher is publishing on a scoped instance.");

                if (_restartCoordinator is null)
                    refusedFailures.Add("AppRestartCoordinator was never constructed, so it cannot be verified.");
                else if (!_restartCoordinator.IsScopeGated)
                    refusedFailures.Add("AppRestartCoordinator is not scope-gated.");

                if (refusedFailures.Count > 0)
                {
                    foreach (var failure in refusedFailures)
                        Log.Fatal("Instance scoping failed: {Failure}", failure);
                    Log.CloseAndFlush();
                    Shutdown(InstanceStartupPolicy.RefusalExitCode);
                    return;
                }

                // The refused list is DERIVED from what was verified above, never a literal — a
                // hard-coded ["Supervision", "SelfRestart"] would report both as refused even if the
                // coordinator had never been constructed or never been passed isScoped.
                var refused = new List<string> { "Supervision", "SelfRestart" };

                InstanceManifest.Write(_appDataDir, new InstanceManifest(
                    _instanceId, Environment.ProcessId, DateTime.UtcNow, IsTestMode,
                    reported, refused));
            }
```

**The ordering requires moving two constructions, not moving this block.** Verified in the tree:
`ShowOverlays` is at `App.xaml.cs:240`, `gazeEngine.Start()` at `:245`, `_heartbeatPublisher` at
`:260` and `_restartCoordinator` at `:271`. Both refused-subsystem objects are therefore built
*after* input processing begins, so there is no position that is both below them and above the
show/start — an earlier draft of this plan hedged exactly that and the hedge was unsatisfiable.

Move the `_heartbeatPublisher` and `_restartCoordinator` constructions (`:246-300`, everything
from the publisher through the coordinator wiring) to **before** `ShowOverlays` at `:240`, then
place this phase-2 block immediately after them and before `ShowOverlays`. Check as you move:

- the publisher subscribes `gazeEngine.GazeSampled` (`:261`), which is fine before `Start()`;
- its `DispatcherTimer` (`:262-265`) may start before the gaze engine — a stamp with no samples
  yet is harmless, `_lastFrameTicks` is seeded at construction;
- the coordinator's constructor registers a messenger handler (`AppRestartCoordinator.cs:105`) and
  touches nothing that needs overlays.

Note also why the null-conditional check below is written as an explicit `is` pattern rather than
`_heartbeatPublisher?.IsPublishing == true`: on a null publisher that expression is `false`, so it
would pass vacuously and report success for a subsystem that was never constructed.

- [ ] **Step 7: Build and run the full headless suite**

```bash
dotnet build EyeCtrl.slnx
dotnet test --filter "Category!=UI"
```

Expected: `Fehler: 0` everywhere. This is the task most likely to break existing tests — `AppBootstrapper`'s signature changed and two constructors gained required arguments. Fix call sites rather than reverting the signature.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App src/EyeCtrl.ViewModels
git commit -m "feat(app): scope startup, force supervision off, verify and record the instance"
```

---

## Task 9: The SDK latch

`DwellClient` hard-codes `private const string PipeName = DwellPipe.Name;` (`:17`), so under scoping the server binds `eyectrl_dwell_<id>` and a consumer still dials `eyectrl_dwell`. The SDK does **not** read the environment: a host inheriting a valid-but-stale id would silently target another instance's pipe, and `DwellClient` swallows all connection failures (`:96`, `:151`).

**Files:**
- Create: `src/EyeCtrl.Sdk.Abstractions/DwellHead.cs`
- Modify: `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs:17,80`
- Modify: `src/EyeCtrl.Sdk.Abstractions/DwellHeadCore.cs:9-16`
- Test: `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellHeadTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellHeadTests.cs`:

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

/// <summary>
/// DwellHead is process-wide static state, so these tests reset it between cases via the
/// test-only hook and must not run in parallel with each other. They are in one class, which xUnit
/// serialises by default.
/// </summary>
public class DwellHeadTests : IDisposable
{
    public DwellHeadTests() => DwellHead.ResetForTests();
    public void Dispose() => DwellHead.ResetForTests();

    [Fact]
    public void DefaultsToTheProductionPipeName()
    {
        Check.That(DwellHead.PipeName).IsEqualTo(DwellPipe.Name);
        Check.That(DwellHead.IsLatched).IsFalse();
    }

    [Fact]
    public void AcceptsASetBeforeTheLatch()
    {
        DwellHead.PipeName = "eyectrl_dwell_run7";

        Check.That(DwellHead.PipeName).IsEqualTo("eyectrl_dwell_run7");
    }

    [Fact]
    public void LatchReturnsTheCurrentValue_AndMarksItLatched()
    {
        DwellHead.PipeName = "eyectrl_dwell_run7";

        Check.That(DwellHead.Latch()).IsEqualTo("eyectrl_dwell_run7");
        Check.That(DwellHead.IsLatched).IsTrue();
    }

    [Fact]
    public void SecondLatchReturnsTheSameValue_SoWpfAndWinUiCannotDisagree()
    {
        DwellHead.PipeName = "eyectrl_dwell_run7";
        var first = DwellHead.Latch();
        var second = DwellHead.Latch();

        Check.That(second).IsEqualTo(first);
    }

    [Fact]
    public void SetAfterTheLatch_Throws_RatherThanBeingSilentlyIgnored()
    {
        // The failure this replaces: "set before first use, ignored afterwards" made a late set
        // indistinguishable from success, and Dwell.Enabled="True" in XAML latches during
        // InitializeComponent — early enough that almost any startup hook is too late.
        DwellHead.Latch();

        InvalidOperationException? ex = null;
        try { DwellHead.PipeName = "eyectrl_dwell_toolate"; }
        catch (InvalidOperationException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.Message).Contains("latched");
    }

    [Fact]
    public void SettingTheSameValueAfterTheLatch_IsAllowed()
    {
        // Idempotent sets are not a bug and throwing on them would punish a host that configures
        // defensively in two places.
        DwellHead.PipeName = "eyectrl_dwell_run7";
        DwellHead.Latch();

        DwellHead.PipeName = "eyectrl_dwell_run7";   // must not throw

        Check.That(DwellHead.PipeName).IsEqualTo("eyectrl_dwell_run7");
    }

    [Fact]
    public void RejectsABlankName()
    {
        ArgumentException? ex = null;
        try { DwellHead.PipeName = "  "; } catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests --filter "FullyQualifiedName~DwellHeadTests"
```

Expected: **compile error** — `DwellHead` does not exist.

- [ ] **Step 3: Write `DwellHead`**

Create `src/EyeCtrl.Sdk.Abstractions/DwellHead.cs`:

```csharp
namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

/// <summary>
/// Process-wide configuration for the dwell client.
/// <para>
/// The SDK deliberately does <b>not</b> read <c>EYECTRL_INSTANCE_ID</c>. A host that inherited a
/// valid-but-stale id would silently target another instance's pipe, and
/// <see cref="DwellClient"/> swallows every connection failure — so the host names the pipe
/// explicitly or gets the production default.
/// </para>
/// <para>
/// <b>Latching:</b> the value is captured at the first <c>DwellHeadCore.EnsureStarted</c>. A later
/// set throws rather than being ignored, because "set before first use" is a contract a host cannot
/// reliably satisfy: <c>Dwell.Enabled="True"</c> in XAML latches during
/// <c>InitializeComponent</c>. The latch flag is static rather than per-client so a process using
/// both the WPF and WinUI heads — which own separate clients — cannot bind two different names.
/// </para>
/// </summary>
public static class DwellHead
{
    private static readonly object Gate = new();
    private static string _pipeName = DwellPipe.Name;
    private static bool _latched;

    /// <summary>
    /// Pipe this process's dwell client connects to. Defaults to <see cref="DwellPipe.Name"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// The name has already been latched by a started client and the new value differs.
    /// </exception>
    public static string PipeName
    {
        get { lock (Gate) return _pipeName; }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Pipe name must be non-blank.", nameof(value));

            lock (Gate)
            {
                if (_latched && !string.Equals(_pipeName, value, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The dwell pipe name is already latched to '{_pipeName}'; a dwell client has "
                        + "started. Set DwellHead.PipeName before the first dwell-enabled control is "
                        + "realized — note that Dwell.Enabled=\"True\" in XAML latches during "
                        + "InitializeComponent.");

                _pipeName = value;
            }
        }
    }

    /// <summary>Whether a client has started and fixed the name.</summary>
    public static bool IsLatched { get { lock (Gate) return _latched; } }

    /// <summary>
    /// Captures the name for a starting client. Idempotent: a second head latching returns the same
    /// value the first one got.
    /// </summary>
    public static string Latch()
    {
        lock (Gate)
        {
            _latched = true;
            return _pipeName;
        }
    }

    /// <summary>Test-only reset. Not part of the supported surface.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _pipeName = DwellPipe.Name; _latched = false; }
    }
}
```

Add to `src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj` so the test project can call `ResetForTests`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="EyeCtrl.Sdk.Abstractions.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Make `DwellClient` take the name**

**`DwellClient` has no explicit constructor** — only the implicit parameterless one (verified). So
this *adds* a constructor rather than extending one, and the parameter must be optional: a required
`string pipeName` would break `DwellClientTests` at `:10` and `:23`, which construct it bare.

In `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs`, replace the const at `:17` with a field and add a
constructor:

```csharp
    // Latched once by DwellHeadCore and read from this field thereafter. NOT read from
    // DwellHead.PipeName inside the reconnect loop: doing that would make a late set apply on the
    // next reconnect, contradicting the latch contract.
    private readonly string _pipeName;
```

```csharp
    /// <summary>
    /// Creates a client for a specific pipe.
    /// </summary>
    /// <param name="pipeName">
    /// Pipe to connect to. Defaults to <see cref="DwellPipe.Name"/>; <c>DwellHeadCore</c> passes the
    /// latched <see cref="DwellHead.PipeName"/>. Optional because <c>DwellClientTests</c> constructs
    /// this type bare, and because a shipped assembly should not break consumers on a name change.
    /// </param>
    public DwellClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? DwellPipe.Name;
    }
```

Then replace the use at `:80` with `_pipeName`.

- [ ] **Step 5: Move `Client` construction into `EnsureStarted`**

`src/EyeCtrl.Sdk.Abstractions/DwellHeadCore.cs:9` is a field initialiser, so the client exists before
any name could be passed.

**The whole file is 19 lines and all of it matters** — `_idPrefix`, `_idCounter`, the
`DwellHeadCore(string idPrefix)` constructor and `NextId()` must survive. A paste-over of `:9-16`
deletes the constructor and `NextId`, which every WPF/WinUI target id depends on. Replace only the
`Client` member and `EnsureStarted`, leaving the rest intact:

```csharp
    private DwellClient? _client;
    private int _started;

    /// <summary>
    /// The client. Constructed on first <see cref="EnsureStarted"/> so the latched pipe name can be
    /// passed to it — a field initialiser ran too early for that.
    /// </summary>
    /// <exception cref="InvalidOperationException">Accessed before <see cref="EnsureStarted"/>.</exception>
    public DwellClient Client => _client
        ?? throw new InvalidOperationException(
            "DwellHeadCore.EnsureStarted has not run; there is no client yet.");

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _client = new DwellClient(DwellHead.Latch());
        _client.Start();
    }
```

Then find every `Client` access and confirm each happens after `EnsureStarted`:

```bash
grep -rn "\.Client" src/EyeCtrl.Sdk.Wpf src/EyeCtrl.Sdk.WinUI --include=*.cs
```

Expected: all uses in `Dwell.cs` follow the `EnsureStarted` call in the `Enabled` property-changed handler. If any precedes it, that call site needs `EnsureStarted()` first — do not paper over it by reverting to eager construction.

- [ ] **Step 6: Run to verify**

```bash
dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests
dotnet test tests/EyeCtrl.Sdk.Wpf.Tests
dotnet test tests/EyeCtrl.Sdk.WinUI.Tests
```

Expected: `Fehler: 0` for all three; **7** new tests in the first (seven `[Fact]` methods, no theories).

`Sdk.Abstractions.Tests` has no assembly-level `DisableTestParallelization`, and `DwellHead` is
process-wide static state — so `DwellHeadTests` can race any sibling class that starts a head. The
existing `DwellHeadCoreTests.EnsureStarted_IsIdempotent` latches without resetting. Add
`tests/EyeCtrl.Sdk.Abstractions.Tests/AssemblyInfo.cs`:

```csharp
using Xunit;

// DwellHead is process-wide static state (the latched pipe name). Distinct test classes run in
// parallel collections by default, so DwellHeadTests' ResetForTests would race any sibling class
// that starts a head — DwellHeadCoreTests.EnsureStarted_IsIdempotent latches without resetting.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Sdk.Abstractions tests/EyeCtrl.Sdk.Abstractions.Tests
git commit -m "feat(sdk): let the host name the dwell pipe, latched at first use"
```

---

## Task 10: Stop the supervisor relaunching a scoped child

`ProcessController.Launch` sets no environment (`:55-57`) and `UseShellExecute = false` inherits it, so a supervisor whose own environment carried an instance id would relaunch a scoped, non-test-mode child that exits 78 repeatedly, booking crash strikes toward `FallenBack` — which launches a fallback assistive technology on the user's desktop.

**Files:**
- Modify: `src/EyeCtrl.Supervisor/Supervision/ProcessController.cs:48-59`
- Test: `tests/EyeCtrl.Supervisor.Tests/Supervision/ProcessControllerEnvironmentTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Supervisor.Tests/Supervision/ProcessControllerEnvironmentTests.cs`:

```csharp
namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Diagnostics;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// Uses the LITERAL variable name rather than <c>InstanceScope.EnvVar</c>, matching the production
/// code: the Supervisor may not reference Contracts without breaking the layering invariant, and a
/// test that reached across for the constant would hide that constraint. The two are tied together
/// by a separate assertion in a project that can see both.
/// </summary>
public class ProcessControllerEnvironmentTests
{
    private const string EnvVar = "EYECTRL_INSTANCE_ID";

    [Fact]
    public void StripInstanceScope_RemovesTheInstanceId_SoAChildIsNeverScoped()
    {
        // A scoped, non-test-mode child refuses with exit 78, which is indistinguishable from a
        // crash to the supervisor — so it books strikes and escalates to launching a fallback AT.
        var psi = new ProcessStartInfo("EyeCtrl.App.exe") { UseShellExecute = false };
        psi.Environment[EnvVar] = "run7";

        ProcessController.StripInstanceScope(psi);

        Check.That(psi.Environment.ContainsKey(EnvVar)).IsFalse();
    }

    [Fact]
    public void StripInstanceScope_IsANoOp_WhenNoInstanceIdIsPresent()
    {
        var psi = new ProcessStartInfo("EyeCtrl.App.exe") { UseShellExecute = false };
        var before = psi.Environment.Count;

        ProcessController.StripInstanceScope(psi);

        Check.That(psi.Environment.Count).IsEqualTo(before);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~ProcessControllerEnvironmentTests"
```

Expected: **compile error** — `StripInstanceScope` does not exist.

**Do not add a `Supervisor -> Contracts` reference to make the test's `InstanceScope.EnvVar`
compile.** `tests/EyeCtrl.Architecture.Tests/LayeringTests.cs` pins the Supervisor's dependency
graph to Core + Platform + Controls, in both the csproj map (`:98`) and an IL usage test
(`:167-176`). That project **is** in `EyeCtrl.slnx` and runs under `Category!=UI` — verified — so the
reference would go green under this task's own filter and red in CI.

Instead the Supervisor hard-codes the variable name, and a test in a project that can see *both*
assemblies asserts the two agree. Use the literal in the test above as well:

```csharp
        psi.Environment["EYECTRL_INSTANCE_ID"] = "run7";
```

and add this to `tests/EyeCtrl.App.Tests/Startup/InstanceStartupPolicyTests.cs`, which already
references Contracts:

```csharp
    [Fact]
    public void SupervisorsHardCodedEnvVarNameMatchesTheContract()
    {
        // EyeCtrl.Supervisor cannot reference Contracts without breaking LayeringTests, so it spells
        // the variable name literally. This is the guard against those two drifting apart.
        Check.That(EyeCtrl.Supervisor.Supervision.ProcessController.InstanceScopeEnvVarName)
             .IsEqualTo(InstanceScope.EnvVar);
    }
```

That requires `EyeCtrl.App.Tests` to reference `EyeCtrl.Supervisor`. Check whether it already does;
if adding it would itself trip a layering invariant, put the test in
`EyeCtrl.Architecture.Tests` instead — that project exists to know about every assembly.

- [ ] **Step 3: Implement**

In `src/EyeCtrl.Supervisor/Supervision/ProcessController.cs`:

```csharp
    /// <summary>
    /// Removes <see cref="InstanceScope.EnvVar"/> from a child's environment.
    /// <para>
    /// The supervisor's own environment is inherited by the app it launches. If it carried an
    /// instance id — a stray <c>setx</c>, a shell that set it — the child would be scoped, would
    /// lack <c>--test-mode</c>, and would refuse with exit 78. That is indistinguishable from a
    /// crash here, so the supervisor would book strikes and escalate toward launching a fallback
    /// assistive technology on the user's desktop.
    /// </para>
    /// </summary>
    /// <summary>
    /// Spelled literally rather than read from <c>EyeCtrl.Contracts.InstanceScope.EnvVar</c>: this
    /// project may not reference Contracts without breaking the layering invariant in
    /// <c>EyeCtrl.Architecture.Tests</c>. A test that can see both assemblies asserts they agree.
    /// </summary>
    internal const string InstanceScopeEnvVarName = "EYECTRL_INSTANCE_ID";

    internal static void StripInstanceScope(ProcessStartInfo psi)
    {
        // Touching psi.Environment materialises the inherited block, which is what makes the
        // removal effective; UseShellExecute must stay false for it to be honoured at all.
        psi.Environment.Remove(InstanceScopeEnvVarName);
    }
```

Call it inside `Launch` where the `ProcessStartInfo` is built (`:48-59`), before starting the
process. `EyeCtrl.Supervisor` already exposes internals to its test project via a csproj
`InternalsVisibleTo` item (`:19`) — do **not** add an `[assembly:]` attribute; this repo uses the
MSBuild item form everywhere.

**Add a test that drives `Launch`, not only the helper.** The two tests above would stay green if
someone implemented `StripInstanceScope` and forgot to call it — which is the whole failure mode.
`ProcessController` already takes an injectable launcher delegate (`:31-33`) for exactly this kind
of test; capture the `ProcessStartInfo` it would have started and assert the variable is absent.
Read `:31-59` and adapt the seam rather than guessing its shape.

If the seam cannot supply the variable directly and the test must set it process-wide, note that
`Supervisor.Tests` has no assembly-level parallelism opt-out — so prefer the seam, or add the
opt-out.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Supervisor.Tests
```

Expected: `Fehler: 0`; 2 new tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Supervisor tests/EyeCtrl.Supervisor.Tests
git commit -m "fix(supervisor): strip EYECTRL_INSTANCE_ID from relaunched children"
```

---

## Task 11: The drift guard

This document's enumeration was wrong four times out of four, once on a row it had already written the correct fix for elsewhere. Hand enumeration is the method being replaced. The guard scans syntax nodes rather than text, because regex over source would match the pipe commentary in `ExternalDwellService.cs:20-35` and the tray commentary in `SupervisorTrayIcon.cs:12-19` while missing anything reached through a wrapper.

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Startup/InstanceScopeDriftTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`

- [ ] **Step 1: Add the Roslyn reference**

The repo has never referenced Roslyn; this is the acknowledged cost of the only mechanism that sees through wrappers. In `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`:

```xml
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
```

- [ ] **Step 2: Write the guard**

Create `tests/EyeCtrl.App.Tests/Startup/InstanceScopeDriftTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Startup;

using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Fails the build when a new process-external side effect appears without being instance-scoped.
/// <para>
/// This exists because hand enumeration of this surface failed four times out of four while people
/// were actively looking — including once on a resource the same document had already written the
/// correct fix for. The guard is the mechanism being trusted instead of care.
/// </para>
/// <para>
/// Syntax nodes, not regex: a textual scan matches the extensive pipe commentary in
/// <c>ExternalDwellService</c> and the tray commentary in <c>SupervisorTrayIcon</c> while missing
/// anything reached through a wrapper.
/// </para>
/// </summary>
public class InstanceScopeDriftTests
{
    /// <summary>Construction and member-access spellings that create process-external state.</summary>
    private static readonly string[] WatchedTypes =
    [
        "Mutex", "EventWaitHandle", "Semaphore",
        "NamedPipeServerStream", "NamedPipeClientStream",
        "NotifyIcon", "MemoryMappedFile"
    ];

    private static readonly string[] WatchedMembers =
    [
        "Registry.CurrentUser", "Registry.LocalMachine", "Registry.ClassesRoot",
        "EventWaitHandle.OpenExisting", "Mutex.OpenExisting",
        "SpecialFolder.ApplicationData",
        // The supervisor's heartbeat server binds via the ACL factory, not `new
        // NamedPipeServerStream` (HeartbeatPipeServer.cs:89). Omitting this is why an earlier draft
        // of this guard could not see the very resource v1's enumeration missed.
        "NamedPipeServerStreamAcl.Create"
    ];

    /// <summary>
    /// Every known site, keyed by "<c>RelativePath::Symbol</c>", with the reason it is safe. Entries
    /// for refused resources point at the GATE, not merely at the call site — the distinction
    /// matters because an allow-list entry that just names a residual legitimises it.
    /// </summary>
    private static readonly Dictionary<string, string> AllowList = new()
    {
        [@"src\EyeCtrl.Core\Services\SingleInstanceGuard.cs::Mutex"] =
            "Scoped: name injected by the composition root, reported via EffectiveName.",
        [@"src\EyeCtrl.Core\Settings\SettingsService.cs::Mutex"] =
            "Scoped: writerMutexName injected, reported via EffectiveWriterMutexName.",
        [@"src\EyeCtrl.Core\Services\AppPaths.cs::SpecialFolder.ApplicationData"] =
            "The single profile resolver. Every other site delegates here.",
        [@"src\EyeCtrl.App\Services\ExternalDwellService.cs::NamedPipeServerStream"] =
            "Scoped: pipeName injected, reported via EffectivePipeName.",
        [@"src\EyeCtrl.App\Services\HeartbeatPublisher.cs::NamedPipeClientStream"] =
            "REFUSED, not scoped. Gate: supervision forced off => IsPublishing false, and "
            + "SignalShuttingDown guards its send. See the spec's 'heartbeat exit path'.",
        [@"src\EyeCtrl.App\Services\RestartHandoff.cs::EventWaitHandle.OpenExisting"] =
            "REFUSED, not scoped. Gate: phase 1 rejects --restart-handoff when scoped.",
        [@"src\EyeCtrl.App\Services\SelfRestartStrategy.cs::EventWaitHandle"] =
            "REFUSED, not scoped. Gate: Begin returns early when scoped.",
        [@"src\EyeCtrl.App\Services\TrayIconManager.cs::NotifyIcon"] =
            "Unnamed per-process shell icon: no name to scope, no collision. Cosmetic only.",
        [@"src\EyeCtrl.Platform\Services\WindowsStartupRegistrar.cs::Registry.CurrentUser"] =
            "Scoped: valueName injected, reported via EffectiveValueName.",
        // NOTE the two entries that deliberately do NOT appear here. The supervisor singleton is
        // `new SingleInstanceGuard(SupervisorMutexName)` (Program.cs:28), which this guard attributes
        // to SingleInstanceGuard.cs::Mutex — a `Program.cs::Mutex` key would never resolve and
        // EveryAllowListEntryStillResolves would fail. Same for the heartbeat server, which uses the
        // ACL factory rather than `new NamedPipeServerStream`; it is keyed below by that factory.
        [@"src\EyeCtrl.Supervisor\Ipc\HeartbeatPipeServer.cs::NamedPipeServerStreamAcl.Create"] =
            "Separate process, out of scope: no scoped instance launches a supervisor.",
        [@"src\EyeCtrl.Supervisor\Program.cs::SpecialFolder.ApplicationData"] =
            "Separate process, out of scope (same reason).",
        [@"src\EyeCtrl.Supervisor\UI\SupervisorTrayIcon.cs::NotifyIcon"] =
            "Separate process; unnamed per-process icon.",
        [@"src\EyeCtrl.Supervisor\Fallback\FallbackAtLauncher.cs::Registry.LocalMachine"] =
            "Read-only HKLM probe to discover installed assistive technology. Writes nothing.",
        [@"src\EyeCtrl.Sdk.Abstractions\DwellClient.cs::NamedPipeClientStream"] =
            "Named by the host via DwellHead.PipeName, latched at first use.",
    };

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not locate the repo root.");
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        // The spec says "the SDK", which is three assemblies, not one.
        foreach (var project in new[] { "EyeCtrl.App", "EyeCtrl.Core", "EyeCtrl.Platform",
                                        "EyeCtrl.ViewModels", "EyeCtrl.Supervisor",
                                        "EyeCtrl.Sdk.Abstractions", "EyeCtrl.Sdk.Wpf",
                                        "EyeCtrl.Sdk.WinUI" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Generated and build output are not hand-written side effects.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return file;
            }
        }
    }

    private static List<(string Key, string File, int Line)> FindSites(string root)
    {
        var found = new List<(string, string, int)>();

        foreach (var file in SourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text);
            var relative = Path.GetRelativePath(root, file);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                string? symbol = node switch
                {
                    // `new Mutex(...)`, `new NamedPipeServerStream(...)`
                    ObjectCreationExpressionSyntax oc
                        when WatchedTypes.Contains(TypeName(oc.Type)) => TypeName(oc.Type),

                    // `Registry.CurrentUser`, `EventWaitHandle.OpenExisting`, `SpecialFolder.ApplicationData`
                    MemberAccessExpressionSyntax ma
                        when WatchedMembers.Contains(Flatten(ma)) => Flatten(ma),

                    _ => null
                };

                if (symbol == null) continue;

                var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                found.Add(($"{relative}::{symbol}", relative, line));
            }
        }

        return found;
    }

    private static string TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => type.ToString()
    };

    private static string Flatten(MemberAccessExpressionSyntax ma)
    {
        var left = ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
            _ => ma.Expression.ToString()
        };
        return $"{left}.{ma.Name.Identifier.Text}";
    }

    [Fact]
    public void EveryProcessExternalSiteIsAllowListed()
    {
        var root = RepoRoot();
        var unexpected = FindSites(root)
            .Where(s => !AllowList.ContainsKey(s.Key))
            .Select(s => $"{s.File}:{s.Line} -> {s.Key}")
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // Joined into one string so a failure prints every offending site. NFluent's collection
        // message would say "has 3 items" without naming them, which is useless here.
        Check.That(string.Join(Environment.NewLine, unexpected)).IsEqualTo(string.Empty);
    }

    [Fact]
    public void EveryAllowListEntryStillResolves()
    {
        // A stale exemption must fail rather than silently widen the allowed surface.
        var root = RepoRoot();
        var actual = FindSites(root).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var stale = AllowList.Keys.Where(k => !actual.Contains(k)).OrderBy(x => x).ToList();

        Check.That(stale).IsEmpty();
    }

    [Fact]
    public void AppPathsIdRuleMatchesInstanceScope()
    {
        // AppPaths duplicates the id validation because Core cannot reference Contracts. If the two
        // drift, a path could accept an id a pipe name rejects.
        // Non-blank candidates only. Blank means "no id" to both Resolve and Scope, but TryParseId
        // answers a different question — "is this a valid id" — and returns false for it. Including
        // "" would compare two things that are deliberately not the same.
        foreach (var candidate in new[] { "ok", "ok-1", "ok_1", "bad id", "bad\\id", "bad/id", "bad.id" })
        {
            var contractsAccepts = EyeCtrl.Contracts.InstanceScope.TryParseId(candidate, out _);

            bool appPathsAccepts;
            try
            {
                EyeCtrl.Core.Services.AppPaths.Resolve(null, @"C:\r", candidate);
                appPathsAccepts = true;
            }
            catch (ArgumentException) { appPathsAccepts = false; }

            Check.That(appPathsAccepts).IsEqualTo(contractsAccepts);
        }
    }
}
```

- [ ] **Step 3: Run it and reconcile**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~InstanceScopeDriftTests"
```

Expected on the first run: `EveryProcessExternalSiteIsAllowListed` **fails** with a list of sites. For each one, decide deliberately — scope it, or add an allow-list entry saying why it is safe. Do not bulk-add entries to get green; that converts the guard into a rubber stamp.

`EveryAllowListEntryStillResolves` may also fail if a path in the list above is wrong for this tree. Correct the path; do not delete the entry.

- [ ] **Step 4: Verify the guard would have caught the historical misses**

Temporarily remove the `SelfRestartStrategy.cs::EventWaitHandle` entry and re-run. `EveryProcessExternalSiteIsAllowListed` must fail naming that file — this is the resource v2's regex-based guard would have missed. Restore the entry.

Do the same for `SettingsService.cs::Mutex`, the resource v1 missed.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.App.Tests
git commit -m "test(app): add a Roslyn drift guard for process-external side effects"
```

---

## Task 12: Integration test and documentation

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Tests/InstanceScopingIntegrationTests.cs`
- Modify: `AGENTS.md`
- Modify: `docs/superpowers/specs/2026-08-12-instance-scoping-design.md`

- [ ] **Step 1: Write the integration test**

Create `tests/EyeCtrl.App.Tests/Tests/InstanceScopingIntegrationTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Tests;

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EyeCtrl.App.Startup;
using EyeCtrl.Contracts;
using EyeCtrl.App.Tests.Fixtures;
using Microsoft.Win32;

/// <summary>
/// Launches the real app scoped and asserts the manifest. Category=UI because it starts a process
/// and shows windows; it does not move the cursor.
/// </summary>
[Trait("Category", "UI")]
public class InstanceScopingIntegrationTests
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public async Task ScopedLaunch_ReportsEveryResourceScoped_AndLeavesTheLiveRunValueUntouched()
    {
        AppFixture.SkipIfDisabled();

        var instanceId = "it" + Guid.NewGuid().ToString("N")[..10];
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "instances", instanceId);

        // The regression this whole design exists for: a launch used to delete the live autostart
        // entry via SetStartOnBoot. Snapshot it and compare byte for byte afterwards.
        object? liveRunValueBefore;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            liveRunValueBefore = key?.GetValue("EyeCtrl");

        var exe = FindAppExe();
        var psi = new ProcessStartInfo(exe) { Arguments = "--test-mode", UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = instanceId;

        using var proc = Process.Start(psi)!;
        try
        {
            var manifestPath = Path.Combine(profile, InstanceManifest.FileName);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(manifestPath) && DateTime.UtcNow < deadline && !proc.HasExited)
                await Task.Delay(200);

            Check.That(proc.HasExited).IsFalse();
            Check.That(File.Exists(manifestPath)).IsTrue();

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var root = doc.RootElement;

            Check.That(root.GetProperty("instanceId").GetString()).IsEqualTo(instanceId);
            Check.That(root.GetProperty("testMode").GetBoolean()).IsTrue();

            var unscoped = root.GetProperty("resources").EnumerateArray()
                .Where(r => !r.GetProperty("scoped").GetBoolean())
                .Select(r => r.GetProperty("name").GetString()!)
                .ToArray();
            Check.That(unscoped).IsEmpty();

            var refused = root.GetProperty("refused").EnumerateArray()
                .Select(r => r.GetString()!).ToArray();
            Check.That(refused).Contains("Supervision");
            Check.That(refused).Contains("SelfRestart");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(5_000);
            try { Directory.Delete(profile, recursive: true); } catch { }
        }

        object? liveRunValueAfter;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            liveRunValueAfter = key?.GetValue("EyeCtrl");

        Check.That(liveRunValueAfter?.ToString()).IsEqualTo(liveRunValueBefore?.ToString());
    }

    [Fact]
    public void ScopedLaunchWithoutTestMode_RefusesWithExitCode78()
    {
        AppFixture.SkipIfDisabled();

        var psi = new ProcessStartInfo(FindAppExe()) { UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = "it" + Guid.NewGuid().ToString("N")[..10];

        using var proc = Process.Start(psi)!;
        Check.That(proc.WaitForExit(20_000)).IsTrue();
        Check.That(proc.ExitCode).IsEqualTo(InstanceStartupPolicy.RefusalExitCode);
    }

    private static string FindAppExe()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Directory.GetParent(dir)?.FullName;

        var exe = Path.Combine(dir!, "src", "EyeCtrl.App", "bin", "Debug", "net10.0-windows",
                               "EyeCtrl.App.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"Build EyeCtrl.App first: {exe}");
        return exe;
    }
}
```

- [ ] **Step 2: Ask before running it**

**STOP.** This launches the real app twice and shows its overlays. It does not move the cursor. Ask for an explicit go-ahead, and confirm no other `EyeCtrl.App` is running — the second test asserts an exit code, and a default-instance collision would confuse it.

```bash
$env:EYECTRL_UI_TESTS=1; dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~InstanceScopingIntegrationTests"
```

Expected: `Fehler: 0, erfolgreich: 2`.

- [ ] **Step 3: Add the graceful-exit heartbeat assertion**

The spec's integration requirement includes "a graceful exit sends nothing to `eyectrl_watch`", and
the test above cannot cover it: it `Kill()`s the process, so `OnExit` and `SignalShuttingDown` never
run — the exact path that was the round-2 BLOCKER.

Add a third test that exits the app gracefully. There is no CLI switch for a clean exit, so close
it the way the tray menu does — post `WM_CLOSE` to its main window, or use the existing
single-instance behaviour: launch a *second* default-instance process, which exits cleanly at
`App.xaml.cs:104` after the mutex check. The second option is simpler and still exercises
`SignalShuttingDown` via `OnExit`:

```csharp
    [Fact]
    public async Task GracefulExit_SendsNothingToTheHeartbeatPipe()
    {
        AppFixture.SkipIfDisabled();

        // Bind the production watch pipe ourselves and assert nothing ever connects. A supervisor in
        // adoption mode would authenticate this exe on path + session, so a stray shutdown frame
        // here is what would get a live app force-killed.
        using var server = new NamedPipeServerStream(
            "eyectrl_watch", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var instanceId = "it" + Guid.NewGuid().ToString("N")[..10];
        var psi = new ProcessStartInfo(FindAppExe()) { Arguments = "--test-mode", UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = instanceId;

        using var proc = Process.Start(psi)!;
        await Task.Delay(TimeSpan.FromSeconds(8));          // let it finish starting
        proc.CloseMainWindow();                              // graceful: runs OnExit
        proc.WaitForExit(15_000);

        var finished = await Task.WhenAny(connected, Task.Delay(2000));
        Check.That(finished).Not.IsSameReferenceAs(connected);

        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "instances", instanceId);
        try { Directory.Delete(profile, recursive: true); } catch { }
    }
```

This test **binds the production `eyectrl_watch` name**, which is unavoidable — the assertion is
that the app does not dial *that* name. So it cannot run while a real supervisor is active, and it
must not run concurrently with a sibling worktree's suite. Note that in the test's XML docs, and
confirm the box is quiet before running:

```powershell
Get-Process -Name "dotnet","testhost*","EyeCtrl*" -ErrorAction SilentlyContinue
```

Also add profile cleanup to `ScopedLaunchWithoutTestMode_RefusesWithExitCode78` — it refuses before
writing a manifest, but `InitializeInstanceAndLogging` has already created the log directory.

- [ ] **Step 4: Document it in `AGENTS.md`**

Under `## Dependencies & Quirks`:

```markdown
- **`EYECTRL_INSTANCE_ID`** scopes every process-external name a self-contained app instance binds:
  the profile directory (`%APPDATA%\EyeCtrl\instances\<id>`), the single-instance and settings-writer
  mutexes, the dwell pipe, and the `HKCU\…\Run` value. It **requires `--test-mode`** — scoping
  separates names, not the eye tracker, the tray or the overlays — and a scoped instance refuses
  supervision and self-restart. Startup fails closed with exit code 78 rather than running
  half-scoped, and writes `instance.json` into the profile so a harness can verify it. The rule lives
  in `EyeCtrl.Contracts.InstanceScope`; `EYECTRL_APPDATA_DIR` remains an explicit path override.
  `InstanceScopeDriftTests` fails the build when a new process-external side effect appears
  unscoped and un-allow-listed.
```

- [ ] **Step 5: Mark the spec implemented**

Change the spec's `**Status:**` line to:

```markdown
**Status:** Implemented (see `docs/superpowers/plans/2026-08-13-instance-scoping.md`).
```

- [ ] **Step 6: Full verification**

```bash
dotnet test --filter "Category!=UI"
```

Expected: `Fehler: 0` on every assembly. Count `Fehler <FQN>` lines to confirm — the output is German and grepping for "Failed" reports green on a red run.

- [ ] **Step 7: Commit**

```bash
git add tests/EyeCtrl.App.Tests AGENTS.md docs/superpowers/specs/2026-08-12-instance-scoping-design.md
git commit -m "test(app): integration-test instance scoping; document it"
```

---

## What this plan deliberately leaves out

- **Concurrent scoped instances.** Scoping separates names only. Two scoped instances still share the cursor, the overlays, the tray area and foreign-window actions; the `--test-mode` requirement enforces only the eye-tracker boundary. Serialisation is the E2E harness's job, via the run lock its own plan specifies.
- **Scoping the supervisor process.** Unreachable by construction — a scoped instance never launches one — so `Local\EyeCtrl.Supervisor.Singleton`, the supervisor's profile and `HeartbeatPipeServer`'s fixed name stay as they are.
- **Tier-2 coverage of supervision, restart and heartbeat.** Those subsystems are refused while scoped, so they cannot be exercised through a scoped instance and will need a harness that drives the supervisor directly.
- **Whether `EYECTRL_APPDATA_DIR` should survive.** It does, as an explicit path override. Removing it is a three-line simplification available at any time.
