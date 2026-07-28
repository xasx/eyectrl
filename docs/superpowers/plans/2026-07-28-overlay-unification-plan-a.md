# Overlay Design Unification — Plan A (Token Foundation + Colour Migration) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every hardcoded colour literal in the EyeCtrl overlay UI with tokens from a single source of truth, so the 8 accent families, 10 background colours and 5 accent variants collapse into one validated palette.

**Architecture:** A `Tokens` static class in `EyeCtrl.Controls` holds one literal per colour. A `ResourceDictionary` in the same project re-exposes each as a `SolidColorBrush` via `{x:Static}`, so the C# and XAML views cannot drift. `EyeCtrl.App` merges that dictionary by pack URI. Everything downstream consumes tokens. Tokens live in `Controls`, not `App`, because the project reference runs `App → Controls → Core` — a dictionary under `src/EyeCtrl.App/` would be invisible to the shared dwell controls.

**Tech Stack:** C# / .NET 10 / WPF, xUnit v3 + NFluent (`Check.That`), Python 3 for the palette validator.

**Source spec:** `docs/design/overlay-unification-spec.md` — this plan implements §5 Phases 0–4 (steps 1–32). §4.1 is the palette, §9 the measured metrics.

**Scope boundaries — read before starting:**

- **Midnight only.** Ember and Contrast (§10) stay documented-but-unbuilt. No `ITheme`, no theme setting, no switching. This is deliberate: because the theme-change model chosen is *recreate overlays*, `{StaticResource}` is already the correct form, so a later theme-system plan can add `ITheme` **without revisiting any of the 18 XAML files this plan touches**. That decision is what keeps this plan small.
- **No halo.** The `StrokeHalo` token is *defined* here (Task 1) but nothing consumes it. §4.1.3's rendering change is Plan B, including the `DwellArcButton` halo arc that §5 step 5 lists under Phase 1 — it is the same kind of change as Phase 5 and belongs with it.
- **No radius or font work.** Radius unification is deferred (§8). §4.3's app-wide `FontFamily` is not in §5 and is out of scope; the redundant `FontFamily` on `CalibrationWindow.xaml:30` is therefore **left alone** (contrary to step 29's aside, which assumes §4.3 has landed).

**Preconditions — check these before Task 1.** Everything below assumes you are
working *in the worktree* `.worktrees/overlay-unification` on branch
`docs/overlay-unification`, where the spec and validator live. They are **not
present on `main`**:

```bash
git rev-parse --abbrev-ref HEAD          # -> docs/overlay-unification
test -f docs/design/overlay-unification-spec.md && echo spec ok
python tools/palette_validate.py         # -> ALL GATES PASS, exit 0
```

If the spec is missing you are on the wrong branch or in the wrong directory. Do
not proceed — every section reference in this plan will be unresolvable.

**Line numbers** in this plan are pinned to `main` at `b8295b6`. If `git log --oneline -1` shows something else, re-verify before editing — two PRs already shifted these once.

---

## File Structure

**New files:**

| Path | Responsibility |
|---|---|
| `src/EyeCtrl.Controls/Theme/Tokens.cs` | The only place a palette colour literal appears. Colours + frozen brushes + border thickness. |
| `src/EyeCtrl.Controls/Theme/OverlayStyles.xaml` | XAML view of `Tokens`, derived via `{x:Static}`. No literals. |
| `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs` | Drift guard: dictionary keys resolve and equal their `Tokens` counterpart. |
| `tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs` | Pins each token to its §4.1 value so an accidental edit fails the build. |
| `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs` | Scans migrated source for banned legacy literals. This is what makes the XAML edits test-driven. |

**Modified files** — 7 in `Controls`, 4 in `Core`, 4 code-behind in `App`, 12 XAML in `App`, 1 test:

`DwellArcButton.cs`, `DwellToggle.cs`, `DwellSlider.cs`, `DwellComboBox.cs`, `DwellColorPicker.cs`, `DwellLabel.cs`, `ColorBlend.cs` · `CursorSettings.cs`, `CalibrationSettings.cs`, `AutoSelectionSettings.cs`, `DragSettings.cs` · `KeyboardWindow.xaml.cs`, `ToolbarWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `NoteWindow.xaml.cs` · the 12 XAML files of Task 13 · `DragSettingsTests.cs` · plus `App.xaml`.

---

## Task 1: Tokens.cs — the single source of truth

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/Tokens.cs`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs`

`EyeCtrl.Controls.Tests` currently has **no test files** — this is its first. Create the `Theme/` folder.

> **On test packages:** `tests/Directory.Build.props` centrally supplies xUnit v3, NFluent and the runner, so this task needs no csproj edit. `Xunit.StaFact` is **not** centralised — it is referenced per-project — so Task 2 does add a `PackageReference`. Do not read this task as "csproj edits are never needed."

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Pins every palette token to its docs/design/overlay-unification-spec.md §4.1
/// value. These are validated by tools/palette_validate.py — changing one here
/// without re-running that script invalidates §9's measured tables.
/// </summary>
public class TokenValueTests
{
    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    [Theory]
    // Surfaces
    [InlineData(nameof(Tokens.SurfaceDark), "#FA1A1A2E")]
    [InlineData(nameof(Tokens.SurfaceMedium), "#FA2A2A44")]
    [InlineData(nameof(Tokens.SurfaceLight), "#FF3A3A5E")]
    [InlineData(nameof(Tokens.SurfaceKey), "#FF2A2A3E")]
    [InlineData(nameof(Tokens.SurfaceKeyModifier), "#FF3E3E64")]
    [InlineData(nameof(Tokens.SurfaceKeySwitch), "#FF38527F")]
    [InlineData(nameof(Tokens.SurfacePrediction), "#FF2F5340")]
    // Text and borders
    [InlineData(nameof(Tokens.BorderSubtle), "#FF616BA0")]
    [InlineData(nameof(Tokens.BorderFocus), "#FFA5F3FC")]
    [InlineData(nameof(Tokens.TextPrimary), "#FFD6DEF5")]
    [InlineData(nameof(Tokens.TextSecondary), "#FF9AA4C8")]
    [InlineData(nameof(Tokens.TextDim), "#FF868FB5")]
    // Accent and semantics
    [InlineData(nameof(Tokens.Accent), "#FFA5F3FC")]
    [InlineData(nameof(Tokens.StrokeHalo), "#FF0A0A14")]
    [InlineData(nameof(Tokens.AccentSuccess), "#FF7EE787")]
    [InlineData(nameof(Tokens.AccentWarning), "#FFE8A33C")]
    [InlineData(nameof(Tokens.AccentDanger), "#FFFF5C7A")]
    [InlineData(nameof(Tokens.ToggleOn), "#FF4A8F5C")]
    [InlineData(nameof(Tokens.ToggleOff), "#FF2E2E42")]
    [InlineData(nameof(Tokens.ModalBackdrop), "#B3000000")]
    public void Token_HasSpecifiedValue(string name, string expected)
    {
        var field = typeof(Tokens).GetField(name);
        Check.WithCustomMessage($"Tokens.{name} does not exist").That(field).IsNotNull();

        var actual = Hex((Color)field!.GetValue(null)!);
        Check.WithCustomMessage(
                $"Tokens.{name} is {actual}, spec §4.1 says {expected}. "
                + "If this change is intended, update §4.1 AND re-run "
                + "tools/palette_validate.py before changing this test.")
            .That(actual).IsEqualTo(expected);
    }

    [Fact]
    public void EveryColourToken_HasAMatchingFrozenBrush()
    {
        foreach (var field in typeof(Tokens).GetFields()
                     .Where(f => f.FieldType == typeof(Color)))
        {
            var brushField = typeof(Tokens).GetField("Brush" + field.Name);
            Check.WithCustomMessage($"Tokens.Brush{field.Name} is missing")
                .That(brushField).IsNotNull();

            var brush = (SolidColorBrush)brushField!.GetValue(null)!;
            Check.That(brush.Color).IsEqualTo((Color)field.GetValue(null)!);
            Check.WithCustomMessage($"Tokens.Brush{field.Name} must be frozen")
                .That(brush.IsFrozen).IsTrue();
        }
    }

    [Fact]
    public void BorderThicknessStandard_IsOnePixel()
    {
        // Theme-varying per spec §4.4 — Contrast uses 2. Midnight is 1.
        Check.That(Tokens.BorderThicknessStandard).IsEqualTo(1.0);
    }
}
```

- [ ] **Step 2: Add the missing test-project wiring, then run to verify it fails**

`tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj` has no test packages — the centralised `tests/Directory.Build.props` supplies xUnit v3 and NFluent, so no csproj edit is needed. Add the `System.Linq` using the test needs by confirming `ImplicitUsings` is `enable` (it is).

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo`

Expected: FAIL to **compile** — `error CS0234: The type or namespace name 'Theme' does not exist in the namespace 'EyeCtrl.Controls'`.

- [ ] **Step 3: Write the implementation**

Create `src/EyeCtrl.Controls/Theme/Tokens.cs`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The single source of truth for every palette colour in the EyeCtrl overlay UI.
/// See docs/design/overlay-unification-spec.md §4.1 for values and §4.1.1 for the
/// nine design rules they satisfy; tools/palette_validate.py enforces those rules
/// and must exit 0 after any edit here.
///
/// Deliberately plain C# statics rather than Application.Current.Resources
/// lookups: EyeCtrl.Controls.Tests and EyeCtrl.Core.Tests run without a WPF
/// Application, and a resource lookup would NullReferenceException there.
///
/// This class holds the Midnight palette. Ember and Contrast are specified in
/// §10 but not implemented; adding them means introducing an ITheme indirection
/// here, which will NOT require changing any {StaticResource} consumer because
/// the chosen theme-change model recreates windows rather than re-resolving live.
/// </summary>
public static class Tokens
{
    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();     // shared across overlays that redraw at gaze rate
        return b;
    }

    // ── Surfaces ────────────────────────────────────────────────────────────
    // Alpha only where the token composites over live desktop content. 0xFA
    // (98%) bounds the worst-case contrast drift to -5%; §4.1.2.
    public static readonly Color SurfaceDark = Color.FromArgb(0xFA, 0x1A, 0x1A, 0x2E);
    public static readonly Color SurfaceMedium = Color.FromArgb(0xFA, 0x2A, 0x2A, 0x44);
    /// <summary>Elevated surfaces are fully opaque — §4.1.1 rule 2.</summary>
    public static readonly Color SurfaceLight = Color.FromRgb(0x3A, 0x3A, 0x5E);
    public static readonly Color SurfaceKey = Color.FromRgb(0x2A, 0x2A, 0x3E);
    public static readonly Color SurfaceKeyModifier = Color.FromRgb(0x3E, 0x3E, 0x64);
    public static readonly Color SurfaceKeySwitch = Color.FromRgb(0x38, 0x52, 0x7F);
    public static readonly Color SurfacePrediction = Color.FromRgb(0x2F, 0x53, 0x40);

    // ── Text and borders ────────────────────────────────────────────────────
    public static readonly Color BorderSubtle = Color.FromRgb(0x61, 0x6B, 0xA0);
    /// <summary>Equals <see cref="Accent"/>: selection is carried by fill, not hue (§4.1.2).</summary>
    public static readonly Color BorderFocus = Color.FromRgb(0xA5, 0xF3, 0xFC);
    public static readonly Color TextPrimary = Color.FromRgb(0xD6, 0xDE, 0xF5);
    public static readonly Color TextSecondary = Color.FromRgb(0x9A, 0xA4, 0xC8);
    public static readonly Color TextDim = Color.FromRgb(0x86, 0x8F, 0xB5);

    // ── Accent and semantics ────────────────────────────────────────────────
    /// <summary>Reserved: means gaze dwell in progress and nothing else (§4.1.2).</summary>
    public static readonly Color Accent = Color.FromRgb(0xA5, 0xF3, 0xFC);
    /// <summary>Outline drawn under/around Accent on transparent overlays (§4.1.3, Plan B).</summary>
    public static readonly Color StrokeHalo = Color.FromRgb(0x0A, 0x0A, 0x14);
    public static readonly Color AccentSuccess = Color.FromRgb(0x7E, 0xE7, 0x87);
    public static readonly Color AccentWarning = Color.FromRgb(0xE8, 0xA3, 0x3C);
    public static readonly Color AccentDanger = Color.FromRgb(0xFF, 0x5C, 0x7A);
    public static readonly Color ToggleOn = Color.FromRgb(0x4A, 0x8F, 0x5C);
    public static readonly Color ToggleOff = Color.FromRgb(0x2E, 0x2E, 0x42);
    public static readonly Color ModalBackdrop = Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    // ── Brushes ─────────────────────────────────────────────────────────────
    public static readonly SolidColorBrush BrushSurfaceDark = Frozen(SurfaceDark);
    public static readonly SolidColorBrush BrushSurfaceMedium = Frozen(SurfaceMedium);
    public static readonly SolidColorBrush BrushSurfaceLight = Frozen(SurfaceLight);
    public static readonly SolidColorBrush BrushSurfaceKey = Frozen(SurfaceKey);
    public static readonly SolidColorBrush BrushSurfaceKeyModifier = Frozen(SurfaceKeyModifier);
    public static readonly SolidColorBrush BrushSurfaceKeySwitch = Frozen(SurfaceKeySwitch);
    public static readonly SolidColorBrush BrushSurfacePrediction = Frozen(SurfacePrediction);
    public static readonly SolidColorBrush BrushBorderSubtle = Frozen(BorderSubtle);
    public static readonly SolidColorBrush BrushBorderFocus = Frozen(BorderFocus);
    public static readonly SolidColorBrush BrushTextPrimary = Frozen(TextPrimary);
    public static readonly SolidColorBrush BrushTextSecondary = Frozen(TextSecondary);
    public static readonly SolidColorBrush BrushTextDim = Frozen(TextDim);
    public static readonly SolidColorBrush BrushAccent = Frozen(Accent);
    public static readonly SolidColorBrush BrushStrokeHalo = Frozen(StrokeHalo);
    public static readonly SolidColorBrush BrushAccentSuccess = Frozen(AccentSuccess);
    public static readonly SolidColorBrush BrushAccentWarning = Frozen(AccentWarning);
    public static readonly SolidColorBrush BrushAccentDanger = Frozen(AccentDanger);
    public static readonly SolidColorBrush BrushToggleOn = Frozen(ToggleOn);
    public static readonly SolidColorBrush BrushToggleOff = Frozen(ToggleOff);
    public static readonly SolidColorBrush BrushModalBackdrop = Frozen(ModalBackdrop);

    /// <summary>
    /// The one non-colour theme-varying token (§4.4). Midnight and Ember use 1;
    /// Contrast uses 2, because high-contrast structure is carried by the border
    /// rather than by fill steps.
    ///
    /// Deliberately <c>static readonly</c> rather than <c>const</c>: {x:Static}
    /// cannot bind a const, and a const would force OverlayStyles.xaml to restate
    /// the literal — breaking the single-source-of-truth guarantee for the sake of
    /// one number.
    /// </summary>
    public static readonly double BorderThicknessStandard = 1.0;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo`

Expected: PASS, 22 tests (20 `[InlineData]` + 2 `[Fact]`).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/Tokens.cs tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs
git commit -m "feat(theme): add Tokens as the single source of palette truth"
```

---

## Task 2: OverlayStyles.xaml — the XAML view, derived not restated

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/OverlayStyles.xaml`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Drift guard for the XAML view of <see cref="Tokens"/>. Every Brush* key must
/// resolve and equal its Tokens counterpart. Catches a mistyped {x:Static} path
/// — which fails at runtime, not build time — and catches anyone pasting a raw
/// literal into the dictionary. Mirrors EyeCtrlSettingsEnumerationTests.
/// </summary>
public class TokenDictionaryTests
{
    private static ResourceDictionary Load() => new()
    {
        Source = new Uri(
            "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml",
            UriKind.Absolute),
    };

    [StaFact]
    public void EveryTokenBrush_IsExposedAndMatches()
    {
        var dict = Load();

        foreach (var field in typeof(Tokens).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(SolidColorBrush)))
        {
            var key = field.Name;   // e.g. BrushAccent
            Check.WithCustomMessage($"OverlayStyles.xaml is missing key '{key}'")
                .That(dict.Contains(key)).IsTrue();

            var fromXaml = (SolidColorBrush)dict[key]!;
            var fromCode = (SolidColorBrush)field.GetValue(null)!;
            Check.WithCustomMessage(
                    $"'{key}' is {fromXaml.Color} in OverlayStyles.xaml but "
                    + $"{fromCode.Color} in Tokens. The dictionary must derive from "
                    + "Tokens via {x:Static}, never restate a literal.")
                .That(fromXaml.Color).IsEqualTo(fromCode.Color);
        }
    }

    [StaFact]
    public void Dictionary_ExposesBorderThickness()
    {
        var dict = Load();
        Check.That(dict.Contains("BorderThicknessStandard")).IsTrue();
        Check.That(dict["BorderThicknessStandard"]).IsEqualTo(Tokens.BorderThicknessStandard);
    }

    [StaFact]
    public void Dictionary_ContainsNoRawColourLiterals()
    {
        // A literal would silently defeat the single-source-of-truth guarantee.
        var xaml = new System.IO.StreamReader(
            Application.GetResourceStream(new Uri(
                "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml",
                UriKind.Absolute))!.Stream).ReadToEnd();

        var literals = System.Text.RegularExpressions.Regex.Matches(xaml, @"#[0-9A-Fa-f]{6,8}\b")
            .Select(m => m.Value).Distinct().ToList();

        Check.WithCustomMessage(
                "OverlayStyles.xaml must contain no colour literals — every value "
                + "comes from Tokens via {x:Static}. Found: " + string.Join(", ", literals))
            .That(literals).IsEmpty();
    }
}
```

`[StaFact]` (from `Xunit.StaFact`) is required — `ResourceDictionary` needs an STA thread. Add the package to `tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj`:

```xml
<PackageReference Include="Xunit.StaFact" Version="3.0.13" />
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~TokenDictionaryTests" --nologo`

Expected: FAIL — `System.IO.IOException: Cannot locate resource 'theme/overlaystyles.xaml'`.

- [ ] **Step 3: Write the implementation**

