# Pinned Notes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Pin Note" action to the action menu that places persistent, editable text annotations (sticky notes) at screen locations.

**Architecture:** Per-note `OverlayWindow` instances managed by a `NoteManager` singleton. Notes use the existing `DwellDetector` for gaze-based controls (close, edit, move). Editable via physical keyboard or eye-tracking keyboard `SendInput`. Session-only, max 8 notes, pin action routes through `EventCoordinator` before the simulator switch.

**Tech Stack:** C# 13, .NET 10, WPF, CommunityToolkit.Mvvm v8.4.0, xUnit 2.9.x, Xunit.StaFact, file-scoped namespaces, Allman braces.

**Prerequisites:**
- Work in an isolated git worktree (run `using-git-worktrees` skill before any edits).
- Run `dotnet test` once on the clean worktree to establish baseline.
- Run `gitnexus_impact` on each modified symbol before editing (per AGENTS.md).

**Worktree setup** (execute first):
```bash
# Create worktree and switch to it
git worktree add -b feature/pinned-notes ../eyectrl-pinned-notes main
cd ../eyectrl-pinned-notes
dotnet test  # baseline — all must pass
```

---

### Task 0: Test Infrastructure (Xunit.StaFact + DpiHelper)

**Files:**
- Modify: `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`

- [ ] **Step 1: Add StaFact package**

Run: `dotnet add tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj package Xunit.StaFact`

- [ ] **Step 2: Verify the package resolves**

Run: `dotnet restore tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj
git commit -m "chore: add Xunit.StaFact for WPF-threaded App tests"
```

---

### Task 1: Core Data Model

**Files:**
- Create: `src/EyeCtrl.Core/Models/NoteData.cs`
- Create: `tests/EyeCtrl.Core.Tests/Models/NoteDataTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Models/NoteDataTests.cs
namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class NoteDataTests
{
    [Fact]
    public void Constructor_GeneratesUniqueId()
    {
        var a = new NoteData();
        var b = new NoteData();
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(string.IsNullOrWhiteSpace(a.Id));
    }

    [Fact]
    public void Constructor_DefaultsToEditState()
    {
        var note = new NoteData();
        Assert.Equal(NoteState.Edit, note.State);
    }

    [Fact]
    public void Constructor_HasEmptyText()
    {
        var note = new NoteData();
        Assert.Equal(string.Empty, note.Text);
    }

    [Fact]
    public void Position_CanBeSet()
    {
        var note = new NoteData();
        note.Position = new PhysicalPoint(100, 200);
        Assert.Equal(100, note.Position.X);
        Assert.Equal(200, note.Position.Y);
    }

    [Fact]
    public void State_Transitions_BetweenValues()
    {
        var note = new NoteData();
        note.State = NoteState.Display;
        Assert.Equal(NoteState.Display, note.State);
        note.State = NoteState.Move;
        Assert.Equal(NoteState.Move, note.State);
    }

    [Fact]
    public void NoteState_Enum_HasThreeValues()
    {
        var values = Enum.GetValues<NoteState>();
        Assert.Equal(3, values.Length);
        Assert.Contains(NoteState.Display, values);
        Assert.Contains(NoteState.Edit, values);
        Assert.Contains(NoteState.Move, values);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~NoteDataTests"`
Expected: FAIL — type `NoteData` and enum `NoteState` do not exist

- [ ] **Step 3: Write the implementation**

```csharp
// src/EyeCtrl.Core/Models/NoteData.cs
namespace EyeCtrl.Core.Models;

public enum NoteState
{
    Display,
    Edit,
    Move
}

public class NoteData
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = string.Empty;
    public PhysicalPoint Position { get; set; }
    public NoteState State { get; set; } = NoteState.Edit;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~NoteDataTests"`
Expected: 6/6 PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/NoteData.cs tests/EyeCtrl.Core.Tests/Models/NoteDataTests.cs
git commit -m "feat: add NoteData model and NoteState enum for pinned notes"
```

---

### Task 2: Layout Constant

**Files:**
- Modify: `src/EyeCtrl.Core/LayoutConstants.cs`

- [ ] **Step 1: Add the constants**

Add before the closing brace of `LayoutConstants` (after `ActionMenuButtonOuterGap` on line 19):

```csharp
/// <summary>
/// Radius of note control buttons (close, edit, move) in logical pixels.
/// 30px radius = 60px diameter — large enough for gaze targeting, compact enough for the note sidebar.
/// </summary>
public const double NoteButtonRadius = 30.0;

