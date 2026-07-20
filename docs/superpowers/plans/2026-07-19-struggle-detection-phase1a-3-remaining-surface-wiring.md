# Struggle Detection — Phase 1a-③ Remaining Surface Wiring & Keyboard Emission — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the `DwellOutcome` fan-in so **every** dwell surface reports into the telemetry sink: the `KeyboardViewModel` nearest-key tracker (net-new emission logic), the `ScrollOverlayViewModel`, the two dwell services, the eye-care banner, and the manual-`new` keyboard/emoji overlays — all with **zero behaviour change** (the sink still only buffers).

**Architecture:** Reuses the sink and `DwellContext` from 1a-①/②. Surfaces that own a `Core.DwellDetector` just pass `(context, sink)` to it. The keyboard's *own* nearest-key accumulator (not a `DwellDetector`) gets net-new, sink-gated emission mirroring `DwellDetector`'s semantics. Wiring paths differ per surface (DI auto-inject / factory lambda / manual `new` / the `EventCoordinationContext` record), all verified against source.

**Tech Stack:** C# / .NET 10; `EyeCtrl.ViewModels`, `EyeCtrl.App`; xUnit v3 + NFluent.

**Depends on:** 1a-① (contracts + `DwellDetector(context, sink)`), 1a-② (`IStruggleTelemetrySink` registered in DI; base overlay wiring). **Core enforces CS1591.**

**Scope boundary:** gaze-quality signals (jitter / tracking / fatigue push) → plan 1a-④; effective-settings layer → plan 1a-⑤. This plan finishes the *dwell-outcome* fan-in only.

**Testability note (read first):** only `KeyboardViewModel` (Task 1) is cleanly unit-testable headlessly — its `ProcessKeyGaze` is a pure public method. The other surfaces are App-layer services/`Window`s whose emission flows through a `Core.DwellDetector` **already proven** by 1a-①'s `DwellDetectorTelemetryTests`; those tasks are verified by `dotnet build` + the correct `DwellContext` literal (confirmed by review), not by new per-surface WPF tests. This is called out honestly per task.

---

## File Structure

**Modify (ViewModels):** `KeyboardViewModel.cs` (net-new emission), `ScrollOverlayViewModel.cs` (context/sink).
**Modify (App):** `AppBootstrapper.cs` (3 factory lambdas + OverlayManager lambda), `IEventCoordinator.cs` (record param), `App.xaml.cs` (supply sink), `ScrollModeController.cs` (pass sink), `Services/ExternalDwellService.cs`, `Services/ManualSelectionDwellService.cs`, `Overlays/EyeCareBannerWindow.xaml.cs`, `Services/OverlayManager.cs`, `Overlays/KeyboardWindow.xaml.cs`, `Overlays/EmojiGridManager.cs`.
**Test (create):** `tests/EyeCtrl.ViewModels.Tests/Adaptation/KeyboardTelemetryTests.cs`.

