# Struggle Detection — Phase 1a-② Serialized Sink & Primary Overlay Wiring — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide the single serialized, monotonic, no-throw telemetry sink that dwell outcomes flow into, and wire it into the 9 DI-registered `DwellOverlayViewModelBase` subclasses so they report `DwellOutcome`s tagged with their `DwellContext` — with **zero behaviour change** (the sink only buffers).

**Architecture:** A pure `BufferingTelemetrySink` (Core) keeps a bounded ring buffer and drops out-of-order outcomes. An App-layer `DispatcherTelemetrySink` serializes every report onto the UI dispatcher and swallows inner-sink exceptions. The sink is registered in DI; the base overlay class threads a `(context, sink)` pair into its `DwellDetector`, and each DI-registered subclass passes its literal context. Because `Report` only buffers, no interaction behaviour changes.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (pure), `EyeCtrl.App` (MEDI DI, WPF `Dispatcher`), `EyeCtrl.ViewModels`; xUnit v3 + NFluent tests.

**Depends on:** plan 1a-① (contracts `IStruggleTelemetrySink`, `DwellOutcome`, `DwellContext`, and `DwellDetector`'s `(context, sink)` constructor). **Core enforces CS1591** (`EyeCtrl.Core.csproj`): every public member below is XML-documented — do not strip the doc comments.

**Scope boundary (deferred to the next plan, 1a-③):** the factory/manual-`new` surfaces — `ScrollOverlayViewModel` (manual `new` in `ScrollModeController.cs:77`), `ExternalDwellService` / `ManualSelectionDwellService` (factory lambdas), `EyeCareBannerWindow` (factory), `EmojiGridManager` + `KeyboardWindow._predictionDwellDetector` (manual `new` threaded via `OverlayManager`), and `KeyboardViewModel`'s net-new nearest-key abandonment emission. Gaze-quality signals (jitter/tracking/fatigue) and the effective-settings layer are later plans. This plan wires the base class + its 9 DI subclasses only; the base ctor's new params **default** to `(Other, null)` so the deferred `ScrollOverlayViewModel` keeps compiling unchanged.

---

## File Structure

**Create (Core):** `src/EyeCtrl.Core/Adaptation/BufferingTelemetrySink.cs`
**Create (App):** `src/EyeCtrl.App/Services/DispatcherTelemetrySink.cs`
**Modify (ViewModels):** `DwellOverlayViewModelBase.cs` (base ctor) + the 9 DI subclasses (append `IStruggleTelemetrySink sink`, pass context to base).
**Modify (App):** `src/EyeCtrl.App/Services/AppBootstrapper.cs` (register the sink).
**Test (create):** `tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkTests.cs`, `tests/EyeCtrl.App.Tests/Services/DispatcherTelemetrySinkTests.cs`, `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs`.

---

## Task 1: Core `BufferingTelemetrySink` (bounded + monotonic)

**Files:** Create `src/EyeCtrl.Core/Adaptation/BufferingTelemetrySink.cs`; Test `tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class BufferingTelemetrySinkTests
{
    private static DwellOutcome Outcome(long ts) => new(
        ts, DwellContext.ActionMenu, "t", OutcomeKind.Activated, AbandonReason.None,
        1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null);

    [Fact]
    public void RetainsReportsInOrder()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(10));
        sink.Report(Outcome(20));

        var snap = sink.Snapshot();
        Check.That(snap).HasSize(2);
        Check.That(snap[0].TimestampMicroseconds).IsEqualTo(10L);
        Check.That(snap[1].TimestampMicroseconds).IsEqualTo(20L);
    }

    [Fact]
    public void DropsOutOfOrderReports()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.Report(Outcome(100));
        sink.Report(Outcome(50)); // older than last accepted → dropped

        Check.That(sink.Snapshot()).HasSize(1);
        Check.That(sink.DroppedOutOfOrder).IsEqualTo(1L);
    }

    [Fact]
    public void BoundedToCapacity_KeepsNewest()
    {
        var sink = new BufferingTelemetrySink(capacity: 2);
        sink.Report(Outcome(1));
        sink.Report(Outcome(2));
        sink.Report(Outcome(3)); // evicts oldest (ts=1)

        var snap = sink.Snapshot();
        Check.That(snap).HasSize(2);
        Check.That(snap[0].TimestampMicroseconds).IsEqualTo(2L);
        Check.That(snap[1].TimestampMicroseconds).IsEqualTo(3L);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~BufferingTelemetrySinkTests"`
Expected: FAIL to compile — `BufferingTelemetrySink` does not exist.

- [ ] **Step 3: Create the sink**

`src/EyeCtrl.Core/Adaptation/BufferingTelemetrySink.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// In-memory <see cref="IStruggleTelemetrySink"/> that retains the most recent outcomes in a bounded
/// ring buffer and drops any whose timestamp precedes the last accepted one (marshaling across
/// threads can reorder near-simultaneous events; design §9). <see cref="Report"/> never throws.
/// Serves as the single consumer behind the App-layer dispatcher sink until the StruggleMonitor
/// lands in Phase 1b.
/// </summary>
public sealed class BufferingTelemetrySink : IStruggleTelemetrySink
{
    private readonly DwellOutcome[] _buffer;
    private int _count;
    private int _next;
    private long _lastAcceptedTimestamp = long.MinValue;

    /// <summary>Number of outcomes dropped because their timestamp preceded the last accepted one.</summary>
    public long DroppedOutOfOrder { get; private set; }

    /// <summary>Initializes the sink with the given ring-buffer capacity.</summary>
    /// <param name="capacity">Maximum number of recent outcomes retained; must be positive.</param>
    public BufferingTelemetrySink(int capacity = 256)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _buffer = new DwellOutcome[capacity];
    }

    /// <inheritdoc />
    public void Report(in DwellOutcome outcome)
    {
        if (outcome.TimestampMicroseconds < _lastAcceptedTimestamp)
        {
            DroppedOutOfOrder++;
            return;
        }
        _lastAcceptedTimestamp = outcome.TimestampMicroseconds;
        _buffer[_next] = outcome;
        _next = (_next + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    /// <summary>Returns the retained outcomes, oldest first.</summary>
    /// <returns>A snapshot list of retained outcomes.</returns>
    public IReadOnlyList<DwellOutcome> Snapshot()
    {
        var result = new List<DwellOutcome>(_count);
        int start = (_next - _count + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < _count; i++)
            result.Add(_buffer[(start + i) % _buffer.Length]);
        return result;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~BufferingTelemetrySinkTests"`
Expected: PASS (3 tests). No CS1591.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/BufferingTelemetrySink.cs tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkTests.cs
git commit -m "feat(adaptation): add bounded monotonic BufferingTelemetrySink"
```

---

## Task 2: App `DispatcherTelemetrySink` + DI registration

**Files:** Create `src/EyeCtrl.App/Services/DispatcherTelemetrySink.cs`; Modify `src/EyeCtrl.App/Services/AppBootstrapper.cs`; Test `tests/EyeCtrl.App.Tests/Services/DispatcherTelemetrySinkTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Services/DispatcherTelemetrySinkTests.cs` (headless — uses the delegate-seam constructor, no WPF, no `[Trait("Category","UI")]`):

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class DispatcherTelemetrySinkTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    private sealed class ThrowingSink : IStruggleTelemetrySink
    {
        public void Report(in DwellOutcome outcome) => throw new InvalidOperationException("boom");
    }

    private static DwellOutcome Outcome(long ts) => new(
        ts, DwellContext.Toolbar, "t", OutcomeKind.Activated, AbandonReason.None,
        1.0, new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, null);

    [Fact]
    public void OnThread_ForwardsDirectly()
    {
        var inner = new RecordingSink();
        var sink = new DispatcherTelemetrySink(inner, hasThreadAccess: () => true, post: _ => { });
        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).HasSize(1);
    }

    [Fact]
    public void OffThread_PostsToSerializedChannel()
    {
        var inner = new RecordingSink();
        Action? queued = null;
        var sink = new DispatcherTelemetrySink(inner, hasThreadAccess: () => false, post: a => queued = a);
        sink.Report(Outcome(1));
        Check.That(inner.Outcomes).IsEmpty();   // not forwarded until the channel runs it
        queued!();
        Check.That(inner.Outcomes).HasSize(1);
    }

    [Fact]
    public void InnerThrows_IsSwallowed()
    {
        var sink = new DispatcherTelemetrySink(new ThrowingSink(), hasThreadAccess: () => true, post: _ => { });
        sink.Report(Outcome(1)); // must not throw
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DispatcherTelemetrySinkTests"`
Expected: FAIL to compile — `DispatcherTelemetrySink` does not exist.

- [ ] **Step 3: Create the dispatcher sink**

`src/EyeCtrl.App/Services/DispatcherTelemetrySink.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System;
using System.Windows.Threading;
using EyeCtrl.Core.Adaptation;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IStruggleTelemetrySink"/> wrapper that serializes every report onto one thread (the UI
/// dispatcher) before delegating to an inner sink, and swallows any exception the inner throws
/// (design §9 serialized channel, §11 no-throw on the gaze path). This is the sink handed to
/// interaction surfaces.
/// </summary>
public sealed class DispatcherTelemetrySink : IStruggleTelemetrySink
{
    private readonly IStruggleTelemetrySink _inner;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<Action> _post;
    private readonly ILogger<DispatcherTelemetrySink>? _logger;

    /// <summary>Production constructor: serializes onto <paramref name="dispatcher"/>.</summary>
    /// <param name="inner">The sink that receives reports on the dispatcher thread.</param>
    /// <param name="dispatcher">The UI dispatcher that owns the serialized channel.</param>
    /// <param name="logger">Optional logger for swallowed inner-sink exceptions.</param>
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Dispatcher dispatcher,
        ILogger<DispatcherTelemetrySink>? logger = null)
        : this(inner, dispatcher.CheckAccess, a => dispatcher.BeginInvoke(a), logger)
    {
    }

    /// <summary>Testing constructor: injects the thread-access check and post delegate directly.</summary>
    /// <param name="inner">The inner sink.</param>
    /// <param name="hasThreadAccess">Returns true when the caller is already on the serialized thread.</param>
    /// <param name="post">Queues an action onto the serialized thread.</param>
    /// <param name="logger">Optional logger for swallowed inner-sink exceptions.</param>
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Func<bool> hasThreadAccess,
        Action<Action> post, ILogger<DispatcherTelemetrySink>? logger = null)
    {
        _inner = inner;
        _hasThreadAccess = hasThreadAccess;
        _post = post;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Report(in DwellOutcome outcome)
    {
        var copy = outcome; // struct copy so it can be captured when marshaling
        if (_hasThreadAccess())
            Forward(copy);
        else
            _post(() => Forward(copy));
    }

    private void Forward(DwellOutcome outcome)
    {
        try { _inner.Report(outcome); }
        catch (Exception ex) { _logger?.LogError(ex, "Struggle telemetry sink threw; outcome dropped."); }
    }
}
```

- [ ] **Step 4: Register the sink in DI**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, add `using EyeCtrl.Core.Adaptation;` if not present. Then, in `BuildServiceProvider(...)` immediately **before** the ViewModel registrations (the `// ViewModels (singleton ...)` block near line 148), add:

```csharp
// Struggle-detection telemetry sink (Phase 1a): surfaces report DwellOutcomes here.
// BufferingTelemetrySink only buffers (zero behaviour change); DispatcherTelemetrySink
// serializes onto the UI dispatcher and swallows inner exceptions.
services.AddSingleton<BufferingTelemetrySink>();
services.AddSingleton<IStruggleTelemetrySink>(sp => new DispatcherTelemetrySink(
    sp.GetRequiredService<BufferingTelemetrySink>(),
    dispatcher,
    sp.GetRequiredService<ILogger<DispatcherTelemetrySink>>()));
```

(`dispatcher` is the local already used by the nearby factory lambdas; `ILogger<T>` resolves via the existing `AddLogging` registration.)

- [ ] **Step 5: Run tests + build**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DispatcherTelemetrySinkTests"`
Expected: PASS (3 tests).
Run: `dotnet build src/EyeCtrl.App`
Expected: builds (DI registration compiles).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/DispatcherTelemetrySink.cs src/EyeCtrl.App/Services/AppBootstrapper.cs tests/EyeCtrl.App.Tests/Services/DispatcherTelemetrySinkTests.cs
git commit -m "feat(adaptation): add serialized no-throw DispatcherTelemetrySink + DI registration"
```

---

## Task 3: Thread `(context, sink)` through the base class and the 9 DI overlay subclasses

**Files:** Modify `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs` + 9 subclass files; Test `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs`. The first test drives a minimal concrete subclass to prove the base-class threading end-to-end; the second is a drift guard that fails if any overlay subclass lacks a context decision.

```csharp
namespace EyeCtrl.ViewModels.Tests.Adaptation;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public class OverlayTelemetryWiringTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    // Minimal concrete overlay that exposes target creation, to test the base-class (context, sink) threading.
    private sealed class ProbeOverlayViewModel : DwellOverlayViewModelBase
    {
        public ProbeOverlayViewModel(IMessenger messenger, IStruggleTelemetrySink sink)
            : base(messenger, dwellCooldownMs: 0, context: DwellContext.QuickActions, sink: sink)
        {
        }

        public void AddProbeTarget() =>
            DwellDetector.AddTarget(new DwellTarget("probe", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        protected override void OnTargetActivated(string targetId) { }
        protected override void OnTargetProgress(string targetId, double progress) { }
    }

    [Fact]
    public void BaseClass_ThreadsContextAndSink_IntoDwellDetector()
    {
        var sink = new RecordingSink();
        var vm = new ProbeOverlayViewModel(new WeakReferenceMessenger(), sink) { IsVisible = true };
        vm.AddProbeTarget();

        long t = 0;
        for (int i = 0; i < 9; i++) // 200 ms dwell at 30 ms/sample
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), t));
            t += 30_000;
        }

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.QuickActions);
    }

    [Fact]
    public void EveryDwellOverlaySubclass_HasAnAssignedContext()
    {
        // Intended context for every concrete DwellOverlayViewModelBase subclass. ScrollOverlay's
        // ctor wiring lands in plan 1a-③; its entry here documents intent and keeps this guard green.
        var expected = new Dictionary<string, DwellContext>
        {
            [nameof(ActionMenuViewModel)] = DwellContext.ActionMenu,
            [nameof(ActivationButtonViewModel)] = DwellContext.ActivationButton,
            [nameof(DragTrailViewModel)] = DwellContext.DragTrail,
            [nameof(EdgeButtonBarViewModel)] = DwellContext.EdgeButtonBar,
            [nameof(GlassBackdropViewModel)] = DwellContext.Backdrop,
            [nameof(ManualSelectionOverlayViewModel)] = DwellContext.ManualSelection,
            [nameof(QuickActionsGridViewModel)] = DwellContext.QuickActions,
            [nameof(ScrollOverlayViewModel)] = DwellContext.Scroll,
            [nameof(TaskSwitcherViewModel)] = DwellContext.TaskSwitcher,
            [nameof(ToolbarViewModel)] = DwellContext.Toolbar,
        };

        var subclasses = typeof(DwellOverlayViewModelBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DwellOverlayViewModelBase).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Check.That(subclasses).Not.IsEmpty();
        foreach (var name in subclasses)
            Check.That(expected.ContainsKey(name)).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiringTests"`
Expected: FAIL to compile — the base ctor has no `context`/`sink` parameters.

- [ ] **Step 3: Change the base constructor**

In `src/EyeCtrl.ViewModels/DwellOverlayViewModelBase.cs`, add `using EyeCtrl.Core.Adaptation;`, then change:

```csharp
    protected DwellOverlayViewModelBase(IMessenger messenger, int dwellCooldownMs)
        : base(messenger)
    {
        DwellDetector = new DwellDetector(dwellCooldownMs);
        DwellDetector.TargetActivated += OnTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
    }
```

to:

```csharp
    protected DwellOverlayViewModelBase(IMessenger messenger, int dwellCooldownMs,
        DwellContext context = DwellContext.Other, IStruggleTelemetrySink? sink = null)
        : base(messenger)
    {
        DwellDetector = new DwellDetector(dwellCooldownMs, context, sink);
        DwellDetector.TargetActivated += OnTargetActivated;
        DwellDetector.TargetProgress += OnTargetProgress;
    }
```

The defaulted `(Other, null)` keeps the deferred `ScrollOverlayViewModel` (and any not-yet-wired subclass) compiling and behaviourally unchanged.

- [ ] **Step 4: Wire the 9 DI-registered subclasses**

Each edit appends `IStruggleTelemetrySink sink` to the constructor (MEDI auto-resolves it by type — position is irrelevant) and adds `, DwellContext.<X>, sink` to the `: base(...)` call. Add `using EyeCtrl.Core.Adaptation;` to each file. Apply all nine exactly:

**ActionMenuViewModel.cs** →
```csharp
public ActionMenuViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActionMenu, sink)
{
}
```
**ActivationButtonViewModel.cs** →
```csharp
public ActivationButtonViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActivationButton, sink)
{
    _settings = settings;
}
```
**DragTrailViewModel.cs** →
```csharp
public DragTrailViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.DragTrail, sink)
{
    _settings = settings;

    _settleDetector = new FixationDetector(
        settings.Drag.SettleRadiusPixels,
        settings.Drag.SettleDwellMs);
    _settleDetector.FixationStarted += OnSettleStarted;
    _settleDetector.FixationEnded += OnSettleEnded;
}
```
**EdgeButtonBarViewModel.cs** →
```csharp
public EdgeButtonBarViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.EdgeButtonBar, sink)
{
}
```
**GlassBackdropViewModel.cs** →
```csharp
public GlassBackdropViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Backdrop, sink)
{
    _settings = settings;
}
```
**ManualSelectionOverlayViewModel.cs** →
```csharp
public ManualSelectionOverlayViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ManualSelection, sink)
{
}
```
**QuickActionsGridViewModel.cs** →
```csharp
public QuickActionsGridViewModel(
    IQuickActionsStore store,
    EyeCtrlSettings settings,
    IMessenger messenger,
    IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.QuickActions, sink)
{
    _store = store;
    _settings = settings;
    _messenger = messenger;
}
```
**TaskSwitcherViewModel.cs** →
```csharp
public TaskSwitcherViewModel(
    IWindowManager windowManager,
    EyeCtrlSettings settings,
    IMessenger messenger,
    IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.TaskSwitcher, sink)
{
    _windowManager = windowManager;
    _messenger = messenger;
    _settings = settings;
}
```
**ToolbarViewModel.cs** → change only the signature line and the `: base(...)` line; leave the constructor body (the button/target setup) exactly as-is:
```csharp
public ToolbarViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink sink)
    : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink)
{
    // ... existing body unchanged ...
```

- [ ] **Step 5: Run the wiring tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~OverlayTelemetryWiringTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Full build + headless suites (confirm DI resolves and nothing regressed)**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds — the 9 subclasses' new `IStruggleTelemetrySink` param resolves from the Task 2 registration; `ScrollOverlayViewModel` still compiles via the defaulted base params.
Run: `dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.ViewModels.Tests`
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS (headless subsets green).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.ViewModels tests/EyeCtrl.ViewModels.Tests/Adaptation/OverlayTelemetryWiringTests.cs
git commit -m "feat(adaptation): report DwellOutcomes from the 9 DI overlay view-models"
```

---

## Self-Review

**Spec coverage (this slice):**
- §9 serialized channel + monotonic guard — `DispatcherTelemetrySink` (serialize onto dispatcher) + `BufferingTelemetrySink` (drop out-of-order). ✓
- §11 no-throw on the gaze path — `DispatcherTelemetrySink.Forward` try/catch; `BufferingTelemetrySink.Report` makes no external calls. ✓
- §4 shared sink across surfaces + per-context tagging — base-class threading + 9 subclass contexts; drift guard locks coverage. ✓
- §9.1 zero behaviour change — the sink only buffers; overlays' interaction logic is untouched; base params default so the deferred subclass is unchanged. ✓
- **Deferred (documented, not gaps):** factory/manual surfaces (`ScrollOverlay`, `ExternalDwellService`, `ManualSelectionDwellService`, `EyeCareBannerWindow`, `EmojiGridManager`, `KeyboardWindow` prediction), `KeyboardViewModel` net-new key emission, gaze-quality signals + fatigue, effective-settings → plans 1a-③ / 1a-④. The drift-guard test already lists `ScrollOverlayViewModel`'s intended context so it stays green until that wiring lands.

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** base ctor `(IMessenger messenger, int dwellCooldownMs, DwellContext context = DwellContext.Other, IStruggleTelemetrySink? sink = null)` matches all 9 subclass `: base(...)` calls and the `ProbeOverlayViewModel` test. `BufferingTelemetrySink` / `DispatcherTelemetrySink` implement `IStruggleTelemetrySink.Report(in DwellOutcome)` from 1a-①. `DwellOutcome` is constructed with all 10 positional args (incl. `TargetBounds: null`) in every test helper. DI registration types (`BufferingTelemetrySink`, `IStruggleTelemetrySink`, `ILogger<DispatcherTelemetrySink>`) match the classes.

**Verified facts this plan relies on (from source):** MEDI, all `AddSingleton`; the 9 listed subclasses are `AddSingleton<T>()` type-registrations (auto-inject the new param); `ScrollOverlayViewModel` is *not* in DI (manual `new` in `ScrollModeController.cs:77`) hence deferred; `dispatcher` and `ILogger<T>` are available in `AppBootstrapper.BuildServiceProvider`. Subclass constructor signatures are reproduced verbatim from source.

**Implementer notes:**
- `EyeCtrl.App.Tests` runs the delegate-seam sink test headlessly (plain `[Fact]`, no WPF) — keep it out of the `Category=UI` set.
- If `dotnet build` reports an unresolved `IStruggleTelemetrySink` for any subclass, the Task 2 DI registration was skipped or ordered after use — it must be registered before `BuildServiceProvider()` returns (order within the method does not matter for singletons, but the registration must exist).