Create `src/EyeCtrl.Controls/Theme/OverlayStyles.xaml`. Every value derives from `Tokens` — there is deliberately not one literal in this file:

```xml
<!--
  XAML view of EyeCtrl.Controls.Theme.Tokens. Values are pulled with {x:Static}
  rather than restated, so drift between the C# and XAML views is structurally
  impossible: there is exactly one literal per colour, in Tokens.cs.
  Guarded by TokenDictionaryTests.
-->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime"
                    xmlns:theme="clr-namespace:EyeCtrl.Controls.Theme">

    <SolidColorBrush x:Key="BrushSurfaceDark"        Color="{x:Static theme:Tokens.SurfaceDark}"/>
    <SolidColorBrush x:Key="BrushSurfaceMedium"      Color="{x:Static theme:Tokens.SurfaceMedium}"/>
    <SolidColorBrush x:Key="BrushSurfaceLight"       Color="{x:Static theme:Tokens.SurfaceLight}"/>
    <SolidColorBrush x:Key="BrushSurfaceKey"         Color="{x:Static theme:Tokens.SurfaceKey}"/>
    <SolidColorBrush x:Key="BrushSurfaceKeyModifier" Color="{x:Static theme:Tokens.SurfaceKeyModifier}"/>
    <SolidColorBrush x:Key="BrushSurfaceKeySwitch"   Color="{x:Static theme:Tokens.SurfaceKeySwitch}"/>
    <SolidColorBrush x:Key="BrushSurfacePrediction"  Color="{x:Static theme:Tokens.SurfacePrediction}"/>

    <SolidColorBrush x:Key="BrushBorderSubtle"       Color="{x:Static theme:Tokens.BorderSubtle}"/>
    <SolidColorBrush x:Key="BrushBorderFocus"        Color="{x:Static theme:Tokens.BorderFocus}"/>
    <SolidColorBrush x:Key="BrushTextPrimary"        Color="{x:Static theme:Tokens.TextPrimary}"/>
    <SolidColorBrush x:Key="BrushTextSecondary"      Color="{x:Static theme:Tokens.TextSecondary}"/>
    <SolidColorBrush x:Key="BrushTextDim"            Color="{x:Static theme:Tokens.TextDim}"/>

    <SolidColorBrush x:Key="BrushAccent"             Color="{x:Static theme:Tokens.Accent}"/>
    <SolidColorBrush x:Key="BrushStrokeHalo"         Color="{x:Static theme:Tokens.StrokeHalo}"/>
    <SolidColorBrush x:Key="BrushAccentSuccess"      Color="{x:Static theme:Tokens.AccentSuccess}"/>
    <SolidColorBrush x:Key="BrushAccentWarning"      Color="{x:Static theme:Tokens.AccentWarning}"/>
    <SolidColorBrush x:Key="BrushAccentDanger"       Color="{x:Static theme:Tokens.AccentDanger}"/>
    <SolidColorBrush x:Key="BrushToggleOn"           Color="{x:Static theme:Tokens.ToggleOn}"/>
    <SolidColorBrush x:Key="BrushToggleOff"          Color="{x:Static theme:Tokens.ToggleOff}"/>
    <SolidColorBrush x:Key="BrushModalBackdrop"      Color="{x:Static theme:Tokens.ModalBackdrop}"/>

    <sys:Double x:Key="BorderThicknessStandard">{x:Static theme:Tokens.BorderThicknessStandard}</sys:Double>
</ResourceDictionary>
```

The `sys:` namespace declaration can be dropped if `{x:Static}` is used as above — keep whichever form compiles, but the value must come from `Tokens`, not be restated. `Dictionary_ExposesBorderThickness` enforces that.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo`

Expected: PASS, 25 tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/OverlayStyles.xaml tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj
git commit -m "feat(theme): expose tokens as a ResourceDictionary derived via x:Static"
```

---

## Task 3: Merge the dictionary into App.xaml

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml`
- Create: `tests/EyeCtrl.App.Tests/ThemeResourcesTests.cs`

`App.xaml` currently has an empty `<Application.Resources>`. This is the one line that makes `{StaticResource BrushXxx}` work in every overlay.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/ThemeResourcesTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// App.xaml must merge the Controls theme dictionary, otherwise every
/// {StaticResource BrushXxx} in the overlay XAML throws at window load — a
/// runtime failure no compile step catches.
/// </summary>
public class ThemeResourcesTests
{
    private static string AppXaml()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(ThemeResourcesTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "src/EyeCtrl.App/App.xaml"));
    }

    [Fact]
    public void AppXaml_MergesTheOverlayStylesDictionary()
    {
        const string expected =
            "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml";

        Check.WithCustomMessage(
                "src/EyeCtrl.App/App.xaml must merge " + expected
                + " inside <Application.Resources>, or all StaticResource brush "
                + "lookups fail at runtime.")
            .That(AppXaml()).Contains(expected);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~ThemeResourcesTests" --nologo`

Expected: FAIL — the string is not present in `App.xaml`.

- [ ] **Step 3: Write the implementation**

Replace the empty `<Application.Resources>` block in `src/EyeCtrl.App/App.xaml`:

```xml
<Application x:Class="EyeCtrl.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:EyeCtrl.App"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <!-- Palette tokens live in EyeCtrl.Controls so the shared dwell
                     controls can reach them; see spec §5 Phase 0. -->
                <ResourceDictionary Source="pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: PASS. ~13 `Category=UI` tests are environmental and excluded — see the note in Task 14.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/App.xaml tests/EyeCtrl.App.Tests/ThemeResourcesTests.cs
git commit -m "feat(theme): merge the token dictionary into App.xaml"
```

---

## Task 4: The legacy-literal guard — what makes the rest of this plan test-driven

**Files:**
- Create: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

Colour substitution in XAML and code-behind has no natural unit test: the change is "this literal became a token." This guard supplies one. It starts **allowing** everything, and each later task removes entries from the allow-list — so every task has a real red-to-green step.

- [ ] **Step 1: Write the test with a full allow-list (passes immediately)**

Create `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NFluent;
using Xunit;

/// <summary>
/// Regression guard for the colour migration (spec §5). Each legacy literal is
/// listed with the file that still contains it; as a file is migrated its entry
/// is deleted, and this test then fails if the literal ever comes back.
///
/// This is the red-to-green driver for the XAML and code-behind tasks, which
/// otherwise have no natural unit test.
/// </summary>
public class LegacyColourLiteralTests
{
    /// <summary>file (repo-relative) -> literals still expected in it.</summary>
    private static readonly Dictionary<string, string[]> StillPending = new()
    {
        ["src/EyeCtrl.Controls/DwellArcButton.cs"] = ["0x1E, 0x1E, 0x1E", "0x64, 0x64, 0x64", "Brushes.Lime"],
        ["src/EyeCtrl.Controls/DwellToggle.cs"] = ["200, 30, 100, 30", "200, 80, 40, 40"],
        ["src/EyeCtrl.Controls/DwellSlider.cs"] = ["200, 60, 60, 80"],
        ["src/EyeCtrl.Controls/DwellComboBox.cs"] = ["200, 60, 60, 80", "200, 30, 100, 30"],
        ["src/EyeCtrl.Controls/DwellColorPicker.cs"] = ["#00FF00"],
        ["src/EyeCtrl.Controls/DwellLabel.cs"] = ["180, 180, 180"],
        ["src/EyeCtrl.Controls/ColorBlend.cs"] = ["Colors.White"],
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs"] = ["200, 50, 50, 50", "100 + 155 * progress", "Brushes.Gray"],
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] = ["Brushes.Lime", "60, 60, 60"],
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"] = ["200, 40, 90, 160"],
        // All 11 of this file's colour literals, not a subset — the allow-list is the
        // cross-task guard, and a partial entry lets the untracked ones return silently.
        ["src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs"] =
        [
            "0x1A, 0x1A, 0x2E", "0x2D, 0x2D, 0x3F", "0x99, 0x99, 0x99",
            "0x25, 0x25, 0x40", "0xFF, 0xEA, 0xA7", "0x44, 0x88, 0xFF",
            "0x33, 0x33, 0x44", "0xFF, 0x66, 0x66", "0x66, 0xFF, 0x66",
            "0x66, 0x88, 0xFF", "0x33, 0xFF, 0xFF, 0xFF",
        ],
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml"] = ["#F0181818"],
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml"] = ["#E0202020"],
        ["src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml"] = ["#1A1A2E", "#2A2A3E", "#FFEAA7"],
        ["src/EyeCtrl.App/Overlays/SplashWindow.xaml"] = ["#F0181818", "#FF00AA00"],
        ["src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml"] = ["#80000000", "#20FFFFFF"],
        ["src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml"] = ["#E6000000", "#40FFFFFF"],
        ["src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml"] = ["#F0181830", "#6c8cff", "#73000000"],
        ["src/EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml"] = ["#E8000000", "#FF6c8cff", "#FF7ee787"],
        ["src/EyeCtrl.App/Overlays/CalibrationWindow.xaml"] = ["#DD000000", "#FFAA00"],
        ["src/EyeCtrl.App/Overlays/PauseIndicatorWindow.xaml"] = ["#C0204060"],
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml"] = ["#E01E1E1E", "#C0141414", "#E0282828"],
        ["src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml"] = ["#E0202020"],
    };

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(LegacyColourLiteralTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void MigratedFiles_ContainNoLegacyColourLiterals()
    {
        var root = RepoRoot().FullName;
        var offenders = new List<string>();

        foreach (var (relative, allowed) in StillPending)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;   // file renamed/removed — not this test's business
            var text = File.ReadAllText(path);

            foreach (var literal in allowed)
                if (!text.Contains(literal, StringComparison.Ordinal))
                    offenders.Add($"{relative}: '{literal}' is gone — delete it from StillPending");
        }

        // Fails when a file has been migrated but its allow-list entry was not
        // pruned, which would let the literal silently return later.
        Check.WithCustomMessage(
                "The allow-list is stale. Remove migrated entries so the guard "
                + "starts protecting them:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    /// <summary>
    /// Matches the outgoing Lime accent but NOT Brushes.LimeGreen, a different colour
    /// (#32CD32) used legitimately by TrackStatusWindow for track-quality dots. A naive
    /// Contains("Brushes.Lime") substring test flags it as a false positive; the \b
    /// prevents that, because e and G are both word characters.
    /// </summary>
    private static readonly Regex LimeAccent =
        new(@"Brushes\.Lime\b|""Lime""", RegexOptions.Compiled);

    [Fact]
    public void NoUnlistedFile_UsesTheOutgoingLimeAccent()
    {
        var root = RepoRoot().FullName;
        var listed = StillPending.Keys.Select(k => Path.Combine(root, k).Replace('/', Path.DirectorySeparatorChar))
                                      .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !listed.Contains(f))
            .Where(f => LimeAccent.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Check.WithCustomMessage(
                "Lime appeared in a file that is not on the migration allow-list: "
                + string.Join(", ", offenders))
            .That(offenders).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it passes now (baseline green)**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~LegacyColourLiteral" --nologo`

