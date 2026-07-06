# Unified Right-Edge Button Bar — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the four duplicated transient right-edge dwell-button surfaces (drag cancel/back, manual-selection close/recapture, scroll stop) with one context-driven `EdgeButtonBar` (VM + window) built on PR #56's `DwellArcButton` and `DwellOverlayViewModelBase`.

**Architecture:** A single long-lived `EdgeButtonBarWindow` backed by `EdgeButtonBarViewModel : DwellOverlayViewModelBase`. The active interaction context (drag / scroll / manual selection) declares a button set via `ShowButtons(definitions, edgeAnchor)`; each `EdgeButtonDefinition` carries a fixed `VerticalOffset` from the anchor Y, so buttons never move when the set changes. Activations route via per-button `Action<IMessenger>` callbacks that send existing messages.

**Tech Stack:** .NET 10, WPF (`EyeCtrl.App`), CommunityToolkit.Mvvm (`[ObservableProperty]`, `ObservableRecipient`), xUnit + NFluent. Builds on PR #56 (`DwellArcButton`, `DwellOverlayViewModelBase`, `MessengerViewModelBase`, `ScrollModeController`).

**Spec:** `docs/superpowers/specs/2026-07-05-unified-edge-button-bar-design.md`

**Spec refinement (called out in plan):** the spec's `EdgeButtonDefinition(string Id, string Symbol, int DwellMs, object ActivationMessage, double VerticalOffset)` is changed to use `Action<IMessenger> Activate` instead of `object ActivationMessage`. Reason: `IMessenger.Send<TMessage>` is generic over the compile-time message type, so a stored `object` cannot be dispatched to type-specific handlers without reflection. An `Action<IMessenger>` closes over the concrete message type and is reflection-free. All other API shape is unchanged.

**Conventions (from AGENTS.md):**
- File-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled.
- NFluent: `Check.That(x).IsEqualTo(y)` etc. xUnit runner.
- No mocking — private nested fake classes.
- Test class names `XxxTests`, underscore-separated PascalCase test methods, namespaces mirror source.
- Run `impact({target, direction:"upstream"})` on GitNexus before editing any existing symbol; run `detect_changes()` before committing.
- Build: `dotnet build EyeCtrl.slnx`. Tests: `dotnet test tests/<Project>.Tests`.

---

## File Structure

**New files:**
- `src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs` — immutable input record (id, symbol, dwell ms, activate callback, vertical offset).
- `src/EyeCtrl.ViewModels/EdgeButtonState.cs` — observable runtime state the View binds to (id, symbol, dwell progress).
- `src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs` — `DwellOverlayViewModelBase` subclass; owns the button set + dwell targets.
- `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml` + `EdgeButtonBarWindow.xaml.cs` — right-edge overlay composing `DwellArcButton`s.
- `tests/EyeCtrl.ViewModels.Tests/EdgeButtonBarViewModelTests.cs` — headless VM tests.

**Modified files (by phase):**
- Phase 1 wiring: `src/EyeCtrl.App/Services/IOverlayManager.cs`, `OverlayManager.cs`, `src/EyeCtrl.App/Services/AppBootstrapper.cs`.
- Phase 2 (scroll): `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (add `ExitScrollModeMessage`), `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs`, `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml` + `.cs`, `src/EyeCtrl.App/Services/ScrollModeController.cs`, `tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs`.
- Phase 3 (manual selection): `src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs`, `src/EyeCtrl.App/Services/EventCoordinator.cs`, `tests/EyeCtrl.ViewModels.Tests/ManualSelectionOverlayViewModelTests.cs`. **Delete:** `src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml` + `.cs`.
- Phase 4 (drag): `src/EyeCtrl.ViewModels/DragTrailViewModel.cs`, `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (rename `DragDismissZoomMessage`), `src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs`, `src/EyeCtrl.App/Services/EventCoordinator.cs`, `tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs`.

---

## Phase 0: Prerequisites

### Task 0.1: Create feature branch