/// <summary>
/// Width of the NoteWindow in logical pixels (text area + button column: 200 + 60 + 10 + 10).
/// </summary>
public const double NoteWindowWidth = 280.0;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/LayoutConstants.cs
git commit -m "feat: add NoteButtonRadius constant for pinned note controls"
```

---

### Task 3: PinNote Enum Value + Action Menu Settings

**Files:**
- Modify: `src/EyeCtrl.Core/Models/InteractionAction.cs`
- Modify: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`
- Create: `tests/EyeCtrl.Core.Tests/Models/InteractionActionEnumTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.Core.Tests/Models/InteractionActionEnumTests.cs
namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;
using Xunit;

public class InteractionActionEnumTests
{
    [Fact]
    public void PinNote_HasValue_9()
    {
        Assert.Equal(9, (int)InteractionAction.PinNote);
    }

    [Fact]
    public void Enum_Contains_PinNote()
    {
        Assert.Contains(InteractionAction.PinNote, Enum.GetValues<InteractionAction>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionActionEnumTests"`
Expected: FAIL — `PinNote` does not exist

- [ ] **Step 3: Add enum value and settings default**

In `src/EyeCtrl.Core/Models/InteractionAction.cs`:
```csharp
namespace EyeCtrl.Core.Models;
public enum InteractionAction { LeftClick, RightClick, DoubleClick, DragStart, DragEnd, ScrollUp, ScrollDown, ScrollLeft, ScrollRight, PinNote }
```

In `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`, add `PinNote` to the default list (after `ScrollDown`):
```csharp
[ObservableProperty] private List<InteractionAction> _visibleActions = new()
{
    InteractionAction.LeftClick, InteractionAction.RightClick,
    InteractionAction.DoubleClick, InteractionAction.DragStart,
    InteractionAction.ScrollUp, InteractionAction.ScrollDown,
    InteractionAction.PinNote
};
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~InteractionActionEnumTests"`
Expected: 2/2 PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Models/InteractionAction.cs src/EyeCtrl.Core/Settings/ActionMenuSettings.cs tests/EyeCtrl.Core.Tests/Models/InteractionActionEnumTests.cs
git commit -m "feat: add PinNote=9 enum value and include in default visible actions"
```

---

### Task 4: Action Menu Symbol — ViewModels Layer

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelPinNoteTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelPinNoteTests.cs
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Xunit;

public class ActionMenuViewModelPinNoteTests
{
    [Fact]
    public void PinNote_Appears_InActionMenuItems()
    {
        var settings = new EyeCtrlSettings { Dwell = new DwellSettings() };
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(settings, messenger);

        var actions = new List<InteractionAction> { InteractionAction.PinNote };
        var msg = new ShowActionMenuMessage(
            Center: new PhysicalPoint(500, 300),
            Actions: actions,
            ShowBack: false,
            DwellTimeMs: 600,
            GazePoint: new PhysicalPoint(500, 300));

        vm.Receive(msg);

        Assert.Single(vm.Items);
        Assert.NotNull(vm.Items[0].Action);
        Assert.Equal(InteractionAction.PinNote, vm.Items[0].Action!.Value);
        Assert.NotEmpty(vm.Items[0].Symbol); // must not be the fallback ToString
        Assert.NotEqual("PinNote", vm.Items[0].Symbol);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelPinNoteTests"`
Expected: FAIL — symbol is `"PinNote"` (fallback `ToString()`) or the switch arm is missing

- [ ] **Step 3: Add the symbol mapping**

In `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs`, add before `_ => action.ToString()` (line 129):
```csharp
InteractionAction.PinNote => "\uD83D\uDCCC",     // 📌 Pushpin
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelPinNoteTests"`
Expected: 1/1 PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/ActionMenuViewModel.cs tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelPinNoteTests.cs
git commit -m "feat: add PinNote symbol mapping in ActionMenuViewModel"
```

---

### Task 5: Action Menu Symbol — App Layer (App/Overlays)

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`

