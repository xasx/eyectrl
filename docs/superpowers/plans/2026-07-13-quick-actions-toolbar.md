# Quick Actions Toolbar — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a configurable grid of user-defined action buttons summoned via hot-edge gaze, with 6 action types (keyboard shortcuts, canned text, EXE/Store app launch, URL, media control).

**Architecture:** 4 checkpoints: data layer → summon/grid → execution → settings. Follows `ToolbarAutoHideController` (hot-edge state machine), `TaskSwitcherViewModel` (IRecipient + DwellOverlayViewModelBase), and `OverlayManager.Initialize` (overlay registration). No changes to `InteractionManager` or `InteractionAction`.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm 8.4.0, xUnit + NFluent 3.1.0, no mocking library.

---

### Task A1: QuickAction model + QuickActionType enum

**Files:**
- Create: `src/EyeCtrl.Core/Models/QuickAction.cs`
- Create: `tests/EyeCtrl.Core.Tests/Models/QuickActionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.Core.Tests/Models/QuickActionTests.cs
namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

public class QuickActionTests
{
    [Fact]
    public void Constructor_AssignsUniqueId()
    {
        var a = new QuickAction();
        var b = new QuickAction();
        Check.That(a.Id).IsNotNull();
        Check.That(a.Id).IsNotEmpty();
        Check.That(a.Id).Not.IsEqualTo(b.Id);
    }

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var qa = new QuickAction();
        Check.That(qa.Label).IsEqualTo("");
        Check.That(qa.Payload).IsEqualTo("");
        Check.That(qa.Enabled).IsTrue();
    }

    [Fact]
    public void Properties_AreSettable()
    {
        var qa = new QuickAction
        {
            Type = QuickActionType.KeyboardShortcut,
            Label = "Copy",
            Payload = "Ctrl+C"
        };
        Check.That(qa.Type).IsEqualTo(QuickActionType.KeyboardShortcut);
        Check.That(qa.Label).IsEqualTo("Copy");
        Check.That(qa.Payload).IsEqualTo("Ctrl+C");
    }

    [Fact]
    public void QuickActionType_AllValues_AreDistinct()
    {
        var values = Enum.GetValues<QuickActionType>();
        Check.That(values).HasSize(6);
        Check.That(values.Distinct().Count()).IsEqualTo(values.Length);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~QuickActionTests"`
Expected: test project fails to compile — `QuickAction` and `QuickActionType` not found.

- [ ] **Step 3: Write the model**

```csharp
// src/EyeCtrl.Core/Models/QuickAction.cs
namespace EyeCtrl.Core.Models;

public enum QuickActionType
{
    KeyboardShortcut,
    CannedText,
    LaunchExe,
    LaunchWindowsApp,
    LaunchUrl,
    MediaControl
}

public class QuickAction
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public QuickActionType Type { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~QuickActionTests"`
Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/QuickAction.cs tests/EyeCtrl.Core.Tests/Models/QuickActionTests.cs
git commit -m "feat: add QuickAction model + QuickActionType enum"
```

---

### Task A2: QuickActionsSettings

**Files:**
- Create: `src/EyeCtrl.Core/Settings/QuickActionsSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Settings/QuickActionsSettingsDefaultsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/QuickActionsSettingsDefaultsTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class QuickActionsSettingsDefaultsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var s = new QuickActionsSettings();
        Check.That(s.Enabled).IsTrue();
        Check.That(s.SummonEdge).IsEqualTo("Left");
        Check.That(s.SummonThresholdMs).IsEqualTo(400);
        Check.That(s.DismissTimeoutMs).IsEqualTo(5000);
        Check.That(s.GridColumns).IsEqualTo(3);
    }

    [Fact]
    public void Validate_Defaults_NoErrors()
    {
        Check.That(new QuickActionsSettings().Validate()).IsEmpty();
    }

    [Fact]
    public void Validate_SummonThresholdOutOfRange_ReturnsError()
    {
        var s = new QuickActionsSettings { SummonThresholdMs = 150 };
        Check.That(s.Validate()).Not.IsEmpty();
    }

    [Fact]
    public void Validate_SummonThresholdAtBounds_NoErrors()
    {
        var s = new QuickActionsSettings();
        s.SummonThresholdMs = 200;
        Check.That(s.Validate().All(e => !e.Contains("SummonThresholdMs"))).IsTrue();
        s.SummonThresholdMs = 2000;
        Check.That(s.Validate().All(e => !e.Contains("SummonThresholdMs"))).IsTrue();
    }

    [Fact]
    public void Validate_DismissTimeoutOutOfRange_ReturnsError()
    {
        var s = new QuickActionsSettings { DismissTimeoutMs = 500 };
        Check.That(s.Validate()).Not.IsEmpty();
    }

    [Fact]
    public void Validate_GridColumnsOutOfRange_ReturnsError()
    {
        var s = new QuickActionsSettings { GridColumns = 1 };
        Check.That(s.Validate()).Not.IsEmpty();
        s.GridColumns = 6;
        Check.That(s.Validate()).Not.IsEmpty();
    }

    [Fact]
    public void Validate_InvalidSummonEdge_ReturnsError()
    {
        var s = new QuickActionsSettings { SummonEdge = "Northeast" };
        Check.That(s.Validate()).Not.IsEmpty();
    }

    [Fact]
    public void SummonEdge_ClampedToValidValue()
    {
        var s = new QuickActionsSettings { SummonEdge = "Northeast" };
        // The partial OnSummonEdgeChanging setter clamps — setting
        // an invalid value stores the previous valid value.
        Check.That(s.SummonEdge).IsEqualTo("Left");
    }
}
```

- [ ] **Step 2: Run test to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~QuickActionsSettingsDefaultsTests"`
Expected: project fails to compile.

- [ ] **Step 3: Write QuickActionsSettings**