Local `main` is already at `origin/main` (verified: behind=0/ahead=0; PR #56 artifacts present locally).

- [ ] **Step 1: Verify currency**

```bash
git rev-list --count main..origin/main   # expect 0
git rev-list --count origin/main..main   # expect 0
```
Expected: both print `0`. If not, run `git pull --ff-only origin main` first.

- [ ] **Step 2: Create feature branch**

```bash
git checkout -b feat/unified-edge-button-bar
```

- [ ] **Step 3: Verify the prerequisite symbols build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Build succeeded, 0 warnings.

### Task 0.2: Re-run the GitNexus index

PR #56 changed the graph substantially; the index must be current for mandatory impact analysis.

- [ ] **Step 1: Re-analyze**

```bash
node .gitnexus/run.cjs analyze
```
(If `run.cjs` is absent: `npx gitnexus analyze`.) Expected: analysis completes and reports the new symbol count.

---

## Phase 1: Add the bar (dormant)

Goal: create the bar VM, window, and types, register them in DI/`OverlayManager`, but wire to no context yet. Additive; no behavior change.

### Task 1.1: `EdgeButtonDefinition` record

**Files:**
- Create: `src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs`

- [ ] **Step 1: Create the record**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Immutable declarative definition of one edge-button. Pure data (no WPF).
/// The bar places the button at (edgeAnchor.X, edgeAnchor.Y + VerticalOffset).
/// </summary>
public sealed record EdgeButtonDefinition(
    string Id,
    string Symbol,
    int DwellMs,
    Action<IMessenger> Activate,
    double VerticalOffset);
```

- [ ] **Step 2: Build**

```bash
dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs
git commit -m "feat(edge-bar): add EdgeButtonDefinition record"
```

### Task 1.2: `EdgeButtonState` observable

**Files:**
- Create: `src/EyeCtrl.ViewModels/EdgeButtonState.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/EdgeButtonStateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.ComponentModel;

public class EdgeButtonStateTests
{
    [Fact]
    public void DwellProgress_Set_RaisesPropertyChanged()
    {
        var state = new EdgeButtonState("Drag_cancel", "\u2715");
        var fired = new List<string?>();
        ((INotifyPropertyChanged)state).PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        state.DwellProgress = 0.5;

        Check.That(fired).Contains(nameof(EdgeButtonState.DwellProgress));
        Check.That(state.DwellProgress).IsEqualTo(0.5);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~EdgeButtonStateTests"
```
Expected: FAIL — type `EdgeButtonState` not defined.

- [ ] **Step 3: Write the implementation**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Observable runtime state for one visible edge-button. The View binds to this
/// (one instance per button in <see cref="EdgeButtonBarViewModel"/>).
/// </summary>
public partial class EdgeButtonState : ObservableObject
{
    public string Id { get; }
    public string Symbol { get; }

    [ObservableProperty] private double _dwellProgress;

    public EdgeButtonState(string id, string symbol)
    {
        Id = id;
        Symbol = symbol;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~EdgeButtonStateTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/EdgeButtonState.cs tests/EyeCtrl.ViewModels.Tests/EdgeButtonStateTests.cs
git commit -m "feat(edge-bar): add EdgeButtonState observable"
```

### Task 1.3: `EdgeButtonBarViewModel` (TDD)

This is the core of the feature. `DwellOverlayViewModelBase` already owns the `DwellDetector`, gaze subscription, and disposal; this subclass adds button-set management.

**Files:**
- Create: `src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs`
- Test: `tests/EyeCtrl.ViewModels.Tests/EdgeButtonBarViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class EdgeButtonBarViewModelTests
{
    private static EyeCtrlSettings TestSettings() => new();

    private static EdgeButtonBarViewModel CreateVm(out FakeMessenger messenger)
    {
        messenger = new FakeMessenger();
        var settings = TestSettings();
        return new EdgeButtonBarViewModel(settings, messenger);
    }

    private static EdgeButtonDefinition CancelDef(double offset = 30) =>
        new("Drag_cancel", "\u2715", 50, m => m.Send(new DragCancelMessage()), offset);

    [Fact]
    public void ShowButtons_AddsStatesAndSetsVisible()
    {
        var vm = CreateVm(out _);

        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Buttons).HasSize(1);
        Check.That(vm.Buttons[0].Id).IsEqualTo("Drag_cancel");
    }

    [Fact]
    public void Hide_ClearsButtonsAndVisibility()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        vm.Hide();

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Buttons).IsEmpty();
    }

    [Fact]
    public void OnTargetActivated_InvokesActivateCallback()
    {
        var vm = CreateVm(out var messenger);
        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        vm.SimulateTargetActivated("Drag_cancel"); // test hook -> OnTargetActivated

        Check.That(messenger.Sent).HasSize(1);
        Check.That(messenger.Sent[0]).IsInstanceOf<DragCancelMessage>();
    }

    [Fact]
    public void OnTargetProgress_UpdatesButtonState()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef() }, new PhysicalPoint(1000, 500));

        vm.SimulateTargetProgress("Drag_cancel", 0.7);

        Check.That(vm.Buttons[0].DwellProgress).IsEqualTo(0.7);
    }

    [Fact]
    public void ShowButtons_SeedsTargetCenterFromAnchorPlusOffset()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef(offset: 30) },
                       new PhysicalPoint(1000, 500));

        var center = vm.GetTargetCenter("Drag_cancel"); // test hook

        Check.That(center.Y).IsEqualTo(530.0);
        Check.That(center.X).IsEqualTo(1000.0);
    }

    [Fact]
    public void AddButton_DoesNotMoveSurvivingButton_PreservesProgress()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef(offset: 30) }, new PhysicalPoint(1000, 500));
        vm.SimulateTargetProgress("Drag_cancel", 0.4);
        var cancelCenterBefore = vm.GetTargetCenter("Drag_cancel");

        var back = new EdgeButtonDefinition("Drag_back", "\u21A9", 50,
            m => { }, offset: -30);
        vm.AddButton(back);

        Check.That(vm.GetTargetCenter("Drag_cancel")).IsEqualTo(cancelCenterBefore);
        Check.That(vm.Buttons).HasSize(2);
        // cancel's progress survives
        Check.That(vm.Buttons.Single(b => b.Id == "Drag_cancel").DwellProgress).IsEqualTo(0.4);
    }

    [Fact]
    public void RemoveButton_RemovesOnlyThatButton()
    {
        var vm = CreateVm(out _);
        vm.ShowButtons(new[] { CancelDef(offset: 30) }, new PhysicalPoint(1000, 500));
        var back = new EdgeButtonDefinition("Drag_back", "\u21A9", 50, _ => { }, offset: -30);
        vm.AddButton(back);

        vm.RemoveButton("Drag_back");

        Check.That(vm.Buttons.Select(b => b.Id)).ContainsExactly("Drag_cancel");
    }

    // Minimal fake messenger that captures Send calls. Registers nothing.
    private sealed class FakeMessenger : IMessenger
    {
        public List<object> Sent { get; } = new();

        public void Send<TMessage>(TMessage message, string token) where TMessage : class
            => Sent.Add(message!);
        public void Send<TMessage>(TMessage message) where TMessage : class
            => Sent.Add(message!);

        // Everything below: throw NotImplemented — unused by these tests.
        public void RegisterAll(object recipient) => throw new NotImplementedException();
        public void RegisterAll(object recipient, string token) => throw new NotImplementedException();
        public void Register<TMessage>(object recipient, Action<TMessage> action)
            where TMessage : class => throw new NotImplementedException();
        public void Register<TMessage>(object recipient, string token, Action<TMessage> action)
            where TMessage : class => throw new NotImplementedException();
        public void UnregisterAll(object recipient) => throw new NotImplementedException();
        public void UnregisterAll(object recipient, string token) => throw new NotImplementedException();
        public void Unregister<TMessage>(object recipient) where TMessage : class => throw new NotImplementedException();
        public void Unregister<TToken, TMessage>(object recipient, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => throw new NotImplementedException();
        public void Unregister<TMessage>(object recipient, string token) where TMessage : class => throw new NotImplementedException();
        public bool IsRegistered<TMessage>(object recipient) where TMessage : class => throw new NotImplementedException();
        public bool IsRegistered<TMessage>(object recipient, string token) where TMessage : class => throw new NotImplementedException();
        public void Cleanup() => throw new NotImplementedException();
        public TMessage Send<TMessage, TToken>(TMessage message, TToken token)
            where TMessage : class where TToken : IEquatable<TToken> => throw new NotImplementedException();
    }
}
```

> **Note on the fake messenger:** CommunityToolkit's `IMessenger` interface is large. If the exact interface shape differs across the pinned version (8.4.0), adjust the `throw new NotImplementedException()` members to match `IMessenger` in `CommunityToolkit.Mvvm` — only `Send<TMessage>(TMessage)` is exercised here. Keep the fake nested and private per repo convention.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~EdgeButtonBarViewModelTests"
```
Expected: FAIL — `EdgeButtonBarViewModel` not defined.

- [ ] **Step 3: Write the implementation**

```csharp
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Context-driven right-edge button bar. Owns one DwellDetector (via the base) and a
/// declarative button set supplied per interaction context. Each button occupies a fixed
/// vertical slot (anchor.Y + VerticalOffset); buttons never move when the set changes.
/// </summary>
public partial class EdgeButtonBarViewModel : DwellOverlayViewModelBase
{
    private readonly Dictionary<string, EdgeButtonDefinition> _definitions = new();
    private readonly Dictionary<string, PhysicalPoint> _seedCenters = new();
    private PhysicalPoint _edgeAnchor;

    public ObservableCollection<EdgeButtonState> Buttons { get; } = new();

    public EdgeButtonBarViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
    }

    public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
    {
        _edgeAnchor = edgeAnchor;
        DwellDetector.ClearTargets();
        _definitions.Clear();
        _seedCenters.Clear();
        Buttons.Clear();

        foreach (var def in definitions)
            AddTarget(def);

        IsVisible = true;
    }

    /// <summary>
    /// Adds a button using the anchor cached by the last <see cref="ShowButtons"/> call.
    /// One-arg on purpose: a caller passing a *different* anchor here would misalign the new
    /// button relative to the survivors — exactly what the fixed-slot model prevents.
    /// </summary>
    public void AddButton(EdgeButtonDefinition definition)
    {
        AddTarget(definition);
    }

    public void RemoveButton(string id)
    {
        if (!_definitions.Remove(id)) return;
        _seedCenters.Remove(id);
        DwellDetector.RemoveTarget(id);
        var state = Buttons.FirstOrDefault(s => s.Id == id);
        if (state is not null) Buttons.Remove(state);
    }

    public void Hide()
    {
        DwellDetector.ClearTargets();
        _definitions.Clear();
        _seedCenters.Clear();
        Buttons.Clear();
        IsVisible = false;
    }

    // Seed centers are authoritative (see Task 1.4): the window places each button at its seed
    // center, so the detector target and the rendered button coincide with no View->VM feedback.

    protected override void OnTargetActivated(string targetId)
    {
        if (_definitions.TryGetValue(targetId, out var def))
            def.Activate(Messenger);
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var state = Buttons.FirstOrDefault(s => s.Id == targetId);
        if (state is not null)
            state.DwellProgress = progress;
    }

    private void AddTarget(EdgeButtonDefinition def)
    {
        _definitions[def.Id] = def;
        var center = _edgeAnchor + new PhysicalPoint(0, def.VerticalOffset);
        _seedCenters[def.Id] = center;
        DwellDetector.AddTarget(new DwellTarget(def.Id, center, ButtonRadiusPhysical(), def.DwellMs));
        Buttons.Add(new EdgeButtonState(def.Id, def.Symbol));
    }

    private static double ButtonRadiusPhysical()
    {
        // Match the existing edge buttons: DragButtonRadius / ManualSelection ButtonRadius (20 logical).
        return DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);
    }

    // ---- test hooks (used only by EdgeButtonBarViewModelTests) ----
    internal void SimulateTargetActivated(string id) => OnTargetActivated(id);
    internal void SimulateTargetProgress(string id, double progress) => OnTargetProgress(id, progress);
    internal PhysicalPoint GetTargetCenter(string id) => _seedCenters[id];
}
```

> **Why `_seedCenters`:** `DwellDetector` does not expose its target list publicly, so tests assert against the seed center the VM computed (anchor + offset). Because the window renders each button at this seed center (see Task 1.4), the seed center is authoritative — the detector target and the rendered button coincide with no View→VM feedback loop.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~EdgeButtonBarViewModelTests"
```
Expected: 7 passing.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs tests/EyeCtrl.ViewModels.Tests/EdgeButtonBarViewModelTests.cs
git commit -m "feat(edge-bar): add EdgeButtonBarViewModel with TDD tests"
```

### Task 1.4: `EdgeButtonBarWindow`

The window composes `DwellArcButton`s and mirrors the `ToolbarWindow` lazy-position-report pattern.

**Files:**
- Create: `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs`

- [ ] **Step 1: Run impact analysis on the pattern source**

```text
impact({target: "ToolbarWindow", direction: "upstream"})
```
Confirm LOW/MEDIUM risk for mirroring the pattern (no edit to ToolbarWindow itself).

- [ ] **Step 2: Create the XAML**

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.EdgeButtonBarWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
    AutomationProperties.AutomationId="EdgeButtonBar_Window"
    WindowStyle="None" AllowsTransparency="True" Background="Transparent"
    Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize">
    <Canvas x:Name="ButtonCanvas" />
</overlays:OverlayWindow>
```

> **No `SizeToContent`:** the window's `Left`/`Top`/`Width`/`Height` are set explicitly in `SyncButtons` from the button bounding box. `SizeToContent="WidthAndHeight"` would override manual sizing, so it is omitted.

- [ ] **Step 3: Create the code-behind**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.App.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class EdgeButtonBarWindow : OverlayWindow
{
    private const double ButtonMargin = 10;
    private const double ButtonRadiusLogical = 20; // matches UIConstants.DragButtonRadius

    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private double _originX, _originY; // window origin in logical (DIP) coords

    public EdgeButtonBarWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public EdgeButtonBarViewModel? EdgeBarVm => DataContext as EdgeButtonBarViewModel;

    // Pass-throughs so callers use the window (codebase convention — cf.
    // ManualSelectionSideButtonWindow.ShowAt) without each reaching into DataContext.
    public void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)
        => EdgeBarVm?.ShowButtons(definitions, edgeAnchor);
    public void AddButton(EdgeButtonDefinition definition) => EdgeBarVm?.AddButton(definition);
    public void RemoveButton(string id) => EdgeBarVm?.RemoveButton(id);
    public void Hide() => EdgeBarVm?.Hide();

    /// <summary>
    /// Rebuild DwellArcButtons whenever the VM's button set changes. Seed centers are
    /// authoritative — the VM computes them arithmetically and the detector targets use them —
    /// so the window places each button at its seed center with NO View->VM feedback loop.
    /// This mirrors <c>ManualSelectionSideButtonWindow.ShowAt</c> (bounding-box window +
    /// window-relative canvas coords), NOT <c>ToolbarWindow</c> (which needs feedback because
    /// a StackPanel determines its layout). The window is sized to a bounding box and each
    /// button is drawn at (seedCenter - windowOrigin) so the button's screen center equals
    /// its seed center exactly.
    /// </summary>
    public void SyncButtons()
    {
        ButtonCanvas.Children.Clear();
        _buttons.Clear();

        var vm = EdgeBarVm;
        if (vm is null) return;

        // Logical (DIP) center per button, converted from the VM's physical seed centers.
        var logical = vm.SeedCenters.ToDictionary(
            kv => kv.Key,
            kv => new Point(
                DpiHelper.PhysicalToLogicalX(kv.Value.X),
                DpiHelper.PhysicalToLogicalY(kv.Value.Y)));

        if (logical.Count == 0) return;

        // Bounding box -> explicit window rect.
        double minX = logical.Values.Min(p => p.X), maxX = logical.Values.Max(p => p.X);
        double minY = logical.Values.Min(p => p.Y), maxY = logical.Values.Max(p => p.Y);
        var r = ButtonRadiusLogical + ButtonMargin;
        _originX = minX - r; _originY = minY - r;
        Left = _originX; Top = _originY;
        Width = (maxX - minX) + 2 * r; Height = (maxY - minY) + 2 * r;

        foreach (var state in vm.Buttons)
        {
            var c = logical[state.Id];
            // Window-relative coords: subtract the window origin so the button's screen
            // center lands exactly on its seed center.
            var btn = DwellArcButton.Create(
                new Point(c.X - _originX, c.Y - _originY), ButtonRadiusLogical, state.Symbol);
            btn.UpdateProgress(state.DwellProgress);
            btn.AddTo(ButtonCanvas);
            _buttons[state.Id] = btn;

            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EdgeButtonState.DwellProgress)
                    && _buttons.TryGetValue(state.Id, out var b))
                    b.UpdateProgress(state.DwellProgress);
            };
        }
    }
}
```

- [ ] **Step 4: Expose the VM's seed centers (physical) for the window**

The window converts physical→logical itself (keeping the VM WPF-free). Add one internal accessor to `EdgeButtonBarViewModel`:

```csharp
internal IReadOnlyDictionary<string, PhysicalPoint> SeedCenters => _seedCenters;
```

(`_seedCenters` is already populated/cleared alongside `_definitions` in the Task 1.3 implementation.) The window's `SyncButtons()` is triggered by a single `Buttons.CollectionChanged` subscription wired in Task 1.5 — that covers `ShowButtons`/`AddButton`/`RemoveButton`/`Hide` automatically, so Phases 2–4 never call `SyncButtons()` explicitly.

- [ ] **Step 5: Build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Build succeeded. (The window is dormant — `SyncButtons` is not yet called; that wiring lands in Task 1.5.)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs
git commit -m "feat(edge-bar): add EdgeButtonBarWindow composing DwellArcButtons"
```

