# SettingsWindow Refactoring Plan

**Current State:** 1190 lines
**Target:** ~400-500 lines (60% reduction)
**Status:** Planning Phase

## Problem Analysis

The SettingsWindow has grown despite MVVM refactoring because:
1. **Control builders** (AddSlider, AddToggle, AddColorPicker, etc.) contain repetitive WPF UI construction
2. **Page builders** (BuildCursorPages, BuildDwellPages, etc.) are verbose with repetitive patterns
3. **No reusable UI components** - each control type is built from scratch each time
4. **Mixed concerns** - View construction, styling, and dwell registration all in one place

## Proposed Solution: Custom Dwell-Enabled Controls

Create reusable WPF UserControls with built-in dwell support:

### Phase 1: Custom Control Library (New Project/Namespace)

Create `EyeCtrl.App.Controls` namespace with:

#### 1.1 DwellSlider Control (~80 lines)
```csharp
public class DwellSlider : UserControl
{
    // Dependency Properties
    public static readonly DependencyProperty LabelProperty = ...
    public static readonly DependencyProperty ValueProperty = ...
    public static readonly DependencyProperty MinimumProperty = ...
    public static readonly DependencyProperty MaximumProperty = ...
    public static readonly DependencyProperty StepProperty = ...

    // Dwell Support
    public event Action<string, PhysicalPoint>? RegisterDwellTarget;

    // Internally creates: Label + Down Button + Value Display + Up Button
    // Exposes: Value (two-way binding), styling properties
    // Handles: Mouse fallback, visual state updates
}
```

**Benefits:**
- Eliminates `AddSlider` method (currently ~40 lines)
- Used 50+ times across page builders → saves ~200+ lines
- Reusable across different settings windows

#### 1.2 DwellToggle Control (~60 lines)
```csharp
public class DwellToggle : UserControl
{
    // Dependency Properties
    public static readonly DependencyProperty LabelProperty = ...
    public static readonly DependencyProperty IsCheckedProperty = ...

    // Dwell Support
    public event Action<string, PhysicalPoint>? RegisterDwellTarget;

    // Internally creates: Checkbox icon + Label
    // Exposes: IsChecked (two-way binding)
}
```

**Benefits:**
- Eliminates `AddToggle` method (currently ~40 lines)
- Used 10+ times → saves ~100+ lines

#### 1.3 DwellComboBox Control (~90 lines)
```csharp
public class DwellComboBox : UserControl
{
    // Dependency Properties
    public static readonly DependencyProperty LabelProperty = ...
    public static readonly DependencyProperty ItemsProperty = ...
    public static readonly DependencyProperty SelectedItemProperty = ...

    // Dwell Support (registers each option as dwell target)
    public event Action<string, PhysicalPoint>? RegisterDwellTarget;

    // Internally creates: Label + Horizontal list of options
    // Exposes: SelectedItem (two-way binding)
}
```

**Benefits:**
- Eliminates `AddHorizontalList` method (currently ~80 lines)
- Used 15+ times → saves ~150+ lines

#### 1.4 DwellColorPicker Control (~70 lines)
```csharp
public class DwellColorPicker : UserControl
{
    // Dependency Properties
    public static readonly DependencyProperty LabelProperty = ...
    public static readonly DependencyProperty SelectedColorProperty = ...
    public static readonly DependencyProperty ColorPresetsProperty = ...

    // Dwell Support
    public event Action<string, PhysicalPoint>? RegisterDwellTarget;

    // Internally creates: Label + Color swatches grid
}
```

**Benefits:**
- Eliminates `AddColorPicker` method (currently ~50 lines)
- Used 2+ times → saves ~30+ lines

#### 1.5 DwellButton Control (~50 lines)
```csharp
public class DwellButton : UserControl
{
    // Dependency Properties
    public static readonly DependencyProperty TextProperty = ...
    public static readonly DependencyProperty CommandProperty = ...

    // Dwell Support
    public event Action<string, PhysicalPoint>? RegisterDwellTarget;
}
```

### Phase 2: XAML-Based Page Definitions

Move from procedural code to declarative XAML:

#### Before (C# code-behind, ~50 lines per page):
```csharp
private List<List<Action>> BuildCursorPages()
{
    return new List<List<Action>>
    {
        new List<Action>
        {
            () => AddToggle("highContrast", "High Contrast",
                () => _settings.Cursor.CircleHighContrast,
                v => _settings.Cursor.CircleHighContrast = v),
            () => AddSlider("circleSize", "Circle Size", 10, 500, 10, 0,
                () => _settings.Cursor.CircleSize,
                v => _settings.Cursor.CircleSize = v),
            // ... 8 more controls
        }
    };
}
```