All sink params are **optional (`= null`)** so existing direct constructions in tests compile unchanged and MEDI still injects the registered sink (per 1a-②'s C1 rationale).

---

## Task 1: `KeyboardViewModel` nearest-key emission (net-new, full TDD)

The keyboard runs its own accumulator (`ProcessKeyGaze`), not a `DwellDetector`, so emission is net-new. It must mirror `DwellDetector` semantics: emit **Activated** on completion, **Abandoned/NearestChanged** when the nearest key switches with progress ≥ threshold, **Abandoned/GazeLeft** when gaze leaves the key area with progress ≥ threshold, and **never** during the post-activation cooldown.

**Files:** Modify `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`; Test `tests/EyeCtrl.ViewModels.Tests/Adaptation/KeyboardTelemetryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.ViewModels.Tests/Adaptation/KeyboardTelemetryTests.cs`:

```csharp
namespace EyeCtrl.ViewModels.Tests.Adaptation;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

public class KeyboardTelemetryTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private static readonly Dictionary<string, PhysicalPoint> TwoKeys = new()
    {
        ["A"] = new PhysicalPoint(100, 100),
        ["B"] = new PhysicalPoint(400, 100),
    };

    private static KeyboardViewModel MakeVm(RecordingSink sink, int keyDwellMs = 200, int cooldownMs = 400)
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink);
        vm.Configure(keyDwellMs, cooldownMs);
        return vm;
    }

    [Fact]
    public void KeyDwellCompletes_EmitsActivated_TaggedKeyboard()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), isGazeInsideWindow: true, 0);
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), isGazeInsideWindow: true, 200_000);

        Check.That(sink.Outcomes).HasSize(1);
        var o = sink.Outcomes[0];
        Check.That(o.Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(o.Context).IsEqualTo(DwellContext.Keyboard);
        Check.That(o.TargetId).IsEqualTo("A");
        Check.That(o.TargetCenter).IsEqualTo(new PhysicalPoint(100, 100));
    }

    [Fact]
    public void NearestKeySwitchesAfterEnoughProgress_EmitsAbandoned_NearestChanged()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);        // enter A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 100_000);  // switch to B (A at 50%)

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.NearestChanged);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
    }

    [Fact]
    public void GazeLeavesKeyAreaAfterEnoughProgress_EmitsAbandoned_GazeLeft()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);          // enter A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), false, 100_000);   // gaze leaves the key area

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
    }

    [Fact]
    public void SwitchBelowThreshold_EmitsNothing()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);         // enter A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 30_000);    // switch at ~15% (<0.3)

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void Cooldown_AfterActivation_NeverEmitsAbandoned()
    {
        var sink = new RecordingSink();
        var vm = MakeVm(sink, keyDwellMs: 200, cooldownMs: 400);

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 200_000);   // activates A
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 230_000);   // still within cooldown
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(400, 100), true, 260_000);   // still within cooldown

        Check.That(sink.Outcomes.TrueForAll(o => o.Kind == OutcomeKind.Activated)).IsTrue();
        Check.That(sink.Outcomes).HasSize(1);
    }

    [Fact]
    public void NullSink_StillFiresKeyDwellCompleted_NeverThrows()
    {
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()); // no sink
        vm.Configure(200, 400);
        string? completed = null;
        vm.KeyDwellCompleted += id => completed = id;

        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(TwoKeys, new PhysicalPoint(100, 100), true, 200_000);

        Check.That(completed).IsEqualTo("A");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardTelemetryTests"`
Expected: FAIL to compile — `KeyboardViewModel` has no sink constructor parameter.

- [ ] **Step 3: Add the sink, threshold, helpers, and emission points**

In `src/EyeCtrl.ViewModels/KeyboardViewModel.cs`, add `using EyeCtrl.Core.Adaptation;`. Add a field + const next to the existing gaze-tracking fields (after `private int _cooldownMs;`):

```csharp
    private readonly IStruggleTelemetrySink? _sink;

    /// <summary>Minimum dwell fraction an abandoned key must have reached to be reported.</summary>
    private const double KeyAbandonProgressThreshold = 0.3;
```

Change the constructor:

```csharp
    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger)
    {
        _settings = settings;
    }
```

to (optional sink so existing test constructions compile; MEDI injects the real one):

```csharp
    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null)
        : base(messenger)
    {
        _settings = settings;
        _sink = sink;
    }
```

Replace the whole `ProcessKeyGaze` method with the version below (three sink-gated emission points added; the cooldown branch deliberately does **not** emit). The non-emission control flow is byte-for-byte the original:

```csharp
    public void ProcessKeyGaze(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, bool isGazeInsideWindow, long timestampUs)
    {
        if (keyScreenPositions.Count == 0 || _keyDwellMs <= 0) return;

        if (!isGazeInsideWindow)
        {
            if (NearestKeyId != null)
            {
                EmitAbandonIfNeeded(keyScreenPositions, gazePoint, timestampUs, AbandonReason.GazeLeft);
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Post-activation cooldown (deliberately no emission — not a struggle signal).
        if (timestampUs < _keyCooldownEndTimestamp)
        {
            if (NearestKeyId != null)
            {
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Find nearest key
        string? nearestId = null;
        double nearestDist = double.MaxValue;

        foreach (var (id, center) in keyScreenPositions)
        {
            double dist = gazePoint.DistanceSquaredTo(center);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId != NearestKeyId)
        {
            EmitAbandonIfNeeded(keyScreenPositions, gazePoint, timestampUs, AbandonReason.NearestChanged);
            NearestKeyProgress = 0;
            NearestKeyId = nearestId;
            _nearestKeyEnteredTimestamp = timestampUs;
        }
        else if (nearestId != null)
        {
            long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
            NearestKeyProgress = Math.Min(1.0, (double)elapsed / (_keyDwellMs * MathConstants.MicrosecondsPerMillisecond));

            if (elapsed >= _keyDwellMs * MathConstants.MicrosecondsPerMillisecond)
            {
                EmitActivation(keyScreenPositions, gazePoint, nearestId, timestampUs);
                NearestKeyProgress = 0;
                NearestKeyId = null;
                _keyCooldownEndTimestamp = timestampUs + (_cooldownMs * MathConstants.MicrosecondsPerMillisecond);
                KeyDwellCompleted?.Invoke(nearestId);
            }
        }
    }

    // Reports an abandonment of the CURRENT NearestKeyId (still set at the call point) if it had
    // accumulated at least the threshold. No-op when there is no sink or no in-progress key.
    private void EmitAbandonIfNeeded(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, long timestampUs, AbandonReason reason)
    {
        if (_sink is null || NearestKeyId is null) return;
        long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
        double progress = Math.Min(1.0, (double)elapsed / (_keyDwellMs * MathConstants.MicrosecondsPerMillisecond));
        if (progress < KeyAbandonProgressThreshold) return;
        var center = keyScreenPositions.TryGetValue(NearestKeyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, NearestKeyId,
            OutcomeKind.Abandoned, reason, progress, gazePoint, center, 0, null));
    }

    private void EmitActivation(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, string keyId, long timestampUs)
    {
        if (_sink is null) return;
        var center = keyScreenPositions.TryGetValue(keyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, keyId,
            OutcomeKind.Activated, AbandonReason.None, 1.0, gazePoint, center, 0, null));
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~KeyboardTelemetryTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Confirm no keyboard regressions**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~Keyboard"`
Expected: PASS (existing `KeyboardViewModel` tests unchanged — control flow is byte-for-byte identical when `_sink` is null and otherwise).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/KeyboardViewModel.cs tests/EyeCtrl.ViewModels.Tests/Adaptation/KeyboardTelemetryTests.cs
git commit -m "feat(adaptation): emit gaze-driven keyboard key outcomes (sink-gated)"
```

---

## Task 2: `ScrollOverlayViewModel` via the `EventCoordinationContext` record

`ScrollOverlayViewModel` is a base overlay subclass but is **not** in DI — `ScrollModeController` `new`s it, and the controller only has an `EventCoordinationContext`. So the sink is threaded through that record.

**Files:** `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs`, `src/EyeCtrl.App/Services/IEventCoordinator.cs`, `src/EyeCtrl.App/App.xaml.cs`, `src/EyeCtrl.App/Services/ScrollModeController.cs`

- [ ] **Step 1: Wire the ViewModel**

In `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs` add `using EyeCtrl.Core.Adaptation;` and change:

```csharp
    public ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings)
        : base(messenger, dwellSettings.DwellCooldownMs)
    {
        _scrollSettings = scrollSettings;
    }