```csharp
// src/EyeCtrl.Core/Settings/QuickActionsSettings.cs
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public sealed partial class QuickActionsSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _summonEdge = "Left";
    [ObservableProperty] private int _summonThresholdMs = 400;
    [ObservableProperty] private int _dismissTimeoutMs = 5000;
    [ObservableProperty] private int _gridColumns = 3;

    private static readonly string[] ValidEdges = { "Top", "Bottom", "Left", "Right" };

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(SummonThresholdMs, 200, 2000, nameof(SummonThresholdMs)),
            ValidationHelper.ValidateRange(DismissTimeoutMs, 1000, 15000, nameof(DismissTimeoutMs)),
            ValidationHelper.ValidateRange(GridColumns, 2, 5, nameof(GridColumns)),
            ValidationHelper.ValidateStringEnum(SummonEdge, ValidEdges, nameof(SummonEdge))
        );
    }

    partial void OnSummonThresholdMsChanging(int value) => OnSummonThresholdMsChanged(Math.Clamp(value, 200, 2000));
    partial void OnDismissTimeoutMsChanging(int value) => OnDismissTimeoutMsChanged(Math.Clamp(value, 1000, 15000));
    partial void OnGridColumnsChanging(int value) => OnGridColumnsChanged(Math.Clamp(value, 2, 5));
    partial void OnSummonEdgeChanging(string value)
    {
        if (!ValidEdges.Contains(value))
            OnSummonEdgeChanged(SummonEdge); // restore previous valid value
    }
}
```

Note: `OnXChanging` for `SummonEdge` is a bit unusual — the source generator replaces the `OnSummonEdgeChanged(SummonEdge)` call pattern used here. The generator produces a `partial void OnSummonEdgeChanging(string value)` which fires before the backing field updates. To reject an invalid value, store the old value and restore in `OnSummonEdgeChanged` if the new value is bad. Re-evaluate during Checkpoint A if the generator's actual signature differs.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~QuickActionsSettingsDefaultsTests"`
Expected: 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/QuickActionsSettings.cs tests/EyeCtrl.Core.Tests/Settings/QuickActionsSettingsDefaultsTests.cs
git commit -m "feat: add QuickActionsSettings with validation"
```

---

### Task A3: IQuickActionsStore + QuickActionsStore + wire into EyeCtrlSettings

**Files:**
- Create: `src/EyeCtrl.Core/Interfaces/IQuickActionsStore.cs`
- Create: `src/EyeCtrl.App/Services/QuickActionsStore.cs`
- Create: `tests/EyeCtrl.App.Tests/Services/QuickActionsStoreTests.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`

- [ ] **Step 1: Write IQuickActionsStore**

```csharp
// src/EyeCtrl.Core/Interfaces/IQuickActionsStore.cs
namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

public interface IQuickActionsStore
{
    IReadOnlyList<QuickAction> LoadAll();
    void Save(IReadOnlyList<QuickAction> actions);
    QuickAction? FindById(string id);
}
```

- [ ] **Step 2: Write failing QuickActionsStore test**

```csharp
// tests/EyeCtrl.App.Tests/Services/QuickActionsStoreTests.cs
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

public class QuickActionsStoreTests : IDisposable
{
    private readonly string _tempFile;

    public QuickActionsStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"eyectrl_test_actions_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    private QuickActionsStore CreateStore() => new(_tempFile);

    [Fact]
    public void LoadAll_NewFile_ReturnsEmpty()
    {
        var store = CreateStore();
        Check.That(store.LoadAll()).IsEmpty();
    }

    [Fact]
    public void SaveAndLoadAll_RoundTrip()
    {
        var store = CreateStore();
        var actions = new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" },
            new() { Label = "Gmail", Type = QuickActionType.LaunchUrl, Payload = "https://mail.google.com" }
        };
        store.Save(actions);
        var loaded = store.LoadAll();
        Check.That(loaded).HasSize(2);
        Check.That(loaded[0].Label).IsEqualTo("Copy");
        Check.That(loaded[1].Payload).IsEqualTo("https://mail.google.com");
    }

    [Fact]
    public void FindById_ExistingId_ReturnsAction()
    {
        var store = CreateStore();
        var action = new QuickAction { Label = "Test" };
        store.Save(new[] { action });
        var found = store.FindById(action.Id);
        Check.That(found).IsNotNull();
        Check.That(found!.Label).IsEqualTo("Test");
    }

    [Fact]
    public void FindById_UnknownId_ReturnsNull()
    {
        var store = CreateStore();
        store.Save(new[] { new QuickAction { Label = "Test" } });
        Check.That(store.FindById("nonexistent")).IsNull();
    }

    [Fact]
    public void LoadAll_CorruptedFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "not valid json");
        var store = CreateStore();
        Check.That(store.LoadAll()).IsEmpty();
    }
}
```

- [ ] **Step 3: Run test to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~QuickActionsStoreTests"`
Expected: fails — `QuickActionsStore` not found.

- [ ] **Step 4: Write QuickActionsStore**

```csharp
// src/EyeCtrl.App/Services/QuickActionsStore.cs
namespace EyeCtrl.App.Services;

using System.Text.Json;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class QuickActionsStore : IQuickActionsStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record ActionsWrapper(List<QuickAction> Actions);

    public QuickActionsStore(string filePath)
    {
        _filePath = filePath;
    }

    public IReadOnlyList<QuickAction> LoadAll()
    {
        try
        {
            if (!File.Exists(_filePath)) return Array.Empty<QuickAction>();
            var json = File.ReadAllText(_filePath);
            var wrapper = JsonSerializer.Deserialize<ActionsWrapper>(json, JsonOptions);
            return wrapper?.Actions ?? Array.Empty<QuickAction>();
        }
        catch
        {
            return Array.Empty<QuickAction>();
        }
    }

    public void Save(IReadOnlyList<QuickAction> actions)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var wrapper = new ActionsWrapper(actions.ToList());
        var json = JsonSerializer.Serialize(wrapper, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    public QuickAction? FindById(string id)
    {
        return LoadAll().FirstOrDefault(a => a.Id == id);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~QuickActionsStoreTests"`
Expected: 5 tests PASS.

- [ ] **Step 6: Wire QuickActionsSettings into EyeCtrlSettings**

Open `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`. Add the property after the existing `_trackStatus` field (line 44):

```csharp
    /// <summary>Quick-actions toolbar settings.</summary>
    [ObservableProperty] private QuickActionsSettings _quickActions = new();
```

Add to `EnumerateSubSettings()` after the `TrackStatus` yield (line 68):

