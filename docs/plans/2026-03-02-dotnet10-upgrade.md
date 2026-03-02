# .NET 10 Upgrade Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Upgrade EyeCtrl from net8.0 to net10.0, update all NuGet packages, and adopt WPF Grid shorthand syntax in SettingsWindow.xaml.

**Architecture:** Four sequential tasks — pin SDK via global.json, bump all six TFMs, update test packages to latest, convert SettingsWindow XAML Grid definitions to shorthand. Breaking change audit is pre-cleared: no DynamicResource usage exists, MenuItem/ContextMenu are already fully namespace-qualified.

**Tech Stack:** .NET 10.0.200-preview SDK, C# 13, WPF net10.0-windows, xUnit 2.9.3, FlaUI.UIA3 5.0.0

---

## Pre-flight: Verify SDK

**Step 1: Confirm .NET 10 SDK is present**

Run:
```
dotnet --list-sdks
```
Expected: a line containing `10.0.` — confirmed `10.0.200-preview.0.26103.119` is installed.

---

### Task 1: Add global.json

**Files:**
- Create: `global.json` (repo root: `C:\Users\andre\source\repos\eyectrl\global.json`)

**Step 1: Create the file**

```json
{
  "sdk": {
    "version": "10.0.200-preview.0.26103.119",
    "rollForward": "latestFeature"
  }
}
```

`rollForward: latestFeature` means: use the latest feature band for this major.minor.patch, so any newer 10.0.2xx SDK will be picked up automatically.

**Step 2: Verify SDK is selected**

Run:
```
dotnet --version
```
Expected output: `10.0.200-preview.0.26103.119` (or newer 10.x if installed)

**Step 3: Commit**

```
git add global.json
git commit -m "chore: pin SDK to .NET 10 via global.json"
```

---

### Task 2: Bump All Target Framework Monikers

**Files to modify (exact find/replace in each):**
- `src\EyeCtrl.Core\EyeCtrl.Core.csproj`
- `src\EyeCtrl.Platform\EyeCtrl.Platform.csproj`
- `src\EyeCtrl.App\EyeCtrl.App.csproj`
- `tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj`
- `tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj`
- `tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj`

**Step 1: Edit EyeCtrl.Core.csproj**

Change:
```xml
<TargetFramework>net8.0</TargetFramework>
```
To:
```xml
<TargetFramework>net10.0</TargetFramework>
```

**Step 2: Edit EyeCtrl.Platform.csproj**

Same change: `net8.0` → `net10.0`

**Step 3: Edit EyeCtrl.App.csproj**

Change:
```xml
<TargetFramework>net8.0-windows</TargetFramework>
```
To:
```xml
<TargetFramework>net10.0-windows</TargetFramework>
```

**Step 4: Edit EyeCtrl.Core.Tests.csproj**

Change: `net8.0` → `net10.0`

**Step 5: Edit EyeCtrl.Platform.Tests.csproj**

Change: `net8.0-windows` → `net10.0-windows`

**Step 6: Edit EyeCtrl.App.Tests.csproj**

Change: `net8.0-windows` → `net10.0-windows`

**Step 7: Build to verify TFM change compiles**

Run from repo root:
```
dotnet build EyeCtrl.slnx
```
Expected: `Build succeeded.` with 0 errors. Warnings about test package versions are acceptable at this stage.

If build fails, read the error carefully — the Tobii DLL (tobii_interaction_lib_cs.dll) is .NET Standard 2.0 and is forward-compatible, so it should not be the source of any error.

**Step 8: Commit**

```
git add src\EyeCtrl.Core\EyeCtrl.Core.csproj
git add src\EyeCtrl.Platform\EyeCtrl.Platform.csproj
git add src\EyeCtrl.App\EyeCtrl.App.csproj
git add tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj
git add tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj
git add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj
git commit -m "chore: bump all TFMs from net8.0 to net10.0"
```

---

### Task 3: Update NuGet Packages

All test packages need updating. The three test projects share the same packages; EyeCtrl.App.Tests additionally uses FlaUI.UIA3.

**Target versions (confirmed via `dotnet list package --outdated`):**
- `coverlet.collector`: 6.0.0 → **8.0.0**
- `Microsoft.NET.Test.Sdk`: 17.8.0 → **18.3.0**
- `xunit`: 2.5.3 → **2.9.3**
- `xunit.runner.visualstudio`: 2.5.3 → **3.1.5** (major bump; 3.x supports xunit v2 tests)
- `FlaUI.UIA3` (App.Tests only): 4.0.0 → **5.0.0**

