# EyeCtrl Known Bug Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three known EyeCtrl issues: first-run action menu crash from `GlassBackdropWindow`, XML documentation build warning in `IDwellRegistrar`, and nullable warning in `EventCoordinator`.

**Architecture:** Keep each fix minimal and local to the owning layer. `GlassBackdropWindow` remains a WPF overlay with XAML-backed controls. `IDwellRegistrar` remains a Core contract and must not acquire an App-layer dependency. `EventCoordinator` remains the app-level message/property-change coordinator, with the nullable issue fixed by capturing a non-null dispatcher context snapshot inside the deferred lambda.

**Tech Stack:** C#, .NET 10, WPF, xUnit 2.9.x, Xunit.StaFact, GitNexus impact analysis.

---

## Impact Summary

- `GlassBackdropWindow`: GitNexus impact returned **LOW**. Direct dependents are overlay initialization and backdrop access paths.
- `IDwellRegistrar`: GitNexus impact returned **LOW**. Direct dependents are `DwellControl` and `SettingsWindowViewModel`.
- `EventCoordinator`: GitNexus impact returned **MEDIUM** because `App.OnStartup` depends on it. This is not HIGH/CRITICAL, but the fix should be compile-verified after changing it.
- `EyeCtrl.App.Tests` (Task 0): adding the `Xunit.StaFact` package is additive and test-only — no production impact.

---

## Task 0: Add `Xunit.StaFact` test infrastructure (prerequisite for Task 1)

**Files:**
- Modify: `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`

Task 1's regression test uses `[StaFact]` (single-threaded apartment), which is required to construct a WPF `Window` in a test. The `EyeCtrl.App.Tests` project does **not** currently reference `Xunit.StaFact`, and no test in the repo uses `[StaFact]` yet, so the package must be added first or Task 1 will not compile.

- [ ] **Step 1: Add the `Xunit.StaFact` package reference**

In `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`, add to the `<ItemGroup>` that already contains the `xunit` reference:

```xml
<PackageReference Include="Xunit.StaFact" Version="1.2.69" />
```

> Version `1.2.69` is the newest release that targets xunit **v2** (it depends on `xunit.extensibility.execution` 2.4.2), matching this project's `xunit` 2.9.3. Do **not** use 2.x or 3.x — those depend on xunit **v3** (`xunit.v3.extensibility.core`) and are incompatible.

- [ ] **Step 2: Verify the test project restores and builds**

Run:

```bash
dotnet build tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj
```

Expected: PASS. The package restores and `[StaFact]` resolves via the project's global `<Using Include="Xunit" />` (`StaFact` lives in the `Xunit` namespace).

---

## Task 1: Fix `GlassBackdropWindow` XAML initialization

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs:34-37`
- Create: `tests/EyeCtrl.App.Tests/Overlays/GlassBackdropWindowTests.cs`

- [ ] **Step 1: Add a failing STA regression test**

Requires the `Xunit.StaFact` package added in Task 0. Create `tests/EyeCtrl.App.Tests/Overlays/GlassBackdropWindowTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

public class GlassBackdropWindowTests
{
    [StaFact]
    public void Hide_DoesNotThrow_WhenConstructedWithoutShowing()
    {
        var window = new GlassBackdropWindow();

        var exception = Record.Exception(() => window.Hide());

        Assert.Null(exception);
    }
}
```

- [ ] **Step 2: Run the failing test**

Run:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~GlassBackdropWindowTests"
```

Expected: FAIL with a `NullReferenceException` from `BackdropCanvas` being null before XAML is loaded.

- [ ] **Step 3: Initialize the XAML in the constructor**

Modify `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs` constructor:

```csharp
public GlassBackdropWindow()
{
    InitializeComponent();
    IsClickThrough = true;
}
```

- [ ] **Step 4: Run the regression test**

Run:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~GlassBackdropWindowTests"
```

Expected: PASS.

---

## Task 2: Fix unresolved XML documentation `cref` in `IDwellRegistrar`

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/IDwellRegistrar.cs:30-39`

- [ ] **Step 1: Replace the invalid App-layer `cref` with literal text**

In `src/EyeCtrl.Core/Processing/IDwellRegistrar.cs`, change **only** the `<see cref="Controls.DwellControl"/>` reference to `<c>DwellControl</c>`, leaving the rest of the `<remarks>` block unchanged. After the edit the full block must read exactly:

```csharp
/// <remarks>
/// Implemented by the ViewModel of each overlay window so that
/// <c>DwellControl</c> sub-classes can self-register without
/// taking a direct dependency on the WPF layer or a concrete ViewModel type.
/// All positions must be supplied in physical screen pixels (device pixels) to
/// match the coordinates produced by the Tobii eye-tracker.
/// </remarks>
```

- [ ] **Step 2: Verify the cref warning is gone**

Run:

```bash
dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj -warnaserror:CS1574
```

Expected: PASS. Promote **only** `CS1574` to an error — Core has `GenerateDocumentationFile=true` and many pre-existing `CS1591` "missing XML comment" warnings, so a blanket `-warnaserror` fails the build with ~100 unrelated errors and would NOT validate this fix.

---

## Task 3: Fix nullable warning in `EventCoordinator` deferred backdrop dismissal handler

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:260-274`

- [ ] **Step 1: Replace the nullable `_context` access inside the deferred lambda**

Modify the `BackdropDismissMessage` handler body in `src/EyeCtrl.App/Services/EventCoordinator.cs` to capture a local context snapshot:

```csharp
_context.Dispatcher.BeginInvoke(() =>
{
    var context = _context;
    if (context == null)
        return;

    HideBackdrop();

    if (context.TargetHighlightVM.IsVisible)
    {
        context.TargetHighlightVM.IsVisible = false;
        context.OverlayManager.TargetHighlight.HideHighlight();
    }

    var im = context.InteractionManager;
    if (im.CurrentState == InteractionState.ActionMenuVisible)
        im.OnActionMenuDismissed();
    else if (im.CurrentState == InteractionState.ManualSelectionActive)
        im.OnManualSelectionBack();
});
```

- [ ] **Step 2: Verify the nullable warning is gone**

Run:

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj -warnaserror:CS8602
```

Expected: PASS. Promote **only** `CS8602` to an error. A blanket `-warnaserror` fails on a clean build because building App also compiles Core (`CS1591`) and ViewModels (`MVVMTK0034`), which carry pre-existing tolerated warnings unrelated to this fix.

---

## Final Verification

- [ ] **Step 1: Build the full solution**

Run:

```bash
dotnet build EyeCtrl.slnx
```

Expected: PASS.

- [ ] **Step 2: Run the focused App test**

Run:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~GlassBackdropWindowTests"
```

Expected: PASS.

- [ ] **Step 3: Run UI tests if a display is available**

Run:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"
```

Expected: PASS in an environment with a display.

- [ ] **Step 4: Run the headless-safe unit suite**

Run:

```bash
dotnet test --filter "Category!=UI"
```

Expected: PASS. A bare `dotnet test` also runs ~13 `Category=UI` App.Tests that require an interactive display and fail headless — those failures are environmental, not regressions. Run the full `dotnet test` only in an environment with a display (see Step 3).