```csharp
        yield return (nameof(QuickActions), QuickActions);
```

- [ ] **Step 7: Update enumeration test count**

Open `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs`, line 12. Change `HasSize(16)` to `HasSize(17)`:

```csharp
        Check.That(subs).HasSize(17);
```

- [ ] **Step 8: Run enumeration tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EyeCtrlSettingsEnumerationTests"`
Expected: 3 tests PASS (size 17, covers every validatable property, default validates).

- [ ] **Step 9: Wire into AppBootstrapper DI**

Open `src/EyeCtrl.App/Services/AppBootstrapper.cs`. After line 74 (`services.AddSingleton<IAppIconProvider, Win32AppIconProvider>();`), add:

```csharp
        // Quick Actions
        services.AddSingleton<IQuickActionsStore>(sp =>
        {
            var path = Path.Combine(appDataDir, "actions.json");
            return new QuickActionsStore(path);
        });
```

- [ ] **Step 10: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IQuickActionsStore.cs src/EyeCtrl.App/Services/QuickActionsStore.cs tests/EyeCtrl.App.Tests/Services/QuickActionsStoreTests.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "feat: add IQuickActionsStore + QuickActionsStore + wire into settings/DI"
```

**Checkpoint A complete.** Run `dotnet test` to verify all existing tests still pass.

---

### Task B1: Messages + QuickActionTileViewModel

**Files:**
- Create: `src/EyeCtrl.ViewModels/Messages/QuickActionsMessages.cs`
- Create: `src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs`

- [ ] **Step 1: Write messages**

```csharp
// src/EyeCtrl.ViewModels/Messages/QuickActionsMessages.cs
namespace EyeCtrl.ViewModels.Messages;

using CommunityToolkit.Mvvm.Messaging.Messages;
using EyeCtrl.Core.Models;

public sealed class QuickActionsToggledMessage;
public sealed class QuickActionSelectionMessage(QuickAction action) : ValueChangedMessage<QuickAction>(action);
```

- [ ] **Step 2: Write QuickActionTileViewModel**

```csharp
// src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class QuickActionTileViewModel : ObservableObject
{
    [ObservableProperty] private double _dwellProgress;

    public QuickAction Action { get; }

    public QuickActionTileViewModel(QuickAction action)
    {
        Action = action;
    }
}
```

- [ ] **Step 3: Build and verify no compile errors**

Run: `dotnet build src/EyeCtrl.ViewModels --no-restore`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/QuickActionsMessages.cs src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs
git commit -m "feat: add QuickActions messages + QuickActionTileViewModel"
```

---

### Task B2: QuickActionsGridViewModel + tests

**Files:**
- Create: `src/EyeCtrl.ViewModels/QuickActionsGridViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/QuickActionsGridViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.ViewModels.Tests/QuickActionsGridViewModelTests.cs
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public class QuickActionsGridViewModelTests
{
    public class FakeQuickActionsStore : IQuickActionsStore
    {
        private List<QuickAction> _actions = new();
        public FakeQuickActionsStore(List<QuickAction> actions) => _actions = actions;
        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions) => _actions = actions.ToList();
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    private static QuickActionsGridViewModel CreateVM(List<QuickAction>? actions = null)
    {
        var store = new FakeQuickActionsStore(actions ?? new List<QuickAction>());
        var settings = new EyeCtrlSettings();
        return new QuickActionsGridViewModel(store, settings, WeakReferenceMessenger.Default);
    }

    [Fact]
    public void Constructor_IsNotVisible_AndTilesEmpty()
    {
        var vm = CreateVM();
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles).IsEmpty();
    }

    [Fact]
    public void Receive_ToggleWhenHidden_BecomesVisible()
    {
        var vm = CreateVM(new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" }
        });
        vm.Receive(new QuickActionsToggledMessage());
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles).HasSize(1);
    }

    [Fact]
    public void Receive_ToggleWhenVisible_HidesOverlay()
    {
        var vm = CreateVM();
        vm.Receive(new QuickActionsToggledMessage()); // show
        vm.Receive(new QuickActionsToggledMessage()); // hide
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Reload_OnlyIncludesEnabledActions()
    {
        var actions = new List<QuickAction>
        {
            new() { Label = "A", Type = QuickActionType.CannedText, Payload = "hello", Enabled = true },
            new() { Label = "B", Type = QuickActionType.MediaControl, Payload = "PlayPause", Enabled = false },
        };
        var vm = CreateVM(actions);
        vm.Receive(new QuickActionsToggledMessage());
        Check.That(vm.Tiles).HasSize(1);
        Check.That(vm.Tiles[0].Action.Label).IsEqualTo("A");
    }

    [Fact]
    public void OnTargetActivated_SendsSelectionMessage()
    {
        var action = new QuickAction { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" };
        var vm = CreateVM(new List<QuickAction> { action });
        var messenger = WeakReferenceMessenger.Default;
        QuickActionSelectionMessage? received = null;
        messenger.Register<QuickActionSelectionMessage>(this, (r, m) => received = m);

        vm.Receive(new QuickActionsToggledMessage());
        // Simulate dwell detector firing OnTargetActivated (base class calls it)
        // We call it directly via the protected method — use reflection or a test-accessor
        var method = typeof(QuickActionsGridViewModel).GetMethod("OnTargetActivated",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(vm, new object[] { action.Id });

        Check.That(received).IsNotNull();
        Check.That(received!.Value).IsSameReferenceAs(action);
        Check.That(vm.IsVisible).IsFalse();
    }
}
```

Note: the `OnTargetActivated` test uses reflection because the method is `protected override`. If InternalsVisibleTo is not configured from ViewModels to its test project, add `[assembly: InternalsVisibleTo("EyeCtrl.ViewModels.Tests")]` or expose a `SimulateTargetActivated(string targetId)` test hook on the VM (add it in Step 2).

- [ ] **Step 2: Write QuickActionsGridViewModel**

