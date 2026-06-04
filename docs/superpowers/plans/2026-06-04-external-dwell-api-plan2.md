# External Dwell API — Plan 2 (WinUI 3 Head) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.
>
> **Depends on Plan 1.** This plan assumes `EyeCtrl.Contracts`, `EyeCtrl.Sdk.Abstractions` (`IDwellElementAdapter`, `DwellClient`, `OutboundRectTracker`, `DwellMode`), and the EyeCtrl-side `ExternalDwellService` are already merged and green.

**Goal:** Add a WinUI 3 (Windows App SDK) head so cooperating **WinUI 3** apps get the same one-attached-property dwell integration as WPF apps. EyeCtrl is untouched: the pipe contract is framework-agnostic, so a WinUI consumer looks identical to EyeCtrl.

**What's new vs Plan 1:** only the things that touch a UI element are framework-specific — coordinate resolution, HWND lookup, UI-thread marshaling, automation-peer invoke, attached properties, and the sample. Everything else is reused.

**Why the EyeCtrl side already supports WinUI:** WinUI 3 hosts XAML content in a **child island HWND**, so `WindowFromPoint(gaze)` returns that child, not the top-level window that `WindowNative.GetWindowHandle` returns. Plan 1's `ExternalDwellService.RootWindowAtPointImpl` already composes `WindowFromPoint` + `GetAncestor(…, GA_ROOT)`, which collapses the island child to the top-level window — so gating matches the HWND this SDK reports. No EyeCtrl change needed; add a regression test (Task 5) to lock that behavior.

**Tech Stack:** .NET 8, WinUI 3 / Windows App SDK **1.4+** (1.4 is the floor for `XamlRoot.ContentIslandEnvironment`), `Microsoft.UI.Xaml.Automation.*`, `DispatcherQueue`, xUnit. TFM `net8.0-windows10.0.19041.0` (Win10 2004 floor; **Win11 not required**), consumable by the net10/Win11 app.

---

### Task 1: Create EyeCtrl.Sdk.WinUI project + EyeCtrlDwell entry point

**Files:**
- Create: `src/EyeCtrl.Sdk.WinUI/EyeCtrl.Sdk.WinUI.csproj`
- Create: `src/EyeCtrl.Sdk.WinUI/EyeCtrlDwell.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <UseWinUI>true</UseWinUI>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <PackageId>EyeCtrl.Sdk.WinUI</PackageId>
    <Description>EyeCtrl gaze-dwell integration for WinUI 3. Attach eyectrl:Dwell.Enabled="True" to any FrameworkElement.</Description>
  </PropertyGroup>
  <ItemGroup>
    <!-- 1.4+ required for XamlRoot.ContentIslandEnvironment (implicit HWND fallback). -->
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.5.240607001" />
    <ProjectReference Include="..\EyeCtrl.Sdk.Abstractions\EyeCtrl.Sdk.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

> Pin to a concrete `Microsoft.WindowsAppSDK` version that matches the consumer app's version to avoid mismatch warnings. Any 1.4+ works for the API used.

- [ ] **Step 2: Add to `EyeCtrl.slnx`** under `/src/`:
```xml
<Project Path="src/EyeCtrl.Sdk.WinUI/EyeCtrl.Sdk.WinUI.csproj" />
```

- [ ] **Step 3: Create EyeCtrlDwell.cs (optional explicit window registration)**

```csharp
namespace EyeCtrl.Sdk.WinUI;

using Microsoft.UI.Xaml;

/// <summary>
/// Optional one-time initialization for WinUI hosts. Call once with your main Window to
/// supply the owning HWND explicitly. If you don't, the adapter resolves it implicitly via
/// XamlRoot.ContentIslandEnvironment (Windows App SDK 1.4+).
/// </summary>
public static class EyeCtrlDwell
{
    internal static long ExplicitHwnd;