```

to:

```csharp
    public ScrollOverlayViewModel(IMessenger messenger, ScrollSettings scrollSettings, DwellSettings dwellSettings,
        IStruggleTelemetrySink? sink = null)
        : base(messenger, dwellSettings.DwellCooldownMs, DwellContext.Scroll, sink)
    {
        _scrollSettings = scrollSettings;
    }
```

- [ ] **Step 2: Add a defaulted sink parameter to the context record**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, add `using EyeCtrl.Core.Adaptation;` and append a **last, defaulted** positional parameter to the `EventCoordinationContext` record so existing constructions stay valid. Change the final parameter line:

```csharp
    IWindowsAppLauncher WindowsAppLauncher,
    IInstalledAppProvider InstalledAppProvider);
```

to:

```csharp
    IWindowsAppLauncher WindowsAppLauncher,
    IInstalledAppProvider InstalledAppProvider,
    IStruggleTelemetrySink? StruggleTelemetrySink = null);
```

- [ ] **Step 3: Supply the real sink at the construction site**

In `src/EyeCtrl.App/App.xaml.cs`, the context is built at lines 167-179. Append the resolved sink as the final argument. Change the closing argument line:

```csharp
                _serviceProvider.GetRequiredService<IWindowsAppLauncher>(),
                _serviceProvider.GetRequiredService<IInstalledAppProvider>());
```

to:

```csharp
                _serviceProvider.GetRequiredService<IWindowsAppLauncher>(),
                _serviceProvider.GetRequiredService<IInstalledAppProvider>(),
                _serviceProvider.GetRequiredService<IStruggleTelemetrySink>());