```csharp
// src/EyeCtrl.ViewModels/QuickActionsGridViewModel.cs
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class QuickActionsGridViewModel : DwellOverlayViewModelBase,
    IRecipient<QuickActionsToggledMessage>
{
    private readonly IQuickActionsStore _store;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;

    public ObservableCollection<QuickActionTileViewModel> Tiles { get; } = new();
    public int GridColumns => _settings.QuickActions.GridColumns;
    public int RowCount => Tiles.Count == 0 ? 0 : (int)Math.Ceiling((double)Tiles.Count / _settings.QuickActions.GridColumns);

    public QuickActionsGridViewModel(
        IQuickActionsStore store,
        EyeCtrlSettings settings,
        IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _store = store;
        _settings = settings;
        _messenger = messenger;
    }

    public void Receive(QuickActionsToggledMessage message)
    {
        if (IsVisible) { HideOverlay(); return; }
        Reload();
        IsVisible = true;
    }

    public void HideOverlay()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
        Tiles.Clear();
    }

    public void Reload()
    {
        Tiles.Clear();
        DwellDetector.ClearTargets();
        var actions = _store.LoadAll().Where(a => a.Enabled).ToList();
        foreach (var action in actions)
        {
            Tiles.Add(new QuickActionTileViewModel(action));
        }
        // Dwell targets are NOT created here. The View calls UpdateTilePosition()
        // after layout with each tile's real screen center, which creates the target
        // at its true position (matching TaskSwitcherViewModel pattern).
    }

    /// <summary>
    /// Called by the View after layout. Creates the dwell target at the tile's
    /// real screen position. Reuses ToolbarButtonDwellMs (700 ms default).
    /// </summary>
    public void UpdateTilePosition(string tileId, PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            tileId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));
    }

    protected override void OnTargetActivated(string targetId)
    {
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null)
        {
            _messenger.Send(new QuickActionSelectionMessage(tile.Action));
        }
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null)
            tile.DwellProgress = progress;
    }

    // Test-only hook — same pattern as ToolbarViewModel.SimulateButtonActivated.
    // Exposed so tests can trigger activation without dwelling the DwellDetector.
    public void SimulateTargetActivated(string targetId) => OnTargetActivated(targetId);
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~QuickActionsGridViewModelTests"`
Expected: 4 tests PASS (update the OnTargetActivated test to use `vm.SimulateTargetActivated(action.Id)` instead of reflection).

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/QuickActionsGridViewModel.cs tests/EyeCtrl.ViewModels.Tests/QuickActionsGridViewModelTests.cs
git commit -m "feat: add QuickActionsGridViewModel with toggle + tile management"
```

---

### Task B3: QuickActionsSummonController + tests

**Files:**
- Create: `src/EyeCtrl.App/Services/QuickActionsSummonController.cs`
- Create: `tests/EyeCtrl.App.Tests/Services/QuickActionsSummonControllerTests.cs`

- [ ] **Step 1: Write failing test skeleton**

```csharp
// tests/EyeCtrl.App.Tests/Services/QuickActionsSummonControllerTests.cs
namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

public class QuickActionsSummonControllerTests
{
    private QuickActionsSummonController CreateController(
        string edge = "Left", double screenW = 1920, double screenH = 1080,
        int summonMs = 400, int dismissMs = 5000, Func<bool>? calibrationActive = null)
    {
        return new QuickActionsSummonController(
            edge, screenW, screenH, summonMs, dismissMs,
            calibrationActive: calibrationActive ?? (() => false));
    }

    [Fact]
    public void Constructor_StateIsIdle()
    {
        var c = CreateController();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_ValidGazePastLeftEdge_SummonsAfterThreshold()
    {
        var c = CreateController(edge: "Left", screenW: 1920, summonMs: 400);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        // Gaze at (-10, 540) = past left edge, valid
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 0, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(toggles).IsNull();

        // Enough time passes
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 500_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(toggles).IsEqualTo(1);
    }

    [Fact]
    public void Feed_InvalidGaze_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 0, isValid: false);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 500_000, isValid: false);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(toggles).IsNull();
    }

    [Fact]
    public void Feed_GazeInsideScreen_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        c.Feed(new PhysicalPoint(100, 540), timestampUs: 0, isValid: true);
        c.Feed(new PhysicalPoint(100, 540), timestampUs: 500_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_GazePastWrongEdge_DoesNotSummon()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        // Past right edge
        c.Feed(new PhysicalPoint(1930, 540), timestampUs: 0, isValid: true);
        c.Feed(new PhysicalPoint(1930, 540), timestampUs: 500_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_DismissTimeout_FiresAfterGazeAway()
    {
        var c = CreateController(edge: "Left", summonMs: 400, dismissMs: 5000);
        int? toggles = null;
        c.SummonRequested += () => toggles = (toggles ?? 0) + 1;

        // Summon
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 0, isValid: true);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 500_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        // Gaze returns inside
        c.Feed(new PhysicalPoint(100, 540), timestampUs: 600_000, isValid: true);
        c.Feed(new PhysicalPoint(100, 540), timestampUs: 6_000_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void Feed_BlinkDuringSummonRun_ResetsRun()
    {
        var c = CreateController(edge: "Left", summonMs: 400);

        // Start summon run
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 0, isValid: true);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 200_000, isValid: true);

        // Blink (invalid)
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 250_000, isValid: false);

        // Resume — run should restart, not continue
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 300_000, isValid: true);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 500_000, isValid: true);
        // Only 200ms accumulated after blink, not enough for 400ms threshold
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void SetSuspended_PreventsSummonAndForcesIdle()
    {
        var c = CreateController(edge: "Left", summonMs: 400);
        // Summon first
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 0, isValid: true);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 500_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);

        c.SetSuspended(true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);

        // Should not re-summon while suspended
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 600_000, isValid: true);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 1_100_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void CalibrationActive_Gate_PreventsSummon()
    {
        bool calibrationActive = true;
        var c = CreateController(calibrationActive: () => calibrationActive);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 0, isValid: true);
        c.Feed(new PhysicalPoint(-10, 540), timestampUs: 500_000, isValid: true);
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }

    [Fact]
    public void EnterSummoned_SetsState()
    {
        var c = CreateController();
        c.EnterSummoned();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
    }

    [Fact]
    public void ResetToIdle_SetsState()
    {
        var c = CreateController();
        c.EnterSummoned();
        c.ResetToIdle();
        Check.That(c.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
    }
}
```

- [ ] **Step 2: Write QuickActionsSummonController**

```csharp
// src/EyeCtrl.App/Services/QuickActionsSummonController.cs
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