**Step 1: Update EyeCtrl.Core.Tests packages**

Run:
```
dotnet add tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj package coverlet.collector --version 8.0.0
dotnet add tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj package Microsoft.NET.Test.Sdk --version 18.3.0
dotnet add tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj package xunit --version 2.9.3
dotnet add tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj package xunit.runner.visualstudio --version 3.1.5
```

**Step 2: Update EyeCtrl.Platform.Tests packages**

Run:
```
dotnet add tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj package coverlet.collector --version 8.0.0
dotnet add tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj package Microsoft.NET.Test.Sdk --version 18.3.0
dotnet add tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj package xunit --version 2.9.3
dotnet add tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj package xunit.runner.visualstudio --version 3.1.5
```

**Step 3: Update EyeCtrl.App.Tests packages**

Run:
```
dotnet add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj package coverlet.collector --version 8.0.0
dotnet add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj package Microsoft.NET.Test.Sdk --version 18.3.0
dotnet add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj package xunit --version 2.9.3
dotnet add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj package xunit.runner.visualstudio --version 3.1.5
dotnet add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj package FlaUI.UIA3 --version 5.0.0
```

**Step 4: Restore and build**

Run:
```
dotnet build EyeCtrl.slnx
```
Expected: `Build succeeded.` with 0 errors.

**Step 5: Run the unit tests**

Run (Core + Platform tests only — App.Tests requires running app):
```
dotnet test tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj --verbosity normal
dotnet test tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj --verbosity normal
```
Expected: all tests pass (30 total across both projects).

If any tests fail with xunit.runner.visualstudio 3.1.5, check that xunit 2.9.3 is in the same project — the runner v3 supports xunit v2 assemblies but both must be present.

**Step 6: Commit**

```
git add tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj
git add tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj
git add tests\EyeCtrl.App.Tests\EyeCtrl.App.Tests.csproj
git commit -m "chore: update test packages to latest (xunit 2.9.3, Test.Sdk 18.3.0, FlaUI 5.0.0)"
```

---

### Task 4: XAML Grid Shorthand in SettingsWindow.xaml

**File:**
- Modify: `src\EyeCtrl.App\Views\SettingsWindow.xaml`

Only this file has Grid definition blocks. There are exactly 3 to convert.

**Step 1: Convert outer Grid ColumnDefinitions**

Current (lines 10–14):
```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="220"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
```

Replace with:
```xml
<Grid ColumnDefinitions="220, *">
```

(Remove the 5-line block; add the attribute to the opening `<Grid>` tag.)

**Step 2: Convert left sidebar Grid RowDefinitions**

Current (lines 17–21):
```xml
<Grid Grid.Column="0" Background="#C0141414">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
    </Grid.RowDefinitions>
```

Replace with:
```xml
<Grid Grid.Column="0" Background="#C0141414" RowDefinitions="Auto, *">
```

**Step 3: Convert right content Grid RowDefinitions**

Current (lines 41–45):
```xml
<Grid Grid.Column="1" Margin="16">
    <Grid.RowDefinitions>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
```

Replace with:
```xml
<Grid Grid.Column="1" Margin="16" RowDefinitions="*, Auto">
```

**Step 4: Build to verify XAML is valid**

Run:
```
dotnet build src\EyeCtrl.App\EyeCtrl.App.csproj
```
Expected: `Build succeeded.` with 0 errors.

If you see a XAML parse error on the Grid shorthand, verify you are targeting `net10.0-windows` — the `ColumnDefinitions`/`RowDefinitions` attributes are only available in .NET 10 WPF.

**Step 5: Run all unit tests one final time**

Run:
```
dotnet test tests\EyeCtrl.Core.Tests\EyeCtrl.Core.Tests.csproj
dotnet test tests\EyeCtrl.Platform.Tests\EyeCtrl.Platform.Tests.csproj
```
Expected: all 30 tests pass.

**Step 6: Commit**

```
git add src\EyeCtrl.App\Views\SettingsWindow.xaml
git commit -m "feat: adopt .NET 10 WPF Grid shorthand syntax in SettingsWindow"
```

---

## Done Checklist

- [ ] `global.json` at repo root, `dotnet --version` returns 10.x
- [ ] All 6 `.csproj` files target `net10.0` or `net10.0-windows`
- [ ] `dotnet build EyeCtrl.slnx` → 0 errors
- [ ] All 30 unit tests pass on .NET 10
- [ ] `SettingsWindow.xaml` uses Grid shorthand (no `<Grid.ColumnDefinitions>` blocks remain)
- [ ] 4 commits on `main`
