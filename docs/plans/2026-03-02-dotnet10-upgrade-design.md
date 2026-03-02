# .NET 10 Upgrade Design

**Date:** 2026-03-02
**Approach:** B — TFM bump + NuGet package updates + XAML Grid shorthand

## Goal

Upgrade EyeCtrl from .NET 8 to .NET 10 (LTS), adopting all applicable improvements: free runtime gains from the TFM change, updated test packages, and the new WPF Grid shorthand syntax.

## What's Free (no code changes required)

By changing the TFM, EyeCtrl automatically benefits from:

- **JIT improvements**: foreach devirtualization, struct register packing, 3-opt block layout, try-finally inlining — benefits dwell detection and gaze processing hot paths
- **GC / DATAS enabled by default**: heap automatically adapts to workload — good for a long-running background process
- **WPF rendering performance**: optimized font rendering, XAML parsing, pixel format conversions, dynamic resource handling — benefits overlay redraws
- **WPF Fluent styles extended**: TextBox (used in keyboard and settings), DatePicker, GridSplitter, GroupBox now styled
- **WinForms dark mode stable**: available without action (EyeCtrl uses `UseWindowsForms=true`)

## Section 1 — Project File Changes

All 6 `.csproj` files have their `TargetFramework` updated:

| Project | From | To |
|---|---|---|
| `EyeCtrl.Core` | `net8.0` | `net10.0` |
| `EyeCtrl.Platform` | `net8.0` | `net10.0` |
| `EyeCtrl.App` | `net8.0-windows` | `net10.0-windows` |
| `EyeCtrl.Core.Tests` | `net8.0` | `net10.0` |
| `EyeCtrl.Platform.Tests` | `net8.0-windows` | `net10.0-windows` |
| `EyeCtrl.App.Tests` | `net8.0-windows` | `net10.0-windows` |

A `global.json` is added at the repo root to pin the SDK:

```json
{
  "sdk": {
    "version": "10.0.0",
    "rollForward": "latestMinor"
  }
}
```

## Section 2 — NuGet Package Updates

| Package | Current | Action |
|---|---|---|
| `xunit` | 2.5.3 | Update to latest 2.x |
| `xunit.runner.visualstudio` | 2.5.3 | Update to latest |
| `Microsoft.NET.Test.Sdk` | 17.8.0 | Update to latest 17.x |
| `coverlet.collector` | 6.0.0 | Update to latest 6.x |
| `FlaUI.UIA3` | 4.0.0 | Update to latest (5.x if released) |
| `System.Drawing.Common` | 10.0.3 | No change — already current |

The Tobii SDK (`tobii_interaction_lib_cs.dll`, .NET Standard 2.0) is forward-compatible with .NET 10 and requires no change.

## Section 3 — XAML Grid Shorthand

Only `SettingsWindow.xaml` contains Grid definition blocks (3 total). All are converted to the new attribute syntax introduced in .NET 10 WPF:

```xml
<!-- Before -->
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="220"/>
    <ColumnDefinition Width="*"/>
</Grid.ColumnDefinitions>

<!-- After (on the parent Grid element) -->
<Grid ColumnDefinitions="220, *">
```

The three blocks to convert:
1. Outer Grid: `ColumnDefinitions="220, *"`
2. Left sidebar Grid: `RowDefinitions="Auto, *"`
3. Right content Grid: `RowDefinitions="*, Auto"`

No other XAML files are affected.

## Section 4 — Breaking Change Audit

Three breaking changes from .NET 8→10 require verification:

1. **Empty `ColumnDefinitions`/`RowDefinitions`**: Now throws at runtime. Scan of all XAML confirms none exist — no action needed.
2. **Invalid `DynamicResource` usage**: .NET 10 crashes instead of failing silently. All XAML files must be scanned; any `DynamicResource` referencing a non-existent key must be fixed.
3. **WPF + WinForms `MenuItem`/`ContextMenu` ambiguity**: `EyeCtrl.App` uses both (`UseWPF=true` + `UseWindowsForms=true`). Any unqualified `MenuItem` or `ContextMenu` in code-behind must be namespace-qualified.

These are verification/fix tasks that gate the "done" state.

## Success Criteria

- Solution builds without warnings or errors targeting `net10.0` / `net10.0-windows`
- All 30 unit tests pass on .NET 10
- App launches and eye tracking functions correctly
- No runtime crashes from breaking changes
- `global.json` present at repo root