### Task 1.5: Register the bar in DI and OverlayManager (dormant)

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs`

- [ ] **Step 1: Impact analysis on OverlayManager.Initialize**

```text
impact({target: "Initialize", file_path: "src/EyeCtrl.App/Services/OverlayManager.cs", direction: "upstream"})
```

- [ ] **Step 2: Register the VM (AppBootstrapper.cs)**

Add next to the other VM singleton registrations:

```csharp
services.AddSingleton<EdgeButtonBarViewModel>();
```

- [ ] **Step 3: Add the window to IOverlayManager.cs**

```csharp
/// <summary>Gets the context-driven right-edge button bar window.</summary>
EdgeButtonBarWindow EdgeButtonBar { get; }
```

- [ ] **Step 4: Create + DataContext in OverlayManager.cs**

Add the auto-property with the others:

```csharp
public EdgeButtonBarWindow EdgeButtonBar { get; private set; } = null!;
```

In `Initialize(...)`, after the other window creations:

```csharp
EdgeButtonBar = new EdgeButtonBarWindow();
```

Set its DataContext. `OverlayViewModels` does not currently carry `EdgeButtonBarViewModel`; the simplest dormant wiring is to resolve it from the DI provider at the call site of `Initialize`. Since `OverlayManager.Initialize` receives `OverlayViewModels viewModels`, **add `EdgeButtonBar` to the `OverlayViewModels` record**:

```csharp
public record OverlayViewModels(
    CursorCircleViewModel CursorCircle,
    ActivationButtonViewModel ActivationButton,
    ManualSelectionOverlayViewModel ManualSelectionOverlay,
    ActionMenuViewModel ActionMenu,
    ToolbarViewModel Toolbar,
    KeyboardViewModel Keyboard,
    ManualSelectionGazeIndicatorViewModel ManualSelectionIndicator,
    CalibrationViewModel Calibration,
    TargetHighlightOverlayViewModel TargetHighlight,
    EdgeButtonBarViewModel EdgeButtonBar);