#### After (XAML with data binding, ~20 lines per page):
```xaml
<controls:SettingsPage x:Key="CursorPage1" Title="Cursor">
    <controls:DwellToggle
        Label="High Contrast"
        IsChecked="{Binding Settings.Cursor.CircleHighContrast, Mode=TwoWay}"
        DwellId="highContrast"/>

    <controls:DwellSlider
        Label="Circle Size"
        Value="{Binding Settings.Cursor.CircleSize, Mode=TwoWay}"
        Minimum="10" Maximum="500" Step="10"
        DwellId="circleSize"/>

    <!-- ... 8 more controls -->
</controls:SettingsPage>
```

**Benefits:**
- Reduces `BuildCursorPages` from ~100 lines to XAML resource
- Eliminates 8 similar Build*Pages methods
- Better separation of concerns (UI in XAML, logic in ViewModel)
- Designer support for visual editing

### Phase 3: Automated Dwell Registration

Create `DwellControlHost` container that automatically handles registration:

```csharp
public class DwellControlHost : Panel
{
    private SettingsWindowViewModel _viewModel;

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        // Automatically discover child controls implementing IDwellTarget
        // Auto-register with ViewModel based on control's DwellId property
        // Update positions on layout changes
    }
}
```

**Benefits:**
- Eliminates manual `RegisterDwellTarget` + `AddControlTarget` calls
- Reduces boilerplate by ~500 lines total
- Cleaner, more maintainable code

## Implementation Estimate

### Line Count Breakdown

**Current: 1190 lines**

Removable sections:
- Control builder methods (AddSlider, AddToggle, etc.): ~250 lines
- Page builder methods (BuildCursorPages, etc.): ~350 lines
- MakeButton helper methods: ~80 lines
- Repetitive dwell registration: ~150 lines

**Total removable: ~830 lines**

Replacement:
- Custom control classes: ~350 lines (separate files)
- XAML page definitions: ~200 lines (XAML)
- DwellControlHost: ~80 lines (separate file)
- Remaining SettingsWindow code: ~360 lines

**Final SettingsWindow: ~360 lines (70% reduction)**

### Development Effort

- Phase 1 (Custom Controls): 2-3 days
  - Create 5 custom control classes
  - Implement dwell event interfaces
  - Add styling and visual states
  - Unit tests for each control

- Phase 2 (XAML Migration): 1-2 days
  - Convert Build*Pages to XAML resources
  - Update SettingsWindow to load XAML pages
  - Test data binding paths

- Phase 3 (Auto-registration): 1 day
  - Create DwellControlHost
  - Update controls to work with host
  - Integration testing

**Total: 4-6 days**

## Benefits Summary

1. **Maintainability**: Control logic encapsulated in reusable components
2. **Testability**: Each control can be unit tested independently
3. **Reusability**: Controls can be used in other dwell-enabled windows
4. **Readability**: XAML declarations clearer than procedural code
5. **Designer Support**: Visual editing of settings pages in XAML designer
6. **Separation of Concerns**: UI structure (XAML) separate from behavior (ViewModel)
7. **Line Count**: 70% reduction in SettingsWindow.xaml.cs (1190 → 360 lines)

## Risks & Mitigations

**Risk 1**: Breaking existing functionality during migration
- **Mitigation**: Incremental migration one page at a time, maintain parallel old/new code paths initially

**Risk 2**: Custom controls harder to debug than simple code
- **Mitigation**: Comprehensive unit tests, logging in controls, fallback to mouse testing

**Risk 3**: XAML binding complexity
- **Mitigation**: Use existing SettingsViewModel with INotifyPropertyChanged, test bindings incrementally

**Risk 4**: Performance impact from WPF control overhead
- **Mitigation**: Profile before/after, controls are only created on settings window open (not performance critical)

## Alternative Approach (Less Ambitious)

If custom controls are too complex, consider:

### Option B: Extract to Helper Classes (~40% reduction)

Create `SettingsPageBuilder` helper class:
```csharp
public class SettingsPageBuilder
{
    private readonly Panel _container;
    private readonly SettingsWindowViewModel _viewModel;

    public SettingsPageBuilder AddSlider(...) { /* centralized implementation */ }
    public SettingsPageBuilder AddToggle(...) { /* centralized implementation */ }
    // etc.
}
```

**Benefits:**
- Simpler to implement (1-2 days)
- Still removes ~400 lines from SettingsWindow
- Reusable builder pattern

**Drawbacks:**
- Still procedural code, not XAML
- Less reusable than custom controls
- No designer support

## Recommendation

**Proceed with Phase 1 (Custom Controls) first** as a proof of concept:
1. Implement DwellSlider control
2. Migrate one page (e.g., Zoom page with only 3 controls) to use it
3. Measure code reduction and complexity
4. Decide whether to continue with full migration or switch to Option B

This validates the approach with minimal risk and provides concrete metrics for decision-making.
