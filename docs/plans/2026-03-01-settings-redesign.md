# Settings Window Redesign Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Redesign the settings window as a large borderless overlay with live-apply, paginated content, dwell-friendly navigation, reset functionality, and relaxed default dwell times.

**Architecture:** Replace the current 850×620 windowed SettingsWindow with a ~90% screen borderless overlay. Remove save/cancel workflow — all changes apply immediately to the live `EyeCtrlSettings` and auto-save with debounce. Content is paginated per tab (no scrolling). Reset confirmation uses a dwell-enabled modal overlay.

**Tech Stack:** C# / .NET 8 / WPF, existing `DwellDetector`, `SettingsService`, `EyeCtrlSettings`

**Design doc:** `docs/plans/2026-03-01-settings-redesign-design.md`

---

### Task 1: Update Default Dwell Times in DwellSettings.cs

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/DwellSettings.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/DwellSettingsDefaultsTests.cs`

**Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Settings/DwellSettingsDefaultsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class DwellSettingsDefaultsTests
{
    [Fact]
    public void DefaultDwellTimes_AreRelaxed()
    {
        var dwell = new DwellSettings();

        Assert.Equal(300, dwell.FixationDetectionMs);
        Assert.Equal(700, dwell.ActivationButtonDwellMs);
        Assert.Equal(600, dwell.ActionMenuDwellMs);
        Assert.Equal(600, dwell.ZoomActionMenuDwellMs);
        Assert.Equal(700, dwell.ToolbarButtonDwellMs);
        Assert.Equal(450, dwell.KeyboardKeyDwellMs);
        Assert.Equal(500, dwell.KeyboardPredictionDwellMs);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter DwellSettingsDefaultsTests -v n`
Expected: FAIL — current defaults are 200, 500, 400, 400, 500, 300, 350.

**Step 3: Update the defaults**

In `src/EyeCtrl.Core/Settings/DwellSettings.cs`, change the default property values:

```csharp
public int FixationDetectionMs { get; set; } = 300;       // was 200
public int ActivationButtonDwellMs { get; set; } = 700;   // was 500
public int ActionMenuDwellMs { get; set; } = 600;          // was 400
public int ZoomActionMenuDwellMs { get; set; } = 600;      // was 400
public int ToolbarButtonDwellMs { get; set; } = 700;       // was 500
public int KeyboardKeyDwellMs { get; set; } = 450;         // was 300
public int KeyboardPredictionDwellMs { get; set; } = 500;  // was 350
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter DwellSettingsDefaultsTests -v n`
Expected: PASS

**Step 5: Run all existing tests to check nothing breaks**

Run: `dotnet test tests/EyeCtrl.Core.Tests -v n`
Expected: All pass. Existing users with a `settings.json` file will keep their saved values; only fresh installs get new defaults.

**Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Settings/DwellSettings.cs tests/EyeCtrl.Core.Tests/Settings/DwellSettingsDefaultsTests.cs
git commit -m "feat: relax default dwell times (~40-50% increase)"
```

---

### Task 2: Add SaveAsync with Debounce to SettingsService

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/SettingsService.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceDebounceTests.cs`

**Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceDebounceTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class SettingsServiceDebounceTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SettingsService _service;

    public SettingsServiceDebounceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        _service = new SettingsService(_tempFile);
    }

    [Fact]
    public async Task SaveDebounced_SingleCall_SavesAfterDelay()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationDetectionMs = 999;

        _service.SaveDebounced(settings);

        // Not saved yet immediately
        Assert.False(File.Exists(_tempFile));

        // Wait for debounce (500ms + buffer)
        await Task.Delay(800);

        Assert.True(File.Exists(_tempFile));
        var loaded = _service.Load();
        Assert.Equal(999, loaded.Dwell.FixationDetectionMs);
    }

    [Fact]
    public async Task SaveDebounced_RapidCalls_OnlySavesLast()
    {
        var settings = new EyeCtrlSettings();

        settings.Dwell.FixationDetectionMs = 100;
        _service.SaveDebounced(settings);

        await Task.Delay(100);
        settings.Dwell.FixationDetectionMs = 200;
        _service.SaveDebounced(settings);

        await Task.Delay(100);
        settings.Dwell.FixationDetectionMs = 300;
        _service.SaveDebounced(settings);

        // Wait for debounce
        await Task.Delay(800);

        var loaded = _service.Load();
        Assert.Equal(300, loaded.Dwell.FixationDetectionMs);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter SettingsServiceDebounceTests -v n`
Expected: FAIL — `SaveDebounced` method doesn't exist yet.

**Step 3: Implement SaveDebounced**

In `src/EyeCtrl.Core/Settings/SettingsService.cs`, add a debounced save method using a `CancellationTokenSource`:

```csharp
private CancellationTokenSource? _debounceCts;
private const int DebounceMs = 500;

public void SaveDebounced(EyeCtrlSettings settings)
{
    _debounceCts?.Cancel();
    _debounceCts = new CancellationTokenSource();
    var token = _debounceCts.Token;

    Task.Run(async () =>
    {
        try
        {
            await Task.Delay(DebounceMs, token);
            if (!token.IsCancellationRequested)
                Save(settings);
        }
        catch (TaskCanceledException) { }
    });
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter SettingsServiceDebounceTests -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/SettingsService.cs tests/EyeCtrl.Core.Tests/Settings/SettingsServiceDebounceTests.cs
git commit -m "feat: add debounced save to SettingsService (500ms)"
```

---

### Task 3: Rewrite SettingsWindow.xaml as Large Borderless Overlay

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml`

This task replaces the XAML only — the code-behind is rewritten in the next tasks.

**Step 1: Rewrite the XAML**

Replace `src/EyeCtrl.App/Views/SettingsWindow.xaml` entirely:

```xml
<Window x:Class="EyeCtrl.App.Views.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="EyeCtrl Settings"
        WindowStyle="None" AllowsTransparency="True"
        Background="#E01E1E1E"
        Topmost="True"
        WindowStartupLocation="CenterScreen">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="220"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- Left: Sidebar with tab buttons -->
        <Grid Grid.Column="0" Background="#C0141414">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <!-- Close button at top -->
            <Border x:Name="CloseButton" Grid.Row="0"
                    Height="80" Width="80"
                    HorizontalAlignment="Right" Margin="8"
                    CornerRadius="8" Background="#C8641E1E"
                    Cursor="Hand">
                <TextBlock Text="✕" Foreground="White"
                           FontSize="28" FontWeight="Bold"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center"/>
            </Border>

            <!-- Tab buttons -->
            <StackPanel x:Name="TabButtonPanel" Grid.Row="1"
                        Margin="8" VerticalAlignment="Top"/>
        </Grid>

        <!-- Right: Content area with pagination -->
        <Grid Grid.Column="1" Margin="16">
            <Grid.RowDefinitions>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <!-- Content page (no ScrollViewer — paginated) -->
            <StackPanel x:Name="ContentPanel" Grid.Row="0"
                        Margin="16"/>

            <!-- Pagination bar -->
            <StackPanel x:Name="PaginationPanel" Grid.Row="1"
                        Orientation="Horizontal"
                        HorizontalAlignment="Center"
                        Margin="0,8,0,16"/>
        </Grid>

        <!-- Modal overlay (hidden by default) -->
        <Grid x:Name="ModalOverlay" Grid.ColumnSpan="2"
              Visibility="Collapsed">
            <Border Background="#A0000000"/>
            <Border x:Name="ModalContent"
                    HorizontalAlignment="Center"
                    VerticalAlignment="Center"
                    CornerRadius="12"
                    Background="#E0282828"
                    Padding="40,30"
                    MinWidth="400">
                <StackPanel HorizontalAlignment="Center">
                    <TextBlock x:Name="ModalMessage"
                               Foreground="White" FontSize="20"
                               FontWeight="SemiBold"
                               TextAlignment="Center"
                               TextWrapping="Wrap"
                               Margin="0,0,0,24"/>
                    <StackPanel x:Name="ModalButtonPanel"
                                Orientation="Horizontal"
                                HorizontalAlignment="Center"/>
                </StackPanel>
            </Border>
        </Grid>
    </Grid>
</Window>
```

**Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build errors from code-behind referencing removed elements (`ActionButtonPanel`, `ScrollViewer`). That's expected — we'll fix the code-behind in the next tasks.

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml
git commit -m "feat: rewrite SettingsWindow XAML as borderless overlay with pagination and modal"
```

---

### Task 4: Rewrite SettingsWindow.xaml.cs — Core Structure, Sidebar, Close Button

This is the first of 3 tasks that rebuild the code-behind. This task covers: window sizing, sidebar tabs, close button, and the core dwell wiring. Content builders and pagination come next.

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

**Step 1: Replace the code-behind with the new core structure**

Replace the entire file with:

```csharp
namespace EyeCtrl.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public partial class SettingsWindow : Window
{
    // ────── Constants ──────

    private const double ControlBtnRadius = 40;
    private const double TabBtnRadius = 70;
    private const double PagBtnRadius = 50;
    private const double ModalBtnRadius = 60;

    private static readonly Color BtnBase = Color.FromArgb(200, 60, 60, 80);
    private static readonly Color TabActive = Color.FromArgb(200, 40, 90, 160);
    private static readonly Color TabInactive = Color.FromArgb(200, 50, 50, 50);
    private static readonly Color ResetBase = Color.FromArgb(200, 128, 80, 32);
    private static readonly Color ModalYes = Color.FromArgb(200, 30, 100, 30);
    private static readonly Color ModalNo = Color.FromArgb(200, 100, 30, 30);
    private static readonly Color CloseBase = Color.FromArgb(200, 100, 30, 30);

    // ────── State ──────

    private readonly EyeCtrlSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly int _dwellMs;

    private readonly DwellDetector _tabDwell = new();
    private readonly DwellDetector _controlDwell = new();
    private readonly DwellDetector _pagDwell = new();
    private readonly DwellDetector _modalDwell = new();

    private readonly Dictionary<string, Border> _tabButtons = new();
    private readonly Dictionary<string, Border> _controlBorders = new();
    private readonly Dictionary<string, Action> _controlActions = new();
    private readonly Dictionary<string, Border> _pagButtons = new();
    private readonly Dictionary<string, Border> _modalButtons = new();

    private string _activeTab = "Cursor";
    private int _currentPage;
    private int _totalPages;

    private Action? _modalYesAction;

    // Called by App.xaml.cs after settings change to update overlays
    public event Action<EyeCtrlSettings>? SettingsChanged;

    private static readonly (string Id, string Icon, string Label)[] TabDefs =
    [
        ("Cursor",   "◎", "Cursor"),
        ("Dwell",    "⏱", "Dwell Times"),
        ("Zoom",     "⊕", "Zoom"),
        ("Keyboard", "⌨", "Keyboard"),
        ("Toolbar",  "▣", "Toolbar"),
        ("Theme",    "◐", "Theme"),
        ("General",  "⚙", "General")
    ];

    public SettingsWindow(EyeCtrlSettings settings, SettingsService settingsService)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _dwellMs = settings.Dwell.ToolbarButtonDwellMs;

        // Size to ~90% of screen
        Width = SystemParameters.PrimaryScreenWidth * 0.9;
        Height = SystemParameters.PrimaryScreenHeight * 0.9;

        // Wire dwell events
        _tabDwell.TargetActivated += id => Dispatcher.Invoke(() => ShowTab(id));
        _tabDwell.TargetProgress += OnTabProgress;

        _controlDwell.TargetActivated += id => Dispatcher.Invoke(() =>
        {
            if (_controlActions.TryGetValue(id, out var action)) action();
        });
        _controlDwell.TargetProgress += OnControlProgress;

        _pagDwell.TargetActivated += id => Dispatcher.Invoke(() =>
        {
            if (id == "pag_prev") GoPage(_currentPage - 1);
            else if (id == "pag_next") GoPage(_currentPage + 1);
        });
        _pagDwell.TargetProgress += OnPagProgress;

        _modalDwell.TargetActivated += id => Dispatcher.Invoke(() =>
        {
            if (id == "modal_yes") { _modalYesAction?.Invoke(); HideModal(); }
            else if (id == "modal_no") HideModal();
        });
        _modalDwell.TargetProgress += OnModalProgress;

        // Close button
        CloseButton.MouseLeftButtonDown += (_, _) => Close();
        _tabDwell.AddTarget(new DwellTarget("close", new ScreenPoint(0, 0), TabBtnRadius, _dwellMs));

        BuildTabButtons();
        ShowTab("Cursor");
    }

    // ────── Sidebar tabs ──────

    private void BuildTabButtons()
    {
        foreach (var (id, icon, label) in TabDefs)
        {
            var border = new Border
            {
                Height = 80,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(TabInactive),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 16, 0),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = icon,
                            Foreground = Brushes.White,
                            FontSize = 24,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 12, 0)
                        },
                        new TextBlock
                        {
                            Text = label,
                            Foreground = Brushes.White,
                            FontSize = 18,
                            FontWeight = FontWeights.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };

            border.MouseLeftButtonDown += (_, _) => ShowTab(id);
            _tabButtons[id] = border;
            TabButtonPanel.Children.Add(border);
            _tabDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), TabBtnRadius, _dwellMs));
        }
    }

    // ────── Tab switching ──────

    private void ShowTab(string tabId)
    {
        _activeTab = tabId;
        _currentPage = 0;

        foreach (var (id, border) in _tabButtons)
            border.Background = new SolidColorBrush(id == tabId ? TabActive : TabInactive);

        RebuildContent();
    }

    // ────── Live apply + debounced save ──────

    private void ApplyAndSave()
    {
        SettingsChanged?.Invoke(_settings);
        _settingsService.SaveDebounced(_settings);
    }

    // ────── Gaze update ──────

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        // Close button position
        try
        {
            var cPos = CloseButton.PointToScreen(
                new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
            _tabDwell.UpdateTargetCenter("close", new ScreenPoint(cPos.X, cPos.Y));
        }
        catch { }

        UpdatePositions(_tabButtons, _tabDwell);
        UpdatePositions(_controlBorders, _controlDwell);
        UpdatePositions(_pagButtons, _pagDwell);

        _tabDwell.Update(screenPoint, timestampMicroseconds);
        _controlDwell.Update(screenPoint, timestampMicroseconds);
        _pagDwell.Update(screenPoint, timestampMicroseconds);

        if (ModalOverlay.Visibility == Visibility.Visible)
        {
            UpdatePositions(_modalButtons, _modalDwell);
            _modalDwell.Update(screenPoint, timestampMicroseconds);
        }
    }

    private static void UpdatePositions(Dictionary<string, Border> borders, DwellDetector detector)
    {
        foreach (var (id, border) in borders)
        {
            try
            {
                var pos = border.PointToScreen(
                    new Point(border.ActualWidth / 2, border.ActualHeight / 2));
                detector.UpdateTargetCenter(id, new ScreenPoint(pos.X, pos.Y));
            }
            catch { }
        }
    }

    // ────── Dwell visual feedback ──────

    private void OnTabProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (targetId == "close")
            {
                CloseButton.Background = new SolidColorBrush(
                    Blend(CloseBase, Colors.White, progress * 0.5));
                if (progress >= 1.0) Close();
                return;
            }
            if (!_tabButtons.TryGetValue(targetId, out var border)) return;
            if (targetId == _activeTab) return;
            border.Background = new SolidColorBrush(Blend(TabInactive, TabActive, progress));
        });
    }

    private void OnControlProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_controlBorders.TryGetValue(targetId, out var border)) return;
            var highlight = Color.FromArgb(200, 0, 180, 0);
            border.Background = new SolidColorBrush(Blend(BtnBase, highlight, progress));
        });
    }

    private void OnPagProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_pagButtons.TryGetValue(targetId, out var border)) return;
            border.Background = new SolidColorBrush(Blend(BtnBase, Colors.White, progress * 0.4));
        });
    }

    private void OnModalProgress(string targetId, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_modalButtons.TryGetValue(targetId, out var border)) return;
            var baseC = targetId == "modal_yes" ? ModalYes : ModalNo;
            border.Background = new SolidColorBrush(Blend(baseC, Colors.White, progress * 0.5));
        });
    }

    private static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A * (1 - t) + to.A * t),
            (byte)(from.R * (1 - t) + to.R * t),
            (byte)(from.G * (1 - t) + to.G * t),
            (byte)(from.B * (1 - t) + to.B * t));
    }

    // ────── Modal confirmation ──────

    private void ShowModal(string message, Action onYes)
    {
        _modalYesAction = onYes;
        ModalMessage.Text = message;
        ModalButtonPanel.Children.Clear();
        _modalButtons.Clear();
        _modalDwell.ClearTargets();

        var yesBtn = MakeModalButton("modal_yes", "✓ Yes", ModalYes);
        var noBtn = MakeModalButton("modal_no", "✕ No", ModalNo);

        ModalButtonPanel.Children.Add(yesBtn);
        ModalButtonPanel.Children.Add(noBtn);

        ModalOverlay.Visibility = Visibility.Visible;
    }

    private void HideModal()
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        _modalDwell.ClearTargets();
        _modalButtons.Clear();
        _modalYesAction = null;
    }

    private Border MakeModalButton(string id, string text, Color bg)
    {
        var border = new Border
        {
            Width = 140,
            Height = 64,
            Margin = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(bg),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        border.MouseLeftButtonDown += (_, _) =>
        {
            if (id == "modal_yes") { _modalYesAction?.Invoke(); HideModal(); }
            else HideModal();
        };

        _modalButtons[id] = border;
        _modalDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ModalBtnRadius, _dwellMs));
        return border;
    }

    // Placeholder — filled in Task 5
    private void RebuildContent() { }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: Should compile (RebuildContent is a placeholder). Warnings about unused variables are OK.

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: rewrite SettingsWindow code-behind — core structure, sidebar, close, modal"
```

---

### Task 5: SettingsWindow.xaml.cs — Pagination and Control Builders

This task adds the pagination system and all control builder methods (sliders, combos, toggles, color picker, reset buttons).

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

**Step 1: Replace the placeholder `RebuildContent()` and add all control builders**

Add the following methods to the class (replace the placeholder `RebuildContent`):

```csharp
    // ────── Pagination ──────

    private List<List<Action>> _pages = new();

    private void RebuildContent()
    {
        _controlDwell.ClearTargets();
        _controlBorders.Clear();
        _controlActions.Clear();
        ContentPanel.Children.Clear();

        _pages = BuildPages();
        _totalPages = _pages.Count;
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _totalPages - 1));

        RenderCurrentPage();
        RenderPagination();
    }

    private void RenderCurrentPage()
    {
        _controlDwell.ClearTargets();
        _controlBorders.Clear();
        _controlActions.Clear();
        ContentPanel.Children.Clear();

        if (_currentPage < _pages.Count)
        {
            foreach (var builder in _pages[_currentPage])
                builder();
        }
    }

    private void GoPage(int page)
    {
        if (page < 0 || page >= _totalPages) return;
        _currentPage = page;
        RenderCurrentPage();
        RenderPagination();
    }

    private void RenderPagination()
    {
        PaginationPanel.Children.Clear();
        _pagButtons.Clear();
        _pagDwell.ClearTargets();

        if (_totalPages <= 1) return;

        var prev = MakePagButton("pag_prev", "◀");
        var indicator = new TextBlock
        {
            Text = $"{_currentPage + 1} / {_totalPages}",
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 20, 0),
            MinWidth = 80,
            TextAlignment = TextAlignment.Center
        };
        var next = MakePagButton("pag_next", "▶");

        // Dim buttons at boundary
        if (_currentPage <= 0) prev.Opacity = 0.3;
        if (_currentPage >= _totalPages - 1) next.Opacity = 0.3;

        PaginationPanel.Children.Add(prev);
        PaginationPanel.Children.Add(indicator);
        PaginationPanel.Children.Add(next);
    }

    private Border MakePagButton(string id, string text)
    {
        var btn = new Border
        {
            Width = 80,
            Height = 64,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(BtnBase),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        btn.MouseLeftButtonDown += (_, _) =>
        {
            if (id == "pag_prev") GoPage(_currentPage - 1);
            else GoPage(_currentPage + 1);
        };

        _pagButtons[id] = btn;
        _pagDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), PagBtnRadius, _dwellMs));
        return btn;
    }

    // ────── Page builders per tab ──────

    // Each tab returns a List<List<Action>> where each inner list = one page of control builders.
    // We estimate ~4 controls per page for the available content height.

    private List<List<Action>> BuildPages()
    {
        return _activeTab switch
        {
            "Cursor" => BuildCursorPages(),
            "Dwell" => BuildDwellPages(),
            "Zoom" => BuildZoomPages(),
            "Keyboard" => BuildKeyboardPages(),
            "Toolbar" => BuildToolbarPages(),
            "Theme" => BuildThemePages(),
            "General" => BuildGeneralPages(),
            _ => new()
        };
    }

    private List<List<Action>> BuildCursorPages()
    {
        return
        [
            // Page 1: filter, smoothing, snap, color
            [
                () => AddCombo("c_filter", "Filter Mode",
                    Enum.GetNames<Core.Models.GazeFilterMode>(),
                    () => _settings.Cursor.FilterMode.ToString(),
                    v => { _settings.Cursor.FilterMode = Enum.Parse<Core.Models.GazeFilterMode>(v); ApplyAndSave(); }),
                () => AddSlider("c_smooth", "Smoothing Factor", 0, 1, 0.1, 1,
                    () => _settings.Cursor.SmoothingFactor,
                    v => { _settings.Cursor.SmoothingFactor = v; ApplyAndSave(); }),
                () => AddSlider("c_snap", "Snap Radius (px)", 10, 100, 5, 0,
                    () => _settings.Cursor.SnapRadiusPixels,
                    v => { _settings.Cursor.SnapRadiusPixels = (int)v; ApplyAndSave(); }),
                () => AddColorPicker("c_color", "Circle Color",
                    () => _settings.Cursor.CircleColor,
                    v => { _settings.Cursor.CircleColor = v; ApplyAndSave(); }),
            ],
            // Page 2: opacity, thickness, high contrast, reset
            [
                () => AddSlider("c_opacity", "Circle Opacity", 0.1, 1, 0.1, 1,
                    () => _settings.Cursor.CircleOpacity,
                    v => { _settings.Cursor.CircleOpacity = v; ApplyAndSave(); }),
                () => AddSlider("c_thick", "Circle Thickness", 1, 6, 0.5, 1,
                    () => _settings.Cursor.CircleThickness,
                    v => { _settings.Cursor.CircleThickness = v; ApplyAndSave(); }),
                () => AddToggle("c_hc", "High Contrast Mode",
                    () => _settings.Cursor.CircleHighContrast,
                    v => { _settings.Cursor.CircleHighContrast = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Cursor Settings", () =>
                    ShowModal("Reset Cursor settings to defaults?", () =>
                    {
                        var defaults = new CursorSettings();
                        _settings.Cursor.FilterMode = defaults.FilterMode;
                        _settings.Cursor.SmoothingFactor = defaults.SmoothingFactor;
                        _settings.Cursor.SnapRadiusPixels = defaults.SnapRadiusPixels;
                        _settings.Cursor.CircleColor = defaults.CircleColor;
                        _settings.Cursor.CircleOpacity = defaults.CircleOpacity;
                        _settings.Cursor.CircleThickness = defaults.CircleThickness;
                        _settings.Cursor.CircleHighContrast = defaults.CircleHighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildDwellPages()
    {
        return
        [
            // Page 1: first 4 dwell sliders
            [
                () => AddSlider("d_fix", "Fixation Detection (ms)", 50, 500, 25, 0,
                    () => _settings.Dwell.FixationDetectionMs,
                    v => { _settings.Dwell.FixationDetectionMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_act", "Activation Button (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ActivationButtonDwellMs,
                    v => { _settings.Dwell.ActivationButtonDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_menu", "Action Menu (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ActionMenuDwellMs,
                    v => { _settings.Dwell.ActionMenuDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_zoom", "Zoom Action Menu (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ZoomActionMenuDwellMs,
                    v => { _settings.Dwell.ZoomActionMenuDwellMs = (int)v; ApplyAndSave(); }),
            ],
            // Page 2: remaining dwell sliders + fixation radius + reset
            [
                () => AddSlider("d_tool", "Toolbar / Settings (ms)", 200, 1500, 50, 0,
                    () => _settings.Dwell.ToolbarButtonDwellMs,
                    v => { _settings.Dwell.ToolbarButtonDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_key", "Keyboard Key (ms)", 100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardKeyDwellMs,
                    v => { _settings.Dwell.KeyboardKeyDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_pred", "Prediction (ms)", 100, 1000, 25, 0,
                    () => _settings.Dwell.KeyboardPredictionDwellMs,
                    v => { _settings.Dwell.KeyboardPredictionDwellMs = (int)v; ApplyAndSave(); }),
                () => AddSlider("d_rad", "Fixation Radius (px)", 20, 120, 5, 0,
                    () => _settings.Dwell.FixationRadiusPixels,
                    v => { _settings.Dwell.FixationRadiusPixels = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Dwell Settings", () =>
                    ShowModal("Reset Dwell settings to defaults?", () =>
                    {
                        var d = new DwellSettings();
                        _settings.Dwell.FixationDetectionMs = d.FixationDetectionMs;
                        _settings.Dwell.ActivationButtonDwellMs = d.ActivationButtonDwellMs;
                        _settings.Dwell.ActionMenuDwellMs = d.ActionMenuDwellMs;
                        _settings.Dwell.ZoomActionMenuDwellMs = d.ZoomActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.FixationRadiusPixels;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildZoomPages()
    {
        return
        [
            [
                () => AddToggle("z_en", "Enable Zoom Mode",
                    () => _settings.Zoom.Enabled,
                    v => { _settings.Zoom.Enabled = v; ApplyAndSave(); }),
                () => AddSlider("z_rad", "Circle Radius (px)", 50, 300, 10, 0,
                    () => _settings.Zoom.CircleRadiusPixels,
                    v => { _settings.Zoom.CircleRadiusPixels = (int)v; ApplyAndSave(); }),
                () => AddSlider("z_mag", "Magnification Level", 1.5, 5, 0.5, 1,
                    () => _settings.Zoom.MagnificationLevel,
                    v => { _settings.Zoom.MagnificationLevel = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Zoom Settings", () =>
                    ShowModal("Reset Zoom settings to defaults?", () =>
                    {
                        var z = new ZoomSettings();
                        _settings.Zoom.Enabled = z.Enabled;
                        _settings.Zoom.CircleRadiusPixels = z.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = z.MagnificationLevel;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildKeyboardPages()
    {
        return
        [
            [
                () => AddCombo("k_layout", "Active Layout",
                    ["QWERTY", "QWERTZ", "AZERTY"],
                    () => _settings.Keyboard.ActiveLayout,
                    v => { _settings.Keyboard.ActiveLayout = v; ApplyAndSave(); }),
                () => AddSlider("k_size", "Key Size Multiplier", 0.5, 2.0, 0.1, 1,
                    () => _settings.Keyboard.KeySizeMultiplier,
                    v => { _settings.Keyboard.KeySizeMultiplier = v; ApplyAndSave(); }),
                () => AddToggle("k_pred", "Word Prediction",
                    () => _settings.Keyboard.PredictionEnabled,
                    v => { _settings.Keyboard.PredictionEnabled = v; ApplyAndSave(); }),
                () => AddCombo("k_edge", "Screen Edge", ["Bottom", "Top"],
                    () => _settings.Keyboard.ScreenEdge,
                    v => { _settings.Keyboard.ScreenEdge = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Keyboard Settings", () =>
                    ShowModal("Reset Keyboard settings to defaults?", () =>
                    {
                        var k = new KeyboardSettings();
                        _settings.Keyboard.ActiveLayout = k.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = k.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = k.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = k.LanguagePack;
                        _settings.Keyboard.ScreenEdge = k.ScreenEdge;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildToolbarPages()
    {
        return
        [
            [
                () => AddToggle("t_dock", "Dock to Screen Edge",
                    () => _settings.Toolbar.IsDocked,
                    v => { _settings.Toolbar.IsDocked = v; ApplyAndSave(); }),
                () => AddCombo("t_edge", "Docked Edge", ["Top", "Bottom", "Left", "Right"],
                    () => _settings.Toolbar.DockedEdge,
                    v => { _settings.Toolbar.DockedEdge = v; ApplyAndSave(); }),
                () => AddSlider("t_size", "Button Size", 32, 120, 4, 0,
                    () => _settings.Toolbar.Size,
                    v => { _settings.Toolbar.Size = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Toolbar Settings", () =>
                    ShowModal("Reset Toolbar settings to defaults?", () =>
                    {
                        var t = new ToolbarSettings();
                        _settings.Toolbar.IsDocked = t.IsDocked;
                        _settings.Toolbar.DockedEdge = t.DockedEdge;
                        _settings.Toolbar.Size = t.Size;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildThemePages()
    {
        return
        [
            [
                () => AddCombo("th_mode", "Theme Mode", ["System", "Light", "Dark"],
                    () => _settings.Theme.Mode,
                    v => { _settings.Theme.Mode = v; ApplyAndSave(); }),
                () => AddSlider("th_op", "Overlay Opacity", 0.3, 1.0, 0.1, 1,
                    () => _settings.Theme.OverlayOpacity,
                    v => { _settings.Theme.OverlayOpacity = v; ApplyAndSave(); }),
                () => AddToggle("th_hc", "High Contrast",
                    () => _settings.Theme.HighContrast,
                    v => { _settings.Theme.HighContrast = v; ApplyAndSave(); }),
                () => AddResetButton("Reset Theme Settings", () =>
                    ShowModal("Reset Theme settings to defaults?", () =>
                    {
                        var th = new ThemeSettings();
                        _settings.Theme.Mode = th.Mode;
                        _settings.Theme.OverlayOpacity = th.OverlayOpacity;
                        _settings.Theme.HighContrast = th.HighContrast;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    private List<List<Action>> BuildGeneralPages()
    {
        return
        [
            [
                () => AddToggle("g_boot", "Start on Windows Boot",
                    () => _settings.General.StartOnBoot,
                    v => { _settings.General.StartOnBoot = v; ApplyAndSave(); }),
                () => AddToggle("g_pause", "Start Paused",
                    () => _settings.General.StartPaused,
                    v => { _settings.General.StartPaused = v; ApplyAndSave(); }),
                () => AddResetButton("Reset General Settings", () =>
                    ShowModal("Reset General settings to defaults?", () =>
                    {
                        var g = new GeneralSettings();
                        _settings.General.StartOnBoot = g.StartOnBoot;
                        _settings.General.StartPaused = g.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    })),
                () => AddResetButton("⚠ Reset ALL Settings", () =>
                    ShowModal("Reset ALL settings to defaults?\nThis cannot be undone.", () =>
                    {
                        var d = new EyeCtrlSettings();
                        _settings.Cursor.FilterMode = d.Cursor.FilterMode;
                        _settings.Cursor.SmoothingFactor = d.Cursor.SmoothingFactor;
                        _settings.Cursor.SnapRadiusPixels = d.Cursor.SnapRadiusPixels;
                        _settings.Cursor.CircleColor = d.Cursor.CircleColor;
                        _settings.Cursor.CircleOpacity = d.Cursor.CircleOpacity;
                        _settings.Cursor.CircleThickness = d.Cursor.CircleThickness;
                        _settings.Cursor.CircleHighContrast = d.Cursor.CircleHighContrast;
                        _settings.Dwell.FixationDetectionMs = d.Dwell.FixationDetectionMs;
                        _settings.Dwell.ActivationButtonDwellMs = d.Dwell.ActivationButtonDwellMs;
                        _settings.Dwell.ActionMenuDwellMs = d.Dwell.ActionMenuDwellMs;
                        _settings.Dwell.ZoomActionMenuDwellMs = d.Dwell.ZoomActionMenuDwellMs;
                        _settings.Dwell.ToolbarButtonDwellMs = d.Dwell.ToolbarButtonDwellMs;
                        _settings.Dwell.KeyboardKeyDwellMs = d.Dwell.KeyboardKeyDwellMs;
                        _settings.Dwell.KeyboardPredictionDwellMs = d.Dwell.KeyboardPredictionDwellMs;
                        _settings.Dwell.FixationRadiusPixels = d.Dwell.FixationRadiusPixels;
                        _settings.Zoom.Enabled = d.Zoom.Enabled;
                        _settings.Zoom.CircleRadiusPixels = d.Zoom.CircleRadiusPixels;
                        _settings.Zoom.MagnificationLevel = d.Zoom.MagnificationLevel;
                        _settings.Keyboard.ActiveLayout = d.Keyboard.ActiveLayout;
                        _settings.Keyboard.KeySizeMultiplier = d.Keyboard.KeySizeMultiplier;
                        _settings.Keyboard.PredictionEnabled = d.Keyboard.PredictionEnabled;
                        _settings.Keyboard.LanguagePack = d.Keyboard.LanguagePack;
                        _settings.Keyboard.ScreenEdge = d.Keyboard.ScreenEdge;
                        _settings.Toolbar.IsDocked = d.Toolbar.IsDocked;
                        _settings.Toolbar.DockedEdge = d.Toolbar.DockedEdge;
                        _settings.Toolbar.Size = d.Toolbar.Size;
                        _settings.Theme.Mode = d.Theme.Mode;
                        _settings.Theme.OverlayOpacity = d.Theme.OverlayOpacity;
                        _settings.Theme.HighContrast = d.Theme.HighContrast;
                        _settings.General.StartOnBoot = d.General.StartOnBoot;
                        _settings.General.StartPaused = d.General.StartPaused;
                        ApplyAndSave();
                        RebuildContent();
                    })),
            ]
        ];
    }

    // ────── Generic dwellable control builders (scaled up) ──────

    private void AddSlider(string id, string label, double min, double max, double step,
        int decimals, Func<double> get, Action<double> set)
    {
        AddLabel(label);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };

        var valueText = new TextBlock
        {
            Text = FormatVal(get(), decimals),
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Width = 140,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var minus = MakeControlButton(id + "_dn", "\u2212");
        var plus = MakeControlButton(id + "_up", "+");

        _controlActions[id + "_dn"] = () =>
        {
            var v = Math.Round(Math.Max(min, get() - step), decimals);
            set(v);
            valueText.Text = FormatVal(v, decimals);
        };
        _controlActions[id + "_up"] = () =>
        {
            var v = Math.Round(Math.Min(max, get() + step), decimals);
            set(v);
            valueText.Text = FormatVal(v, decimals);
        };

        row.Children.Add(minus);
        row.Children.Add(valueText);
        row.Children.Add(plus);
        ContentPanel.Children.Add(row);
    }

    private void AddCombo(string id, string label, string[] options,
        Func<string> get, Action<string> set)
    {
        AddLabel(label);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };

        var valueText = new TextBlock
        {
            Text = get(),
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Width = 200,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var prev = MakeControlButton(id + "_prev", "\u25C0");
        var next = MakeControlButton(id + "_next", "\u25B6");

        _controlActions[id + "_prev"] = () =>
        {
            var idx = Array.IndexOf(options, get());
            idx = (idx - 1 + options.Length) % options.Length;
            set(options[idx]);
            valueText.Text = options[idx];
        };
        _controlActions[id + "_next"] = () =>
        {
            var idx = Array.IndexOf(options, get());
            idx = (idx + 1) % options.Length;
            set(options[idx]);
            valueText.Text = options[idx];
        };

        row.Children.Add(prev);
        row.Children.Add(valueText);
        row.Children.Add(next);
        ContentPanel.Children.Add(row);
    }

    private void AddToggle(string id, string label, Func<bool> get, Action<bool> set)
    {
        var border = new Border
        {
            Height = 64,
            Margin = new Thickness(0, 4, 0, 12),
            Padding = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var text = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        border.Child = text;

        void UpdateVisual()
        {
            var on = get();
            text.Text = (on ? "\u2611 " : "\u2610 ") + label;
            border.Background = new SolidColorBrush(on
                ? Color.FromArgb(200, 30, 100, 30)
                : Color.FromArgb(200, 80, 40, 40));
        }

        UpdateVisual();

        _controlActions[id] = () =>
        {
            set(!get());
            UpdateVisual();
        };

        border.MouseLeftButtonDown += (_, _) => _controlActions[id]();
        _controlBorders[id] = border;
        _controlDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ControlBtnRadius, _dwellMs));
        ContentPanel.Children.Add(border);
    }

    private static readonly (string Hex, string Name)[] ColorPresets =
    [
        ("#00FF00", "Green"), ("#0088FF", "Blue"), ("#FF0000", "Red"),
        ("#FFFF00", "Yellow"), ("#00FFFF", "Cyan"), ("#FF00FF", "Magenta"),
        ("#FFFFFF", "White"), ("#FF8800", "Orange")
    ];

    private void AddColorPicker(string id, string label, Func<string> get, Action<string> set)
    {
        AddLabel(label);

        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) };

        for (int i = 0; i < ColorPresets.Length; i++)
        {
            var (hex, name) = ColorPresets[i];
            var swatchId = $"{id}_{i}";
            var color = (Color)ColorConverter.ConvertFromString(hex);

            var swatch = new Border
            {
                Width = 72,
                Height = 60,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(3),
                BorderBrush = hex.Equals(get(), StringComparison.OrdinalIgnoreCase)
                    ? Brushes.White : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name
            };

            var capturedHex = hex;
            var capturedSwatch = swatch;
            _controlActions[swatchId] = () =>
            {
                set(capturedHex);
                foreach (UIElement child in row.Children)
                    if (child is Border b) b.BorderBrush = Brushes.Transparent;
                capturedSwatch.BorderBrush = Brushes.White;
            };

            swatch.MouseLeftButtonDown += (_, _) => _controlActions[swatchId]();
            _controlBorders[swatchId] = swatch;
            _controlDwell.AddTarget(new DwellTarget(swatchId, new ScreenPoint(0, 0), 30, _dwellMs));
            row.Children.Add(swatch);
        }

        ContentPanel.Children.Add(row);
    }

    private void AddResetButton(string label, Action onClick)
    {
        var id = $"reset_{label.GetHashCode():X}";
        var border = new Border
        {
            Width = 260,
            Height = 64,
            Margin = new Thickness(0, 20, 0, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(ResetBase),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        _controlActions[id] = onClick;
        border.MouseLeftButtonDown += (_, _) => onClick();
        _controlBorders[id] = border;
        _controlDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ControlBtnRadius, _dwellMs));
        ContentPanel.Children.Add(border);
    }

    // ────── Shared helpers ──────

    private void AddLabel(string text)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            FontSize = 18,
            Margin = new Thickness(0, 8, 0, 2)
        });
    }

    private Border MakeControlButton(string id, string text)
    {
        var btn = new Border
        {
            Width = 80,
            Height = 64,
            Margin = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(BtnBase),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        btn.MouseLeftButtonDown += (_, _) =>
        {
            if (_controlActions.TryGetValue(id, out var action)) action();
        };

        _controlBorders[id] = btn;
        _controlDwell.AddTarget(new DwellTarget(id, new ScreenPoint(0, 0), ControlBtnRadius, _dwellMs));
        return btn;
    }

    private static string FormatVal(double v, int decimals) =>
        decimals == 0 ? ((int)v).ToString() : v.ToString($"F{decimals}");
```

**Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS

**Step 3: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: add pagination, control builders, reset buttons, live-apply to SettingsWindow"
```

---

### Task 6: Update App.xaml.cs Wiring

The settings window now uses `SettingsChanged` instead of `SettingsSaved`, edits the live settings directly, and no longer needs a deep clone or save-then-close workflow.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Update OpenSettings() to use SettingsChanged instead of SettingsSaved**

In `App.xaml.cs`, find the `OpenSettings()` method (around line 298) and change it to:

```csharp
private void OpenSettings()
{
    if (_settingsWindow != null) return;

    _settingsWindow = new SettingsWindow(_settings, _settingsService);
    _settingsWindow.SettingsChanged += OnSettingsSaved;
    _settingsWindow.Closed += (_, _) => _settingsWindow = null;
    _settingsWindow.Show();
}
```

The only change is `SettingsSaved` → `SettingsChanged` on the event subscription. `OnSettingsSaved` callback stays the same since it already handles applying settings to overlays.

**Step 2: Verify it compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: PASS

**Step 3: Run all tests**

Run: `dotnet test -v n`
Expected: All pass.

**Step 4: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire SettingsChanged event in App.xaml.cs for live-apply settings"
```

---

### Task 7: Manual Testing & Polish

**Step 1: Build and launch**

Run: `dotnet build src/EyeCtrl.App && dotnet run --project src/EyeCtrl.App`

**Step 2: Verify visually**

Check each item:
- [ ] Settings overlay opens at ~90% screen, borderless, dark, topmost
- [ ] 7 tabs in left sidebar with icons + labels, dwell-enabled
- [ ] Close button (✕) at top-right of sidebar, dwellable
- [ ] Cursor tab: 2 pages, ◀ 1/2 ▶ pagination visible
- [ ] Dwell tab: 2 pages with 4+4 sliders + reset
- [ ] Zoom, Keyboard, Toolbar, Theme: 1 page each, no pagination bar
- [ ] General tab: 1 page with 2 toggles + per-tab reset + Reset All
- [ ] All controls (sliders, combos, toggles, swatches) are dwell-enabled with progress feedback
- [ ] Changes apply immediately (e.g. changing cursor color updates the cursor circle in real-time)
- [ ] Reset per-tab shows confirmation modal, resets only that tab's settings
- [ ] Reset All shows confirmation modal with warning, resets everything
- [ ] After reset, current tab content rebuilds with default values

**Step 3: Fix any layout issues**

If controls are cut off or pages are too tall/short, adjust the number of controls per page in the `Build*Pages()` methods. The target is ~4 controls per page for comfortable spacing.

**Step 4: Commit any fixes**

```bash
git add -u
git commit -m "fix: polish settings overlay layout after manual testing"
```

---

### Task 8: Run Full Test Suite & Final Commit

**Step 1: Run all tests**

Run: `dotnet test -v n`
Expected: All pass (existing + new debounce + defaults tests).

**Step 2: Verify build**

Run: `dotnet build`
Expected: Clean build, no warnings.

**Step 3: Final commit if any outstanding changes**

```bash
git status
# If any changes:
git add -u
git commit -m "chore: final cleanup for settings redesign"
```