public sealed class QuickActionsSummonController
{
    public enum SummonState { Idle, Summoned }

    private string _edgeKey;
    private readonly double _screenW;
    private readonly double _screenH;
    private readonly int _summonThresholdUs;
    private readonly int _dismissTimeoutUs;
    private readonly Func<bool>? _calibrationActive;

    private int _state = (int)SummonState.Idle;
    private long _runStartUs = long.MinValue;
    private long _dismissStartUs = long.MinValue;
    private bool _suspended;

    public SummonState State => (SummonState)Volatile.Read(ref _state);

    public event Action? SummonRequested;

    public QuickActionsSummonController(
        string edge,
        double screenW,
        double screenH,
        int summonThresholdMs,
        int dismissTimeoutMs,
        Func<bool>? calibrationActive = null)
    {
        _edgeKey = edge.ToLowerInvariant();
        _screenW = screenW;
        _screenH = screenH;
        _summonThresholdUs = summonThresholdMs * 1000;
        _dismissTimeoutUs = dismissTimeoutMs * 1000;
        _calibrationActive = calibrationActive;
    }

    public void Feed(PhysicalPoint gazePoint, long timestampUs, bool isValid)
    {
        if (_suspended) return;

        var pastEdge = IsPastEdge(gazePoint);
        var shouldSuppress = (_calibrationActive?.Invoke() ?? false) || !isValid;

        if (State == SummonState.Idle)
        {
            if (pastEdge && !shouldSuppress)
            {
                if (_runStartUs == long.MinValue)
                    _runStartUs = timestampUs;

                if (timestampUs - _runStartUs >= _summonThresholdUs)
                {
                    Volatile.Write(ref _state, (int)SummonState.Summoned);
                    _runStartUs = long.MinValue;
                    _dismissStartUs = long.MinValue;
                    SummonRequested?.Invoke();
                }
            }
            else
            {
                _runStartUs = long.MinValue;
            }
        }
        else // Summoned
        {
            if (shouldSuppress)
            {
                ResetToIdle();
                return;
            }

            // If gaze is inside screen (not past edge), start dismiss timer
            if (!pastEdge)
            {
                if (_dismissStartUs == long.MinValue)
                    _dismissStartUs = timestampUs;

                if (timestampUs - _dismissStartUs >= _dismissTimeoutUs)
                    ResetToIdle();
            }
            else
            {
                _dismissStartUs = long.MinValue;
            }
        }
    }