```

Update `AppBootstrapper.ResolveViewModels` to pass the resolved `EdgeButtonBarViewModel` (resolve from `sp`).

In `OverlayManager.Initialize`, set the DataContext and wire `SyncButtons` to the VM's button collection:

```csharp
EdgeButtonBar.DataContext = viewModels.EdgeButtonBar;
viewModels.EdgeButtonBar.Buttons.CollectionChanged += (_, _) => EdgeButtonBar.SyncButtons();
```

`ObservableCollection.CollectionChanged` fires on every `Add`/`Remove`/`Reset`, so a single subscription covers `ShowButtons`, `AddButton`, `RemoveButton`, and `Hide` (all of which mutate `Buttons`) — no per-call-site `SyncButtons()` needed in Phases 2–4.

- [ ] **Step 5: Build**

```bash
dotnet build EyeCtrl.slnx
```
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs
git commit -m "feat(edge-bar): register EdgeButtonBar in DI and OverlayManager (dormant)"
```

---

## Phase 2: Migrate scroll stop

Scroll's stop button moves from the scroll HUD to the bar. `ScrollModeController` becomes the bar driver for scroll. **New message** `ExitScrollModeMessage` is required because scroll-stop is today an `Action` event, not a message.

> **Build-green note (Tasks 2.2–2.4):** removing the stop target touches the VM (2.2), the window (2.3), and the controller (2.4) together. The signature change `EnterScrollMode(anchor, stopButtonCenter)` → `(anchor)` lands in 2.2 but its caller (`ScrollModeController`) is only updated in 2.4, so **`dotnet build EyeCtrl.slnx` will not pass between the 2.2 commit and the end of 2.4.** Each task verifies only its own unit-test slice (`dotnet test tests/EyeCtrl.ViewModels.Tests`); the full-solution build is restored and verified at the end of Task 2.4. Do not run the full build mid-phase.

### Task 2.1: Add `ExitScrollModeMessage`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`