    public static void Initialize(Window window)
        => ExplicitHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64();
}
```

- [ ] **Step 4: Build verify** — `dotnet build src/EyeCtrl.Sdk.WinUI/EyeCtrl.Sdk.WinUI.csproj`.
- [ ] **Step 5: Commit**
```bash
git add src/EyeCtrl.Sdk.WinUI/ EyeCtrl.slnx
git commit -m "feat: scaffold EyeCtrl.Sdk.WinUI + EyeCtrlDwell.Initialize"
```

---

### Task 2: WinUI coordinate helper (pure) + tests

The fiddly physical-pixel math is isolated into a pure function so it's unit-testable without a running WinUI app.

**Files:**
- Create: `src/EyeCtrl.Sdk.WinUI/WinUiCoords.cs`
- Create: `tests/EyeCtrl.Sdk.WinUI.Tests/EyeCtrl.Sdk.WinUI.Tests.csproj`
- Create: `tests/EyeCtrl.Sdk.WinUI.Tests/WinUiCoordsTests.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create WinUiCoords.cs**

```csharp
namespace EyeCtrl.Sdk.WinUI;

using EyeCtrl.Contracts;

/// <summary>Pure coordinate composition for WinUI element → physical screen rect.</summary>
public static class WinUiCoords
{
    /// <param name="dipX">Element X relative to the window content root, in DIPs (from TransformToVisual).</param>
    /// <param name="dipY">Element Y relative to the window content root, in DIPs.</param>
    /// <param name="dipW">Element ActualWidth in DIPs.</param>
    /// <param name="dipH">Element ActualHeight in DIPs.</param>
    /// <param name="scale">XamlRoot.RasterizationScale (DIP→physical).</param>
    /// <param name="clientOriginX">Client-area top-left X in physical screen px (from ClientToScreen(hwnd,{0,0})).</param>
    /// <param name="clientOriginY">Client-area top-left Y in physical screen px.</param>
    public static SdkRect ToScreenRect(
        double dipX, double dipY, double dipW, double dipH,
        double scale, double clientOriginX, double clientOriginY)
        => new(
            clientOriginX + dipX * scale,
            clientOriginY + dipY * scale,
            dipW * scale,
            dipH * scale);
}
```

- [ ] **Step 2: Create the test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Plain net8.0: tests exercise the pure helper only; no WinUI runtime needed. -->
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Compile the pure helper + Contracts directly to avoid a WinUI runtime dependency in tests. -->
    <Compile Include="..\..\src\EyeCtrl.Sdk.WinUI\WinUiCoords.cs" Link="WinUiCoords.cs" />
    <ProjectReference Include="..\..\src\EyeCtrl.Contracts\EyeCtrl.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

> Match xUnit `Version="…"` to the existing test projects (or central package management). Linking `WinUiCoords.cs` keeps the helper testable on plain `net8.0`; if the team prefers referencing the WinUI project, the test must target the WinUI TFM and run on Windows.

- [ ] **Step 3: Add to `EyeCtrl.slnx`** under `/tests/`:
```xml
<Project Path="tests/EyeCtrl.Sdk.WinUI.Tests/EyeCtrl.Sdk.WinUI.Tests.csproj" />
```

- [ ] **Step 4: Create WinUiCoordsTests.cs**

```csharp
namespace EyeCtrl.Sdk.WinUI.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.WinUI;
using Xunit;

public class WinUiCoordsTests
{
    [Fact]
    public void Scale1_NoOffset_PassesThrough()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 1.0, clientOriginX: 0, clientOriginY: 0);
        Assert.Equal(new SdkRect(10, 20, 80, 40), r);
    }

    [Fact]
    public void Scale150_AppliesScaleToPositionAndSize()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 1.5, clientOriginX: 0, clientOriginY: 0);
        Assert.Equal(new SdkRect(15, 30, 120, 60), r);
    }

    [Fact]
    public void ClientOrigin_OffsetsPositionOnly_NotSize()
    {
        var r = WinUiCoords.ToScreenRect(10, 20, 80, 40, scale: 2.0, clientOriginX: 1000, clientOriginY: 500);
        Assert.Equal(new SdkRect(1000 + 20, 500 + 40, 160, 80), r);
    }
}
```