```

- [ ] **Step 4: Pass it when constructing the ViewModel**

In `src/EyeCtrl.App/Services/ScrollModeController.cs`, change line 77:

```csharp
        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell);
```

to:

```csharp
        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell,
            _context.StruggleTelemetrySink);
```

- [ ] **Step 5: Build + verify context correctness**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. The record's defaulted param keeps any other `new EventCoordinationContext(...)` sites compiling; `App.xaml.cs` now supplies the real sink.
Verification: `ScrollOverlayViewModel`'s `DwellContext.Scroll` is already the intended value asserted by 1a-②'s drift-guard map, and its base-class threading is proven by 1a-②'s `ProbeOverlayViewModel` test. No new runtime test (its show flow needs the App host); the literal is confirmed by review.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/ScrollModeController.cs
git commit -m "feat(adaptation): report scroll-overlay dwell outcomes via context sink"
```

---

## Task 3: Factory-registered services + eye-care banner

Each owns a `Core.DwellDetector`; give it `(context, sink)` and pass the sink through the AppBootstrapper factory lambda.

**Files:** `Services/ExternalDwellService.cs`, `Services/ManualSelectionDwellService.cs`, `Overlays/EyeCareBannerWindow.xaml.cs`, `Services/AppBootstrapper.cs`

- [ ] **Step 1: ExternalDwellService**

In `src/EyeCtrl.App/Services/ExternalDwellService.cs` add `using EyeCtrl.Core.Adaptation;`, append `IStruggleTelemetrySink? sink = null` to the constructor parameter list, and change the detector creation at line 61:

```csharp
        _detector = new DwellDetector(settings.CooldownMs);
```

to:

```csharp
        _detector = new DwellDetector(settings.CooldownMs, DwellContext.ExternalDwell, sink);
```

- [ ] **Step 2: ManualSelectionDwellService**

In `src/EyeCtrl.App/Services/ManualSelectionDwellService.cs` add `using EyeCtrl.Core.Adaptation;`, add a field `private readonly IStruggleTelemetrySink? _sink;`, append `IStruggleTelemetrySink? sink = null` to the constructor and assign `_sink = sink;`, then change the lazy creation at line 67:

```csharp
        _dwellDetector = new DwellDetector(cooldownMs: 0);
```

to:

```csharp
        _dwellDetector = new DwellDetector(cooldownMs: 0, context: DwellContext.ManualSelection, sink: _sink);
```

- [ ] **Step 3: EyeCareBannerWindow**

In `src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs` add `using EyeCtrl.Core.Adaptation;`, change the constructor signature `public EyeCareBannerWindow(int dwellMs = 700)` to `public EyeCareBannerWindow(int dwellMs = 700, IStruggleTelemetrySink? sink = null)`, and change line 29:

```csharp
        _dwell = new DwellDetector(cooldownMs: 0);
```

to:

```csharp
        _dwell = new DwellDetector(cooldownMs: 0, context: DwellContext.EyeCare, sink: sink);
```

- [ ] **Step 4: Feed the three factory lambdas**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, add the sink to each factory lambda.