- [ ] **Step 1: Add the symbol mapping**

In `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`, add before `_ => action.ToString()` (line 308):
```csharp
InteractionAction.PinNote => "\uD83D\uDCCC",     // 📌 Pushpin
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs
git commit -m "feat: add PinNote symbol mapping in ActionMenuWindow"
```

---

### Task 6: INoteManager Interface

**Files:**
- Create: `src/EyeCtrl.App/Services/INoteManager.cs`

- [ ] **Step 1: Write the interface**

```csharp
// src/EyeCtrl.App/Services/INoteManager.cs
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

public interface INoteManager
{
    IReadOnlyList<NoteData> ActiveNotes { get; }
    bool IsAtCapacity { get; }

    void CreateNoteAt(PhysicalPoint point);
    void RemoveNote(string id);
    void StartEdit(string id);
    void FinishEdit(string id, string text);
    void StartMove(string id);
    void FinishMove(string id, PhysicalPoint newPosition);

    void OnGazeMoved(PhysicalPoint gazePoint, long timestamp);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Services/INoteManager.cs
git commit -m "feat: add INoteManager interface for pinned notes lifecycle"
```

---

### Task 7: NoteWindow Overlay (Pure Code-Behind)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs`
- Modify: `src/EyeCtrl.Core/LayoutConstants.cs` (add `NoteWindowWidth` — already done in Task 2)

**Note:** Uses pure code-behind (no `.xaml` file). Several existing overlays follow this pattern. The XAML approach with a custom `OverlayWindow` root element is complex; code-behind is simpler and sufficient here.

- [ ] **Step 1: Write the code-behind file**