- [ ] **Step 5: Run** — `dotnet test tests/EyeCtrl.Sdk.WinUI.Tests` (3 pass).
- [ ] **Step 6: Commit**
```bash
git add src/EyeCtrl.Sdk.WinUI/WinUiCoords.cs tests/EyeCtrl.Sdk.WinUI.Tests/ EyeCtrl.slnx
git commit -m "feat: add pure WinUiCoords helper with tests"
```

---

### Task 3: WinUI element adapter (coords, HWND, marshal, invoke)

**Files:**
- Create: `src/EyeCtrl.Sdk.WinUI/WinUiElementAdapter.cs`

- [ ] **Step 1: Create WinUiElementAdapter.cs**

```csharp
namespace EyeCtrl.Sdk.WinUI;

using System.Runtime.InteropServices;
using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;
using Microsoft.UI;                       // Win32Interop
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

internal sealed class WinUiElementAdapter : IDwellElementAdapter
{
    private readonly FrameworkElement _element;
    private long _hwndCache;

    public string Id { get; }
    public DwellMode Mode { get; }

    public WinUiElementAdapter(string id, FrameworkElement element, DwellMode mode)
    {
        Id = id;
        _element = element;
        Mode = mode;
    }

    public long WindowHandle => _hwndCache != 0 ? _hwndCache : (_hwndCache = ResolveHwnd());

    private long ResolveHwnd()
    {
        if (EyeCtrlDwell.ExplicitHwnd != 0) return EyeCtrlDwell.ExplicitHwnd;
        // Implicit fallback (Windows App SDK 1.4+): XamlRoot → ContentIslandEnvironment → AppWindowId → HWND.
        var env = _element.XamlRoot?.ContentIslandEnvironment;
        if (env is null) return 0;
        return Win32Interop.GetWindowFromWindowId(env.AppWindowId).ToInt64();
    }

    public bool TryGetScreenRect(out SdkRect rect)
    {
        rect = default;
        var xr = _element.XamlRoot;
        if (xr is null || _element.ActualWidth <= 0 || _element.ActualHeight <= 0) return false;

        var hwnd = WindowHandle;
        if (hwnd == 0) return false;

        try
        {
            // Element position relative to the window content root, in DIPs.
            var dip = _element.TransformToVisual(xr.Content).TransformPoint(new Point(0, 0));
            // Client-area origin in physical screen px.
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(new IntPtr(hwnd), ref origin)) return false;

            rect = WinUiCoords.ToScreenRect(
                dip.X, dip.Y, _element.ActualWidth, _element.ActualHeight,
                xr.RasterizationScale, origin.X, origin.Y);
            return true;
        }
        catch { return false; }
    }

    public void Activate() => _element.DispatcherQueue.TryEnqueue(() =>
    {
        if (Mode == DwellMode.Focus) { _element.Focus(FocusState.Programmatic); return; }

        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(_element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }

        if (_element is ButtonBase b && b.Command?.CanExecute(b.CommandParameter) == true)
            b.Command.Execute(b.CommandParameter);
    });

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
}
```

> `TransformToVisual(xr.Content)` yields position relative to the window's content root, which maps to the client area's top-left — so adding `ClientToScreen({0,0})` gives the true physical-screen rect, DPI handled by `RasterizationScale`. This is the WinUI analogue of WPF's `PointToScreen` (which already returns physical px).

- [ ] **Step 2: Build verify** — `dotnet build src/EyeCtrl.Sdk.WinUI/EyeCtrl.Sdk.WinUI.csproj`.
- [ ] **Step 3: Commit**
```bash
git add src/EyeCtrl.Sdk.WinUI/WinUiElementAdapter.cs
git commit -m "feat: add WinUiElementAdapter (coords, HWND resolution, UIA invoke)"
```