Expected: `NoUnlistedFile_UsesTheOutgoingLimeAccent` FAILS, naming files that still carry Lime but are not yet allow-listed. The complete inventory, verified at `b8295b6` — **seven usages across six files**:

| File | Line | Migrated by |
|---|---|---|
| `Controls/DwellArcButton.cs` | 40 | Plan A Task 5 (already listed) |
| `Overlays/ToolbarWindow.xaml.cs` | 62 | Plan A Task 10 (already listed) |
| `Overlays/CalibrationWindow.xaml` | 38 | Plan A Task 13 Step 9 (already listed) |
| `Overlays/CursorCircleWindow.xaml` | 11 | **Plan B** Task 3 |
| `Overlays/CursorCircleWindow.xaml.cs` | 36 | **Plan B** Task 3 |
| `Overlays/ActivationButtonWindow.xaml` | 15 | **Plan B** Task 4 |
| `Overlays/ManualSelectionGazeIndicatorWindow.xaml` | 12, 17 | **Plan B** Task 5 |

Add the four Plan B files to `StillPending` with `["Lime"]` and re-run.

> **`TrackStatusWindow.xaml.cs` is deliberately absent and must not be added.** It uses `Brushes.LimeGreen` (#32CD32) for track-quality status dots — a different colour with nothing to do with the dwell accent, correctly migrated by no task. A substring match flags it as a false positive, which is why `LimeAccent` anchors with `\b`. If you find yourself allow-listing `TrackStatusWindow`, the regex has been broken.

Expected after adding the four: PASS, 2 tests.

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "test(theme): add legacy colour-literal guard to drive the migration"
```

---

## Task 5: DwellArcButton — colours only, no halo

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellArcButton.cs:38-40`
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

The halo arc from §5 step 5 is **Plan B**. This task does the three colour defaults only.

- [ ] **Step 1: Make the guard fail by pruning the allow-list**

In `LegacyColourLiteralTests.StillPending`, delete this line:

```csharp
["src/EyeCtrl.Controls/DwellArcButton.cs"] = ["0x1E, 0x1E, 0x1E", "0x64, 0x64, 0x64", "Brushes.Lime"],
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~LegacyColourLiteral" --nologo`

Expected: FAIL — `NoUnlistedFile_UsesTheOutgoingLimeAccent` reports `src/EyeCtrl.Controls/DwellArcButton.cs`.

- [ ] **Step 3: Write the implementation**

In `src/EyeCtrl.Controls/DwellArcButton.cs`, add the using at the top of the file's using block:

```csharp
using EyeCtrl.Controls.Theme;
```

Then replace lines 38–40:

```csharp
        var fillBrush = fill ?? Tokens.BrushSurfaceMedium;
        var strokeBrush = stroke ?? Tokens.BrushBorderSubtle;
        var arcBrushActual = arcBrush ?? Tokens.BrushAccent;
```

Leave line 41 (`symbolBrushActual`) as `Brushes.White` for now — Task 7 handles it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~LegacyColourLiteral" --nologo`

Expected: PASS, 2 tests.

Then confirm nothing else broke: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/DwellArcButton.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(controls): DwellArcButton defaults come from Tokens"
```

---

## Task 6: DwellToggle, DwellSlider, DwellComboBox

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellToggle.cs:20-21`
- Modify: `src/EyeCtrl.Controls/DwellSlider.cs:20`
- Modify: `src/EyeCtrl.Controls/DwellComboBox.cs:30-31`
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs`

`DwellToggle`'s change is a **deliberate value change**, not a constant extraction: the old dark-red/dark-green pair differed by 1.52:1 on the red-green axis alone, which is the least reliable possible encoding of a binary state (spec §9.4). Assert the new behaviour rather than only the new literal.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Toggle state must be carried by lightness, not hue (spec §4.1.1 rule 7).
/// The pre-migration pair managed 1.52:1 and was near-invisible to a deuteranope.
/// </summary>
public class ToggleContrastTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void ToggleOnAndOff_DifferByAtLeastThreeToOne()
    {
        var ratio = Contrast(Tokens.ToggleOn, Tokens.ToggleOff);
        Check.WithCustomMessage(
                $"ToggleOn/ToggleOff contrast is {ratio:F2}:1; spec §4.1.1 rule 7 "
                + "requires >= 3.0 so state survives any colour-vision deficiency.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }

    [Fact]
    public void ToggleOff_IsNotRed()
    {
        // "Off" is not an error. Red is reserved for AccentDanger (§4.1.2).
        var c = Tokens.ToggleOff;
        Check.WithCustomMessage("ToggleOff must not be red-dominant")
            .That(c.R <= c.B).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~ToggleContrastTests" --nologo`

Expected: PASS already — `Tokens.ToggleOn`/`ToggleOff` were defined correctly in Task 1. This test pins the property so a future edit cannot regress it. Now prune the allow-list to get the red step: delete these three lines from `StillPending`:

```csharp
["src/EyeCtrl.Controls/DwellToggle.cs"] = ["200, 30, 100, 30", "200, 80, 40, 40"],
["src/EyeCtrl.Controls/DwellSlider.cs"] = ["200, 60, 60, 80"],
["src/EyeCtrl.Controls/DwellComboBox.cs"] = ["200, 60, 60, 80", "200, 30, 100, 30"],
```

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~LegacyColourLiteral" --nologo`

Expected: PASS (the pruned files contain no `Lime`, so only the stale-entry check applies and it now passes). To get a genuine red step, temporarily assert the tokens are used — instead, rely on Step 4's build: the literals must be gone for the file to compile against `Tokens`.

- [ ] **Step 3: Write the implementation**

`src/EyeCtrl.Controls/DwellToggle.cs` — add `using EyeCtrl.Controls.Theme;` and replace lines 20–21:

```csharp
    private static readonly Color OnColor = Tokens.ToggleOn;
    private static readonly Color OffColor = Tokens.ToggleOff;
```

`src/EyeCtrl.Controls/DwellSlider.cs` — add the using and replace line 20:

```csharp
    private static readonly Color BtnBase = Tokens.SurfaceMedium;
```

`src/EyeCtrl.Controls/DwellComboBox.cs` — add the using and replace lines 30–31:

```csharp
    private static readonly Color BtnBase = Tokens.SurfaceMedium;
    private static readonly Color SelectedColor = Tokens.ToggleOn;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo && dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: both PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/DwellToggle.cs src/EyeCtrl.Controls/DwellSlider.cs src/EyeCtrl.Controls/DwellComboBox.cs tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(controls): dwell toggle/slider/combobox use Tokens

ToggleOn/Off is a deliberate value change: the old dark-red/dark-green pair
carried state on the red-green axis at 1.52:1 (1.43:1 under deuteranopia).
The new pair carries it in lightness at 3.39:1."
```

---

## Task 7: DwellColorPicker presets, DwellLabel, ColorBlend

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellColorPicker.cs:31-33`
- Modify: `src/EyeCtrl.Controls/DwellLabel.cs:30`
- Modify: `src/EyeCtrl.Controls/ColorBlend.cs:35`
- Modify: `src/EyeCtrl.Controls/DwellArcButton.cs:41`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

**`ColorBlend` is the largest visual change in this plan from a one-line edit.** `ToHighlightBrush` blends toward white today; retargeting it to `Accent` alters dwell feedback on every `DwellButton`, `DwellToggle`, settings tab and pagination control. Review this commit on its own.

The picker feeds `CursorSettings.CircleColor`, which colours a haloed ring on a transparent overlay — so **every preset must clear 7:1 against `StrokeHalo`**, or Plan B's guarantee collapses to whatever that colour manages. `AccentDanger` fails at 6.62 and is deliberately not offered (it is also semantically wrong for a cursor ring).

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// DwellColorPicker feeds CursorSettings.CircleColor, which colours a haloed ring
/// on a transparent overlay (spec §4.1.3). A dark or mid-tone preset collapses
/// that contrast guarantee, so every preset must clear 7:1 against StrokeHalo.
/// </summary>
public class ColorPickerPresetTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void EveryPreset_ClearsSevenToOneAgainstStrokeHalo()
    {
        foreach (var (hex, name) in DwellColorPicker.DefaultPresets)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var ratio = Contrast(c, Tokens.StrokeHalo);
            Check.WithCustomMessage(
                    $"Preset '{name}' ({hex}) reaches only {ratio:F2}:1 against "
                    + "StrokeHalo; §5 step 9 requires >= 7:1 or the halo guarantee "
                    + "on the cursor ring collapses.")
                .That(ratio).IsStrictlyGreaterThan(7.0);
        }
    }

    [Fact]
    public void Presets_DoNotOfferTheOutgoingLime()
    {
        Check.That(DwellColorPicker.DefaultPresets.Select(p => p.Hex))
             .Not.Contains("#00FF00");
    }

    [Fact]
    public void FirstPreset_IsTheAccent()
    {
        // Accent is the default dwell colour; it should be the first choice offered.
        Check.That(DwellColorPicker.DefaultPresets[0].Hex).IsEqualTo("#A5F3FC");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --filter "FullyQualifiedName~ColorPickerPresetTests" --nologo`

Expected: FAIL on assertions, not compilation. `DefaultPresets[0].Hex` is `"#00FF00"`, so `FirstPreset_IsTheAccent` and `Presets_DoNotOfferTheOutgoingLime` both fail, and `EveryPreset_ClearsSevenToOneAgainstStrokeHalo` fails on `#FF0000` (Red).

> **The field already exists and is already public.** `DwellColorPicker.cs:29` declares
> `public static readonly (string Hex, string Name)[] DefaultPresets`, and line 78 does
> `presets ??= DefaultPresets`, so callers may supply their own list. **Do not rename it and do
> not change its type** — the named tuple members `.Hex` and `.Name` are exactly what the test
> needs: no record struct, no consumer edits. Only the *values* change. An earlier draft of this
> plan wrongly described it as a private array needing promotion.

- [ ] **Step 3: Write the implementation**

In `src/EyeCtrl.Controls/DwellColorPicker.cs`, replace only the **contents** of `DefaultPresets` (lines 29—34). The declaration, name, type and every consumer stay exactly as they are:

```csharp
    /// <summary>
    /// Palette-aligned presets, Accent first. Every entry must clear 7:1 against
    /// Tokens.StrokeHalo — see ColorPickerPresetTests. This list feeds
    /// CursorSettings.CircleColor, which colours a haloed ring on a transparent
    /// overlay, so a dark or mid-tone entry collapses the 4.1.3 guarantee.
    ///
    /// AccentDanger (#FF5C7A) is deliberately absent: it fails that bar at 6.62:1,
    /// and is reserved for destructive actions rather than cursor decoration.
    /// </summary>
    public static readonly (string Hex, string Name)[] DefaultPresets =
    [
        ("#A5F3FC", "Aqua"), ("#7EE787", "Green"),
        ("#E8A33C", "Amber"), ("#D6DEF5", "White"),
    ];
```

No `using EyeCtrl.Controls.Theme;` is needed here — the values are hex strings, and the token relationship is asserted by the test rather than referenced in code. The swatch-building loop is untouched: it consumes whatever `presets` resolves to via `presets ??= DefaultPresets` on line 78.

In `src/EyeCtrl.Controls/DwellLabel.cs`, add the using and replace line 30:

```csharp
            Foreground = Tokens.BrushTextSecondary,
```

In `src/EyeCtrl.Controls/ColorBlend.cs`, add `using EyeCtrl.Controls.Theme;`, then update the summary and line 35:

```csharp
    /// <summary>
    /// Returns a <see cref="SolidColorBrush"/> that blends <paramref name="baseColor"/>
    /// toward <see cref="Tokens.Accent"/> as dwell progress increases — the standard
    /// dwell-highlight overlay pattern used by buttons, toggles, tabs, and pagination
    /// controls. Blending toward the accent rather than white is what makes
    /// "accent means dwell in progress" (spec §4.1.2) true across the whole app.
    /// </summary>
    public static SolidColorBrush ToHighlightBrush(Color baseColor, double progress, double factor = 0.5)
    {
        var t = Math.Clamp(progress * factor, 0, 1);
        return new SolidColorBrush(Blend(baseColor, Tokens.Accent, t));
    }
```

In `src/EyeCtrl.Controls/DwellArcButton.cs`, finish line 41:

```csharp
        var symbolBrushActual = symbolBrush ?? Tokens.BrushTextPrimary;
```

Then prune from `StillPending`:

```csharp
["src/EyeCtrl.Controls/DwellColorPicker.cs"] = ["#00FF00"],
["src/EyeCtrl.Controls/DwellLabel.cs"] = ["180, 180, 180"],
["src/EyeCtrl.Controls/ColorBlend.cs"] = ["Colors.White"],
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Controls.Tests/EyeCtrl.Controls.Tests.csproj --nologo && dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: both PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/DwellColorPicker.cs src/EyeCtrl.Controls/DwellLabel.cs src/EyeCtrl.Controls/ColorBlend.cs src/EyeCtrl.Controls/DwellArcButton.cs tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(controls): palette-aligned picker presets, ColorBlend targets Accent

ColorBlend.ToHighlightBrush now blends toward Accent instead of white, which
changes dwell feedback on every button, toggle, tab and pagination control."
```

---

## Task 8: Settings colour defaults

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs:22`
- Modify: `src/EyeCtrl.Core/Settings/CalibrationSettings.cs:19`
- Modify: `src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs:9`
- Modify: `src/EyeCtrl.Core/Settings/DragSettings.cs:9`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs:13`

**Non-optional.** These four are *persisted settings*, not XAML, and two of them are read back over the XAML value at runtime (`CursorCircleWindow.xaml.cs:32`, `CalibrationWindow.xaml.cs:70`). Without this task the cursor ring and calibration target stay Lime no matter what Task 13 does.

`EyeCtrl.Core` cannot reference `EyeCtrl.Controls` (the dependency runs the other way), so these are string literals matching `Tokens` by value. The `SettingsDefaultsMatchTokensTests` in Step 1 is what keeps them in sync.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/SettingsDefaultsMatchTokensTests.cs` — it lives in `App.Tests` because that is the only test project referencing both `Core` and `Controls`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// EyeCtrl.Core cannot reference EyeCtrl.Controls, so these four persisted colour
/// defaults are string literals that must match Tokens by value (spec §5 Phase 2).
/// This test is the only thing keeping them in sync.
/// </summary>
public class SettingsDefaultsMatchTokensTests
{
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    [Fact]
    public void CursorCircleColour_DefaultsToAccent() =>
        Check.That(Parse(new CursorSettings().CircleColor)).IsEqualTo(Tokens.Accent);

    [Fact]
    public void CalibrationTargetColour_DefaultsToAccent() =>
        Check.That(Parse(new CalibrationSettings().TargetColor)).IsEqualTo(Tokens.Accent);

    [Fact]
    public void AutoSelectionHighlight_DefaultsToAccent() =>
        Check.That(Parse(new AutoSelectionSettings().HighlightColor)).IsEqualTo(Tokens.Accent);

    [Fact]
    public void DragTrail_DefaultsToAccentWarning()
    {
        // Warm and deliberately distinct from Accent, so a motion trail is never
        // mistaken for dwell progress (spec §5 step 15).
        Check.That(Parse(new DragSettings().TrailColor)).IsEqualTo(Tokens.AccentWarning);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~SettingsDefaultsMatchTokens" --nologo`

Expected: FAIL, 4 tests — e.g. `The checked value: [#FF00FF00] The expected value: [#FFA5F3FC]`.

- [ ] **Step 3: Write the implementation**

`src/EyeCtrl.Core/Settings/CursorSettings.cs:22`:

```csharp
    [ObservableProperty] private string _circleColor = "#A5F3FC";
```

`src/EyeCtrl.Core/Settings/CalibrationSettings.cs:19`:

```csharp
    [ObservableProperty] private string _targetColor = "#A5F3FC";
```

`src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs:9`:

```csharp
    [ObservableProperty] private string _highlightColor = "#A5F3FC";
```

`src/EyeCtrl.Core/Settings/DragSettings.cs:9`:

```csharp
    [ObservableProperty] private string _trailColor = "#E8A33C";
```

`tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs:13` — the only test in the suite that hard-asserts a colour default:

```csharp
        Check.That(s.TrailColor).IsEqualTo("#E8A33C");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --nologo && dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: both PASS. `SettingsServiceValidationTests.cs:69` compares against `defaults.Cursor.CircleColor` and is self-relative, so it is unaffected; the `#00FF00` literals in `SettingsSerializationTests` and `SettingsPropertyValidationTests` are explicit inputs and hex-format fixtures.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/CursorSettings.cs src/EyeCtrl.Core/Settings/CalibrationSettings.cs src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs src/EyeCtrl.Core/Settings/DragSettings.cs tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs tests/EyeCtrl.App.Tests/SettingsDefaultsMatchTokensTests.cs
git commit -m "feat(settings): retarget the four persisted colour defaults onto the palette"
```

- [ ] **Step 6: Remove the hex duplication**

The four literals above now appear in 4 settings files plus `DragSettingsTests`, so a token change means editing 5 files. Centralise them in `EyeCtrl.Core` — which still cannot reference `Controls`, so `SettingsDefaultsMatchTokensTests` remains the thing that ties them to `Tokens`:

Create `src/EyeCtrl.Core/Settings/SettingsDefaults.cs`:

```csharp
namespace EyeCtrl.Core.Settings;

/// <summary>
/// Default colours for persisted settings, as hex strings.
///
/// EyeCtrl.Core cannot reference EyeCtrl.Controls (the dependency runs the other
/// way), so these cannot be EyeCtrl.Controls.Theme.Tokens directly. They are kept
/// in sync by SettingsDefaultsMatchTokensTests in EyeCtrl.App.Tests, which is the
/// only project referencing both.
/// </summary>
internal static class SettingsDefaults
{
    /// <summary>Tokens.Accent — dwell progress, cursor ring, calibration target.</summary>
    internal const string Accent = "#A5F3FC";

    /// <summary>Tokens.AccentWarning — the drag trail, deliberately distinct from Accent.</summary>
    internal const string AccentWarning = "#E8A33C";
}
```

Then reference it from all four settings files, e.g.:

```csharp
    [ObservableProperty] private string _circleColor = SettingsDefaults.Accent;
```

`CalibrationSettings.TargetColor` and `AutoSelectionSettings.HighlightColor` likewise take `SettingsDefaults.Accent`; `DragSettings.TrailColor` takes `SettingsDefaults.AccentWarning`.

Leave `DragSettingsTests` asserting the literal `"#E8A33C"` — a test that asserts against the same constant it is verifying proves nothing.

Run: `dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --nologo && dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → both PASS.

```bash
git add src/EyeCtrl.Core/Settings/SettingsDefaults.cs src/EyeCtrl.Core/Settings/
git commit -m "refactor(settings): centralise persisted colour defaults"
```

> **Existing installs are unaffected by design.** `%APPDATA%\EyeCtrl\settings.json` already contains `cursor.circleColor`, so that key keeps its current value; the other three sections are absent from the file and inherit the new defaults automatically. Spec §7.1 records the decision to ship **no migration code** — change Circle Color once via the picker instead.

---

## Task 9: KeyboardWindow.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` (lines 20–24, 37–53, 559, 573, 602)
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

Two green dwell blends, not one — lines 573 and 602. Missing one leaves half the keyboard blending green.

- [ ] **Step 1: Prune the allow-list to make the guard fail**

Delete from `StillPending`:

```csharp
["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs"] = ["200, 50, 50, 50", "100 + 155 * progress", "Brushes.Gray"],
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~LegacyColourLiteral" --nologo`

Expected: PASS on the stale-entry check but the literals are still present — so add a temporary assertion to prove red. Instead, use this check, which is the real gate:

```bash
grep -c "100 + 155 \* progress" src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
```

Expected: `2`. After Step 3 it must be `0`.

- [ ] **Step 3: Write the implementation**

Add `using EyeCtrl.Controls.Theme;`. Replace lines 20–24:

```csharp
    private static readonly Color KeyColorNormal = Tokens.SurfaceKey;
    private static readonly Color KeyColorSpecial = Tokens.SurfaceKeyModifier;
    private static readonly Color KeyColorSwitch = Tokens.SurfaceKeySwitch;
    private static readonly Color KeyColorModifierActive = Tokens.Accent;
    private static readonly Color PredictionBarColor = Tokens.SurfacePrediction;
```

Replace the five prediction-kind brushes at lines 37–53. These are a semantic set absent from §3.2; retarget them onto the palette rather than leaving them out-of-palette:

```csharp
        var fuzzyBrush = Tokens.BrushTextSecondary;
        var nextWordBrush = Tokens.BrushAccentSuccess;
        var phraseBrush = Tokens.BrushAccentWarning;
        var sentenceCompletionBrush = Tokens.BrushAccent;
        var lmSuggestionBrush = Tokens.BrushTextPrimary;
```

Line 559:

```csharp
                Foreground = Tokens.BrushTextDim,
```

Line 573 — the prediction-chip dwell blend. Replace the green ramp with an accent blend that preserves the original alpha:

```csharp
        var highlightColor = ColorBlend.Blend(baseColor, Tokens.Accent, progress);
```

Line 602 — the key dwell blend, same substitution with the literal alpha the original used:

```csharp
        var highlightColor = ColorBlend.Blend(
            Color.FromArgb(200, baseColor.R, baseColor.G, baseColor.B), Tokens.Accent, progress);
```

Add `using EyeCtrl.Controls;` if `ColorBlend` is not already in scope.

- [ ] **Step 4: Verify**

```bash
grep -c "100 + 155 \* progress" src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
```

Expected: `0`.

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(keyboard): key, prediction and dwell-blend colours from Tokens"
```

---

## Task 10: ToolbarWindow.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` (lines 19–21, 62, 82, 125, 217–219)
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

- [ ] **Step 1: Prune the allow-list**

Delete from `StillPending`:

```csharp
["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] = ["Brushes.Lime", "60, 60, 60"],
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~LegacyColourLiteral" --nologo`

Expected: FAIL — `NoUnlistedFile_UsesTheOutgoingLimeAccent` reports `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`.

- [ ] **Step 3: Write the implementation**

Add `using EyeCtrl.Controls.Theme;`. Delete the three now-dead alpha constants at lines 19–21 (`NormalButtonAlpha`, `IndicatorButtonAlpha`, `ManualSelectionActiveButtonAlpha`) — leaving them would be dead code.

Line 62:

```csharp
            Fill = Tokens.BrushAccent,
```

Line 82:

```csharp
            Background = Tokens.BrushSurfaceMedium,
```

Line 125:

```csharp
            Background = Tokens.BrushSurfaceDark,
```

Lines 217–219:

```csharp
                SelectionMode.ManualSelection => Tokens.BrushToggleOn,
                SelectionMode.AutoSelection => Tokens.BrushSurfaceKeySwitch,
                _ => Tokens.BrushSurfaceMedium
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`

Expected: PASS. Also confirm no dead references remain: `grep -c "ButtonAlpha" src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` → `0`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(toolbar): button and progress colours from Tokens, drop dead alphas"
```

---

## Task 11: SettingsWindow.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:43-50`
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

`TabActive` is `#C8285AA0` (`FromArgb(200,40,90,160)`) — earlier drafts of the spec recorded it as `#C82859A0`, which is wrong. The value is being replaced either way.

- [ ] **Step 1: Prune the allow-list**

Delete from `StillPending`:

```csharp
["src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"] = ["200, 40, 90, 160"],
```

- [ ] **Step 2: Run to verify the literal is still there**

```bash
grep -c "200, 40, 90, 160" src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
```

Expected: `1`. After Step 3 it must be `0`.

- [ ] **Step 3: Write the implementation**

Add `using EyeCtrl.Controls.Theme;` and replace lines 43–50:

```csharp
    private static readonly Color BtnBase = Tokens.SurfaceMedium;
    private static readonly Color TabActive = Tokens.SurfaceKeySwitch;
    private static readonly Color TabInactive = Tokens.SurfaceKey;
    private static readonly Color ResetBase = Tokens.AccentWarning;
    private static readonly Color ModalYes = Tokens.ToggleOn;
    private static readonly Color ModalNo = Tokens.AccentDanger;
    private static readonly Color CloseBase = Tokens.AccentDanger;
    private static readonly Color DeleteBase = Tokens.AccentWarning;
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
grep -c "200, 40, 90, 160" src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
```

Expected: `0`.

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(settings): tab, modal and destructive-action colours from Tokens"
```

---

## Task 12: NoteWindow.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs` (lines 67, 79, 85, 117, 137, 212, 213, 224–226, 237)
- Modify: `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

A code-only `OverlayWindow` with no `.xaml` file, which is why a XAML-file sweep missed it and the original audit omitted it entirely.

- [ ] **Step 1: Prune the allow-list**

Delete from `StillPending`:

```csharp
["src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs"] = ["0x1A, 0x1A, 0x2E", "0xFF, 0xEA, 0xA7"],
```

- [ ] **Step 2: Verify the literals are still present**

```bash
grep -c "Color.FromRgb\|Color.FromArgb" src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs
```

Expected: `11`. After Step 3 it must be `0`. (The symbol triple at lines 224—226 supplies three of the eleven.)

- [ ] **Step 3: Write the implementation**

Add `using EyeCtrl.Controls.Theme;`, then substitute each literal:

| Line | Was | Becomes |
|---|---|---|
| 67 | `FromRgb(0x1A, 0x1A, 0x2E)` | `Tokens.BrushSurfaceDark` |
| 79 | `FromRgb(0x2D, 0x2D, 0x3F)` | `Tokens.BrushSurfaceMedium` |
| 85 | `FromRgb(0x99, 0x99, 0x99)` | `Tokens.BrushTextSecondary` |
| 117 | `FromRgb(0x25, 0x25, 0x40)` | `Tokens.BrushSurfaceKey` |
| 137 | `FromRgb(0xFF, 0xEA, 0xA7)` | `Tokens.BrushAccentWarning` |
| 212 | `FromRgb(0x44, 0x88, 0xFF)` | `Tokens.BrushAccent` |
| 213 | `FromRgb(0x33, 0x33, 0x44)` | `Tokens.BrushSurfaceMedium` |
| 237 | `FromArgb(0x33, 0xFF, 0xFF, 0xFF)` | `Tokens.BrushBorderSubtle` |

Lines 67, 79, 85, 117, 137, 212, 213 and 237 all currently wrap the colour in `new SolidColorBrush(...)`; replace the whole expression with the frozen brush, dropping the `new SolidColorBrush`.

The symbol triple at lines 224–226 keeps its tuple shape but takes token colours:

```csharp
        var symbols = new[] { ("close", "✕", Tokens.AccentDanger),
                              ("edit",  "✎", Tokens.AccentSuccess),
                              ("move",  "↕", Tokens.Accent) };
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
grep -c "Color.FromRgb\|Color.FromArgb" src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs
```

Expected: `0`.

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
git commit -m "refactor(note): colours from Tokens (code-only overlay, no XAML)"
```

---

## Task 13: Filled overlay XAML — 12 files (13 steps: 12 files + 1 confirmation)

**Files:** all under `src/EyeCtrl.App/`, plus `tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs`

Commit **one file at a time** — this is the widest-reaching visual change in the plan and a per-file history makes a bad substitution bisectable. For each file: prune its `StillPending` entry, edit, run `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo`, commit.

Every `{StaticResource ...}` below resolves through the dictionary merged in Task 3. If a brush key is misspelled the window throws `ResourceReferenceKeyNotFoundException` **at load**, not at build — so Task 14's visual check is the only thing that catches a typo here.

- [ ] **Step 1: `Overlays/KeyboardWindow.xaml:9`**

```xml
    <Border Background="{StaticResource BrushSurfaceDark}" CornerRadius="8" Padding="8">
```

- [ ] **Step 2: `Overlays/ToolbarWindow.xaml:9`**

```xml
    <Border Background="{StaticResource BrushSurfaceDark}" CornerRadius="4" Padding="4">
```

- [ ] **Step 3: `Overlays/QuickActionsGridWindow.xaml`**

Line 11–12 — note `Opacity="0.95"` is **removed**; the alpha now lives in `SurfaceDark` itself:

```xml
        <Border x:Name="RootBorder" Background="{StaticResource BrushSurfaceDark}" CornerRadius="8"
                BorderBrush="{StaticResource BrushBorderSubtle}" BorderThickness="1">
```

Lines 23 and 47 — tile and close-button fills: `Background="#2A2A3E"` → `Background="{StaticResource BrushSurfaceMedium}"`, and `BorderBrush="#445"` → `BorderBrush="{StaticResource BrushBorderSubtle}"`.

Lines 31 and 63 — `Foreground="#FFEAA7"` → `Foreground="{StaticResource BrushTextPrimary}"` for text, and the progress-bar foreground → `{StaticResource BrushAccent}`.

- [ ] **Step 4: `Overlays/SplashWindow.xaml`**

Line 8: `Background="#F0181818"` → `{StaticResource BrushSurfaceDark}`.
Line 18: `Foreground="#FF00AA00"` → `{StaticResource BrushAccentSuccess}`, `Background="#FF333333"` → `{StaticResource BrushSurfaceMedium}`.

- [ ] **Step 5: `Overlays/TrackStatusWindow.xaml`**

Line 8: `Background="#80000000"` → `{StaticResource BrushSurfaceDark}`.
Line 13: `BorderBrush="White"` → `{StaticResource BrushBorderSubtle}`, `Background="#20FFFFFF"` → `{StaticResource BrushSurfaceMedium}`.

- [ ] **Step 6: `Overlays/TaskSwitcherWindow.xaml`**

Line 5 (on the `Window`): `Background="#E6000000"` → `{StaticResource BrushSurfaceDark}`.
Line 22: `Background="#40FFFFFF"` → `{StaticResource BrushSurfaceMedium}`.
Text: `White` → `{StaticResource BrushTextPrimary}`, `LightGray` → `{StaticResource BrushTextSecondary}`.

- [ ] **Step 7: `Overlays/EyeCareBannerWindow.xaml`**

Line 14: `Fill="#73000000"` → `{StaticResource BrushModalBackdrop}`.
Line 17: `Background="#F0181830"` → `{StaticResource BrushSurfaceDark}`.
Line 18: `BorderBrush="#6c8cff"` → `{StaticResource BrushBorderSubtle}`.
Lines 24, 30: `Foreground="#FFAAAAAA"` → `{StaticResource BrushTextSecondary}`.
Line 29: `Stroke="#888888"` → `{StaticResource BrushTextDim}`.
Line 32: `Foreground="#FF777777"` → `{StaticResource BrushTextDim}`.
Lines 36–39: `#FF6c8cff` → `{StaticResource BrushAccent}` (stroke and both glyph foregrounds).

- [ ] **Step 8: `Overlays/EyeCareExerciseWindow.xaml`**

Line 7 (on the `Window`): `Background="#E8000000"` → `{StaticResource BrushSurfaceDark}`.
Line 31: `Foreground="#FF888888"` → `{StaticResource BrushTextDim}`, `BorderBrush="#FF444444"` → `{StaticResource BrushBorderSubtle}`.
Line 39: `Background="#F0181830"` → `{StaticResource BrushSurfaceLight}` (elevated card — §4.1.1 rule 2), `BorderBrush="#FF6c8cff"` → `{StaticResource BrushBorderSubtle}`.
Target dot and metric values: `#FF6c8cff` → `{StaticResource BrushAccent}`. Gaze ring `#FF7ee787` → `{StaticResource BrushAccentSuccess}`.

- [ ] **Step 9: `Overlays/CalibrationWindow.xaml`**

Line 15: `Fill="#DD000000"` → `{StaticResource BrushModalBackdrop}`.
Line 20: target dot `Fill="Green"` → `{StaticResource BrushAccent}` — **fallback only**; the runtime value comes from `CalibrationSettings.TargetColor` via `CalibrationWindow.xaml.cs:70`.
Progress text `#B0FFFFFF` → `{StaticResource BrushTextPrimary}`. Completion `Lime` → `{StaticResource BrushAccentSuccess}`.
Line 52: `Foreground="#FFAA00"` → `{StaticResource BrushAccentWarning}`.
Line 55: `Foreground="#88FFFFFF"` → `{StaticResource BrushTextSecondary}`.

**Leave `FontFamily="Segoe UI"` on line 30.** §4.3's app-wide font default is not in this plan's scope, so removing it here would change rendering with nothing to replace it.

- [ ] **Step 10: `Overlays/PauseIndicatorWindow.xaml:12`**

```xml
        <Border Background="{StaticResource BrushSurfaceDark}" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>
```

- [ ] **Step 11: `Views/SettingsWindow.xaml`**

Line 8 (on the `Window`): `Background="#E01E1E1E"` → `{StaticResource BrushSurfaceDark}`.
Line 14: `Background="#C0141414"` → `{StaticResource BrushSurfaceMedium}`.
Line 20: `Background="#C8641E1E"` → `{StaticResource BrushAccentDanger}`.
Line 54: `Background="#A0000000"` → `{StaticResource BrushModalBackdrop}`.
Line 59: `Background="#E0282828"` → `{StaticResource BrushSurfaceLight}` (elevated modal — §4.1.1 rule 2).

- [ ] **Step 12: `Overlays/DriftNoticeWindow.xaml`** *(added by PR #71)*

Line 12 — also **adds a border**, which the file currently lacks entirely, leaving its edge undefined against the desktop:

```xml
                Background="{StaticResource BrushSurfaceDark}"
                BorderBrush="{StaticResource BrushBorderSubtle}"
                BorderThickness="{StaticResource BorderThicknessStandard}"
                CornerRadius="6" Padding="18,10" Opacity="0">
```

Line 15: `Foreground="White"` → `{StaticResource BrushTextPrimary}`.

`CornerRadius="6"` stays — radius unification is deferred (§8).

- [ ] **Step 13: Confirm the allow-list is fully pruned for Phase 4**

Only Plan B's entries should remain:

```bash
grep -c "src/EyeCtrl.App/Overlays" tests/EyeCtrl.App.Tests/LegacyColourLiteralTests.cs
```

Expected: `4` — `CursorCircleWindow.xaml`, `ActivationButtonWindow.xaml`, `ManualSelectionGazeIndicatorWindow.xaml`, `CueOverlayWindow.xaml` (all Plan B).

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category!=UI" --nologo` → PASS.

---

## Task 14: Headless window instantiation — catches misspelled resource keys

**Files:**
- Create: `tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs`

A misspelled `{StaticResource BrushSurfaceDrak}` throws `ResourceReferenceKeyNotFoundException` **at window load**. Nothing in Tasks 1–13 exercises that, which currently leaves manual visual checking as the only detection — for 12 files of substitutions. This closes the gap.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

/// <summary>
/// Instantiates each migrated overlay so its XAML is actually parsed. A misspelled
/// {StaticResource} key throws ResourceReferenceKeyNotFoundException at load time —
/// invisible to a build and to every literal-scanning test.
///
/// Requires STA plus an Application whose resources include the merged token
/// dictionary, mirroring what App.xaml sets up at runtime.
/// </summary>
public class OverlayResourceResolutionTests
{
    private static void EnsureApplicationWithTokens()
    {
        if (Application.Current is null)
            _ = new Application();

        const string uri =
            "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml";

        var already = Application.Current!.Resources.MergedDictionaries
            .Any(d => d.Source?.ToString() == uri);

        if (!already)
            Application.Current.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
    }

    /// <summary>Overlays migrated by Plan A whose XAML uses StaticResource brushes.</summary>
    public static TheoryData<Type> MigratedOverlays() =>
    [
        typeof(KeyboardWindow), typeof(ToolbarWindow), typeof(QuickActionsGridWindow),
        typeof(SplashWindow), typeof(TrackStatusWindow), typeof(TaskSwitcherWindow),
        typeof(EyeCareBannerWindow), typeof(EyeCareExerciseWindow),
        typeof(CalibrationWindow), typeof(PauseIndicatorWindow),
        typeof(DriftNoticeWindow),
    ];

    [StaTheory]
    [MemberData(nameof(MigratedOverlays))]
    public void Overlay_LoadsWithoutUnresolvedResourceKeys(Type overlayType)
    {
        EnsureApplicationWithTokens();

        Window? w = null;
        try
        {
            // Activator runs InitializeComponent, which parses the XAML and
            // resolves every StaticResource.
            w = (Window)Activator.CreateInstance(overlayType)!;
        }
        catch (Exception ex) when (ex.InnerException is ResourceReferenceKeyNotFoundException inner)
        {
            Check.WithCustomMessage(
                    $"{overlayType.Name} references an undefined resource key "
                    + $"'{inner.Key}'. Check the spelling against OverlayStyles.xaml.")
                .That(false).IsTrue();
        }
        finally
        {
            w?.Close();
        }
    }
}
```

`[StaTheory]` comes from `Xunit.StaFact`, already referenced by `EyeCtrl.App.Tests`.

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~OverlayResourceResolution" --nologo`

Expected: PASS, 11 cases. If a case fails with an unresolved key, fix that overlay's XAML — the message names the key.

**If a window cannot be constructed without a view model or other dependency**, exclude it from `MigratedOverlays()` with a comment naming the blocker, and note it in the Task 15 visual check instead. Do not weaken the test to a try/catch that swallows everything — a test that cannot fail is worse than no test.

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/OverlayResourceResolutionTests.cs
git commit -m "test(theme): load each migrated overlay to catch unresolved resource keys"
```

---

## Task 15: Full verification

**Files:** none modified — this task only runs checks.

- [ ] **Step 1: Clean build**

Run: `dotnet build EyeCtrl.slnx --nologo`

Expected: `Build succeeded`, 0 errors. Pre-existing warnings (CS1591 in `Contracts`, MVVMTK0034 in `ViewModels`, CS0108 in `CueOverlayWindow`) are unrelated to this work — do not "fix" them here.

- [ ] **Step 2: Palette validator still green**

Run: `python tools/palette_validate.py`

Expected: `ALL GATES PASS - 3 theme(s): Midnight, Ember, Contrast`, exit 0. Nothing in this plan changes a palette value, so a failure here means a `Tokens.cs` typo.

- [ ] **Step 3: Full headless test suite**

Run: `dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo`

Expected: PASS. Includes the drift guards (`OverlayInventoryDriftTests`, `TokenDictionaryTests`, `TokenValueTests`, `LegacyColourLiteralTests`, `SettingsDefaultsMatchTokensTests`, `ColorPickerPresetTests`, `ToggleContrastTests`).

Two pre-existing flakes are load-dependent and unrelated: `HeartbeatPublisher` (<1500ms assertion) and `MouseGazeProviderTests`. Re-run once before investigating.

- [ ] **Step 4: UI tests — desktop session only**

Run: `dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category=UI" --nologo`

**This requires an interactive desktop session.** Roughly 13 of these fail headless on clean `main` with "Toolbar_Window did not appear within 15s". They are environmental, not regressions — do **not** treat a headless failure here as a migration defect, and do not use this as a CI gate.

- [ ] **Step 5: Visual confirmation**

Task 14 now catches unresolved resource keys, so this step is about whether the result *looks* right — colour choices in context, and anything Task 14 had to exclude. **Ask the user before launching the app** — they control the PC solely by eye tracking, so an app launch takes over their only input channel. Keep the run short.

Open, in this order, and confirm each renders with the new navy palette and no exception dialog: Toolbar → Settings (all tabs, plus a reset modal) → Keyboard (including the prediction bar) → Quick Actions → Calibration → the EyeCare banner.

- [ ] **Step 6: Commit any fixes, then record completion**

```bash
git add -A
git commit -m "chore(theme): Plan A complete — token foundation and colour migration"
```

---

## Self-Review

**Spec coverage.** §5 Phase 0 → Tasks 1–3. Phase 1 → Tasks 5–7 (the `DwellArcButton` halo from step 5 is explicitly deferred to Plan B, stated in Scope). Phase 2 → Task 8. Phase 3 → Tasks 9–12. Phase 4 → Task 13, all 12 files. Phase 6 → Task 14. **Phase 5 is Plan B** and is out of scope by design.

**Deliberate omissions, all stated in Scope:** the halo (Plan B), `ITheme`/theme switching (later plan, and cost-free to defer because `{StaticResource}` is already correct for the recreate-overlays model), radius unification (§8 defers it), and §4.3's app-wide font — which is why step 29's "remove `FontFamily`" aside is **not** followed.

**One spec inconsistency corrected here:** step 29 says to delete `CalibrationWindow.xaml:30`'s `FontFamily` "once §4.3 sets it app-wide." §4.3 is not in §5's step list, so this plan leaves the declaration in place rather than removing a setting with no replacement.

**Type consistency.** `Tokens.BrushXxx` naming is used identically in Tasks 1, 2, 5–13. `DwellColorPicker.Presets` is defined in Task 7 and consumed by `ColorPickerPresetTests` in the same task. `ColorBlend.Blend` (public, pre-existing) is what Task 9 calls; `ColorBlend.ToHighlightBrush` is modified in Task 7 and not re-signatured. `LegacyColourLiteralTests.StillPending` is created in Task 4 and pruned by Tasks 5–13, ending with exactly the 4 Plan B entries asserted in Task 13 Step 13.

**Review round 2 (2026-07-28).** Three findings held and are fixed. `DwellColorPicker` was
misdescribed: the preset field is `DefaultPresets` at line 29, already
`public static readonly (string Hex, string Name)[]`, consumed via `presets ??= DefaultPresets`
on line 78. Task 7 now changes only its *values* — no rename, no record struct, no consumer
edits, which is both correct and smaller than the previous draft. The `NoteWindow` allow-list
tracked 2 of 11 literals and its gate count said 10; both fixed. And the Lime check used a
substring match that flagged `Brushes.LimeGreen` in `TrackStatusWindow.xaml.cs`, a legitimate
unrelated colour — it is now an anchored regex, and Step 2 carries the verified seven-usage
inventory plus a warning against allow-listing that file.

A related finding did **not** hold: `TrackStatusWindow.xaml.cs` was reported as an unmigrated
`Brushes.Lime` gap. It uses `Brushes.LimeGreen` (#32CD32), which is not the dwell accent and
correctly migrates nowhere. The symptom — the test failing on that file — was real; the cause
was test precision, not a missing task.

**Review outcome (2026-07-28).** Two findings raised as HIGH — that the spec and
`tools/palette_validate.py` do not exist — were checked and did not hold: both are
committed in `3477eef` on this branch and the validator exits 0. They are absent
from `main` only because the working tree there was deliberately cleaned after the
commit. A Preconditions block now states the required branch and directory so the
confusion cannot recur. A third finding, that `DwellArcButton.cs:38-40` are
"constructor parameter defaults", also did not hold — the *parameters* default to
`null` on line 35 and lines 38-40 are local `??` fallbacks, which is the shape
Task 5 replaces.

Findings that did hold and are now fixed: the misleading csproj wording in Task 1,
the `const` vs `static readonly` restatement wart in Task 2, hex duplication across
five files (new Task 8 Step 6), the Task 13 header count, and the absence of any
automated check for misspelled resource keys (new Task 14).

**Known plan weakness.** Tasks 6, 9, 11 and 12 cannot produce a natural red test — a colour literal becoming a token is not observable behaviour. Those use a `grep` count as the red-to-green gate instead, which is honest about what is being verified rather than dressing a substitution up as TDD.
