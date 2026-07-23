# Struggle Detection — Phase 1b-⑥ Sensitive-Context Suppression — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace 1b-⑤b's no-op `AllowAllFeedbackGate` with the **real** `IFeedbackGate` — a `SensitiveContextFeedbackGate` composed from `AdaptationSettings.SuppressInSensitiveContexts` and a new Core `ISensitiveContextDetector` (UI-Automation-backed, implemented in `EyeCtrl.Platform`) — so the on-screen cue, the drift notice, and the verbose adaptation log are suppressed while a password field is focused (§8.10), **while adaptation itself keeps running silently**. This is the LAST Phase-1b slice.

**Architecture:** Three layers plugging into the single §8.10 seam. **Core** gets `ISensitiveContextDetector` (`bool IsSensitiveActive` + `Start`/`Stop`, `: IDisposable` — mirrors `IGazeProvider`). **Platform** gets `UiaSensitiveContextDetector`: a side-effect-free constructor (UIA is touched only in `Start()`, mirroring the Tobii provider), a UIA focus-changed subscription whose callback (on a UIA/RPC thread) reads the focused element's `IsPassword` through a **pure, headless-tested `EvaluateFocusedElement` helper** (own-process focus ⇒ retain the previous value) and publishes a cached bool via `Volatile.Write`; the getter reads it via `Volatile.Read`. Every UIA call is wrapped in `try`/`catch` and degrades to *not sensitive* (best-effort — a false negative only shows a subtle cue, never exposes the credential). **App** gets `SensitiveContextFeedbackGate` (pure, headless truth-table-tested over a fake detector) and swaps the DI registration; the detector is started in `App.OnStartup` and disposed by the container in `OnExit` (removing the UIA hook). The three feedback consumers (`AdaptationCueService`, `AdaptationLog`, `DriftNoticeService`) read `IFeedbackGate.IsFeedbackSuppressed` **unchanged** — only the registration changes.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic, CS1591-strict), `EyeCtrl.Platform` (`System.Windows.Automation` via `UseWPF`), `EyeCtrl.App` (WPF DI/startup); xUnit v3 + NFluent (`Check.That(...)`). Headless tests for the gate composition (App.Tests) and the pure focus evaluator (Platform.Tests, via `InternalsVisibleTo`); the UIA subscription/callback/dispose plumbing is build-verified + a desktop-gated smoke test.

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§13.1 "Sensitive-context detection"** (the primary source: `ISensitiveContextDetector` shape `bool IsSensitiveActive { get; }` + start/stop; UIA focus-changed events; read the focused non-EyeCtrl element's `IsPassword` (+ `ControlType`) → cache a single bool; gate reads it O(1), nothing on the gaze hot path; the UIA callback fires on a UIA/RPC thread so the cached bool is published `Volatile`/`Interlocked`; the subscription is explicitly disposed on shutdown or UIA hooks leak; gates **only** visible/loggable feedback — cue, caregiver toast [Phase 3], verbose log; **best-effort: on UIA failure/unavailability it reports NOT sensitive**, with `SuppressInSensitiveContexts` (default true) + `ShowCue` as backstops; reuses `ControlFinder`'s defensive-COM *pattern*, not copy-paste), **§8.10** (Safety Invariant #10 — "No feedback leakage — cue/toast/verbose logging suppressed in sensitive (password) contexts; adaptation itself still functions silently"), **§10** (`AdaptationSettings.SuppressInSensitiveContexts` default true; cue "Suppressed in sensitive contexts"; log "coarse marker"), **§9.1** (O(1) gate read, nothing on the hot path, "disabled = current cost" is pipeline-scoped).

**Depends on:** **Phase 1a + 1b-①/②/③(all)/④(all)/⑤a/⑤b/⑤c implemented.** From **1b-⑤b**: `IFeedbackGate { bool IsFeedbackSuppressed { get; } }` + `AllowAllFeedbackGate` (both `EyeCtrl.App.Services`) + the DI registration `services.AddSingleton<IFeedbackGate, AllowAllFeedbackGate>();` in `AppBootstrapper` + the `App.OnStartup` adaptation block; `AdaptationCueService` (reads the gate in `OnAssessment`). From **1b-⑤c**: `AdaptationLog` (gate ⇒ coarse marker) and `DriftNoticeService` (gate ⇒ skip banner) — both read the gate unchanged. From **1b-②**: `AdaptationSettings.SuppressInSensitiveContexts` (`bool`, default `true`) and `ShowCue`. All 1b-⑤/⑥ App code is written against the **post-1a/1b-①..⑤c merged** source. **Pre-existing (verified against HEAD `6361572` this session):** `ControlFinder` (the UIA defensive pattern), `IGazeProvider` (the Core-interface precedent), `EyeCtrl.Platform.csproj` (`UseWPF`), `AppBootstrapper` + `App.xaml.cs` (DI + lifecycle).

**Executor pre-flight (do this FIRST — MANDATORY; the struggle-detection slices are plans-only, so `EyeCtrl.Core.Adaptation`, `IFeedbackGate`, and the feedback services do not exist in a fresh tree):** confirm against the *merged* source:

1. **1b-⑤b/⑤c seam is merged.** `src/EyeCtrl.App/Services/IFeedbackGate.cs` defines `public interface IFeedbackGate { bool IsFeedbackSuppressed { get; } }` and `public sealed class AllowAllFeedbackGate : IFeedbackGate { public bool IsFeedbackSuppressed => false; }`, namespace `EyeCtrl.App.Services`. `AppBootstrapper.BuildServiceProvider` contains **`services.AddSingleton<IFeedbackGate, AllowAllFeedbackGate>();`** (the ⑤b line Task 4 replaces). `AdaptationCueService` / `AdaptationLog` / `DriftNoticeService` each take an `IFeedbackGate` and read `IsFeedbackSuppressed` — **do not modify them**; ⑥ only changes which `IFeedbackGate` is registered. If ⑤b named the interface member or class differently, adapt.
2. **1b-② `AdaptationSettings.SuppressInSensitiveContexts`** (`EyeCtrl.Core.Settings`): `bool`, default `true`, reachable as `EyeCtrlSettings.Adaptation.SuppressInSensitiveContexts`. (`ShowCue`, also `bool` default `true`, is already consumed by ⑤b's cue — this slice does not touch it.) Confirm the property exists and defaults true.
3. **`IGazeProvider` precedent** (`src/EyeCtrl.Core/Interfaces/IGazeProvider.cs`): `public interface IGazeProvider : IDisposable` with `void Start()`, `void Stop()`, `bool IsConnected { get; }`. `ISensitiveContextDetector` (Task 1) mirrors this shape and lives in the same folder/namespace `EyeCtrl.Core.Interfaces`. Core has `<ImplicitUsings>enable</ImplicitUsings>` (so `IDisposable` resolves with no explicit `using System;`, as in `IGazeProvider.cs`) and enforces **CS1591 as build errors** — document every public member.
4. **`ControlFinder` UIA defensive pattern** (`src/EyeCtrl.Platform/Services/ControlFinder.cs`): `using System.Windows.Automation;` (`:3`); `private static readonly int OwnProcessId = Environment.ProcessId;` (`:14`); own-process exclusion `if (element.Current.ProcessId == OwnProcessId) return null;` (`:81`); layered COM `try`/`catch (ElementNotAvailableException) / catch (InvalidOperationException) / catch (Exception)` (`:56-70`) + a per-probe `catch (Exception)` (`:134`); ctor takes `ILogger<ControlFinder>`. Task 3 reuses this *pattern* (own-process id, `try`/`catch`, `ILogger<T>`), **not** copy-paste — the focus-changed calls are net-new.
5. **UIA API surface (net-new — the first build in Task 3 Step 7 is the authoritative check; if any member name/signature differs, adjust the detector and rebuild):** in `System.Windows.Automation` —
   - `AutomationElement.Current.IsPassword` → `bool` (the sensitivity signal; same `AutomationElementInformation` struct whose `.ProcessId`/`.ControlType`/`.BoundingRectangle` `ControlFinder` already uses at `:81`/`:88`/`:91`).
   - `AutomationElement.Current.ProcessId` → `int` (own-process check; confirmed used at `ControlFinder.cs:81`).
   - `static void Automation.AddAutomationFocusChangedEventHandler(AutomationFocusChangedEventHandler eventHandler)` and `static void Automation.RemoveAutomationFocusChangedEventHandler(AutomationFocusChangedEventHandler eventHandler)`.
   - delegate `AutomationFocusChangedEventHandler(object sender, AutomationFocusChangedEventArgs e)` — `sender` is the `AutomationElement` that received focus.
6. **`EyeCtrl.Platform.csproj`** (`src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`): `<UseWPF>true</UseWPF>` + `net10.0-windows` ⇒ `System.Windows.Automation` (UIAutomationClient/Types) is already on the framework reference (`ControlFinder` uses it with **no** explicit `<Reference>`). **No csproj reference change and no `#if` conditional (unlike Tobii).** Task 3 adds only one line: `<InternalsVisibleTo Include="EyeCtrl.Platform.Tests" />` (there is currently only `<InternalsVisibleTo Include="EyeCtrl.Supervisor" />` at `:36`), so the pure `EvaluateFocusedElement` helper is headless-testable.
7. **App wiring present (verified against HEAD this session).** `AppBootstrapper.cs` already imports `using EyeCtrl.Core.Interfaces;` (`:8`), `using EyeCtrl.Core.Settings;` (`:13`), `using EyeCtrl.Platform.Services;` (`:20`), and registers Platform services in the interface→impl form (e.g. `services.AddSingleton<IControlFinder, ControlFinder>();` at `:139`, logger auto-injected) — Task 4 needs **no new usings**. `App.xaml.cs` already imports `using EyeCtrl.App.Services;` (`:13`), `using EyeCtrl.Core.Interfaces;` (`:18`), `using EyeCtrl.Core.Settings;` (`:23`); `OnStartup` has the 1b-⑤ adaptation block (coordinator `.Start(gazeEngine)` then the cue/reset/log/drift starts); `OnExit` calls `_serviceProvider.Dispose()` (`App.xaml.cs:299`), which disposes every `IDisposable` singleton — so the detector's UIA hook is removed on shutdown via container disposal (the same lifecycle `AdaptationCoordinator` uses). Task 4 inserts relative to whatever ⑤b/⑤c merged.

If any differ, adjust before proceeding.

**Docs / warnings note:** Core enforces CS1591 as build errors, so `ISensitiveContextDetector` documents every public member. `EyeCtrl.Platform` / `EyeCtrl.App` do not enforce CS1591 as strictly, but every public member added here is documented anyway. `EyeCtrl.Platform.Tests` has no explicit NFluent/xUnit `PackageReference`; those (and their global usings) come from `tests/Directory.Build.props`, exactly as `ControlFinderTests` relies on them.

---

## Scope boundary (explicit — what ⑥ does and does NOT do)

⑥ is the **feedback gate only** (§8.10). It:
- **Adds** the Core `ISensitiveContextDetector` contract, the Platform UIA detector, and the App gate composition; **swaps** the DI registration so all three ⑤ feedback consumers suppress correctly with **no change to their code**.

It does **not**:
- **Touch the adaptation pipeline.** The monitor, controller, coordinator, and `EffectiveSettingsProvider` are untouched; the detector feeds **only** the gate, and the gate feeds **only** the cue/log/drift-notice. Adaptation runs identically whether or not a sensitive context is active (§8.10 "adaptation itself still functions silently").
- **Run anything on the gaze hot path.** The detector updates a cached bool on rare UIA focus-changed events; the gate reads it O(1) on the coordinator's ~4 Hz dispatcher tick (§9.1).
- **Implement the caregiver toast** (§10, Phase 3) — it is listed in §8.10's gated set but does not exist yet; when it lands it will read the same `IFeedbackGate`.
- **Refine the sensitivity rule beyond `IsPassword`.** The focused element's `IsPassword` is the authoritative password signal; `ControlType` is *available* to read for a future refinement but is **not read by the callback today** and is not part of the cached decision (YAGNI — a password field sets `IsPassword` regardless of control type). (§13.1's parenthetical "(and `ControlType`)" is thus satisfied as "could be read", not "is read".)
- **Gate the detector on `AdaptationSettings.Enabled`.** The detector is always started: a system focus hook is *not* gaze-pipeline cost, so "disabled = current cost" (§9.1, pipeline-scoped — the same reasoning ⑤b used for the always-present toolbar button) is satisfied. When adaptation is disabled the gate simply is never read (the coordinator reports `None`).

---

## File Structure

**Create (Core):**
- `src/EyeCtrl.Core/Interfaces/ISensitiveContextDetector.cs` — the framework-agnostic detector contract (`bool IsSensitiveActive` + `Start`/`Stop`, `: IDisposable`).

**Create (Platform):**
- `src/EyeCtrl.Platform/Services/UiaSensitiveContextDetector.cs` — the UIA focus-driven implementation + the `internal static EvaluateFocusedElement` pure helper.

**Create (App):**
- `src/EyeCtrl.App/Services/SensitiveContextFeedbackGate.cs` — the real `IFeedbackGate`: `SuppressInSensitiveContexts && IsSensitiveActive`.

**Modify:**
- `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` — add `<InternalsVisibleTo Include="EyeCtrl.Platform.Tests" />`.
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` — register `ISensitiveContextDetector`; **replace** the `AllowAllFeedbackGate` registration with `SensitiveContextFeedbackGate`.
- `src/EyeCtrl.App/App.xaml.cs` — start the detector in `OnStartup` (disposed by the container in `OnExit`).

**Test (create):**
- `tests/EyeCtrl.App.Tests/Services/SensitiveContextFeedbackGateTests.cs` — headless truth-table over a fake detector.
- `tests/EyeCtrl.Platform.Tests/Services/UiaSensitiveContextDetectorTests.cs` — headless `EvaluateFocusedElement` cases + ctor-is-side-effect-free; a desktop-gated Start/Stop smoke test.

**Untouched (by design):** `AdaptationCueService.cs`, `AdaptationLog.cs`, `DriftNoticeService.cs`, `IFeedbackGate.cs`, `AllowAllFeedbackGate` (kept as a concept/test double — keeps ⑤b's `AllowAllFeedbackGate_NeverSuppresses` test green).

---

## Behaviour change (intended, confined — this is the feature, not a refactor)

Unlike ⑤a–⑤c (net-new/unreferenced), ⑥ **wires** the gate, so it changes behaviour — but only within a tight envelope. Feedback is suppressed **iff** adaptation is `Enabled` (default `false`) **and** a gated struggle occurs **and** `SuppressInSensitiveContexts` is on (default `true`) **and** the focused element is a foreign password field. For the default-off user **no feedback ever appears and the gaze-pipeline cost is unchanged**; the one new runtime side effect at every launch is the always-started detector's process-wide UIA focus hook (disclosed above at "always started" — off the pipeline, a rare O(1) callback, §9.1). When suppression does fire, only the cue/drift-notice/log are affected — never the adaptation itself (§8.10). The gate read is O(1) on the dispatcher; the detector is off the gaze hot path (§9.1). `AllowAllFeedbackGate` remains behaviourally reachable for tests; only the runtime registration changes.

---

## Task 1: Core contract — `ISensitiveContextDetector`

The framework-agnostic detector interface, mirroring `IGazeProvider` (`Start`/`Stop` + `: IDisposable`).

**Files:**
- Create: `src/EyeCtrl.Core/Interfaces/ISensitiveContextDetector.cs`

- [ ] **Step 1: Create the interface**

Create `src/EyeCtrl.Core/Interfaces/ISensitiveContextDetector.cs` (no explicit `using System;` — Core's `ImplicitUsings` supplies `IDisposable`, as in `IGazeProvider.cs`):

```csharp
namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// Detects whether the currently focused UI element is a <em>sensitive</em> (password) context, so the
/// struggle-adaptation feedback surface (on-screen cue, drift notice, verbose log) can be suppressed there
/// (design §8.10 / §13.1) — <b>adaptation itself keeps running</b>, only the visible/loggable feedback is
/// gated. Implemented in <c>EyeCtrl.Platform</c> over UI Automation focus-changed events; the feedback gate
/// reads <see cref="IsSensitiveActive"/> in O(1) (nothing runs on the gaze hot path, §9.1). Best-effort: on
/// UI-Automation failure or unavailability it reports <em>not</em> sensitive — a false negative only shows a
/// subtle cue and never exposes the credential, with <c>AdaptationSettings.SuppressInSensitiveContexts</c>
/// (default true) as the backstop.
/// </summary>
public interface ISensitiveContextDetector : IDisposable
{
    /// <summary>
    /// <c>true</c> when the focused element is a sensitive (password) field. A cached value updated on UI
    /// Automation focus changes and safe to read from any thread; it is <c>false</c> before the first focus
    /// event and whenever detection is stopped, inactive, or has failed.
    /// </summary>
    bool IsSensitiveActive { get; }

    /// <summary>
    /// Starts detection by subscribing to UI Automation focus-changed events. Idempotent (a second call is a
    /// no-op); call once at startup. Any UI-Automation failure is swallowed and leaves detection inactive
    /// (never sensitive), so callers need no <c>try</c>/<c>catch</c>.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops detection by unsubscribing the focus hook (which otherwise leaks) and resets
    /// <see cref="IsSensitiveActive"/> to <c>false</c>. Idempotent; also invoked by <see cref="IDisposable.Dispose"/>.
    /// </summary>
    void Stop();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: builds with **no CS1591** (every public member is documented). Net-new and unreferenced ⇒ zero behaviour change.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/ISensitiveContextDetector.cs
git commit -m "feat(adaptation): add ISensitiveContextDetector Core contract"
```

---

## Task 2: App gate — `SensitiveContextFeedbackGate` (headless TDD)

The pure composition `SuppressInSensitiveContexts && IsSensitiveActive`, over a fake detector. This is the real `IFeedbackGate`; the DI swap happens in Task 4.

**Files:**
- Create: `src/EyeCtrl.App/Services/SensitiveContextFeedbackGate.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/SensitiveContextFeedbackGateTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/SensitiveContextFeedbackGateTests.cs` (`tests/Directory.Build.props` supplies the global `using Xunit;`/`using NFluent;`; the explicit `using NFluent;` below just matches the sibling `AdaptationCueServiceTests` style):

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using NFluent;

public class SensitiveContextFeedbackGateTests
{
    private sealed class FakeSensitiveContextDetector : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static (SensitiveContextFeedbackGate Gate, AdaptationSettings Adaptation, FakeSensitiveContextDetector Detector) Make()
    {
        var settings = new EyeCtrlSettings(); // SuppressInSensitiveContexts defaults true (1b-②)
        var detector = new FakeSensitiveContextDetector();
        var gate = new SensitiveContextFeedbackGate(settings.Adaptation, detector);
        return (gate, settings.Adaptation, detector);
    }

    [Fact]
    public void Suppressed_WhenSettingOn_AndDetectorActive()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = true;
        detector.IsSensitiveActive = true;
        Check.That(gate.IsFeedbackSuppressed).IsTrue();
    }

    [Fact]
    public void NotSuppressed_WhenSettingOn_AndDetectorInactive()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = true;
        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void NotSuppressed_WhenSettingOff_EvenIfDetectorActive()
    {
        // The setting is the backstop: with it off, feedback is never suppressed — even in a password field.
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = false;
        detector.IsSensitiveActive = true;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void NotSuppressed_WhenSettingOff_AndDetectorInactive()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = false;
        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }

    [Fact]
    public void ReadsLiveState_OnEachAccess()
    {
        var (gate, adaptation, detector) = Make();
        adaptation.SuppressInSensitiveContexts = true;

        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();

        detector.IsSensitiveActive = true; // detector flips → gate reflects immediately (no caching in the gate)
        Check.That(gate.IsFeedbackSuppressed).IsTrue();

        detector.IsSensitiveActive = false;
        Check.That(gate.IsFeedbackSuppressed).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SensitiveContextFeedbackGateTests"`
Expected: FAIL to **compile** — `SensitiveContextFeedbackGate` does not exist.

- [ ] **Step 3: Create `SensitiveContextFeedbackGate`**

Create `src/EyeCtrl.App/Services/SensitiveContextFeedbackGate.cs`:

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// The real <see cref="IFeedbackGate"/> (design §8.10): suppresses user-visible/loggable adaptation feedback
/// — the on-screen cue, the drift notice, and the verbose log (which falls back to a coarse marker) — when a
/// sensitive (password) context is active <b>and</b> the user has left
/// <see cref="AdaptationSettings.SuppressInSensitiveContexts"/> on (the default). Adaptation itself keeps
/// running silently regardless. Registered in place of 1b-⑤b's no-op <c>AllowAllFeedbackGate</c>; the three
/// feedback consumers (<c>AdaptationCueService</c>, <c>AdaptationLog</c>, <c>DriftNoticeService</c>) read
/// <see cref="IsFeedbackSuppressed"/> unchanged.
/// </summary>
public sealed class SensitiveContextFeedbackGate : IFeedbackGate
{
    private readonly AdaptationSettings _adaptation;
    private readonly ISensitiveContextDetector _detector;

    /// <summary>Initializes the gate over the suppression toggle and the sensitive-context detector.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.SuppressInSensitiveContexts"/> is the backstop toggle.</param>
    /// <param name="detector">The sensitive-context detector supplying <see cref="ISensitiveContextDetector.IsSensitiveActive"/>.</param>
    public SensitiveContextFeedbackGate(AdaptationSettings adaptation, ISensitiveContextDetector detector)
    {
        _adaptation = adaptation;
        _detector = detector;
    }

    /// <inheritdoc />
    /// <remarks>
    /// True only when the setting is on <b>and</b> a sensitive context is active. The detector read is O(1) (a
    /// cached <c>Volatile</c> bool) — nothing runs on the gaze hot path (§9.1). Evaluated fresh on each read
    /// (on the coordinator's dispatcher tick), so a mid-session focus change or settings toggle is reflected
    /// immediately.
    /// </remarks>
    public bool IsFeedbackSuppressed => _adaptation.SuppressInSensitiveContexts && _detector.IsSensitiveActive;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SensitiveContextFeedbackGateTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/SensitiveContextFeedbackGate.cs tests/EyeCtrl.App.Tests/Services/SensitiveContextFeedbackGateTests.cs
git commit -m "feat(adaptation): add SensitiveContextFeedbackGate composition (headless)"
```

---

## Task 3: Platform detector — `UiaSensitiveContextDetector` (+ pure `EvaluateFocusedElement`)

The UIA focus-driven implementation. The one non-trivial decision — own-process focus ⇒ retain, else sensitive ⇔ `IsPassword` — is factored into a pure `internal static` helper that is headless-tested; the UIA subscription/callback/dispose plumbing is build-verified plus a desktop-gated smoke test.

**Files:**
- Modify: `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` (add the test IVT)
- Create: `src/EyeCtrl.Platform/Services/UiaSensitiveContextDetector.cs`
- Test: `tests/EyeCtrl.Platform.Tests/Services/UiaSensitiveContextDetectorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Platform.Tests/Services/UiaSensitiveContextDetectorTests.cs`. The `EvaluateFocusedElement` cases and the ctor-safety check are unconditional headless tests; the Start/Stop smoke test is gated on an interactive desktop, mirroring `ControlFinderTests` (`Environment.UserInteractive`):

```csharp
namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class UiaSensitiveContextDetectorTests
{
    // UIA focus-changed subscription needs an interactive desktop session; on non-interactive CI agents it
    // can hang, so skip gracefully there (matches the project's ControlFinderTests pattern).
    private static bool DesktopAvailable => Environment.UserInteractive;

    private const int OwnPid = 1234;
    private const int ForeignPid = 5678;

    // --- Pure focus-decision helper (headless) ---

    [Fact]
    public void EvaluateFocusedElement_OwnProcessFocus_ReturnsNull_ToRetainPreviousValue()
    {
        // Focus moving to one of EyeCtrl's own windows/overlays must NOT clear an active sensitive context:
        // the user types passwords via the on-screen keyboard, so retaining the prior value keeps suppression
        // on until focus lands on a real foreign element. (Assert on HasValue/Value rather than comparing a
        // bool? via NFluent's IsEqualTo — the project standardized on that after nullable-comparison traps.)
        var decision = UiaSensitiveContextDetector.EvaluateFocusedElement(OwnPid, OwnPid, isPassword: false);
        Check.That(decision.HasValue).IsFalse();
    }

    [Fact]
    public void EvaluateFocusedElement_ForeignPasswordField_ReturnsTrue()
    {
        var decision = UiaSensitiveContextDetector.EvaluateFocusedElement(ForeignPid, OwnPid, isPassword: true);
        Check.That(decision.HasValue).IsTrue();
        Check.That(decision!.Value).IsTrue();
    }

    [Fact]
    public void EvaluateFocusedElement_ForeignNonPasswordField_ReturnsFalse()
    {
        var decision = UiaSensitiveContextDetector.EvaluateFocusedElement(ForeignPid, OwnPid, isPassword: false);
        Check.That(decision.HasValue).IsTrue();
        Check.That(decision!.Value).IsFalse();
    }

    // --- Constructor is side-effect-free (headless: no desktop needed) ---

    [Fact]
    public void NewDetector_IsNotSensitive_AndCtorTouchesNoUia()
    {
        // The ctor only assigns fields (UIA is touched in Start(), mirroring the Tobii provider), so
        // constructing it is safe without a desktop and the default is the safe not-sensitive value.
        var detector = new UiaSensitiveContextDetector(NullLogger<UiaSensitiveContextDetector>.Instance);
        Check.That(detector.IsSensitiveActive).IsFalse();
        detector.Dispose(); // Dispose before Start() is a no-op and must not throw
    }

    // --- UIA subscription smoke test (desktop-gated; build/integration tier) ---

    [Fact]
    public void StartStopDispose_OnInteractiveDesktop_DoNotThrow()
    {
        if (!DesktopAvailable) return;

        // Smoke test: the real UIA add/remove path runs without throwing and Start/Stop are idempotent.
        // We deliberately do NOT assert IsSensitiveActive after Dispose: on a live desktop a stray foreign
        // password field could gain focus during this window and flip it, so the reset-to-false behaviour
        // is a design property that cannot be asserted race-free headlessly. No-throw/idempotency is the value.
        var detector = new UiaSensitiveContextDetector(NullLogger<UiaSensitiveContextDetector>.Instance);
        detector.Start();
        detector.Start();   // idempotent — must not double-subscribe or throw
        detector.Stop();
        detector.Stop();    // idempotent
        detector.Dispose();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~UiaSensitiveContextDetectorTests"`
Expected: FAIL to **compile** — `UiaSensitiveContextDetector` does not exist (and `EvaluateFocusedElement` is not yet visible to the test project).

- [ ] **Step 3: Add the test `InternalsVisibleTo`**

In `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`, add `EyeCtrl.Platform.Tests` to the existing `InternalsVisibleTo` `ItemGroup` (`:35-37`) so the test project can call the `internal static` helper:

```xml
  <!-- ProcessIdentity (Win32 P/Invokes) is internal; Supervisor's ClientIdentityBinder consumes it. -->
  <ItemGroup>
    <InternalsVisibleTo Include="EyeCtrl.Supervisor" />
    <InternalsVisibleTo Include="EyeCtrl.Platform.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Create `UiaSensitiveContextDetector`**

Create `src/EyeCtrl.Platform/Services/UiaSensitiveContextDetector.cs`. It reuses `ControlFinder`'s defensive idioms (own-process id, layered `try`/`catch`, `ILogger<T>`) but the focus-changed calls are net-new:

```csharp
namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// UI-Automation-backed <see cref="ISensitiveContextDetector"/> (design §13.1). Subscribes to system-wide
/// focus-changed events; on each change it reads the focused element's <c>IsPassword</c> and caches a single
/// boolean the feedback gate reads in O(1). The callback fires on a UI-Automation (RPC) thread, so the cached
/// bool is published with <see cref="System.Threading.Volatile"/>. Every UIA call is wrapped in
/// <c>try</c>/<c>catch</c> and degrades to <em>not sensitive</em> (best-effort — a false negative only shows a
/// subtle cue, never exposing the credential). The constructor is side-effect-free (UIA is touched only in
/// <see cref="Start"/>, mirroring the Tobii provider), so the DI container can construct it on any thread.
/// </summary>
public sealed class UiaSensitiveContextDetector : ISensitiveContextDetector
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    private readonly ILogger<UiaSensitiveContextDetector> _logger;
    // Held in a field so the exact same delegate instance can be removed in Stop() (and so it is never GC'd
    // while the native UIA hook holds it — the same discipline the Tobii gaze callback needs).
    private readonly AutomationFocusChangedEventHandler _focusHandler;
    // Touched only on the UI/dispatcher thread (Start/Stop/Dispose run from App.OnStartup + container
    // disposal), so it needs no synchronization.
    private bool _started;
    // Written on the UIA/RPC callback thread, read on the dispatcher — published with Volatile for
    // cross-thread ordering. A bool is a single aligned byte (atomic read/write on every arch, so no tearing
    // even on x86); the Volatile here supplies the release/acquire fence, not atomicity.
    private bool _isSensitive;

    /// <summary>Initializes the detector. Assigns fields only — no UI Automation call happens until <see cref="Start"/>.</summary>
    /// <param name="logger">Logger for UIA subscription / read failures (which are otherwise swallowed).</param>
    public UiaSensitiveContextDetector(ILogger<UiaSensitiveContextDetector> logger)
    {
        _logger = logger;
        _focusHandler = OnFocusChanged;
    }

    /// <inheritdoc />
    public bool IsSensitiveActive => System.Threading.Volatile.Read(ref _isSensitive);

    /// <summary>
    /// Pure focus decision (headless-testable): returns the new sensitivity value, or <c>null</c> to
    /// <em>retain</em> the previous value. Own-process focus (an EyeCtrl window/overlay) retains — the user
    /// types passwords via the on-screen keyboard, so a momentary focus into EyeCtrl's own UI must not clear an
    /// active sensitive context. Otherwise the context is sensitive iff the focused element is a password field.
    /// </summary>
    /// <param name="focusedProcessId">Process id of the newly focused element.</param>
    /// <param name="ownProcessId">EyeCtrl's own process id.</param>
    /// <param name="isPassword">The focused element's <c>IsPassword</c> flag.</param>
    /// <returns><c>true</c>/<c>false</c> for the new value, or <c>null</c> to keep the current value.</returns>
    internal static bool? EvaluateFocusedElement(int focusedProcessId, int ownProcessId, bool isPassword)
    {
        if (focusedProcessId == ownProcessId)
            return null; // own-process focus: retain the previous cached value
        return isPassword;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focusHandler);
        }
        catch (Exception ex)
        {
            // Best-effort: detection stays inactive (never sensitive), so feedback is simply not suppressed.
            _logger.LogWarning(ex, "Failed to subscribe to UIA focus-changed events; sensitive-context detection is inactive.");
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
            return;
        _started = false;
        try
        {
            Automation.RemoveAutomationFocusChangedEventHandler(_focusHandler);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to unsubscribe the UIA focus-changed handler during shutdown.");
        }
        // Reset to the safe default. RemoveAutomationFocusChangedEventHandler stops *new* invocations but does
        // not cancel one already running, so a callback in flight on the UIA thread could re-write this after
        // the removal above — but that only happens at Stop/Dispose (shutdown), when IsSensitiveActive is no
        // longer read (the coordinator's timer is disposed by the same container disposal), so it is harmless.
        System.Threading.Volatile.Write(ref _isSensitive, false);
    }

    /// <summary>Stops detection (removes the focus hook — it otherwise leaks) via <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();

    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        // The event's `sender` IS the element that received focus when the event fired — the correct
        // per-event choice. (AutomationElement.FocusedElement, which §13.1 names as the example, returns
        // whatever has focus *now*, which for rapid focus flips could be a newer element; either way the
        // ~4 Hz dispatcher read converges to the latest cached value, and using `sender` avoids a round-trip.)
        // Reading it can still fault, so the whole read is guarded and degrades to not-sensitive.
        try
        {
            if (sender is not AutomationElement element)
            {
                // No readable focused element ⇒ treat as not sensitive (best-effort false negative, §13.1).
                System.Threading.Volatile.Write(ref _isSensitive, false);
                return;
            }

            bool? decision = EvaluateFocusedElement(element.Current.ProcessId, OwnProcessId, element.Current.IsPassword);
            if (decision.HasValue)
                System.Threading.Volatile.Write(ref _isSensitive, decision.Value);
        }
        catch (Exception ex)
        {
            // Any UIA read failure (element vanished mid-read, COM fault) ⇒ not sensitive.
            _logger.LogDebug(ex, "UIA focus-change read failed; treating context as not sensitive.");
            System.Threading.Volatile.Write(ref _isSensitive, false);
        }
    }
}
```

- [ ] **Step 5: Run to verify the headless tests pass**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~UiaSensitiveContextDetectorTests"`
Expected: PASS — the 3 `EvaluateFocusedElement` cases + the ctor-safety test always run; `StartStopDispose_OnInteractiveDesktop_DoNotThrow` runs on an interactive dev desktop and is a graceful no-op on non-interactive CI.

- [ ] **Step 6: Run the full Platform + Core suites (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.Platform.Tests`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS. The detector is net-new and unreferenced (wired in Task 4), so no existing Platform/Core behaviour changes.

- [ ] **Step 7: Build the whole solution to verify the UIA surface**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. This is the authoritative check of the net-new UIA API surface (`Automation.AddAutomationFocusChangedEventHandler` / `RemoveAutomationFocusChangedEventHandler`, `AutomationFocusChangedEventHandler`, `AutomationFocusChangedEventArgs`, `AutomationElement.Current.IsPassword`/`.ProcessId`) against `System.Windows.Automation` on the WPF framework reference — no `.csproj` reference needed. **If any member name/signature differs from pre-flight item 5, correct the detector here and rebuild.**

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Platform/EyeCtrl.Platform.csproj src/EyeCtrl.Platform/Services/UiaSensitiveContextDetector.cs tests/EyeCtrl.Platform.Tests/Services/UiaSensitiveContextDetectorTests.cs
git commit -m "feat(adaptation): add UiaSensitiveContextDetector (UIA focus-driven, best-effort)"
```

---

## Task 4: Wire it up — DI swap + start/dispose

Register the detector, replace the `AllowAllFeedbackGate` registration with `SensitiveContextFeedbackGate`, and start the detector in `App.OnStartup` (disposed by the container in `OnExit`).

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`, `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Register the detector + swap the gate**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, find the 1b-⑤b feedback registrations. **Replace** the line

```csharp
        services.AddSingleton<IFeedbackGate, AllowAllFeedbackGate>();
```

with the detector registration + the real gate (same spot, keeping the surrounding ⑤b registrations — `CueOverlayWindow`, `ICuePresenter`, `AdaptationCueService`, `AdaptationResetHandler` — and the ⑤c ones unchanged):

```csharp
        // 1b-⑥ sensitive-context suppression (§8.10). The UIA detector caches whether a password field is
        // focused; the gate suppresses the cue / drift notice / verbose log while it is (and the setting is
        // on), leaving adaptation itself running. Replaces ⑤b's no-op AllowAllFeedbackGate registration.
        // ILogger<UiaSensitiveContextDetector> is auto-injected (as for ControlFinder). Started in
        // App.OnStartup; disposed by the container in OnExit (removing the UIA focus hook).
        services.AddSingleton<ISensitiveContextDetector, UiaSensitiveContextDetector>();
        services.AddSingleton<IFeedbackGate>(sp => new SensitiveContextFeedbackGate(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<ISensitiveContextDetector>()));
```

(No new `using`s: `EyeCtrl.Core.Interfaces` (`ISensitiveContextDetector`) `:8`, `EyeCtrl.Platform.Services` (`UiaSensitiveContextDetector`) `:20`, `EyeCtrl.Core.Settings` (`EyeCtrlSettings`) `:13` are all already imported; `SensitiveContextFeedbackGate`/`IFeedbackGate` are same-namespace `EyeCtrl.App.Services`.)

- [ ] **Step 2: Start the detector in `App.OnStartup`**

In `src/EyeCtrl.App/App.xaml.cs`, inside the 1b-⑤ adaptation startup block (after the `AdaptationCoordinator.Start(gazeEngine)` line and alongside the cue/reset/log/drift starts), add:

```csharp
            // 1b-⑥: start sensitive-context detection so the feedback gate can suppress the cue / drift
            // notice / verbose log while a password field is focused (§8.10). Subscribing to UIA focus events
            // is off the gaze hot path; failures inside Start() are swallowed (detection stays inactive). The
            // container disposes the detector in OnExit, removing the UIA hook.
            _serviceProvider.GetRequiredService<ISensitiveContextDetector>().Start();
```

(No new `using`: `EyeCtrl.Core.Interfaces` is imported at `App.xaml.cs:18`. No `OnExit` edit is needed — `_serviceProvider.Dispose()` at `App.xaml.cs:299` already disposes the detector, whose `Dispose()` calls `Stop()`.)

- [ ] **Step 3: Build**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. `ISensitiveContextDetector` resolves to `UiaSensitiveContextDetector` (logger auto-injected); `IFeedbackGate` now resolves to `SensitiveContextFeedbackGate` (settings + detector); the ⑤b/⑤c cue/log/drift services resolve the same `IFeedbackGate` interface and are unchanged.

- [ ] **Step 4: Run the headless App + Core + Platform suites (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Run: `dotnet test tests/EyeCtrl.Platform.Tests`
Expected: PASS — `SensitiveContextFeedbackGateTests` (5) + `UiaSensitiveContextDetectorTests` + the ⑤b/⑤c headless suites (`AdaptationCueServiceTests`, `AdaptationLogTests`, `DriftNoticeServiceTests`) + existing suites. `AllowAllFeedbackGate_NeverSuppresses` (⑤b) still passes — the class is untouched, only its registration was replaced. The gate swap is inert until adaptation is `Enabled` (default off) AND a gated struggle occurs AND a password field is focused. (The running detector + full DI-container resolution are exercised only when the app launches — the UI-test tier.)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(adaptation): register sensitive-context detector + real feedback gate; start on launch"
```

---

## Self-Review

**Spec coverage (this slice):**
- §13.1 sensitive-context detection — `ISensitiveContextDetector` with `bool IsSensitiveActive` + `Start`/`Stop` (Core); UIA focus-changed subscription reading the focused element's `IsPassword` (Platform); cached bool published `Volatile.Write` / read `Volatile.Read`; O(1) gate read, nothing on the gaze hot path; subscription explicitly removed on `Stop`/`Dispose` (via container disposal in `OnExit`); best-effort *not sensitive* on any UIA failure (`try`/`catch` → `false`); `SuppressInSensitiveContexts` (default true) backstop in the gate; reuses `ControlFinder`'s defensive-COM *pattern* (own-process id, layered `try`/`catch`, `ILogger<T>`), net-new focus calls. ✓
- §8.10 no feedback leakage — `SensitiveContextFeedbackGate` suppresses the cue (⑤b), the verbose log → coarse marker (⑤c), and the drift notice (⑤c) when a password context is active; **adaptation itself still runs** (the detector feeds only the gate; the monitor/controller/coordinator/provider are untouched). ✓
- §10 `SuppressInSensitiveContexts` (default true) is the gate's backstop toggle; cue "suppressed in sensitive contexts" and the log's coarse marker are realized by ⑤b/⑤c consuming this gate unchanged. ✓
- §9.1 O(1) gate read / nothing on the hot path — the gate is a `&&` of a bool property and a cached `Volatile` bool, read on the dispatcher tick; the detector runs off the gaze pipeline entirely; always-started (a focus hook is not pipeline cost — "disabled = current cost" is pipeline-scoped, per ⑤b's ratified reasoning). ✓
- **Own-process exclusion** (§13.1 "non-EyeCtrl element") — `EvaluateFocusedElement` returns `null` (retain) on own-process focus, correctly keeping suppression on while the user operates EyeCtrl's own on-screen keyboard during password entry. ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `ISensitiveContextDetector` (`IsSensitiveActive`/`Start`/`Stop` + `IDisposable`), `SensitiveContextFeedbackGate(AdaptationSettings, ISensitiveContextDetector)` + `IsFeedbackSuppressed`, `UiaSensitiveContextDetector(ILogger<UiaSensitiveContextDetector>)` + `EvaluateFocusedElement(int, int, bool) → bool?` + `Start`/`Stop`/`Dispose`/`IsSensitiveActive`, are used identically across the definitions, the tests, the DI registrations, and the `OnStartup` call. `IFeedbackGate.IsFeedbackSuppressed` (⑤b), `AdaptationSettings.SuppressInSensitiveContexts` (1b-②), `EyeCtrlSettings.Adaptation`, and the `AutomationElement.Current.IsPassword`/`.ProcessId` + `Automation.Add/RemoveAutomationFocusChangedEventHandler` + `AutomationFocusChangedEventHandler`/`AutomationFocusChangedEventArgs` UIA surface match the pre-flight-confirmed 1b-⑤b/② surfaces and the `ControlFinder`-verified accessors.

**Verified facts relied on (from source, this session):**
- `ControlFinder` UIA defensive pattern: `using System.Windows.Automation;` (`:3`), `OwnProcessId = Environment.ProcessId` (`:14`), own-process exclusion (`:81`), layered COM `try`/`catch` (`:56-70`, `:134`), `ILogger<ControlFinder>` ctor — reused as a *pattern*; `.Current.ProcessId`/`.ControlType`/`.BoundingRectangle` confirm the `AutomationElement.Current` accessor family (so `.Current.IsPassword` is the same struct).
- `EyeCtrl.Platform.csproj`: `<UseWPF>true</UseWPF>` + `net10.0-windows` ⇒ `System.Windows.Automation` present with no explicit reference; sole `InternalsVisibleTo` is `EyeCtrl.Supervisor` (`:36`) — Task 3 adds the test IVT.
- `IGazeProvider : IDisposable` with `Start`/`Stop` in `EyeCtrl.Core/Interfaces/` — the interface shape + home precedent; Core `ImplicitUsings` supplies `IDisposable`.
- `AppBootstrapper` imports `EyeCtrl.Core.Interfaces` (`:8`) / `EyeCtrl.Core.Settings` (`:13`) / `EyeCtrl.Platform.Services` (`:20`) and registers Platform services interface→impl with logger auto-injection (`IControlFinder` `:139`); `App.xaml.cs` imports the same three namespaces (`:13`/`:18`/`:23`) and disposes the container in `OnExit` (`:299`).
- `ControlFinderTests` uses the `Environment.UserInteractive` desktop-gate — the precedent for the Start/Stop smoke test; `Platform.Tests` references Platform + `Microsoft.Extensions.Logging.Abstractions` (for `NullLogger`), NFluent/xUnit via `Directory.Build.props`.

**Honest testability limits:** headless-unit-tested (no desktop) — the gate composition (`SensitiveContextFeedbackGate`, 5 truth-table + live-read tests, App.Tests), the pure `EvaluateFocusedElement` (3 cases, Platform.Tests via IVT), and the side-effect-free ctor / not-sensitive default. **Build-verified + desktop-gated smoke test only** (correctly per the Platform test tier): the UIA subscription/callback/`Volatile`/dispose plumbing — the actual focus-changed → `IsPassword` read → cached-bool behaviour needs a real UIA/desktop environment and a focused password field, which cannot be driven headlessly (the smoke test asserts only that `Start`/`Stop`/`Dispose` don't throw and are idempotent, and it no-ops on CI). The full DI-container resolution + the running detector are exercised only when the app launches. The sensitivity rule (`IsPassword`), the own-process retain policy, and the always-started lifecycle are tunable/reviewable design choices.

**Deferred (documented above, not gaps):** the caregiver toast (§10, Phase 3 — will read the same gate); `ControlType`-based sensitivity refinement (YAGNI — `IsPassword` is authoritative); any per-`Enabled` gating of the detector (unnecessary — off the pipeline; the gate is simply never read when adaptation is disabled). With ⑥ complete, **Phase-1b planning is finished** (all slices remain plans-only until an implementation phase is separately greenlit).