---

### Task 4: WinUI attached properties (Dwell) + rect-refresh wiring

Mirrors `EyeCtrl.Sdk.Wpf.Dwell`, using `Microsoft.UI.Xaml` types. Because moving a WinUI window does **not** raise `LayoutUpdated`, also refresh rects on `AppWindow` changes and `XamlRoot` scale changes.

**Files:**
- Create: `src/EyeCtrl.Sdk.WinUI/Dwell.cs`

- [ ] **Step 1: Create Dwell.cs**

```csharp
namespace EyeCtrl.Sdk.WinUI;

using System.Collections.Concurrent;
using EyeCtrl.Sdk.Abstractions;
using Microsoft.UI.Xaml;

public static class Dwell
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Dwell), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(DwellMode), typeof(Dwell), new PropertyMetadata(DwellMode.Invoke));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static DwellMode GetMode(DependencyObject o) => (DwellMode)o.GetValue(ModeProperty);
    public static void SetMode(DependencyObject o, DwellMode v) => o.SetValue(ModeProperty, v);

    private static readonly DwellClient _client = new();
    private static readonly ConcurrentDictionary<FrameworkElement, Entry> _entries = new();
    private static int _started;
    private static long _idCounter;

    private sealed record Entry(WinUiElementAdapter Adapter, RoutedEventHandler Loaded,
                                RoutedEventHandler Unloaded, EventHandler<object> LayoutUpdated);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) Register(el);
        else Unregister(el);
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _client.Start();
    }

    private static void Register(FrameworkElement el)
    {
        EnsureStarted();
        if (_entries.ContainsKey(el)) return;

        var id = $"winui_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WinUiElementAdapter(id, el, GetMode(el));

        RoutedEventHandler loaded = (_, _) => _client.RegisterAdapter(adapter);
        RoutedEventHandler unloaded = (_, _) => _client.UnregisterAdapter(id);
        EventHandler<object> layout = (_, _) => _client.OnAdapterLayoutChanged(id);

        el.Loaded += loaded;
        el.Unloaded += unloaded;
        el.LayoutUpdated += layout;

        _entries[el] = new Entry(adapter, loaded, unloaded, layout);

        if (el.IsLoaded) _client.RegisterAdapter(adapter);
        HookWindowMoves(el, id);  // see Step 2
    }

    private static void Unregister(FrameworkElement el)
    {
        if (!_entries.TryRemove(el, out var entry)) return;
        el.Loaded -= entry.Loaded;
        el.Unloaded -= entry.Unloaded;
        el.LayoutUpdated -= entry.LayoutUpdated;
        _client.UnregisterAdapter(entry.Adapter.Id);
    }

    private static void HookWindowMoves(FrameworkElement el, string id) { /* Step 2 */ }
}
```

- [ ] **Step 2: Implement `HookWindowMoves`** so window moves/resizes/scale-changes re-push the rect (LayoutUpdated alone misses window moves):

```csharp
private static void HookWindowMoves(FrameworkElement el, string id)
{
    void Wire()
    {
        var xr = el.XamlRoot;
        if (xr is null) return;

        // Scale changes (monitor DPI change / move across monitors).
        xr.Changed += (_, _) => _client.OnAdapterLayoutChanged(id);

        // Window move/resize via AppWindow.
        var env = xr.ContentIslandEnvironment;
        if (env is not null)
        {
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(env.AppWindowId);
            if (appWindow is not null)
                appWindow.Changed += (_, _) => _client.OnAdapterLayoutChanged(id);
        }
    }

    if (el.XamlRoot is not null) Wire();
    else el.Loaded += (_, _) => Wire();
}
```

> Alternative if the AppWindow plumbing proves flaky across SDK versions: a single shared `DispatcherQueueTimer` at ~5 Hz that calls `OnAdapterLayoutChanged` for all entries. It's cheap because `OutboundRectTracker` dedupes unchanged rects on the wire. (This is *position reporting*, not dwell detection — unlike the rejected SDK-owned design, nothing time-critical rides on it.)