    private bool IsPastEdge(PhysicalPoint p)
    {
        return _edgeKey switch
        {
            "left" => p.X < 0,
            "right" => p.X >= _screenW,
            "top" => p.Y < 0,
            "bottom" => p.Y >= _screenH,
            _ => false
        };
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended) ResetToIdle();
    }

    public void EnterSummoned()
    {
        Volatile.Write(ref _state, (int)SummonState.Summoned);
        _runStartUs = long.MinValue;
        _dismissStartUs = long.MinValue;
    }

    public void ResetToIdle()
    {
        Volatile.Write(ref _state, (int)SummonState.Idle);
        _runStartUs = long.MinValue;
        _dismissStartUs = long.MinValue;
    }

    public void UpdateEdge(string edge)
    {
        if (State == SummonState.Summoned)
            ResetToIdle();
        _edgeKey = edge.ToLowerInvariant();
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~QuickActionsSummonControllerTests"`
Expected: 11 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/QuickActionsSummonController.cs tests/EyeCtrl.App.Tests/Services/QuickActionsSummonControllerTests.cs
git commit -m "feat: add QuickActionsSummonController state machine + tests"
```

---

### Task B4: QuickActionsGridWindow overlay

**Files:**
- Create: `src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml.cs`

- [ ] **Step 1: Write XAML**

```xml
<!-- src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml -->
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.QuickActionsGridWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
                        xmlns:vm="clr-namespace:EyeCtrl.ViewModels;assembly=EyeCtrl.ViewModels"
                        Width="400" Height="600"
                        WindowStyle="None" AllowsTransparency="True"
                        Background="Transparent" Topmost="True"
                        ShowInTaskbar="False">
    <Grid>
        <Border x:Name="RootBorder" Background="#1A1A2E" CornerRadius="8"
                BorderBrush="#334" BorderThickness="1" Opacity="0.95">
            <Grid Margin="12">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>
                <!-- Close button -->
                <Button x:Name="CloseButton" Content="&#x2715;" Grid.Row="0"
                        Width="30" Height="30" HorizontalAlignment="Right"
                        Style="{StaticResource DwellButtonStyle}"/>
                <!-- Action tiles -->
                <ItemsControl x:Name="TilesControl" Grid.Row="1" Margin="0,8,0,0">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="{Binding GridColumns, FallbackValue=3}"
                                         Rows="{Binding RowCount, FallbackValue=4}"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate DataType="{x:Type vm:QuickActionTileViewModel}">
                            <Border x:Name="TileBorder" Width="120" Height="120"
                                    CornerRadius="6" Background="#2A2A3E"
                                    BorderBrush="#445" BorderThickness="1"
                                    Margin="4">
                                <Grid>
                                    <TextBlock Text="{Binding Action.Label}"
                                               FontSize="14" Foreground="#FFEAA7"
                                               TextWrapping="Wrap" TextAlignment="Center"
                                               VerticalAlignment="Center" Margin="6"/>
                                    <!-- Dwell progress arc added/modified in code-behind -->
                                </Grid>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </Grid>
        </Border>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Write code-behind**

```csharp
// src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml.cs
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Threading;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public partial class QuickActionsGridWindow : OverlayWindow
{
    private QuickActionsGridViewModel? _vm;

    public QuickActionsGridWindow()
    {
        InitializeComponent();
        SetOverlayInteraction(clickThrough: false, focusable: false);
    }

    public void Configure(QuickActionsGridViewModel vm)
    {
        _vm = vm;
        DataContext = vm;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickActionsGridViewModel.IsVisible))
            {
                if (vm.IsVisible)
                    Show();
                else
                    Hide();
            }
        };

        TilesControl.DataContext = vm;
    }

    /// <summary>
    /// Called when a tile container is laid out. Updates the dwell target position.
    /// Mirrors TaskSwitcherWindow's pattern.
    /// </summary>
    public void SyncTilePositions()
    {
        if (_vm == null) return;

        foreach (var item in TilesControl.Items)
        {
            if (item is QuickActionTileViewModel tile)
            {
                var container = TilesControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container == null) continue;

                var center = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
                var physicalCenter = new PhysicalPoint(center.X * DpiHelper.ScaleX, center.Y * DpiHelper.ScaleY);
                _vm.UpdateTilePosition(tile.Action.Id, physicalCenter);
            }
        }
    }
}
```

Note: `SyncTilePositions` needs a trigger. Hook `TilesControl.LayoutUpdated` (with a run-once guard) or call it explicitly from `EventCoordinator` after showing. Add in Step 3 after wiring.

- [ ] **Step 3: Build**

Run: `dotnet build src/EyeCtrl.App --no-restore`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml.cs
git commit -m "feat: add QuickActionsGridWindow overlay"
```

---

### Task B5: Wire overlay + VM into DI, OverlayManager, EventCoordinationContext

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Add overlay to IOverlayManager**

Open `src/EyeCtrl.App/Services/IOverlayManager.cs`. Add property after `TaskSwitcherWindow TaskSwitcher` (line 75):

```csharp
    /// <summary>Gets the quick-actions grid overlay window.</summary>
    QuickActionsGridWindow QuickActionsGrid { get; }
```

Add to `OverlayViewModels` record (line 101, after `TaskSwitcherViewModel TaskSwitcher`):

```csharp
    QuickActionsGridViewModel QuickActionsGrid
```

Fix the closing parenthesis — it's now a 13-field record.

- [ ] **Step 2: Register in OverlayManager.Initialize**

Open `src/EyeCtrl.App/Services/OverlayManager.cs`. Add property after `public TaskSwitcherWindow TaskSwitcher` (line 36):

```csharp
    public QuickActionsGridWindow QuickActionsGrid { get; private set; } = null!;
```

In `Initialize()`, after `TaskSwitcher = new TaskSwitcherWindow();` (line 111), add:

```csharp
        QuickActionsGrid = new QuickActionsGridWindow();
        QuickActionsGrid.Configure(viewModels.QuickActionsGrid);
```

- [ ] **Step 3: Register in AppBootstrapper**

Open `src/EyeCtrl.App/Services/AppBootstrapper.cs`. After line 161 (`services.AddSingleton<TaskSwitcherViewModel>();`), add:

```csharp
        services.AddSingleton<QuickActionsGridViewModel>();
```

In `ResolveViewModels()`, add to the `OverlayViewModels` construction after `TaskSwitcher: sp.GetRequiredService<TaskSwitcherViewModel>()`:

```csharp
            QuickActionsGrid: sp.GetRequiredService<QuickActionsGridViewModel>()
```

- [ ] **Step 4: Add to EventCoordinationContext**

Open `src/EyeCtrl.App/Services/IEventCoordinator.cs`. Add to the `EventCoordinationContext` record after `TaskSwitcherViewModel TaskSwitcherVM` (line 82):

```csharp
    QuickActionsGridViewModel QuickActionsGridVM,
```

Add after `IWindowManager WindowManager` (line 83):

```csharp
    IQuickActionsStore QuickActionsStore,
```

- [ ] **Step 5: Update App.xaml.cs**

Open `src/EyeCtrl.App/App.xaml.cs`. In `ResolveViewModels()` call, add after `TaskSwitcher: ...`:

```csharp
        QuickActionsGrid: sp.GetRequiredService<QuickActionsGridViewModel>()
```

In the `EventCoordinationContext` construction (near `TaskSwitcherVM: ...`), add:

```csharp
    QuickActionsGridVM: vms.QuickActionsGrid,
    QuickActionsStore: sp.GetRequiredService<IQuickActionsStore>(),
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build --no-restore`
Expected: build succeeds, no compile errors.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/IEventCoordinator.cs
git commit -m "feat: wire QuickActionsGrid into DI + OverlayManager + EventCoordinationContext"
```

---

### Task B6: Wire summon controller + VM visibility into EventCoordinator

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Add SetupQuickActionsSummon() method**

Open `src/EyeCtrl.App/Services/EventCoordinator.cs`. Add field near `_toolbarAutoHide` (line 56):

```csharp
    private QuickActionsSummonController? _quickActionsSummon;
```

Add `SetupQuickActionsSummon()` method after `SetupToolbarAutoHide()` (~line 463):

```csharp
    private void SetupQuickActionsSummon()
    {
        var s = _context!.Settings.QuickActions;
        if (!s.Enabled) return;

        // Resolve actual summon edge (avoid conflict with main toolbar)
        var actualEdge = s.SummonEdge;
        if (actualEdge == _context.Settings.Toolbar.DockedEdge)
            actualEdge = actualEdge switch
            {
                "Left" => "Right", "Right" => "Left",
                "Top" => "Bottom", "Bottom" => "Top",
                _ => "Left"
            };

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        var controller = new QuickActionsSummonController(
            actualEdge,
            screen!.Bounds.Width * DpiHelper.ScaleX,
            screen.Bounds.Height * DpiHelper.ScaleY,
            s.SummonThresholdMs,
            s.DismissTimeoutMs,
            calibrationActive: () => _context.CalibrationService.IsSessionActive);

        controller.SummonRequested += () =>
            _context.Dispatcher.BeginInvoke(() =>
                _context.Messenger.Send(new QuickActionsToggledMessage()));

        var handler = new Action<object, GazeSampledEventArgs>((_, e) =>
        {
            var gaze = e.Gaze;
            controller.Feed(gaze.Position, gaze.TimestampMicroseconds, gaze.IsValid);
        });
        _context.GazeEngine.GazeSampled += handler;
        // handler is rooted by GazeSampled; unsubscribed in Dispose

        _quickActionsSummon = controller;

        // React to toolbar edge changes
        _context.Settings.Toolbar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolbarSettings.DockedEdge))
                _quickActionsSummon?.UpdateEdge(_context.Settings.Toolbar.DockedEdge);
        };
    }
```

- [ ] **Step 2: Wire suspension**

Add `SetSuspended` call sites in the existing suspension paths. At settings open (around line 839, where `_toolbarAutoHide?.SetSuspended(true)` is called):

```csharp
    _quickActionsSummon?.SetSuspended(true);
```

At settings resume (around line 930, where `_toolbarAutoHide?.SetSuspended(false)` is called):

```csharp
    _quickActionsSummon?.SetSuspended(false);
```

At keyboard toggle (around line 1163, where `_toolbarAutoHide?.SetSuspended(visible)` is called):

```csharp
    _quickActionsSummon?.SetSuspended(visible);
```

- [ ] **Step 3: Handle QuickActionSelectionMessage**

In the message dispatch loop (where `TaskSwitcherSelectionMessage` is handled), add:

```csharp
    else if (msg is QuickActionSelectionMessage selMsg)
    {
        ExecuteQuickAction(selMsg.Value);
    }
```

- [ ] **Step 4: Call SetupQuickActionsSummon() from Initialize**

In the `Initialize()` method, after the call to `SetupToolbarAutoHide()` (~line 98), add:

```csharp
    SetupQuickActionsSummon();
```

- [ ] **Step 5: Dispose**

In `Dispose()` (~line 1206), add:

```csharp
    _quickActionsSummon = null;
```

- [ ] **Step 6: Add GazeSampledEventArgs using**

Add at top if not present:
```csharp
using EyeCtrl.Core.Models;
```

- [ ] **Step 7: Build**

Run: `dotnet build src/EyeCtrl.App --no-restore`
Expected: may fail — `ExecuteQuickAction` not yet defined. Add a stub:

```csharp
    private void ExecuteQuickAction(QuickAction action)
    {
        // Placeholder — implemented in Checkpoint C (Task C1)
    }
```

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: wire QuickActionsSummonController + selection handler into EventCoordinator"
```

**Checkpoint B complete.** Run `dotnet build --no-restore` and `dotnet test` to verify.

---

### Task C1: IWindowsAppLauncher + WindowsAppLauncher + ExecuteQuickAction

**Files:**
- Create: `src/EyeCtrl.Core/Interfaces/IWindowsAppLauncher.cs`
- Create: `src/EyeCtrl.Platform/Services/WindowsAppLauncher.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Write IWindowsAppLauncher**

```csharp
// src/EyeCtrl.Core/Interfaces/IWindowsAppLauncher.cs
namespace EyeCtrl.Core.Interfaces;

public interface IWindowsAppLauncher
{
    void ActivateApplication(string aumid);
}
```

- [ ] **Step 2: Write WindowsAppLauncher**

```csharp
// src/EyeCtrl.Platform/Services/WindowsAppLauncher.cs
namespace EyeCtrl.Platform.Services;

using System.Runtime.InteropServices;
using EyeCtrl.Core.Interfaces;

public sealed class WindowsAppLauncher : IWindowsAppLauncher
{
    public void ActivateApplication(string aumid)
    {
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        mgr.ActivateApplication(aumid, null, ACTIVATEOPTIONS.AO_NONE, out _);
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ACTIVATEOPTIONS options, out uint processId);
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    private enum ACTIVATEOPTIONS : uint { AO_NONE = 0, AO_DESIGNMODE = 1, AO_NOERRORUI = 2, AO_NOSPLASHSCREEN = 4 }
}
```

- [ ] **Step 3: Wire into AppBootstrapper + EventCoordinationContext + App.xaml.cs**

In `AppBootstrapper.cs`, after line 74:

```csharp
        services.AddSingleton<IWindowsAppLauncher, WindowsAppLauncher>();
```

In `EventCoordinationContext` record, add after `IQuickActionsStore QuickActionsStore`:

```csharp
    IWindowsAppLauncher WindowsAppLauncher
```

In `App.xaml.cs`, add to the `EventCoordinationContext` construction:

```csharp
    WindowsAppLauncher: sp.GetRequiredService<IWindowsAppLauncher>(),
```

- [ ] **Step 4: Implement ExecuteQuickAction in EventCoordinator**

Replace the stub from Task B6 with:

```csharp
    private void ExecuteQuickAction(QuickAction action)
    {
        try
        {
            switch (action.Type)
            {
                case QuickActionType.KeyboardShortcut:
                    ExecuteKeyboardShortcut(action.Payload);
                    break;
                case QuickActionType.CannedText:
                    _context.InputSimulator.SendText(action.Payload);
                    break;
                case QuickActionType.LaunchExe:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.LaunchWindowsApp:
                    _context.WindowsAppLauncher.ActivateApplication(action.Payload);
                    break;
                case QuickActionType.LaunchUrl:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.MediaControl:
                    ExecuteMediaControl(action.Payload);
                    break;
                default:
                    Serilog.Log.Warning("Unknown QuickActionType: {Type}", action.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "QuickAction execution failed: {Type} '{Label}'", action.Type, action.Label);
        }
    }

    private void ExecuteKeyboardShortcut(string chord)
    {
        var parts = chord.Split('+');
        var mods = new HashSet<ModifierKey>();
        string key = parts[^1];

        for (int i = 0; i < parts.Length - 1; i++)
        {
            mods.Add(parts[i].Trim() switch
            {
                "Ctrl" => ModifierKey.Ctrl,
                "Shift" => ModifierKey.Shift,
                "Alt" => ModifierKey.Alt,
                "Win" => ModifierKey.Win,
                _ => ModifierKey.None
            });
        }

        // Reuse existing CharToVirtualKey mapping
        var (vk, _) = Win32InputSimulator.CharToVirtualKey(key.Length == 1 ? key[0] : '\0');
        if (vk == 0)
        {
            // Try special key names handled by HandleSpecialKey
            // Map common key names to virtual key codes
            vk = key switch
            {
                "Esc" => 0x1B, "Enter" => 0x0D, "Tab" => 0x09,
                "Space" => 0x20, "Backspace" => 0x08, "Delete" => 0x2E,
                "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22,
                "Left" => 0x25, "Right" => 0x27, "Up" => 0x26, "Down" => 0x28,
                "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
                "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
                "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
                _ => (ushort)0
            };
        }

        if (vk != 0)
            _context.InputSimulator.SendModifiedKeyPress(mods, vk, isExtendedKey: false);
        else
            Serilog.Log.Warning("QuickAction: could not map keyboard chord '{Chord}'", chord);
    }

    private void ExecuteMediaControl(string command)
    {
        ushort vk = command switch
        {
            "PlayPause" => 0xB3, "Stop" => 0xB2,
            "NextTrack" => 0xB0, "PreviousTrack" => 0xB1,
            "VolumeUp" => 0xAF, "VolumeDown" => 0xAE, "VolumeMute" => 0xAD,
            _ => 0
        };
        if (vk != 0)
            _context.InputSimulator.SendKeyPress(vk, isExtendedKey: true);
    }
```

Add `using EyeCtrl.Core.Models;` at top if not already present (check for `QuickAction` type).

- [ ] **Step 5: Build and run tests**

Run: `dotnet build --no-restore`
Expected: build succeeds.
Run: `dotnet test`
Expected: all existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IWindowsAppLauncher.cs src/EyeCtrl.Platform/Services/WindowsAppLauncher.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: add IWindowsAppLauncher + ExecuteQuickAction dispatch for all 6 types"
```

**Checkpoint C complete.**

---

### Task D1: Toolbar 7th button

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`
- Modify: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`

- [ ] **Step 1: Update test**

In `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs`, find `InitializesWith6Buttons` (line ~17). Update:

```csharp
Check.That(vm.Buttons).HasSize(7);
```
Rename the test method to `InitializesWith7Buttons`.

- [ ] **Step 2: Add 7th button**

Open `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`. In the `buttons` array (~line 38-46), add after `TaskSwitcher`:

```csharp
    ("QuickActions", "\u26A1"),  // ⚡
```

In `OnTargetActivated` switch (~line 99-110), add:

```csharp
    case "QuickActions": Messenger.Send(new QuickActionsToggledMessage()); break;
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ToolbarViewModelTests"`

Also verify the toolbar button count test that asserts `IsEqualTo(7)` (not `IsEqualTo(6)`).

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs
git commit -m "feat: add QuickActions 7th button to toolbar"
```

---

### Task D2: Settings tab infrastructure

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

This is the highest-effort single task — the first custom-card-layout tab in the Settings window. Every existing tab binds to `List<SettingItemViewModel>` with Slider/Combo/Toggle/ColorPicker types. This tab needs a genuinely new layout with action cards, per-type editors, and an "Add Action" flow.

- [ ] **Step 1: Write SettingsQuickActionsEditorViewModel**

Create `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs`:

```csharp
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public partial class SettingsQuickActionsEditorViewModel : ObservableObject
{
    private readonly IQuickActionsStore _store;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;

    [ObservableProperty] private bool _isVisible;

    public ObservableCollection<QuickAction> Actions { get; } = new();

    public SettingsQuickActionsEditorViewModel(IQuickActionsStore store, EyeCtrlSettings settings, IMessenger messenger)
    {
        _store = store;
        _settings = settings;
        _messenger = messenger;
    }

    public void Load()
    {
        Actions.Clear();
        foreach (var a in _store.LoadAll())
            Actions.Add(a);
    }

    public void Save()
    {
        _store.Save(Actions.ToList());
    }

    public void AddAction(QuickActionType type)
    {
        var action = new QuickAction { Type = type, Label = $"New {type}" };
        Actions.Add(action);
        Save();
    }

    public void DeleteAction(string id)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action != null)
        {
            Actions.Remove(action);
            Save();
        }
    }

    public void ToggleEnabled(string id)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action != null)
        {
            action.Enabled = !action.Enabled;
            Save();
        }
    }

    public void UpdateAction(string id, string? label, string? payload, QuickActionType? type)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action == null) return;
        if (label != null) action.Label = label;
        if (payload != null) action.Payload = payload;
        if (type.HasValue) action.Type = type.Value;
        Save();
    }
}
```

- [ ] **Step 2: Register in AppBootstrapper**

```csharp
        services.AddSingleton<SettingsQuickActionsEditorViewModel>();