ExternalDwellService (lines 192-197) — append the argument:
```csharp
        services.AddSingleton(sp => new ExternalDwellService(
            sp.GetRequiredService<EyeCtrlSettings>().ExternalDwell,
            sp.GetRequiredService<GazeEngine>(),
            dispatcher,
            sp.GetRequiredService<ILogger<ExternalDwellService>>(),
            sp.GetRequiredService<IMessenger>(),
            sink: sp.GetRequiredService<IStruggleTelemetrySink>()));
```
(`ExternalDwellService`'s ctor has a `Func<...>? rootWindowAtPoint = null` before the new `sink`; pass `sink` by name so it binds past the skipped optional.)

ManualSelectionDwellService (lines 184-189) — append:
```csharp
        services.AddSingleton(sp => new ManualSelectionDwellService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<ILogger<ManualSelectionDwellService>>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            dispatcher,
            sp.GetRequiredService<GazeEngine>().InteractionManager,
            sp.GetRequiredService<IStruggleTelemetrySink>()));
```

EyeCareBannerWindow (lines 210-211) — append the second argument:
```csharp
        services.AddSingleton(sp => new EyeCareBannerWindow(
            sp.GetRequiredService<EyeCtrlSettings>().Dwell.ActivationButtonDwellMs,
            sp.GetRequiredService<IStruggleTelemetrySink>()));
```

- [ ] **Step 5: Build**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. Emission for these three flows through `Core.DwellDetector`, already proven by 1a-① `DwellDetectorTelemetryTests`; this task only supplies the correct `DwellContext` + sink (verified by review + build). No new WPF/service unit tests.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/ExternalDwellService.cs src/EyeCtrl.App/Services/ManualSelectionDwellService.cs src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "feat(adaptation): report dwell outcomes from external/manual-selection/eye-care surfaces"
```

---

## Task 4: Manual-`new` overlay chain (OverlayManager → KeyboardWindow → EmojiGridManager)

These aren't in DI: `OverlayManager` (factory lambda) `new`s `KeyboardWindow`, which `new`s `EmojiGridManager`. Thread the sink down the chain. Both detectors use target-typed `new()` field initializers, which must move into constructors to receive the sink.

**Files:** `Services/AppBootstrapper.cs`, `Services/OverlayManager.cs`, `Overlays/KeyboardWindow.xaml.cs`, `Overlays/EmojiGridManager.cs`

- [ ] **Step 1: OverlayManager takes the sink and passes it to KeyboardWindow**

In `src/EyeCtrl.App/Services/OverlayManager.cs` add `using EyeCtrl.Core.Adaptation;`, add a field `private readonly IStruggleTelemetrySink _sink;`, append `IStruggleTelemetrySink sink` to the constructor and assign it, then change line 59:

```csharp
        Keyboard = new KeyboardWindow();
```

to:

```csharp
        Keyboard = new KeyboardWindow(_sink);
```

- [ ] **Step 2: Feed the OverlayManager factory lambda**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs` (lines 204-207), append the sink:

```csharp
        services.AddSingleton<IOverlayManager>(sp => new OverlayManager(
            dispatcher,
            sp.GetRequiredService<ILogger<OverlayManager>>(),
            sp.GetRequiredService<IAppIconProvider>(),
            sp.GetRequiredService<IStruggleTelemetrySink>()));
```

- [ ] **Step 3: KeyboardWindow accepts the sink and constructs both detectors with it**

In `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` add `using EyeCtrl.Core.Adaptation;`. Add a field `private readonly IStruggleTelemetrySink? _sink;`, and change the `_predictionDwellDetector` field (line 57) from an initializer to an uninitialized readonly field:

```csharp
    private readonly DwellDetector _predictionDwellDetector = new();
```

to:

```csharp
    private readonly DwellDetector _predictionDwellDetector;
```

Change the constructor (lines 76-82) to accept the sink and build the detector with it:

```csharp
    public KeyboardWindow(IStruggleTelemetrySink? sink = null)
    {
        InitializeComponent();
        _sink = sink;
        _predictionDwellDetector = new DwellDetector(cooldownMs: 0,
            context: DwellContext.KeyboardPrediction, sink: sink);
        SizeChanged += (_, _) => RepositionOnScreen();
        _predictionDwellDetector.TargetActivated += OnPredictionDwelled;
        _predictionDwellDetector.TargetProgress += OnPredictionProgress;
    }
```

Then change the `EmojiGridManager` construction in `SetViewModel` (lines 98-103) to pass the sink:

```csharp
        _emojiManager = new EmojiGridManager(
            KeyRows,
            CategoryBar,
            RegisterKeyVisual,
            () => _keySize,
            ClearKeyState,
            _sink);
```

- [ ] **Step 4: EmojiGridManager accepts the sink and constructs its detector with it**

In `src/EyeCtrl.App/Overlays/EmojiGridManager.cs` add `using EyeCtrl.Core.Adaptation;`. Change the `_categoryDwellDetector` field (line 40):

```csharp
    private readonly DwellDetector _categoryDwellDetector = new();
```

to:

```csharp
    private readonly DwellDetector _categoryDwellDetector;
```

Append `IStruggleTelemetrySink? sink = null` to the constructor parameter list and build the detector at the top of the constructor body:

```csharp
    public EmojiGridManager(
        StackPanel keyRows,
        StackPanel categoryBar,
        Action<string, KeyDefinition, Border, TextBlock?> registerKeyVisual,
        Func<double> getKeySize,
        Action clearKeyState,
        IStruggleTelemetrySink? sink = null)
    {
        _categoryDwellDetector = new DwellDetector(cooldownMs: 0, context: DwellContext.Emoji, sink: sink);
        _keyRows = keyRows;
        _categoryBar = categoryBar;
        _registerKeyVisual = registerKeyVisual;
        _getKeySize = getKeySize;
        _clearKeyState = clearKeyState;
        _categoryDwellDetector.TargetActivated += OnCategoryDwelled;
        _categoryDwellDetector.TargetProgress += OnCategoryProgress;
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds. Emission flows through the two `Core.DwellDetector`s (proven by 1a-①); this task supplies `DwellContext.KeyboardPrediction` / `DwellContext.Emoji` + sink. Verified by review + build (these are `Window`/manager types with no headless unit surface).

- [ ] **Step 6: Full headless suites + commit**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Run: `dotnet test tests/EyeCtrl.ViewModels.Tests`
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS.

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/OverlayManager.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Overlays/EmojiGridManager.cs
git commit -m "feat(adaptation): report dwell outcomes from keyboard-prediction and emoji overlays"
```

---

## Self-Review

**Spec coverage (completes the §4/§6 fan-in):** every dwell surface now reports — the 9 DI overlays (1a-②), plus `KeyboardViewModel` keys (Task 1), `ScrollOverlay` (Task 2), external/manual-selection/eye-care (Task 3), keyboard-prediction + emoji (Task 4). `SettingsWindowViewModel`'s 4 detectors remain intentionally un-instrumented (design §2 non-goal — the remedy surface). Gaze-quality signals + effective-settings are 1a-④/1a-⑤.

**Keyboard emission fidelity:** mirrors `DwellDetector` — Activated on completion; Abandoned/NearestChanged on key switch ≥ threshold; Abandoned/GazeLeft on leaving the key area ≥ threshold; **no** emission during post-activation cooldown (verified by `Cooldown_AfterActivation_NeverEmitsAbandoned`). Reason derivation uses the still-current `NearestKeyId`/`_nearestKeyEnteredTimestamp` at each call point (before reassignment), so progress is the old key's. Zero behaviour change when `_sink` is null.

**Placeholder scan:** none — every step has full code/edits and exact commands.

**Type consistency:** all emissions construct `DwellOutcome` with the 10 positional args (`..., TargetRadius: 0, TargetBounds: null` for keyboard keys, which have no circle/rect geometry); `DwellContext` literals per surface match the 1a-② drift-guard map (`Scroll`, `ExternalDwell`, `ManualSelection`, `EyeCare`, `KeyboardPrediction`, `Emoji`, `Keyboard`). Every sink param is `IStruggleTelemetrySink? = null` (optional), consistent with 1a-②.

**Verified facts relied on (from source):** `KeyboardViewModel` `AddSingleton<KeyboardViewModel>()` (auto-injects); `Configure`/`ProcessKeyGaze` call sites confirmed; `ScrollModeController` reaches deps only via `EventCoordinationContext` (a record with no `IServiceProvider`), constructed at `App.xaml.cs:167`; `OverlayManager`, `ExternalDwellService`, `ManualSelectionDwellService`, `EyeCareBannerWindow` are all **factory** registrations (hand-edited, not auto-injected); `KeyboardWindow`/`EmojiGridManager` constructed once each (`OverlayManager.cs:59`, `KeyboardWindow.xaml.cs:98`).

**Honest testability limits:** only Task 1 gains new unit tests (keyboard `ProcessKeyGaze` is pure). Tasks 2-4 are App-layer wiring verified by `dotnet build` + the already-proven `DwellDetector`/base-overlay emission tests + review of the (obvious) `DwellContext` literal per surface — no fabricated WPF tests. A future end-to-end pass (or 1b's StruggleMonitor consuming real outcomes) will exercise these surfaces live.

**Implementer notes:**
- `ExternalDwellService`'s ctor has an optional `Func<PhysicalPoint,long?>? rootWindowAtPoint = null` positioned before the new `sink`; the factory passes `sink:` by name so it binds correctly past the skipped optional.
- The `EventCoordinationContext` sink param is defaulted (`= null`) so any other construction site (e.g. a test building the record) keeps compiling; only `App.xaml.cs` supplies the real sink. If a test asserts scroll telemetry later, it must pass a sink into the record.