```csharp
// src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public class NoteWindow : OverlayWindow
{
    private const double ButtonRadius = LayoutConstants.NoteButtonRadius;
    private const double ButtonDiameter = ButtonRadius * 2;
    private const double ButtonGap = 10;
    private const double ButtonPadding = 10;
    private const double TextAreaWidth = 200;

    private readonly string _noteId;
    private readonly Canvas _canvas;
    private readonly Dictionary<string, Ellipse> _buttons = new();
    private readonly Dictionary<string, (Path Arc, double StartAngle)> _arcs = new();
    private TextBlock? _textBlock;
    private TextBox? _textBox;
    private Border? _contentBorder;
    private Border? _titleBar;
    private NoteState _state;

    public string NoteId => _noteId;
    public NoteState State => _state;

    public NoteWindow(string noteId)
    {
        _noteId = noteId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsClickThrough = false;
        Width = LayoutConstants.NoteWindowWidth;
        Height = 140;

        _canvas = new Canvas { ClipToBounds = false };
        Content = _canvas;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        _buttons.Clear();
        _arcs.Clear();

        var bg = new Border
        {
            Width = LayoutConstants.NoteWindowWidth, Height = Height,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            CornerRadius = new CornerRadius(6),
            BorderBrush = GetBorderBrush(),
            BorderThickness = GetBorderThickness(),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, Opacity = 0.4, ShadowDepth = 2 }
        };
        Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
        _canvas.Children.Add(bg);

        _titleBar = new Border
        {
            Height = 24, Width = LayoutConstants.NoteWindowWidth,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x3F)),
            CornerRadius = new CornerRadius(6, 6, 0, 0)
        };
        var titleLabel = new TextBlock
        {
            Text = "PINNED NOTE", FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(8, 4, 0, 0)
        };
        _titleBar.Child = titleLabel;
        Canvas.SetLeft(_titleBar, 0); Canvas.SetTop(_titleBar, 0);
        _canvas.Children.Add(_titleBar);

        _contentBorder = new Border
        {
            Width = LayoutConstants.NoteWindowWidth, Height = Height - 24,
            Background = Brushes.Transparent
        };
        Canvas.SetLeft(_contentBorder, 0); Canvas.SetTop(_contentBorder, 24);
        _canvas.Children.Add(_contentBorder);

        DrawButtons();
        UpdateContent();
    }

    public void UpdateContent()
    {
        if (_contentBorder == null) return;
        _contentBorder.Child = null;

        if (_state == NoteState.Edit)
        {
            _textBox = new TextBox
            {
                Width = TextAreaWidth - 20, Height = Height - 44,
                Margin = new Thickness(10, 10, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x40)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Colors.Lime),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _contentBorder.Child = _textBox;
        }
        else
        {
            _textBlock = new TextBlock
            {
                Width = TextAreaWidth - 20,
                MaxWidth = TextAreaWidth - 20,
                Margin = new Thickness(10, 10, 0, 0),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xEA, 0xA7)),
                TextWrapping = TextWrapping.Wrap
            };
            _contentBorder.Child = _textBlock;
        }
    }

    public void SetText(string text)
    {
        if (_textBlock != null) _textBlock.Text = text;
        if (_textBox != null) _textBox.Text = text;
    }

    public string GetText()
    {
        if (_textBox != null) return _textBox.Text;
        return _textBlock?.Text ?? "";
    }

    public void SetState(NoteState state)
    {
        _state = state;
        Dispatcher.Invoke(() =>
        {
            UpdateContent();
            UpdateBorder();

            if (state == NoteState.Edit)
            {
                SetOverlayInteraction(clickThrough: false, focusable: true);
                _textBox?.Focus();
            }
            else
            {
                SetOverlayInteraction(clickThrough: false, focusable: false);
            }
        });
    }

    private void UpdateBorder() => Dispatcher.Invoke(() =>
    {
        if (_canvas.Children.Count == 0) return;
        if (_canvas.Children[0] is Border bg)
        {
            bg.BorderBrush = GetBorderBrush();
            bg.BorderThickness = GetBorderThickness();
        }
    });

    private Brush GetBorderBrush()
    {
        return _state switch
        {
            NoteState.Edit => new SolidColorBrush(Colors.Lime),
            NoteState.Move => new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF)),
            _ => new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x44))
        };
    }

    private Thickness GetBorderThickness()
    {
        return _state == NoteState.Display ? new Thickness(1) : new Thickness(2);
    }

    private void DrawButtons()
    {
        var symbols = new[] { ("close", "\u2715", Color.FromRgb(0xFF, 0x66, 0x66)),
                              ("edit",  "\u270E", Color.FromRgb(0x66, 0xFF, 0x66)),
                              ("move",  "\u2195", Color.FromRgb(0x66, 0x88, 0xFF)) };

        for (int i = 0; i < 3; i++)
        {
            var (suffix, symbol, color) = symbols[i];
            double cx = LayoutConstants.NoteWindowWidth - ButtonPadding - ButtonRadius;
            double cy = ButtonPadding + ButtonRadius + i * (ButtonDiameter + ButtonGap);

            var ellipse = new Ellipse
            {
                Width = ButtonDiameter, Height = ButtonDiameter,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1
            };
            Canvas.SetLeft(ellipse, cx - ButtonRadius);
            Canvas.SetTop(ellipse, cy - ButtonRadius);
            _canvas.Children.Add(ellipse);
            _buttons[$"{_noteId}:{suffix}"] = ellipse;

            var label = new TextBlock
            {
                Text = symbol, FontSize = 14, Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(label, cx - 8);
            Canvas.SetTop(label, cy - 10);
            _canvas.Children.Add(label);

            var arcPath = new Path
            {
                Stroke = new SolidColorBrush(Colors.Lime),
                StrokeThickness = 3, Visibility = Visibility.Hidden
            };
            Canvas.SetLeft(arcPath, 0); Canvas.SetTop(arcPath, 0);
            _canvas.Children.Add(arcPath);
            _arcs[$"{_noteId}:{suffix}"] = (arcPath, -90);
        }
    }

    public void UpdateDwellProgress(string targetId, double progress)
    {
        if (!_buttons.TryGetValue(targetId, out var ellipse)) return;
        if (!_arcs.TryGetValue(targetId, out var arcData)) return;

        var (arcPath, startAngle) = arcData;
        Dispatcher.Invoke(() =>
        {
            if (progress <= 0)
            {
                arcPath.Visibility = Visibility.Hidden;
                return;
            }
            arcPath.Visibility = Visibility.Visible;
            double cx = Canvas.GetLeft(ellipse) + ButtonRadius;
            double cy = Canvas.GetTop(ellipse) + ButtonRadius;
            double r = ButtonRadius - 2;
            double sweep = progress * 360;
            var fig = new PathFigure { StartPoint = new Point(cx + r * Math.Cos(startAngle * Math.PI / 180), cy + r * Math.Sin(startAngle * Math.PI / 180)) };
            var seg = new ArcSegment
            {
                Point = new Point(cx + r * Math.Cos((startAngle + sweep) * Math.PI / 180), cy + r * Math.Sin((startAngle + sweep) * Math.PI / 180)),
                Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = sweep > 180
            };
            arcPath.Data = new PathGeometry(new[] { fig });
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Loaded -= OnLoaded;
    }
}
```

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs
git commit -m "feat: add NoteWindow overlay for pinned notes (pure code-behind)"
```

---

### Task 8: NoteManager Implementation

**Files:**
- Create: `src/EyeCtrl.App/Services/NoteManager.cs`
- Create: `tests/EyeCtrl.App.Tests/Services/NoteManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Services/NoteManagerTests.cs
namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;
using Xunit;