- [ ] **Step 1: Add the record** alongside the other drag/scroll messages:

```csharp
/// <summary>Sent when the user dwells on the scroll stop button (edge bar) to exit scroll mode.</summary>
public record ExitScrollModeMessage;
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build EyeCtrl.slnx && git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs && git commit -m "feat(scroll): add ExitScrollModeMessage"
```

### Task 2.2: Remove the stop target from `ScrollOverlayViewModel`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "ScrollOverlayViewModel", direction: "upstream"})
```

- [ ] **Step 2: Update tests first (TDD)**

In `ScrollOverlayViewModelTests.cs`, remove assertions that the VM owns a stop target / raises `ExitRequested` on stop dwell / sets `StopButtonProgress`. Replace the `EnterScrollMode_SetsAnchorAndStopCenter` test with one asserting the VM no longer carries a stop center:

```csharp
[Fact]
public void EnterScrollMode_NoLongerOwnsStopButton()
{
    var vm = new ScrollOverlayViewModel(new ScrollSettings(), new WeakReferenceMessenger());

    vm.EnterScrollMode(new PhysicalPoint(100, 100));

    Check.That(vm.StopButtonCenter).IsEqualTo(new PhysicalPoint(0, 0));
}
```

Remove the `StopCenter` test constant and any `StopButtonProgress`-driven assertions. Run:

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ScrollOverlayViewModelTests"
```
Expected: FAIL (current VM still owns the stop target).

- [ ] **Step 3: Remove stop wiring from the VM**

In `ScrollOverlayViewModel.cs`:
- Remove the `StopButtonTargetId` / `StopButtonRadiusPx` constants.
- Remove the `_stopButtonCenter` field and `StopButtonCenter` property (keep a stub returning `default` only if external callers still reference it during the transition — but step 2 removes them, so delete outright).
- Remove the `ExitRequested` event and its `OnTargetActivated` raise.
- Remove `StopButtonProgress` and its `OnTargetProgress` assignment.
- In `EnterScrollMode`, drop the `stopButtonCenter` parameter and the `_dwellDetector.AddTarget(stop ...)` call. (The signature becomes `EnterScrollMode(PhysicalPoint anchor)`.)
- `OnTargetActivated` and `OnTargetProgress` become no-ops (or remove the overrides if the base allows null implementations — keep them as empty `protected override` to satisfy the abstract base).

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ScrollOverlayViewModelTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/ScrollOverlayViewModel.cs tests/EyeCtrl.ViewModels.Tests/ScrollOverlayViewModelTests.cs
git commit -m "refactor(scroll): remove stop button from ScrollOverlayViewModel"
```

### Task 2.3: Remove the stop button from `ScrollOverlayWindow`

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml`
- Modify: `src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "ScrollOverlayWindow", direction: "upstream"})
```

- [ ] **Step 2: Remove stop elements from XAML**

Delete the `StopButton`, `StopButtonProgressArc`, and `StopLabel` elements from `ScrollOverlayWindow.xaml`. Keep `AnchorCrosshairH/V`, `CursorCircle`, `AnchorDot`, `VelocityRing`.

- [ ] **Step 3: Remove stop logic from code-behind**

In `ScrollOverlayWindow.xaml.cs`:
- Delete the `_stopArc` / `_stopFigure` / `_stopGeometry` fields and their ctor wiring.
- In `Show(vm)`, delete the `stopCenter` / `StopButton` / `StopButtonProgressArc` / `StopLabel` positioning block.
- Change `UpdateFeedback(double dotOffsetX, double dotOffsetY, double velMagnitude, double stopProgress)` to drop the `stopProgress` parameter:

```csharp
public void UpdateFeedback(double dotOffsetX, double dotOffsetY, double velMagnitude)
{
    // ... existing dot + velocity ring logic only ...
}
```

- [ ] **Step 4: Build (expect compile errors in ScrollModeController — fixed in Task 2.4)**

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```
Expected: errors in `ScrollModeController.OnScrollFeedbackChanged` (passes `StopButtonProgress`) and `EnterScrollMode` (passes stop center). These are resolved next.

### Task 2.4: Drive the bar from `ScrollModeController`

**Files:**
- Modify: `src/EyeCtrl.App/Services/ScrollModeController.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "ScrollModeController", direction: "upstream"})
```

- [ ] **Step 2: Register for `ExitScrollModeMessage`, drop `ExitRequested`**

Add a constructor-level messenger registration (ScrollModeController has access to `_context.Messenger`):

```csharp
_context.Messenger.Register<ScrollModeController, ExitScrollModeMessage>(this, (_, _) =>
    _context.Dispatcher.BeginInvoke(() => ExitScrollMode()));
```

Delete the `_scrollOverlayVm.ExitRequested += OnScrollExitRequested;` subscription (in `EnterScrollMode`) and the matching `-= ` in `ExitScrollMode`. Delete `OnScrollExitRequested`.

- [ ] **Step 3: Stop computing the HUD stop center; show the bar instead**

In `EnterScrollMode()`, replace the `stopButtonCenter` computation block with:

```csharp
var edgeAnchor = ComputeScrollEdgeAnchor();
var scrollStopDwell = _context.Settings.Dwell.ScrollStopButtonDwellMs;
var stopDef = new EdgeButtonDefinition(
    "scroll-stop", "\u23F9", scrollStopDwell,
    m => m.Send(new ExitScrollModeMessage()),
    VerticalOffset: 0);