```

- [ ] **Step 3: Add tab to SettingsViewModel.BuildTabs()**

Open `src/EyeCtrl.ViewModels/SettingsViewModel.cs`. Add after "Eye Care" tab (~line 198):

```csharp
        Tabs.Add(new SettingsTabViewModel("Quick Actions", "\u26A1", new List<SettingItemViewModel>
        {
            SettingItemViewModel.CreateToggle(settings.QuickActions, nameof(QuickActionsSettings.Enabled), "Enable"),
            SettingItemViewModel.CreateCombo(settings.QuickActions, nameof(QuickActionsSettings.SummonEdge), "Edge",
                ["Left", "Right", "Top", "Bottom"]),
            SettingItemViewModel.CreateSlider(settings.QuickActions, nameof(QuickActionsSettings.SummonThresholdMs), 200, 2000, "Summon"),
            SettingItemViewModel.CreateSlider(settings.QuickActions, nameof(QuickActionsSettings.DismissTimeoutMs), 1000, 15000, "Dismiss"),
            SettingItemViewModel.CreateSlider(settings.QuickActions, nameof(QuickActionsSettings.GridColumns), 2, 5, "Columns"),
        }));
```

The first 5 items use the standard `SettingItemViewModel` pattern for settings controls. The custom action card list renders below them via a dedicated `SettingsQuickActionsEditorWindow` popup or an inline section. At impl time, embed the editor in `SettingsWindow.xaml` by adding a `ContentControl` that binds to the editor VM and is visible only when the "Quick Actions" tab is active.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "feat: add Quick Actions settings tab + editor VM"
```

**Checkpoint D complete.** The full feature is wired end-to-end.

---

## Final Verification

Run: `dotnet test --filter "Category!=UI"`
Expected: all tests pass.

Run: `dotnet build --no-restore`
Expected: zero errors, zero warnings.

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`
Expected: app launches, toolbar shows 7 buttons, Quick Actions button dwells and opens the grid. The grid shows any actions from `%APPDATA%/EyeCtrl/actions.json`.