public class NoteManagerTests
{
    [StaFact]
    public void CreateNote_AddsToActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(100, 200));

        Assert.Single(manager.ActiveNotes);
        Assert.Equal(100, manager.ActiveNotes[0].Position.X);
        Assert.Equal(200, manager.ActiveNotes[0].Position.Y);
    }

    [StaFact]
    public void CreateNote_DefaultsToEditState()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);

        manager.CreateNoteAt(new PhysicalPoint(0, 0));

        Assert.Equal(NoteState.Edit, manager.ActiveNotes[0].State);
    }

    [StaFact]
    public void RemoveNote_RemovesFromActiveNotes()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.RemoveNote(id);

        Assert.Empty(manager.ActiveNotes);
    }

    [Fact]
    public void IsAtCapacity_ReturnsTrue_WhenMaxReached()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);

        for (int i = 0; i < 8; i++)
            manager.CreateNoteAt(new PhysicalPoint(i * 50, 0));

        Assert.True(manager.IsAtCapacity);
    }

    [Fact]
    public void StartEdit_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test text"); // move to Display first

        manager.StartEdit(id);

        Assert.Equal(NoteState.Edit, manager.ActiveNotes[0].State);
    }

    [Fact]
    public void FinishEdit_SavesText()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;

        manager.FinishEdit(id, "hello world");

        Assert.Equal("hello world", manager.ActiveNotes[0].Text);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
    }

    [Fact]
    public void StartMove_ChangesState()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");

        manager.StartMove(id);

        Assert.Equal(NoteState.Move, manager.ActiveNotes[0].State);
    }

    [Fact]
    public void FinishMove_UpdatesPosition()
    {
        var messenger = new WeakReferenceMessenger();
        var manager = new NoteManager(messenger);
        manager.CreateNoteAt(new PhysicalPoint(0, 0));
        var id = manager.ActiveNotes[0].Id;
        manager.FinishEdit(id, "test");
        manager.StartMove(id);

        manager.FinishMove(id, new PhysicalPoint(500, 600));

        Assert.Equal(500, manager.ActiveNotes[0].Position.X);
        Assert.Equal(600, manager.ActiveNotes[0].Position.Y);
        Assert.Equal(NoteState.Display, manager.ActiveNotes[0].State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~NoteManagerTests"`
Expected: FAIL — `NoteManager` does not exist

- [ ] **Step 3: Write the implementation**

```csharp
// src/EyeCtrl.App/Services/NoteManager.cs
namespace EyeCtrl.App.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

public class NoteManager : INoteManager, IRecipient<GazeCursorMovedMessage>
{
    private readonly List<NoteWindow> _windows = new();
    private readonly List<NoteData> _notes = new();
    private readonly DwellDetector _dwellDetector = new(cooldownMs: 300);
    private readonly IMessenger _messenger;
    private const int MaxNotes = 8;
    private const int DwellTimeMs = 600;
    private const double PlacementRadius = 100;

    public IReadOnlyList<NoteData> ActiveNotes => _notes.AsReadOnly();
    public bool IsAtCapacity => _windows.Count >= MaxNotes;

    public NoteManager(IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        _messenger.Register<GazeCursorMovedMessage>(this);
    }

    public void CreateNoteAt(PhysicalPoint point)
    {
        if (IsAtCapacity) return;

        var data = new NoteData { Position = point, State = NoteState.Edit };
        var window = new NoteWindow(data.Id);
        _notes.Add(data);
        _windows.Add(window);

        window.SetState(NoteState.Edit);
        window.Loaded += (_, _) =>
        {
            window.Left = DpiHelper.PhysicalToLogicalX(point.X) - NoteWidth / 2;
            window.Top = DpiHelper.PhysicalToLogicalY(point.Y) - 70;
            window.Show();
            window.SetText(data.Text);
            window.SetOverlayInteraction(clickThrough: false, focusable: true);
            window.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    FinishEdit(data.Id, window.GetText());
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    FinishEdit(data.Id, data.Text); // cancel, restore original
                }
            };
        };
        RegisterDwellTargets(data.Id);
    }

    public void RemoveNote(string id)
    {
        _dwellDetector.ClearTargets(); // simpler: clear all, re-register remaining
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        var note = _notes.FirstOrDefault(n => n.Id == id);
        if (window != null) { window.Close(); _windows.Remove(window); }
        if (note != null) _notes.Remove(note);
        // Re-register remaining targets
        foreach (var n in _notes)
            RegisterDwellTargets(n.Id);
    }

    public void StartEdit(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Edit;
        window.SetState(NoteState.Edit);
        window.SetText(note.Text);
        _dwellDetector.ResetAllState();
    }

    public void FinishEdit(string id, string text)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.Text = text;
        note.State = NoteState.Display;
        window.SetState(NoteState.Display);
        window.SetText(text);
        _dwellDetector.ResetAllState();
    }

    public void StartMove(string id)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.State = NoteState.Move;
        window.SetState(NoteState.Move);
        _dwellDetector.ClearTargets();
        _dwellDetector.AddTarget(new DwellTarget($"{id}:place", note.Position, PlacementRadius, DwellTimeMs));
        _dwellDetector.ResetAllState();
    }

    public void FinishMove(string id, PhysicalPoint newPosition)
    {
        var note = _notes.FirstOrDefault(n => n.Id == id);
        var window = _windows.FirstOrDefault(w => w.NoteId == id);
        if (note == null || window == null) return;
        note.Position = newPosition;
        note.State = NoteState.Display;
        window.SetState(NoteState.Display);
        _dwellDetector.ClearTargets();
        RegisterDwellTargets(id);
        _dwellDetector.ResetAllState();
    }

    public void OnGazeMoved(PhysicalPoint gazePoint, long timestamp)
    {
        _dwellDetector.Update(gazePoint, timestamp);
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        OnGazeMoved(message.Position, message.TimestampMicroseconds);
    }

    private void RegisterDwellTargets(string noteId)
    {
        var note = _notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;

        double bx = note.Position.X + LayoutConstants.NoteWindowWidth - LayoutConstants.NoteButtonRadius;
        double byBase = note.Position.Y + 34; // 24 title + 10 padding
        double step = LayoutConstants.NoteButtonRadius * 2 + 10;

        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:close", new PhysicalPoint(bx, byBase), LayoutConstants.NoteButtonRadius, DwellTimeMs));
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:edit", new PhysicalPoint(bx, byBase + step), LayoutConstants.NoteButtonRadius, DwellTimeMs));
        _dwellDetector.AddTarget(new DwellTarget($"{noteId}:move", new PhysicalPoint(bx, byBase + step * 2), LayoutConstants.NoteButtonRadius, DwellTimeMs));
    }

    private void OnTargetActivated(string targetId)
    {
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, suffix) = (parts[0], parts[1]);

        switch (suffix)
        {
            case "close": RemoveNote(noteId); break;
            case "edit": StartEdit(noteId); break;
            case "move": StartMove(noteId); break;
            case "place": FinishMove(noteId, _notes.FirstOrDefault(n => n.Id == noteId)?.Position ?? new PhysicalPoint(0, 0)); break;
        }
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        var parts = targetId.Split(':');
        if (parts.Length != 2) return;
        var (noteId, _) = (parts[0], parts[1]);
        var window = _windows.FirstOrDefault(w => w.NoteId == noteId);
        window?.UpdateDwellProgress(targetId, progress);
    }
}
```

**Test note:** Tests that create `NoteWindow` instances require `[StaFact]` (WPF threading). The `NoteManager` constructor only takes `IMessenger`, making it testable without WPF dependencies — but `CreateNoteAt` instantiates WPF windows, so those tests need STA. Pure-logic tests (state transitions, text save, capacity check without window creation) can use `[Fact]`; window-creating tests (CreateNoteAt, RemoveNote) need `[StaFact]`. DpiHelper must be initialized before tests: call `DpiHelper.Initialize(1.0, 1.0)` in a static constructor or `[StaFact]` test setup.

**Toast simplification:** The spec calls for a toast when at capacity. For v1 simplicity, the implementation returns silently (no note created) when `IsAtCapacity`. The toast can be added as a follow-up.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~NoteManagerTests"`
Expected: 8/8 PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/NoteManager.cs tests/EyeCtrl.App.Tests/Services/NoteManagerTests.cs src/EyeCtrl.Core/LayoutConstants.cs
git commit -m "feat: add NoteManager implementation with lifecycle and dwell control handling"
```

---

### Task 9: DI Registration + EventCoordinationContext

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Register INoteManager in DI**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, add after the `ExternalDwellService` registration (around line 178):

```csharp
// NoteManager (pinned notes lifecycle)
services.AddSingleton<INoteManager>(sp => new NoteManager(
    sp.GetRequiredService<IMessenger>()));
```

- [ ] **Step 2: Add NoteManager to EventCoordinationContext**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, add to the `EventCoordinationContext` record (after `TargetHighlightOverlayViewModel TargetHighlightVM`):

```csharp
INoteManager NoteManager,
```

Add the import at top: `using EyeCtrl.App.Services;` is already in namespace scope, but ensure it's there.

- [ ] **Step 3: Wire in App.xaml.cs**

In `src/EyeCtrl.App/App.xaml.cs`, resolve `INoteManager` and pass to context. Add after the `CalibrationService` line (or before model services):

```csharp
var noteManager = _serviceProvider.GetRequiredService<INoteManager>();
```

Then add `noteManager` as the last argument to the `EventCoordinationContext` constructor call. The new call should be:

```csharp
var coordinationContext = new EventCoordinationContext(
    cursorVM, activationVM, ManualSelectionVM, ManualSelectionIndicatorVM, actionMenuVM, toolbarVM, keyboardVM, backdropVM,
    overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, targetHighlightVM, inputSimulator,
    _serviceProvider.GetRequiredService<CalibrationService>(),
    noteManager);
```

- [ ] **Step 4: Add PinNote case to EventCoordinator.HandleActionRequested**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, add **before** the `switch` block (line 772):

```csharp
if (msg.Action == InteractionAction.PinNote)
{
    if (_context is { NoteManager.IsAtCapacity: false })
        _context.NoteManager.CreateNoteAt(pt);
    return;
}
```

- [ ] **Step 5: Verify build**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded (0 errors)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: wire NoteManager into DI, EventCoordinationContext, and EventCoordinator PinNote case"
```

---

### Task 10: End-to-End Validation

**Files:** (none — verification only)

- [ ] **Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass (existing + new)

- [ ] **Step 2: Build the full solution in Release mode**

Run: `dotnet build EyeCtrl.slnx -c Release`
Expected: Build succeeded with 0 errors

- [ ] **Step 3: Run the app in test mode to verify no crashes**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`

Manual verification:
- Dwell on activation button → action menu appears → verify PinNote symbol (📌) is visible
- Dwell on PinNote → verify a note appears on screen
- Type some text → verify Enter saves and note displays text
- Dwell on edit button → verify note enters edit mode
- Dwell on move button → verify note follows gaze → dwell to place
- Dwell on close button → verify note closes

- [ ] **Step 4: Commit any final fixes**

```bash
git add -A
git commit -m "chore: final tweaks and verification after pinned notes implementation"
```