_context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { stopDef }, edgeAnchor);
```

Add the anchor helper (mirrors today's `stopButtonCenter` formula but returns the edge anchor — X at right edge, Y at screen center). Note this uses `Screen.PrimaryScreen` (WinForms) to match scroll's existing convention; the drag/manual-selection helpers in Phases 3–4 use `SystemParameters` (WPF) to match *their* existing convention. Both resolve to physical px correctly — do not "unify" one to the other.

```csharp
// WinForms screen bounds (scroll's existing convention) — see note above.
private static PhysicalPoint ComputeScrollEdgeAnchor()
{
    var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
    return new PhysicalPoint(
        bounds.Width - DpiHelper.LogicalToPhysicalX(50),
        bounds.Height / 2.0);
}
```

- [ ] **Step 4: Drop the stopProgress argument from `OnScrollFeedbackChanged`**

```csharp
private void OnScrollFeedbackChanged()
{
    if (_scrollOverlayWindow == null || _scrollOverlayVm == null) return;
    _scrollOverlayWindow.UpdateFeedback(
        _scrollOverlayVm.AnchorDotOffsetX,
        _scrollOverlayVm.AnchorDotOffsetY,
        _scrollOverlayVm.VelocityMagnitude);
}
```

- [ ] **Step 5: Hide the bar on exit**

In `ExitScrollMode()`, before existing teardown:

```csharp
_context.OverlayManager.EdgeButtonBar.Hide();
```

- [ ] **Step 6: Build + test**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ScrollOverlayViewModelTests"
dotnet test tests/EyeCtrl.App.Tests
```
Expected: build clean, tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml src/EyeCtrl.App/Overlays/ScrollOverlayWindow.xaml.cs src/EyeCtrl.App/Services/ScrollModeController.cs
git commit -m "refactor(scroll): drive stop button via edge bar; introduce ExitScrollModeMessage"
```

---

## Phase 3: Migrate manual-selection close/recapture

Manual-selection's close/recapture move from `ManualSelectionSideButtonWindow` to the bar, relocated to the right edge in both `AtCenter` and `AtGaze` modes.

### Task 3.1: Remove close/recapture targets from `ManualSelectionOverlayViewModel`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ManualSelectionOverlayViewModelTests.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "ManualSelectionOverlayViewModel", direction: "upstream"})
```

- [ ] **Step 2: Update tests first**

Remove `CloseButtonCenter` / `RecaptureButtonCenter` / `CloseButtonProgress` / `RecaptureButtonProgress` assertions and the right-of-magnifier positioning tests (`CloseButtonCenter.X > 960` etc.). Add a test confirming the VM no longer owns these:

```csharp
[Fact]
public void Receive_StartManualSelection_NoLongerComputesSideButtonCenters()
{
    var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
    vm.Receive(new StartManualSelectionMessage(new PhysicalPoint(960, 540), 150, 2.0, new PhysicalPoint(960, 540)));

    // Side buttons are now the edge bar's responsibility.
    Check.That(vm.CloseButtonCenter).IsEqualTo(default(PhysicalPoint));
}
```

Run; expect FAIL.

- [ ] **Step 3: Remove side-button wiring from the VM**

Delete from `ManualSelectionOverlayViewModel.cs`:
- `CloseButtonCenter` / `RecaptureButtonCenter` properties (or stub to `default` if references remain during transition — Task 3.3 deletes the consumer).
- `CloseButtonProgress` / `RecaptureButtonProgress` observable properties.
- The `buttonX` / `topButtonY` / `bottomButtonY` computation in `Receive(StartManualSelectionMessage)`.
- The `CloseTargetId` / `RecaptureTargetId` `DwellTarget` adds and their `OnTargetActivated`/`OnTargetProgress` handling (keep the magnifier logic).

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ManualSelectionOverlayViewModelTests"
git add src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs tests/EyeCtrl.ViewModels.Tests/ManualSelectionOverlayViewModelTests.cs
git commit -m "refactor(manual-selection): remove close/recapture from VM (move to edge bar)"
```

### Task 3.2: Drive the bar from `EventCoordinator` for manual selection

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "OnManualSelectionSideButtonsPropertyChanged", file_path: "src/EyeCtrl.App/Services/EventCoordinator.cs", direction: "upstream"})
```

- [ ] **Step 2: Replace side-button-window wiring with bar ShowButtons/Hide**

In the `ManualSelectionOverlayViewModel.IsVisible` branch of `OnManualSelectionSideButtonsPropertyChanged` (rename the handler to `OnManualSelectionVisibilityChanged`), replace `ManualSelectionSideButtons.ShowAt(...)` / `.Hide()` with:

```csharp
if (_context.ManualSelectionVM.IsVisible)
{
    var anchor = ComputeManualSelectionEdgeAnchor();
    var dwell = _context.Settings.Dwell.ManualSelectionSideButtonDwellMs;
    var gapPhysical = DpiHelper.LogicalToPhysicalX(UIConstants.ManualSelectionSideButtonGap);
    var defs = new[]
    {
        new EdgeButtonDefinition("ManualSelection_close", "\u2715", dwell,
            m => m.Send(new ManualSelectionCloseMessage()), VerticalOffset: -gapPhysical),
        new EdgeButtonDefinition("ManualSelection_recapture", "\u21BB", dwell,
            m => m.Send(new ManualSelectionRecaptureMessage()), VerticalOffset: gapPhysical),
    };
    _context.OverlayManager.EdgeButtonBar.ShowButtons(defs, anchor);
}
else
{
    _context.OverlayManager.EdgeButtonBar.Hide();
}
```

Add the anchor helper:

```csharp
private static PhysicalPoint ComputeManualSelectionEdgeAnchor()
{
    var w = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
    var h = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);
    return new PhysicalPoint(w - DpiHelper.LogicalToPhysicalX(30), h / 2.0);
}
```

Delete the `CloseButtonProgress` / `RecaptureButtonProgress` branches of the same handler.

- [ ] **Step 3: Build + commit**

```bash
dotnet build EyeCtrl.slnx
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "refactor(manual-selection): drive close/recapture via edge bar"
```

### Task 3.3: Delete `ManualSelectionSideButtonWindow`

**Files:**
- Delete: `src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml`
- Delete: `src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs`, `OverlayManager.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "ManualSelectionSideButtonWindow", direction: "upstream"})
```

- [ ] **Step 2: Remove from IOverlayManager / OverlayManager**

Delete the `ManualSelectionSideButtonWindow ManualSelectionSideButtons { get; }` member from `IOverlayManager`, the auto-property and `new ManualSelectionSideButtonWindow()` creation from `OverlayManager`. Confirm no remaining references.

- [ ] **Step 3: Delete the files**

```bash
git rm src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml.cs
```