- [ ] **Step 3: Build verify** — `dotnet build src/EyeCtrl.Sdk.WinUI/EyeCtrl.Sdk.WinUI.csproj`.
- [ ] **Step 4: Commit**
```bash
git add src/EyeCtrl.Sdk.WinUI/Dwell.cs
git commit -m "feat: add WinUI Dwell attached properties + window-move rect refresh"
```

---

### Task 5: EyeCtrl-side gating regression test for island child HWNDs

Locks in the behavior that makes WinUI gating work: a gaze point over a child (island) HWND must gate to its **root** top-level window — which is what the SDK reports. `ExternalDwellService.RootWindowAtPointImpl` already does this via `GetAncestor(GA_ROOT)`; this test guards it via the injectable `rootWindowAtPoint` seam.

**Files:**
- Modify: `tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs` (add a case) — or add to a new file.

- [ ] **Step 1: Add the test**

```csharp
[Fact]
public void Gating_RootWindowMatchesReportedHwnd_Activates()
{
    // Simulates WinUI: the SDK registered the top-level window HWND (7);
    // the gating seam returns the ROOT window at the point (7), even though
    // WindowFromPoint would have returned a child island HWND in reality.
    var a = new ExternalDwellArbiter(_ => 7, cooldownMs: 0);
    a.AddOrUpdate("btn", new PhysicalRect(100, 100, 80, 40), hwnd: 7, dwellMs: 800);
    var inside = new PhysicalPoint(140, 120);
    a.Process(inside, 0);
    Assert.Equal("btn", a.Process(inside, 800_000).ActivatedId);
}
```

> This documents the contract: the gating seam returns the **root** window handle, matching the HWND the WinUI adapter resolves (`WindowNative.GetWindowHandle` / `ContentIslandEnvironment.AppWindowId`). The real `GetAncestor(GA_ROOT)` composition lives in `ExternalDwellService` (Plan 1) and needs no change.

- [ ] **Step 2: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ExternalDwellArbiterTests"`.
- [ ] **Step 3: Commit**
```bash
git add tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs
git commit -m "test: lock WinUI island-child HWND gating via root-window seam"
```

---

### Task 6: WinUI 3 sample app (unpackaged)

**Files:**
- Create: `samples/EyeCtrl.Sdk.WinUI.Sample/EyeCtrl.Sdk.WinUI.Sample.csproj`
- Create: `samples/EyeCtrl.Sdk.WinUI.Sample/App.xaml` + `App.xaml.cs`
- Create: `samples/EyeCtrl.Sdk.WinUI.Sample/MainWindow.xaml` + `MainWindow.xaml.cs`

- [ ] **Step 1: csproj (unpackaged — easiest to launch)**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.5.240607001" />
    <ProjectReference Include="..\..\src\EyeCtrl.Sdk.WinUI\EyeCtrl.Sdk.WinUI.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: App.xaml / App.xaml.cs** — standard WinUI 3 app that creates and activates `MainWindow` in `OnLaunched`.

```csharp
namespace EyeCtrl.Sdk.WinUI.Sample;

using Microsoft.UI.Xaml;

public partial class App : Application
{
    private Window? _window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
```

- [ ] **Step 3: MainWindow.xaml** (note `xmlns:eyectrl="using:EyeCtrl.Sdk.WinUI"`)

```xml
<Window x:Class="EyeCtrl.Sdk.WinUI.Sample.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:eyectrl="using:EyeCtrl.Sdk.WinUI">
    <StackPanel Margin="20" Spacing="8" VerticalAlignment="Center">
        <TextBlock Text="Dwell-enabled buttons:" FontSize="16"/>
        <Button Content="Save" Width="220" Height="44" Click="OnSave"
                eyectrl:Dwell.Enabled="True"/>
        <Button Content="Focus me" Width="220" Height="44"
                eyectrl:Dwell.Enabled="True" eyectrl:Dwell.Mode="Focus"/>
        <Button Content="Plain (no dwell)" Width="220" Height="44" Click="OnPlain"/>
        <TextBlock x:Name="Log" Foreground="Gray"/>
    </StackPanel>
</Window>
```