- [ ] **Step 4: Build + test + commit**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.App.Tests
git add -A
git commit -m "refactor(manual-selection): delete ManualSelectionSideButtonWindow"
```

---

## Phase 4: Migrate drag cancel/back (highest risk)

Drag's cancel/back move to the bar. `DragTrailViewModel` keeps only the cursor-following release button + settle detection, and becomes a **consumer** of cancel/dismiss messages for cleanup. The `Zoom*` symbols are renamed to `ManualSelection*`.

### Task 4.1: Rename `Zoom*` → `ManualSelection*`

Use the graph-aware rename tool so all call sites update.

- [ ] **Step 1: Rename `DragDismissZoomMessage`**

```text
rename({symbol_name: "DragDismissZoomMessage", new_name: "DragDismissManualSelectionMessage", dry_run: false})
```

- [ ] **Step 2: Rename the enum value `DragSubState.ZoomActive`**

```text
rename({symbol_name: "ZoomActive", new_name: "ManualSelectionActive", file_path: "src/EyeCtrl.ViewModels/DragTrailViewModel.cs", dry_run: false})
```

- [ ] **Step 3: Rename `DismissZoom`**

```text
rename({symbol_name: "DismissZoom", new_name: "DismissManualSelection", file_path: "src/EyeCtrl.ViewModels/DragTrailViewModel.cs", dry_run: false})
```

- [ ] **Step 3b: Rename `ScrollModeController.OnDismissZoom`**

`ScrollModeController` also carries a `Zoom`-named method (`public void OnDismissZoom()` — its class doc reads "entering, exiting, zoom dismissal"). For terminology consistency, rename it too:

```text
rename({symbol_name: "OnDismissZoom", new_name: "OnDismissManualSelection", file_path: "src/EyeCtrl.App/Services/ScrollModeController.cs", dry_run: false})
```

- [ ] **Step 4: Build + run tests (expect failures in DragTrailViewModelTests — fixed in 4.2)**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~DragTrailViewModelTests"
```

- [ ] **Step 5: Commit the rename**

```bash
git add -A
git commit -m "refactor(drag): rename Zoom* symbols to ManualSelection*"
```

### Task 4.2: Refactor `DragTrailViewModel` — keep only release; consume cancel/dismiss

**Files:**
- Modify: `src/EyeCtrl.ViewModels/DragTrailViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "DragTrailViewModel", direction: "upstream"})
```

- [ ] **Step 2: Update tests first (TDD)**

Remove cancel/back dwell tests (the bar owns those now). Add tests asserting `DragTrailViewModel` cleans up on receiving the messages:

```csharp
[Fact]
public void Receive_DragCancelMessage_RunsCleanup()
{
    var vm = CreateVm(out var messenger);
    vm.Receive(new StartDragTrailMessage(
        new PhysicalPoint(100, 100), new PhysicalPoint(0,0), new PhysicalPoint(0,0)));

    messenger.Send(new DragCancelMessage());

    Check.That(vm.IsDragActive).IsFalse();
}

[Fact]
public void Receive_DragDismissManualSelectionMessage_RunsCleanup()
{
    var vm = CreateVm(out var messenger);
    vm.Receive(new StartDragTrailMessage(
        new PhysicalPoint(100, 100), new PhysicalPoint(0,0), new PhysicalPoint(0,0)));

    // (drive settle so ManualSelectionActive engages, then)
    messenger.Send(new DragDismissManualSelectionMessage());

    Check.That(vm.DragState).IsEqualTo(DragSubState.FreeDrag);
}
```

(Adjust the existing `CreateVm` helper to match the renamed ctor. The `StartDragTrailMessage` cancel/back centers become unused — see Task 4.4.)

Run; expect FAIL (VM still owns cancel/back and still sends rather than consumes).

- [ ] **Step 3: Refactor the VM**

In `DragTrailViewModel.cs`:
- Change the class declaration to also receive the cancel/dismiss messages:

```csharp
public partial class DragTrailViewModel : DwellOverlayViewModelBase,
    IRecipient<StartDragTrailMessage>,
    IRecipient<DragCancelMessage>,
    IRecipient<DragDismissManualSelectionMessage>
```

- Add the consumers (cleanup only — no message send):

```csharp
public void Receive(DragCancelMessage _)
{
    IsDragActive = false;
    _settleDetector.Reset();
    DwellDetector.ClearTargets();
    DragState = DragSubState.FreeDrag;
}

public void Receive(DragDismissManualSelectionMessage _)
{
    DragState = DragSubState.FreeDrag;
    ShowBack = false;
    SettleProgress = 0;
    _settleDetector.Reset();
    SetupDwellTargets(); // release-only (see below)
}
```

- In `SetupDwellTargets(bool includeBack = false)`, **remove** the `CancelTargetId` and `BackTargetId` adds. Keep only the release target. Drop the `includeBack` parameter entirely:

```csharp
private void SetupDwellTargets()
{
    DwellDetector.ClearTargets();
    var btnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);
    DwellDetector.AddTarget(new DwellTarget(
        ReleaseTargetId, ReleaseButtonCenter, btnRadius,
        _settings.Dwell.DragReleaseButtonDwellMs));
}
```

- In `OnSettleStarted`, drop the `SetupDwellTargets(includeBack: true)` call (the bar handles the back button); keep the `DragState = DragSubState.ManualSelectionActive; ShowBack = true;` so the App layer can react to `ShowBack` (Task 4.4 wires `AddButton`/`RemoveButton` off `ShowBack`).
- Delete `CancelDrag()` and `DismissManualSelection()` (their cleanup bodies move into the consumers above). Delete `CancelTargetId` / `BackTargetId` constants and `CancelButtonCenter` / `BackButtonCenter` properties.
- `OnTargetActivated` now only handles `ReleaseTargetId`:

```csharp
protected override void OnTargetActivated(string targetId)
{
    if (targetId == ReleaseTargetId)
        CompleteDrag(CursorPosition);
}
```

- `OnTargetProgress` stays empty (release has no progress visual; the trail's settle ring is driven separately).

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~DragTrailViewModelTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/DragTrailViewModel.cs tests/EyeCtrl.ViewModels.Tests/DragTrailViewModelTests.cs
git commit -m "refactor(drag): DragTrailViewModel keeps only release; consumes cancel/dismiss"
```

### Task 4.3: Remove cancel/back rendering from `DragTrailOverlay`

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs`

- [ ] **Step 1: Impact analysis**

```text
impact({target: "DragTrailOverlay", direction: "upstream"})
```

- [ ] **Step 2: Remove cancel/back/release-button visuals that are now edge-bar-owned**

The release (✋) cursor-following button **stays** (it is not an edge button). Remove only `_cancelIcon`, `_cancelBg`, `_backIcon`, `_backBg` fields, their construction in the ctor, their `Add`s to `TrailCanvas`, and their positioning in `Update(...)`. Remove the `cancelPos` / `backPos` / `showBack` parameters from `Update(...)` (and the release-pos param stays). The `ReleaseButtonCenter` continues to be passed by the caller for the cursor-following release icon.

Revised `Update` signature:

```csharp
public void Update(PhysicalPoint grabOrigin, PhysicalPoint cursorPos, double settleProgress,
    PhysicalPoint releasePos)
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build EyeCtrl.slnx
git add src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs
git commit -m "refactor(drag): remove cancel/back from DragTrailOverlay (now on edge bar)"
```

### Task 4.4: Drive the bar from `EventCoordinator` for drag

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` (trim `StartDragTrailMessage`)

- [ ] **Step 1: Impact analysis**

```text
impact({target: "OnDragStarted", file_path: "src/EyeCtrl.App/Services/EventCoordinator.cs", direction: "upstream"})
```

- [ ] **Step 2: Simplify `StartDragTrailMessage`**

The cancel/back centers are now derived from the bar's anchor, so drop them:

```csharp
public record StartDragTrailMessage(PhysicalPoint GrabOrigin);
```

Update the single sender (`EventCoordinator.OnDragStarted`) and the receiver (`DragTrailViewModel.Receive(StartDragTrailMessage)` — drop the `CancelButtonCenter`/`BackButtonCenter` assignments, already removed in Task 4.2).

- [ ] **Step 3: Show the bar's cancel button on drag start**

In `OnDragStarted(PhysicalPoint grabOrigin)`, after `new StartDragTrailMessage(grabOrigin)`:

```csharp
var anchor = ComputeDragEdgeAnchor();
var cancelDef = new EdgeButtonDefinition(
    "Drag_cancel", "\u2715", _context.Settings.Dwell.DragRightEdgeButtonDwellMs,
    m => m.Send(new DragCancelMessage()),
    VerticalOffset: DpiHelper.LogicalToPhysicalY(30));
_context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { cancelDef }, anchor);
```

Replace the old `ComputeDragEdgeButtonCenters()` helper with an anchor helper:

```csharp
private static PhysicalPoint ComputeDragEdgeAnchor()
{
    var w = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
    var h = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);
    return new PhysicalPoint(w - DpiHelper.LogicalToPhysicalX(30), h / 2.0);
}
```

- [ ] **Step 4: Add/remove the back button on `ShowBack`**

Subscribe to `DragTrailVM.PropertyChanged` (the existing `OnDragTrailVMPropertyChanged` handler) for `ShowBack`:

```csharp
case nameof(DragTrailViewModel.ShowBack):
    var bar = _context.OverlayManager.EdgeButtonBar;
    if (_context.DragTrailVM.ShowBack)
    {
        var backDef = new EdgeButtonDefinition(
            "Drag_back", "\u21A9", _context.Settings.Dwell.DragRightEdgeButtonDwellMs,
            m => m.Send(new DragDismissManualSelectionMessage()),
            VerticalOffset: -DpiHelper.LogicalToPhysicalY(30));
        bar.AddButton(backDef);
    }
    else
    {
        bar.RemoveButton("Drag_back");
    }
    break;
```

- [ ] **Step 5: Hide the bar on drag end / cancel**

`OnDragDrop`, `OnDragCancel`, and `OnDragDismissManualSelection` each call `_context.OverlayManager.EdgeButtonBar.Hide();` at the top. Update the `DragTrailOverlay.Update(...)` call site to the trimmed signature (drop cancel/back/release-pos args that were removed in Task 4.3, keeping release).

- [ ] **Step 6: Build + full headless suite**

```bash
dotnet build EyeCtrl.slnx
dotnet test tests/EyeCtrl.ViewModels.Tests tests/EyeCtrl.App.Tests tests/EyeCtrl.Core.Tests
```
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git commit -m "refactor(drag): drive cancel/back via edge bar; simplify StartDragTrailMessage"
```

---

## Phase 5: Cleanup

### Task 5.1: Final verification, dead-code sweep, index refresh

- [ ] **Step 1: Full build + full headless suite**

```bash
dotnet build EyeCtrl.slnx
dotnet test
```
Expected: build 0 warnings; all non-UI tests pass.

- [ ] **Step 2: detect_changes against main**

```text
detect_changes({scope: "compare", base_ref: "main"})
```
Review affected symbols/processes — confirm only expected overlay/VM/scroll flows changed.

- [ ] **Step 3: Re-run GitNexus index**

```bash
node .gitnexus/run.cjs analyze
```

- [ ] **Step 4: Final commit (if any cleanup)**

```bash
git add -A
git commit -m "chore(edge-bar): cleanup and index refresh" || echo "nothing to commit"
```

---

## Self-Review (completed by plan author)

**1. Spec coverage:**
- Single right-edge bar VM+window → Phase 1. ✓
- Built on `DwellArcButton` + `DwellOverlayViewModelBase` → Task 1.3/1.4. ✓
- Per-button `VerticalOffset` / fixed slots → `EdgeButtonDefinition`, `AddTarget`. ✓
- Scroll stop migration + new `ExitScrollModeMessage` → Phase 2. ✓
- Manual-selection relocation + delete side-button window → Phase 3. ✓
- Drag inversion + Zoom→ManualSelection rename → Phase 4. ✓
- Scroll HUD keeps crosshair/velocity → Task 2.3 explicitly preserves them. ✓
- Always-on ToolbarWindow untouched → not mentioned in any task. ✓
- Tests incl. `AddButton_DoesNotMoveSurvivingButton_PreservesProgress` → Task 1.3. ✓

**2. Placeholder scan:** one explicit "use option (b)" implementation note in Task 1.3 with the full code provided inline — not a placeholder, a documented choice. No TBD/TODO. The `FakeMessenger` note flags an interface-shape adaptation risk with concrete guidance.

**3. Type consistency:** `EdgeButtonDefinition(Symbol, DwellMs, Activate, VerticalOffset)` used identically in Phase 1 definition, Phase 2 (scroll-stop), Phase 3 (close/recapture), Phase 4 (cancel/back). `ShowButtons(definitions, edgeAnchor)` (caches the anchor) and 1-arg `AddButton(definition)` (uses the cached anchor) signatures consistent throughout. `VerticalOffset` sign convention (positive = below) consistent: drag cancel +30 (below), back −30 (above), scroll 0, manual close −gap (above) / recapture +gap (below).

**Open risks flagged for the executor:**
- The window↔VM contract (`Buttons.CollectionChanged → SyncButtons`, seed centers authoritative) is the most novel wiring. If `CollectionChanged` fires during `Clear()`+`Add()` sequences in a way that causes flicker, batch with `NotifyCollectionChangedAction.Reset` or debounce `SyncButtons`.
- `FakeMessenger` in Task 1.3 may need `IMessenger` members adjusted to the pinned CommunityToolkit version (8.4.0) — only `Send<TMessage>` is exercised.
- Seed centers are authoritative only because the window places buttons at window-relative coords derived from those seeds (Task 1.4). If a future change moves the window without recomputing origins, re-verify the screen-center == seed-center invariant.