- [ ] **Step 4: MainWindow.xaml.cs** — call `EyeCtrlDwell.Initialize(this)` in the ctor (explicit HWND), and implement `OnSave` / `OnPlain`:

```csharp
namespace EyeCtrl.Sdk.WinUI.Sample;

using Microsoft.UI.Xaml;
using EyeCtrl.Sdk.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        EyeCtrlDwell.Initialize(this);   // explicit HWND (implicit fallback also works if omitted)
    }

    private void OnSave(object sender, RoutedEventArgs e) => Log.Text = $"Saved at {DateTime.Now:T}";
    private void OnPlain(object sender, RoutedEventArgs e) => Log.Text = "Plain click";
}
```

- [ ] **Step 5: Build verify** — `dotnet build samples/EyeCtrl.Sdk.WinUI.Sample/EyeCtrl.Sdk.WinUI.Sample.csproj`.
- [ ] **Step 6: Commit**
```bash
git add samples/EyeCtrl.Sdk.WinUI.Sample/
git commit -m "feat: add WinUI 3 sample app with dwell-enabled controls"
```

---

### Task 7: Full solution build + all tests

- [ ] **Step 1:** `dotnet build EyeCtrl.slnx` — zero errors. (WinUI projects require the Windows App SDK workload/runtime present on the build machine.)
- [ ] **Step 2:** `dotnet test` — all tests pass (WPF + WinUI helper tests + EyeCtrl-side).
- [ ] **Step 3:** Fix any package-version or `using` issues.
- [ ] **Step 4: Commit**
```bash
git add -A
git commit -m "chore: External Dwell API Plan 2 — WinUI 3 head complete"
```

---

### Task 8: Manual smoke test (WinUI consumer)

- [ ] **Step 1:** Start EyeCtrl: `dotnet run --project src/EyeCtrl.App -- --test-mode`
- [ ] **Step 2:** Start the WinUI sample: `dotnet run --project samples/EyeCtrl.Sdk.WinUI.Sample`
- [ ] **Step 3: Verify** (same matrix as Plan 1, plus WinUI specifics)
  1. Dwell **Save** → feedback overlay fills over the button → click fires (log updates).
  2. EyeCtrl's own activation button/action menu is suppressed while over the button; ring hidden; both restore on leave.
  3. Dwell **Focus me** → focus only.
  4. **Plain** still clicks normally.
  5. **Move the window** to another position/monitor mid-session, then dwell again → overlay still lands on the button (validates `AppWindow.Changed`/scale refresh + island-child gating via `GetAncestor(GA_ROOT)`).
  6. Per-monitor DPI: confirm correct placement at 125%/150% scaling and across monitors with different scales.
  7. Remove the `EyeCtrlDwell.Initialize(this)` call and re-run to confirm the **implicit** `ContentIslandEnvironment` HWND fallback also works.
- [ ] **Step 4:** Fix and re-test as needed.

---

## Summary of reuse

| Component | Source | Plan 2 action |
|---|---|---|
| `EyeCtrl.Contracts` (DTOs, `SdkRect`) | Plan 1 | reuse unchanged |
| `EyeCtrl.Sdk.Abstractions` (`DwellClient`, `OutboundRectTracker`, `IDwellElementAdapter`, `DwellMode`) | Plan 1 | reuse unchanged |
| `ExternalDwellService`, `ExternalDwellArbiter`, `DwellTarget` rect, settings, feedback overlay, DI | Plan 1 | reuse unchanged (gating already island-aware) |
| Element adapter, attached properties, coordinate math, HWND lookup, sample | — | **new (this plan)** |
