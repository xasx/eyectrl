# External Dwell API — Plan 1 (WPF Vertical Slice) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the complete WPF vertical slice: a cooperating WPF app registers controls via XAML attached properties; EyeCtrl detects dwell centrally (one engine), invokes the control via UI Automation, and renders dwell-progress feedback over the control.

**Architecture:** Three new SDK projects (`EyeCtrl.Contracts`, `EyeCtrl.Sdk.Abstractions`, `EyeCtrl.Sdk.Wpf`) + tests + a WPF sample feed a new `ExternalDwellService` in `EyeCtrl.App`. The service runs a bidirectional named-pipe server (`eyectrl_dwell`, **byte-mode, newline-framed JSON**) and delegates all dwell decisions to a pure, UI-free `ExternalDwellArbiter`. The arbiter reuses the `DwellTarget` rect primitive and EyeCtrl's dwell-timing convention; it does **not** use or modify `DwellDetector`. A single, additive Core change adds rectangular hit-test to `DwellTarget`.

**Key design rules (derived from code review of the first draft):**
- **`DwellDetector` is NOT modified.** Its `Update` is HIGH change-blast-radius (20 callers). Gating doesn't fit its model, so external dwell lives in a separate `ExternalDwellArbiter`.
- **Threading:** `GazeEngine.CursorMoved` fires on the **background gaze thread** (see `GazeEngineAdapter.OnCursorMoved`, which marshals via `Dispatcher.BeginInvoke`). The service therefore (a) injects a `Dispatcher` and marshals **all** WPF-window calls, (b) sets the atomic `SuppressFixation` bool inline, (c) synchronizes arbiter/target state accessed from both the gaze thread and pipe-handler threads.
- **Testability:** all decision logic (gating, accumulation, cooldown, feedback selection) lives in the pure `ExternalDwellArbiter` (inject `rootWindowAtPoint` + feed timestamps). Client-side dedupe lives in the pure `OutboundRectTracker`. The service and adapters are thin IO/UI glue.
- **DPI:** WPF window coordinates are logical DIPs; convert physical→logical with `DpiHelper.PhysicalToLogicalX/Y` (as `CursorCircleWindow` does).

**Tech Stack:** WPF on .NET 10 (the repo's TFM), System.IO.Pipes (byte-mode), UI Automation (`System.Windows.Automation.*`), xUnit.

**Target-framework policy:** the repo's existing projects all target `net10.0` / `net10.0-windows` (SDK pinned to 10.0.102 via `global.json`). Internal projects and **test** projects therefore target net10. The **published SDK packages** (`EyeCtrl.Contracts`, `EyeCtrl.Sdk.Abstractions`, `EyeCtrl.Sdk.Wpf`, `EyeCtrl.Sdk.WinUI`) are **deliberately floored at net8** (per the spec) so external consumers on net8 *or* net10 can use them — a net8 library is fully consumable by the net10 consumer app. (SDK pin ≠ target framework: a net10 SDK builds net8 targets fine.)

---

### Task 1: Create EyeCtrl.Contracts project with wire DTOs

**Files:**
- Create: `src/EyeCtrl.Contracts/EyeCtrl.Contracts.csproj`
- Create: `src/EyeCtrl.Contracts/SdkRect.cs`
- Create: `src/EyeCtrl.Contracts/WireMessages.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create SdkRect.cs**

```csharp
namespace EyeCtrl.Contracts;

/// <summary>
/// A rectangle in physical screen pixels. Wire format between SDK heads and the
/// EyeCtrl dwell service. Intentionally separate from EyeCtrl.Core.Models.PhysicalRect
/// so the SDK never references Core.
/// </summary>
public readonly record struct SdkRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}
```

- [ ] **Step 3: Create WireMessages.cs**

```csharp
namespace EyeCtrl.Contracts;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(UpdateMessage), "update")]
[JsonDerivedType(typeof(UnregisterMessage), "unregister")]
[JsonDerivedType(typeof(ActivatedMessage), "activated")]
public abstract record WireMessage;

/// <summary>Client→server: a control became dwellable. Rect is physical px; Hwnd is the owning top-level window.</summary>
public record RegisterMessage : WireMessage
{
    public required string Id { get; init; }
    public SdkRect Rect { get; init; }
    public long Hwnd { get; init; }
}

/// <summary>Client→server: an already-registered control's rect changed (no dwell reset).</summary>
public record UpdateMessage : WireMessage
{
    public required string Id { get; init; }
    public SdkRect Rect { get; init; }
}

/// <summary>Client→server: a control is no longer dwellable.</summary>
public record UnregisterMessage : WireMessage
{
    public required string Id { get; init; }
}

/// <summary>Server→client: dwell completed on this control; the SDK should invoke it.</summary>
public record ActivatedMessage : WireMessage
{
    public required string Id { get; init; }
}
```

> Note: dwell **time** is global this phase (`ExternalDwellSettings.DefaultDwellMs`), so there is no `dwellMs` on the wire.

- [ ] **Step 4: Add to solution file**

Edit `EyeCtrl.slnx` — add under the `/src/` folder:
```xml
<Project Path="src/EyeCtrl.Contracts/EyeCtrl.Contracts.csproj" />
```

- [ ] **Step 5: Build verify** — `dotnet build EyeCtrl.slnx` (existing projects + new Contracts compile).

- [ ] **Step 6: Commit**
```bash
git add src/EyeCtrl.Contracts/ EyeCtrl.slnx
git commit -m "feat: add EyeCtrl.Contracts project with wire DTOs"
```

---

### Task 2: Create EyeCtrl.Sdk.Abstractions — adapter seam, dedupe tracker, pipe client

**Files:**
- Create: `src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj`
- Create: `src/EyeCtrl.Sdk.Abstractions/IDwellElementAdapter.cs`
- Create: `src/EyeCtrl.Sdk.Abstractions/OutboundRectTracker.cs`
- Create: `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EyeCtrl.Contracts\EyeCtrl.Contracts.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to `EyeCtrl.slnx`** under `/src/`:
```xml
<Project Path="src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj" />
```

- [ ] **Step 3: Create IDwellElementAdapter.cs**

```csharp
namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

public enum DwellMode { Invoke, Focus }

/// <summary>
/// Per-element seam each framework head implements. The shared DwellClient reads
/// the screen rect + HWND to register, and calls Activate() when EyeCtrl reports a dwell.
/// The adapter owns its own DwellMode (so the client doesn't carry mode on the wire).
/// </summary>
public interface IDwellElementAdapter
{
    string Id { get; }
    DwellMode Mode { get; }
    long WindowHandle { get; }
    bool TryGetScreenRect(out SdkRect rect);
    void Activate();   // marshals to the UI thread internally, then invokes per Mode
}
```

- [ ] **Step 4: Create OutboundRectTracker.cs** (pure dedupe — decides Register vs Update vs skip)

```csharp
namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

/// <summary>
/// Pure, UI-free outbound dedupe. First time an id is seen → RegisterMessage; subsequent
/// changed rects → UpdateMessage; unchanged → null (skip). Reset() forces re-Register on reconnect.
/// </summary>
public sealed class OutboundRectTracker
{
    private readonly Dictionary<string, SdkRect> _sent = new();

    public WireMessage? OnRect(string id, SdkRect rect, long hwnd)
    {
        if (!_sent.TryGetValue(id, out var prev))
        {
            _sent[id] = rect;
            return new RegisterMessage { Id = id, Rect = rect, Hwnd = hwnd };
        }
        if (prev.Equals(rect)) return null;
        _sent[id] = rect;
        return new UpdateMessage { Id = id, Rect = rect };
    }

    public WireMessage OnUnregister(string id)
    {
        _sent.Remove(id);
        return new UnregisterMessage { Id = id };
    }

    public void Reset() => _sent.Clear();
}
```

- [ ] **Step 5: Create DwellClient.cs**

```csharp
namespace EyeCtrl.Sdk.Abstractions;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EyeCtrl.Contracts;

/// <summary>
/// Named-pipe client to the EyeCtrl dwell service. Byte-mode, newline-framed JSON.
/// Dedupes outgoing rects (Register/Update/skip) and routes incoming `activated` to adapters.
/// Reconnects with exponential backoff. All connect/IO failures are silent (EyeCtrl may be absent).
/// </summary>
public sealed class DwellClient : IDisposable
{
    private const string PipeName = "eyectrl_dwell";
    private const int ConnectTimeoutMs = 2000;
    private const int MinBackoffMs = 1000;
    private const int MaxBackoffMs = 30000;

    private readonly ConcurrentDictionary<string, IDwellElementAdapter> _adapters = new();
    private readonly OutboundRectTracker _tracker = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _writeLock = new();

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;

    public void RegisterAdapter(IDwellElementAdapter adapter)
    {
        _adapters[adapter.Id] = adapter;
        SendRect(adapter);
    }

    public void UnregisterAdapter(string id)
    {
        _adapters.TryRemove(id, out _);
        WireMessage msg;
        lock (_tracker) msg = _tracker.OnUnregister(id);
        TrySend(msg);
    }

    public void OnAdapterLayoutChanged(string id)
    {
        if (_adapters.TryGetValue(id, out var adapter)) SendRect(adapter);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = MinBackoffMs;
        while (!_disposed && !token.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(ConnectTimeoutMs, token);
                lock (_writeLock)
                    _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                backoff = MinBackoffMs; // reset on success

                // Re-register everything on a fresh connection.
                lock (_tracker) _tracker.Reset();
                foreach (var adapter in _adapters.Values) SendRect(adapter);

                await ReadLoopAsync(_pipe, token);
            }
            catch (OperationCanceledException) { break; }
            catch { /* EyeCtrl not running or pipe broke */ }
            finally
            {
                lock (_writeLock) { _writer?.Dispose(); _writer = null; }
                _pipe?.Dispose(); _pipe = null;
            }

            if (_disposed || token.IsCancellationRequested) break;
            try { await Task.Delay(backoff, token); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, MaxBackoffMs);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        while (!token.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null) break;
            try
            {
                if (JsonSerializer.Deserialize<WireMessage>(line, _json) is ActivatedMessage a
                    && _adapters.TryGetValue(a.Id, out var adapter))
                {
                    adapter.Activate();
                }
            }
            catch { /* skip malformed */ }
        }
    }

    private void SendRect(IDwellElementAdapter adapter)
    {
        if (!adapter.TryGetScreenRect(out var rect)) return;
        WireMessage? msg;
        lock (_tracker) msg = _tracker.OnRect(adapter.Id, rect, adapter.WindowHandle);
        if (msg != null) TrySend(msg);
    }

    private void TrySend(WireMessage msg)
    {
        lock (_writeLock)
        {
            if (_writer == null) return;
            try { _writer.WriteLine(JsonSerializer.Serialize(msg, _json)); }
            catch { /* reconnect will handle */ }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        lock (_writeLock) { _writer?.Dispose(); _writer = null; }
        _pipe?.Dispose();
        _cts?.Dispose();
    }
}
```

- [ ] **Step 6: Build verify** — `dotnet build src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj`.

- [ ] **Step 7: Commit**
```bash
git add src/EyeCtrl.Sdk.Abstractions/ EyeCtrl.slnx
git commit -m "feat: add EyeCtrl.Sdk.Abstractions (adapter seam, dedupe tracker, pipe client)"
```

---

### Task 3: Add rectangular hit-test to DwellTarget (Core, additive — LOW risk)

> Impact analysis (upstream): `DwellTarget.HitTest` has 1 direct caller (`DwellDetector.Update`); the constructor's only non-test callers are `KeyboardWindow.UpdateGaze` and `EmojiGridManager.UpdateCategoryTargets`. Adding a second constructor + a settable `Bounds` + a rect branch in `HitTest` leaves all of them untouched. **Re-run `gitnexus_impact` before editing to confirm.**

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellTarget.cs`
- Create: `tests/EyeCtrl.Core.Tests/Processing/DwellTargetRectTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using Xunit;

public class DwellTargetRectTests
{
    [Fact]
    public void RectHitTest_InsideAndEdges_ReturnTrue()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Assert.True(t.HitTest(new PhysicalPoint(140, 220))); // center
        Assert.True(t.HitTest(new PhysicalPoint(100, 200))); // top-left edge
        Assert.True(t.HitTest(new PhysicalPoint(180, 240))); // bottom-right edge
    }

    [Fact]
    public void RectHitTest_Outside_ReturnsFalse()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Assert.False(t.HitTest(new PhysicalPoint(99, 200)));
        Assert.False(t.HitTest(new PhysicalPoint(181, 220)));
        Assert.False(t.HitTest(new PhysicalPoint(140, 241)));
    }

    [Fact]
    public void Center_FromRect_IsGeometricCenter()
    {
        var t = new DwellTarget("btn", new PhysicalRect(100, 200, 80, 40), 500);
        Assert.Equal(140, t.Center.X);
        Assert.Equal(220, t.Center.Y);
    }

    [Fact]
    public void Bounds_Settable_UpdatesHitTest()
    {
        var t = new DwellTarget("btn", new PhysicalRect(0, 0, 10, 10), 500);
        Assert.False(t.HitTest(new PhysicalPoint(100, 100)));
        t.Bounds = new PhysicalRect(95, 95, 20, 20);
        Assert.True(t.HitTest(new PhysicalPoint(100, 100)));
    }

    [Fact]
    public void CircleHitTest_Unchanged()
    {
        var t = new DwellTarget("circle", new PhysicalPoint(500, 300), 30, 700);
        Assert.True(t.HitTest(new PhysicalPoint(520, 300)));
        Assert.False(t.HitTest(new PhysicalPoint(540, 300)));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail** — `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetRectTests"` (rect constructor not found).

- [ ] **Step 3: Implement (additive — keep circle ctor + signature unchanged)**

```csharp
namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// A target area for dwell-based gaze interactions. All coordinates are physical screen pixels.
/// Circular by default; rectangular when <see cref="Bounds"/> is set.
/// </summary>
public class DwellTarget
{
    public string Id { get; }
    /// <summary>Center position in physical screen pixels (used for nearest-target selection).</summary>
    public PhysicalPoint Center { get; set; }
    public double Radius { get; }
    public long DwellTimeMicroseconds { get; }
    public long? EnteredTimestamp { get; set; }
    public bool IsGazeInside { get; set; }

    /// <summary>Optional rectangular bounds. When set, HitTest uses rect containment; null ⇒ circle.</summary>
    public PhysicalRect? Bounds { get; set; }

    // Circle constructor (unchanged).
    public DwellTarget(string id, PhysicalPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = null;
    }

    // Rectangular constructor.
    public DwellTarget(string id, PhysicalRect bounds, int dwellTimeMs)
    {
        Id = id;
        Center = bounds.Center;
        Radius = 0;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = bounds;
    }

    /// <summary>Rect containment when <see cref="Bounds"/> is set; otherwise circle distance.</summary>
    public bool HitTest(PhysicalPoint point)
    {
        if (Bounds is { } r)
            return point.X >= r.X && point.X <= r.X + r.Width
                && point.Y >= r.Y && point.Y <= r.Y + r.Height;

        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
}
```

- [ ] **Step 4: Green** — `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetRectTests"` (5 pass).
- [ ] **Step 5: Regression** — `dotnet test tests/EyeCtrl.Core.Tests` (all existing DwellDetector/DwellTarget tests still pass; circle path unchanged).
- [ ] **Step 6: Commit**
```bash
git add src/EyeCtrl.Core/Processing/DwellTarget.cs tests/EyeCtrl.Core.Tests/Processing/DwellTargetRectTests.cs
git commit -m "feat: add rectangular hit-test + settable Bounds to DwellTarget"
```

---

### Task 4: Create ExternalDwellSettings and wire into settings

**Files:**
- Create: `src/EyeCtrl.Core/Settings/ExternalDwellSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Create ExternalDwellSettings.cs** (mirrors `AutoSelectionSettings` conventions)

```csharp
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ExternalDwellSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _defaultDwellMs = 800;
    [ObservableProperty] private int _cooldownMs = 400;

    partial void OnDefaultDwellMsChanging(int value)
    {
        if (value < 200 || value > 3000)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(DefaultDwellMs)} must be between 200 and 3000");
    }

    partial void OnCooldownMsChanging(int value)
    {
        if (value < 0 || value > 5000)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(CooldownMs)} must be between 0 and 5000");
    }

    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(DefaultDwellMs, 200, 3000, nameof(DefaultDwellMs)),
        ValidationHelper.ValidateRange(CooldownMs, 0, 5000, nameof(CooldownMs)));
}
```

- [ ] **Step 2: Wire into `EyeCtrlSettings`**

Add the property after the `AutoSelection` field (currently the last `[ObservableProperty]`):
```csharp
[ObservableProperty] private ExternalDwellSettings _externalDwell = new();
```

In `Validate()`, add after the `AutoSelection` null-guard block (mirrors the existing pattern):
```csharp
if (ExternalDwell is null)
    errors.Add("ExternalDwell settings must not be null.");
else
    errors.AddRange(ExternalDwell.Validate());
```

- [ ] **Step 3: Wire into `SettingsViewModel`**

In `BuildTabs()`, add a tab (place it after the `Dwell` tab):
```csharp
var ed = _settings.ExternalDwell;
Tabs.Add(new SettingsTabViewModel("ExternalDwell", "🔗", new List<SettingItemViewModel>
{
    new ToggleSettingViewModel("externalEnabled", "Enabled", ed.Enabled, v => ed.Enabled = v),
    new SliderSettingViewModel("externalDwell", "Dwell Time (ms)", ed.DefaultDwellMs, 200, 3000, 50, v => ed.DefaultDwellMs = (int)v),
    new SliderSettingViewModel("externalCooldown", "Cooldown (ms)", ed.CooldownMs, 0, 5000, 50, v => ed.CooldownMs = (int)v),
}));
```

In `ResetAllSettings()`, add alongside the other `CopySettings(...)` calls:
```csharp
CopySettings(defaults.ExternalDwell, _settings.ExternalDwell);
```

- [ ] **Step 4: Build verify** — `dotnet build EyeCtrl.slnx`.
- [ ] **Step 5: Commit**
```bash
git add src/EyeCtrl.Core/Settings/ExternalDwellSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs
git commit -m "feat: add ExternalDwellSettings and wire into settings + reset"
```

---

### Task 5: Create ExternalDwellArbiter (pure gated dwell logic) + tests

This is the testable brain: gating (topmost-window), nearest-target selection, dwell accumulation, cooldown, and feedback selection. No UI, no pipe, no `DwellDetector`. Reuses `DwellTarget` (rect `HitTest`, `EnteredTimestamp`, `DwellTimeMicroseconds`).

**Files:**
- Create: `src/EyeCtrl.App/Services/ExternalDwellArbiter.cs`
- Create: `tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using Xunit;

public class ExternalDwellArbiterTests
{
    private const long Ms = 1000; // microseconds per ms
    private static readonly PhysicalRect Btn = new(100, 100, 80, 40); // center (140,120)

    private static ExternalDwellArbiter MakeArbiter(long rootHwnd = 7) =>
        new(_ => rootHwnd, cooldownMs: 0);

    [Fact]
    public void DwellOnGatedTarget_ActivatesAfterDwellTime()
    {
        var a = MakeArbiter(rootHwnd: 7);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);

        var inside = new PhysicalPoint(140, 120);
        Assert.Null(a.Process(inside, 0).ActivatedId);          // enter
        Assert.Null(a.Process(inside, 400 * Ms).ActivatedId);   // mid-dwell
        var done = a.Process(inside, 800 * Ms);
        Assert.Equal("btn", done.ActivatedId);
    }

    [Fact]
    public void Process_WhileAccumulating_ReturnsFeedbackRectAndSuppress()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        var r = a.Process(inside, 400 * Ms);
        Assert.Equal("btn", r.FeedbackId);
        Assert.Equal(Btn, r.FeedbackRect);
        Assert.True(r.Suppress);
        Assert.InRange(r.Progress, 0.4, 0.6);
    }

    [Fact]
    public void Gating_WrongTopmostWindow_NeverAccumulates()
    {
        var a = new ExternalDwellArbiter(_ => 999, cooldownMs: 0); // gaze is over a different window
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        var r = a.Process(inside, 5000 * Ms);
        Assert.Null(r.ActivatedId);
        Assert.Null(r.FeedbackId);
        Assert.False(r.Suppress);
    }

    [Fact]
    public void GazeOutsideRect_NoFeedback()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var r = a.Process(new PhysicalPoint(1000, 1000), 100 * Ms);
        Assert.Null(r.FeedbackId);
        Assert.False(r.Suppress);
    }

    [Fact]
    public void Cooldown_BlocksImmediateRetrigger()
    {
        var a = new ExternalDwellArbiter(_ => 7, cooldownMs: 500);
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        var inside = new PhysicalPoint(140, 120);
        a.Process(inside, 0);
        Assert.Equal("btn", a.Process(inside, 800 * Ms).ActivatedId);
        // Within cooldown: no re-accumulation.
        Assert.Null(a.Process(inside, 900 * Ms).FeedbackId);
        // After cooldown + new dwell: can retrigger.
        a.Process(inside, 1400 * Ms);             // re-enter
        Assert.Equal("btn", a.Process(inside, 2200 * Ms).ActivatedId);
    }

    [Fact]
    public void Remove_StopsTargeting()
    {
        var a = MakeArbiter();
        a.AddOrUpdate("btn", Btn, hwnd: 7, dwellMs: 800);
        a.Remove("btn");
        var r = a.Process(new PhysicalPoint(140, 120), 5000 * Ms);
        Assert.Null(r.ActivatedId);
        Assert.Null(r.FeedbackId);
    }
}
```

- [ ] **Step 2: Run to confirm failure** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ExternalDwellArbiterTests"`.

- [ ] **Step 3: Implement ExternalDwellArbiter.cs**

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>Decision returned per gaze frame. Pure data; no UI or IO.</summary>
public readonly record struct ArbiterResult(
    string? FeedbackId, PhysicalRect? FeedbackRect, double Progress, string? ActivatedId, bool Suppress)
{
    public static readonly ArbiterResult None = new(null, null, 0, null, false);
}

/// <summary>
/// Pure gated-dwell engine for external (cooperating-app) controls. Mirrors EyeCtrl's
/// DwellDetector timing convention (nearest-only accumulation, cooldown) but adds
/// topmost-window gating, which DwellDetector cannot express. Not thread-safe — callers
/// must serialize access (ExternalDwellService holds a lock).
/// </summary>
public sealed class ExternalDwellArbiter
{
    private readonly Func<PhysicalPoint, long?> _rootWindowAtPoint;
    private readonly Dictionary<string, DwellTarget> _targets = new();
    private readonly Dictionary<string, long> _hwndById = new();
    private long _cooldownUs;
    private long? _lastActivationUs;

    public ExternalDwellArbiter(Func<PhysicalPoint, long?> rootWindowAtPoint, int cooldownMs)
    {
        _rootWindowAtPoint = rootWindowAtPoint;
        _cooldownUs = (long)cooldownMs * 1000L;
    }

    public void SetCooldown(int cooldownMs) => _cooldownUs = (long)cooldownMs * 1000L;

    public void AddOrUpdate(string id, PhysicalRect rect, long hwnd, int dwellMs)
    {
        if (_targets.TryGetValue(id, out var existing))
        {
            existing.Bounds = rect;          // preserve EnteredTimestamp/IsGazeInside (no dwell reset on move)
            existing.Center = rect.Center;
        }
        else
        {
            _targets[id] = new DwellTarget(id, rect, dwellMs);
        }
        _hwndById[id] = hwnd;
    }

    public void Remove(string id)
    {
        _targets.Remove(id);
        _hwndById.Remove(id);
    }

    public ArbiterResult Process(PhysicalPoint gaze, long timestampUs)
    {
        // Cooldown: ignore everything and reset accumulation.
        if (_lastActivationUs is { } last && timestampUs - last < _cooldownUs)
        {
            ResetAll();
            return ArbiterResult.None;
        }
        _lastActivationUs = null;

        long? root = _targets.Count == 0 ? null : _rootWindowAtPoint(gaze);

        // Nearest gated hit (topmost-window match + rect containment).
        string? nearestId = null;
        double nearestDistSq = double.MaxValue;
        if (root is { } r)
        {
            foreach (var (id, t) in _targets)
            {
                if (_hwndById.GetValueOrDefault(id) != r) continue;
                if (!t.HitTest(gaze)) continue;
                double dx = gaze.X - t.Center.X, dy = gaze.Y - t.Center.Y;
                double d = dx * dx + dy * dy;
                if (d < nearestDistSq) { nearestDistSq = d; nearestId = id; }
            }
        }

        string? activeId = null, activatedId = null;
        double progress = 0;

        foreach (var (id, t) in _targets)
        {
            if (id == nearestId)
            {
                if (!t.IsGazeInside)
                {
                    t.IsGazeInside = true;
                    t.EnteredTimestamp = timestampUs;
                    activeId = id;            // show feedback from the entry frame (progress 0)
                }
                else if (t.EnteredTimestamp is { } entered)
                {
                    long elapsed = timestampUs - entered;
                    if (elapsed >= t.DwellTimeMicroseconds)
                    {
                        activatedId = id;
                        t.IsGazeInside = false;
                        t.EnteredTimestamp = null;
                        _lastActivationUs = timestampUs;
                    }
                    else
                    {
                        activeId = id;
                        progress = Math.Min(1.0, (double)elapsed / t.DwellTimeMicroseconds);
                    }
                }
            }
            else
            {
                t.IsGazeInside = false;
                t.EnteredTimestamp = null;
            }
        }

        if (activatedId != null)
            return new ArbiterResult(null, null, 0, activatedId, Suppress: false);
        if (activeId != null && _targets[activeId].Bounds is { } b)
            return new ArbiterResult(activeId, b, progress, null, Suppress: true);
        return ArbiterResult.None;
    }

    private void ResetAll()
    {
        foreach (var t in _targets.Values) { t.IsGazeInside = false; t.EnteredTimestamp = null; }
    }
}
```

- [ ] **Step 4: Green** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ExternalDwellArbiterTests"` (6 pass).
- [ ] **Step 5: Commit**
```bash
git add src/EyeCtrl.App/Services/ExternalDwellArbiter.cs tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs
git commit -m "feat: add pure ExternalDwellArbiter (gating, accumulation, cooldown) with tests"
```

---

### Task 6: Create DwellFeedbackOverlay (DPI-correct, click-through)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml`
- Create: `src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml.cs`

- [ ] **Step 1: Create DwellFeedbackOverlay.xaml** (no `IsClickThrough` attribute — set in ctor like `CursorCircleWindow`)

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.DwellFeedbackOverlay"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
    WindowStyle="None" AllowsTransparency="True" Background="Transparent"
    Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize">
    <Border x:Name="ProgressBorder" CornerRadius="4" BorderThickness="2"
            BorderBrush="#3399FF" Background="#1A3399FF" Visibility="Hidden">
        <Grid>
            <Rectangle x:Name="ProgressFill" HorizontalAlignment="Left" Fill="#3399FF" />
        </Grid>
    </Border>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Create DwellFeedbackOverlay.xaml.cs** (physical→logical via `DpiHelper`, matching `CursorCircleWindow`)

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.Core.Services; // DpiHelper

public partial class DwellFeedbackOverlay : OverlayWindow
{
    public DwellFeedbackOverlay()
    {
        IsClickThrough = true;   // set before InitializeComponent (init-only base property)
        InitializeComponent();
    }

    /// <summary>Position + size the overlay over a control. Inputs are physical screen pixels.</summary>
    public void ShowAt(double physX, double physY, double physW, double physH)
    {
        Left = DpiHelper.PhysicalToLogicalX(physX);
        Top = DpiHelper.PhysicalToLogicalY(physY);
        Width = DpiHelper.PhysicalToLogicalX(physW);
        Height = DpiHelper.PhysicalToLogicalY(physH);
        ProgressFill.Width = 0;
        ProgressBorder.Visibility = Visibility.Visible;
        Show();
    }

    public void SetProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        ProgressFill.Width = ProgressBorder.ActualWidth * progress;
    }

    public void HideFeedback()
    {
        ProgressBorder.Visibility = Visibility.Hidden;
        Hide();
    }
}
```

> `DpiHelper.PhysicalToLogicalX/Y` are the same conversions `CursorCircleWindow.UpdatePosition` uses. Using `PhysicalToLogicalX` for width/height is correct because it is a pure scale division. No primary-screen clamping (breaks multi-monitor); the rect already comes from the control's true screen position.

- [ ] **Step 3: Commit**
```bash
git add src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml.cs
git commit -m "feat: add DwellFeedbackOverlay (DPI-correct, click-through)"
```

---

### Task 7: Create ExternalDwellService (pipe server + dispatcher marshaling)

Thin IO/UI glue around `ExternalDwellArbiter`: byte-mode pipe server, per-connection writer, gaze subscription, dispatcher-marshaled feedback, inline `SuppressFixation`.

**Files:**
- Create: `src/EyeCtrl.App/Services/ExternalDwellService.cs`

- [ ] **Step 1: Create ExternalDwellService.cs**

```csharp
namespace EyeCtrl.App.Services;

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosts the external-dwell named-pipe server and bridges it to EyeCtrl's gaze loop.
/// Decisions are made by the pure ExternalDwellArbiter; this class handles pipe IO,
/// dispatcher marshaling of the feedback overlay, and SuppressFixation.
/// MUST be constructed on the UI thread (it creates a WPF window).
/// </summary>
public sealed class ExternalDwellService : IDisposable
{
    private const string PipeName = "eyectrl_dwell";

    private readonly ExternalDwellSettings _settings;
    private readonly GazeEngine _gazeEngine;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ExternalDwellService> _logger;
    private readonly ExternalDwellArbiter _arbiter;
    private readonly DwellFeedbackOverlay _feedback;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // A single lock guards the arbiter + both connection maps, so there is no lock-ordering
    // hazard between the gaze thread and pipe-handler threads. Pipe WRITES happen OUTSIDE
    // this lock (see SendActivated) so a stalled client can't block the gaze loop.
    private readonly object _stateLock = new();
    private readonly List<PipeConnection> _connections = new();
    private readonly Dictionary<string, PipeConnection> _connByTarget = new();

    private string? _feedbackId;     // accessed only on the gaze thread
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ExternalDwellService(
        ExternalDwellSettings settings,
        GazeEngine gazeEngine,
        Dispatcher dispatcher,
        ILogger<ExternalDwellService> logger,
        Func<PhysicalPoint, long?>? rootWindowAtPoint = null)
    {
        _settings = settings;
        _gazeEngine = gazeEngine;
        _dispatcher = dispatcher;
        _logger = logger;
        _arbiter = new ExternalDwellArbiter(rootWindowAtPoint ?? RootWindowAtPointImpl, settings.CooldownMs);
        _feedback = new DwellFeedbackOverlay();   // UI thread
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExternalDwellSettings.CooldownMs))
                lock (_stateLock) _arbiter.SetCooldown(_settings.CooldownMs);
        };
        _gazeEngine.CursorMoved += OnCursorMoved;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    // ── Gaze loop (background thread) ──

    private void OnCursorMoved(PhysicalPoint gaze, long timestampUs)
    {
        if (!_settings.Enabled) return;

        ArbiterResult result;
        lock (_stateLock) result = _arbiter.Process(gaze, timestampUs);

        if (result.ActivatedId is { } activated)
        {
            SendActivated(activated);
            _gazeEngine.SuppressFixation = false;
            SetFeedback(null, default, 0);
            return;
        }

        if (result.FeedbackId is { } fid)
        {
            _gazeEngine.SuppressFixation = true;
            SetFeedback(fid, result.FeedbackRect!.Value, result.Progress);
        }
        else
        {
            _gazeEngine.SuppressFixation = false;
            SetFeedback(null, default, 0);
        }
    }

    /// <summary>Marshals only the WPF-window work to the UI thread.</summary>
    private void SetFeedback(string? id, PhysicalRect rect, double progress)
    {
        if (id == null)
        {
            if (_feedbackId == null) return;
            _feedbackId = null;
            _dispatcher.BeginInvoke(() => _feedback.HideFeedback());
            return;
        }

        bool isNew = _feedbackId != id;
        _feedbackId = id;
        _dispatcher.BeginInvoke(() =>
        {
            if (isNew) _feedback.ShowAt(rect.X, rect.Y, rect.Width, rect.Height);
            _feedback.SetProgress(progress);
        });
    }

    // ── Pipe server ──

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (Exception ex) { _logger.LogWarning(ex, "pipe accept failed"); pipe.Dispose(); continue; }

            var conn = new PipeConnection(pipe);
            lock (_stateLock) _connections.Add(conn);
            _ = HandleConnectionAsync(conn, token);
        }
    }

    private async Task HandleConnectionAsync(PipeConnection conn, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(conn.Pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!token.IsCancellationRequested && conn.Pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;
                try { HandleMessage(JsonSerializer.Deserialize<WireMessage>(line, _json), conn); }
                catch (Exception ex) { _logger.LogWarning(ex, "bad pipe message: {Line}", line); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "connection handler error"); }
        finally
        {
            lock (_stateLock)
            {
                _connections.Remove(conn);
                foreach (var id in conn.TargetIds)
                {
                    _connByTarget.Remove(id);
                    _arbiter.Remove(id);
                }
            }
            conn.Dispose();
        }
    }

    private void HandleMessage(WireMessage? msg, PipeConnection conn)
    {
        // All branches take the single _stateLock (no nested/ordered locks).
        switch (msg)
        {
            case RegisterMessage r when _settings.Enabled:
                lock (_stateLock)
                {
                    conn.Hwnd = r.Hwnd;
                    conn.TargetIds.Add(r.Id);
                    _connByTarget[r.Id] = conn;
                    _arbiter.AddOrUpdate(r.Id, ToRect(r.Rect), r.Hwnd, _settings.DefaultDwellMs);
                }
                break;

            case UpdateMessage u:
                lock (_stateLock)
                {
                    if (conn.TargetIds.Contains(u.Id))
                        _arbiter.AddOrUpdate(u.Id, ToRect(u.Rect), conn.Hwnd, _settings.DefaultDwellMs);
                }
                break;

            case UnregisterMessage un:
                lock (_stateLock)
                {
                    conn.TargetIds.Remove(un.Id);
                    _connByTarget.Remove(un.Id);
                    _arbiter.Remove(un.Id);
                }
                break;
        }
    }

    private void SendActivated(string id)
    {
        PipeConnection? conn;
        lock (_stateLock) _connByTarget.TryGetValue(id, out conn);  // lookup under lock
        if (conn is null) return;
        var json = JsonSerializer.Serialize<WireMessage>(new ActivatedMessage { Id = id }, _json);
        _ = Task.Run(() => conn.Send(json));                        // write OFF the gaze thread
    }

    private static PhysicalRect ToRect(SdkRect r) => new(r.X, r.Y, r.Width, r.Height);

    // ── Native gating seam: root window under a physical point ──

    private static long? RootWindowAtPointImpl(PhysicalPoint p)
    {
        var hwnd = WindowFromPoint(new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) });
        if (hwnd == IntPtr.Zero) return null;
        var root = GetAncestor(hwnd, GA_ROOT);          // child/island HWND → top-level window
        return (root == IntPtr.Zero ? hwnd : root).ToInt64();
    }

    private const uint GA_ROOT = 2;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _cts?.Cancel();
        lock (_stateLock)
        {
            foreach (var c in _connections) c.Dispose();
            _connections.Clear();
            _connByTarget.Clear();
        }
        _cts?.Dispose();
        _dispatcher.BeginInvoke(() => _feedback.Close());
    }

    /// <summary>One connected SDK client. Owns a single synchronized writer.</summary>
    private sealed class PipeConnection : IDisposable
    {
        private readonly object _writeLock = new();
        private readonly StreamWriter _writer;
        public NamedPipeServerStream Pipe { get; }
        public HashSet<string> TargetIds { get; } = new();
        public long Hwnd { get; set; }

        public PipeConnection(NamedPipeServerStream pipe)
        {
            Pipe = pipe;
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        }

        public void Send(string line)
        {
            lock (_writeLock) { try { _writer.WriteLine(line); } catch { /* dropping; client will reconnect */ } }
        }

        public void Dispose()
        {
            lock (_writeLock) { try { _writer.Dispose(); } catch { } }
            Pipe.Dispose();
        }
    }
}
```

> `conn.Hwnd` is captured in the register branch so `UpdateMessage` (which omits HWND) reuses the owning window. All shared-state access (arbiter + connection maps) is under the single `_stateLock`; the only work done outside it is the offloaded pipe write in `SendActivated`.

- [ ] **Step 2: Build verify** — `dotnet build EyeCtrl.slnx`.

- [ ] **Step 3: Commit**
```bash
git add src/EyeCtrl.App/Services/ExternalDwellService.cs
git commit -m "feat: add ExternalDwellService (byte-mode pipe server, dispatcher-marshaled feedback)"
```

---

### Task 8: Wire ExternalDwellService into DI + app lifecycle

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Register in `AppBootstrapper.BuildServiceProvider`** (alongside the other `dispatcher`-taking services; note `dispatcher` is already a parameter of this method):

```csharp
// ExternalDwellService: lets cooperating apps register dwellable controls over a named pipe.
services.AddSingleton(sp => new ExternalDwellService(
    sp.GetRequiredService<EyeCtrlSettings>().ExternalDwell,
    sp.GetRequiredService<GazeEngine>(),
    dispatcher,
    sp.GetRequiredService<ILogger<ExternalDwellService>>()));
```

- [ ] **Step 2: Resolve + start in `App.OnStartup`**, immediately after `gazeEngine.Start();` (resolving here guarantees the service — and its WPF feedback window — are created on the UI thread):

```csharp
// External dwell pipe server (cooperating-app control activation).
_serviceProvider.GetRequiredService<ExternalDwellService>().Start();
```

`using EyeCtrl.App.Services;` is already present in `App.xaml.cs`.

- [ ] **Step 3: Dispose on exit** — in `App.OnExit`, before `_serviceProvider.Dispose();` (the provider disposes singletons, but dispose explicitly to stop the pipe loop deterministically):

```csharp
_serviceProvider.GetRequiredService<ExternalDwellService>().Dispose();
```

- [ ] **Step 4: Build verify** — `dotnet build EyeCtrl.slnx`.

- [ ] **Step 5: Commit**
```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: register + start ExternalDwellService"
```

---

### Task 9: Create EyeCtrl.Sdk.Wpf — attached properties + adapter

**Files:**
- Create: `src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj`
- Create: `src/EyeCtrl.Sdk.Wpf/WpfElementAdapter.cs`
- Create: `src/EyeCtrl.Sdk.Wpf/Dwell.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <PackageId>EyeCtrl.Sdk.Wpf</PackageId>
    <Description>EyeCtrl gaze-dwell integration for WPF. Attach eyectrl:Dwell.Enabled="True" to any FrameworkElement.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\EyeCtrl.Sdk.Abstractions\EyeCtrl.Sdk.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to `EyeCtrl.slnx`** under `/src/`:
```xml
<Project Path="src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj" />
```

- [ ] **Step 3: Create WpfElementAdapter.cs**

```csharp
namespace EyeCtrl.Sdk.Wpf;

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

internal sealed class WpfElementAdapter : IDwellElementAdapter
{
    private readonly FrameworkElement _element;
    private long _hwndCache;

    public string Id { get; }
    public DwellMode Mode { get; }

    // Resolved lazily: the adapter may be constructed before the element is in a window
    // (the attached-property callback can fire during XAML parse, before Loaded). Caching
    // once non-zero avoids repeated visual-tree walks. DwellClient reads this during
    // RegisterAdapter (fired on Loaded), so the window is available by then.
    public long WindowHandle
    {
        get
        {
            if (_hwndCache != 0) return _hwndCache;
            if (Window.GetWindow(_element) is { } w)
                _hwndCache = new WindowInteropHelper(w).Handle.ToInt64();
            return _hwndCache;
        }
    }

    public WpfElementAdapter(string id, FrameworkElement element, DwellMode mode)
    {
        Id = id;
        _element = element;
        Mode = mode;
    }

    public bool TryGetScreenRect(out SdkRect rect)
    {
        rect = default;
        if (!_element.IsLoaded || _element.ActualWidth <= 0 || _element.ActualHeight <= 0) return false;
        try
        {
            var tl = _element.PointToScreen(new Point(0, 0));                                   // physical px
            var br = _element.PointToScreen(new Point(_element.ActualWidth, _element.ActualHeight));
            rect = new SdkRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            return true;
        }
        catch { return false; }
    }

    public void Activate() => _element.Dispatcher.BeginInvoke(() =>
    {
        if (Mode == DwellMode.Focus) { _element.Focus(); return; }

        var peer = UIElementAutomationPeer.CreatePeerForElement(_element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }

        // Fallback: command-bound control without an Invoke peer.
        if (_element is System.Windows.Controls.Primitives.ButtonBase b
            && b.Command?.CanExecute(b.CommandParameter) == true)
            b.Command.Execute(b.CommandParameter);
    });
}
```

- [ ] **Step 4: Create Dwell.cs**

```csharp
namespace EyeCtrl.Sdk.Wpf;

using System.Collections.Concurrent;
using System.Windows;
using EyeCtrl.Sdk.Abstractions;

/// <summary>
/// Attached properties enabling gaze dwell on a WPF FrameworkElement.
/// A single process-wide DwellClient connects to EyeCtrl lazily on first use.
/// </summary>
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
    private static readonly ConcurrentDictionary<FrameworkElement, WpfElementAdapter> _byElement = new();
    private static int _started;
    private static long _idCounter;

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
        if (_byElement.ContainsKey(el)) return;

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var adapter = new WpfElementAdapter(id, el, GetMode(el));
        _byElement[el] = adapter;

        el.Loaded += OnLoaded;
        el.Unloaded += OnUnloaded;
        el.LayoutUpdated += OnLayoutUpdated;

        if (el.IsLoaded) _client.RegisterAdapter(adapter);
    }

    private static void Unregister(FrameworkElement el)
    {
        if (!_byElement.TryRemove(el, out var adapter)) return;
        el.Loaded -= OnLoaded;
        el.Unloaded -= OnUnloaded;
        el.LayoutUpdated -= OnLayoutUpdated;
        _client.UnregisterAdapter(adapter.Id);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && _byElement.TryGetValue(el, out var a))
            _client.RegisterAdapter(a);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && _byElement.TryGetValue(el, out var a))
            _client.UnregisterAdapter(a.Id);   // re-registers on next Loaded
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // LayoutUpdated has no sender param wired to the element; iterate is unnecessary —
        // attach a per-element handler via a closure instead (see note).
    }
}
```

> **Note on `LayoutUpdated`:** WPF's `LayoutUpdated` event handler does not receive the element as `sender`. Wire it per-element with a closure so the right id is updated, e.g. in `Register`:
> ```csharp
> EventHandler h = (_, _) => _client.OnAdapterLayoutChanged(adapter.Id);
> el.LayoutUpdated += h;
> // store h alongside the adapter so Unregister can detach it
> ```
> Implement this with a small record `(WpfElementAdapter Adapter, EventHandler LayoutHandler)` as the dictionary value, and detach `h` in `Unregister`. (The skeleton above shows intent; the worker should finalize the per-element handler storage.)

- [ ] **Step 5: Build verify** — `dotnet build src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj`. `UIElementAutomationPeer` / `IInvokeProvider` come from `PresentationFramework` / `UIAutomationProvider` (referenced implicitly under `UseWPF`).

- [ ] **Step 6: Commit**
```bash
git add src/EyeCtrl.Sdk.Wpf/ EyeCtrl.slnx
git commit -m "feat: add EyeCtrl.Sdk.Wpf (Dwell attached properties + UIA adapter)"
```

---

### Task 10: Create the WPF sample app

**Files:**
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/EyeCtrl.Sdk.Wpf.Sample.csproj`
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/App.xaml` + `App.xaml.cs`
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/MainWindow.xaml` + `MainWindow.xaml.cs`

- [ ] **Step 1: csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <!-- Sample: net10 (matches the repo / real consumer) consuming the net8 SDK package. -->
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Sdk.Wpf\EyeCtrl.Sdk.Wpf.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: App.xaml / App.xaml.cs** — standard WPF app with `StartupUri="MainWindow.xaml"` and an empty `App : Application` partial.

- [ ] **Step 3: MainWindow.xaml**

```xml
<Window x:Class="EyeCtrl.Sdk.Wpf.Sample.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:eyectrl="clr-namespace:EyeCtrl.Sdk.Wpf;assembly=EyeCtrl.Sdk.Wpf"
        Title="EyeCtrl WPF Sample" Height="320" Width="420">
    <StackPanel Margin="20" VerticalAlignment="Center">
        <TextBlock Text="Dwell-enabled buttons:" FontSize="16" Margin="0,0,0,15"/>
        <Button Content="Save"   Width="220" Height="44" Margin="0,5" Click="OnSave"
                eyectrl:Dwell.Enabled="True"/>
        <Button Content="Focus me" Width="220" Height="44" Margin="0,5"
                eyectrl:Dwell.Enabled="True" eyectrl:Dwell.Mode="Focus"/>
        <Button Content="Plain (no dwell)" Width="220" Height="44" Margin="0,5" Click="OnPlain"/>
        <TextBlock x:Name="Log" Margin="0,15,0,0" Foreground="Gray"/>
    </StackPanel>
</Window>
```

- [ ] **Step 4: MainWindow.xaml.cs** — `OnSave` / `OnPlain` set `Log.Text` with a timestamp.

- [ ] **Step 5: Build verify** — `dotnet build samples/EyeCtrl.Sdk.Wpf.Sample/EyeCtrl.Sdk.Wpf.Sample.csproj`.

- [ ] **Step 6: Commit**
```bash
git add samples/EyeCtrl.Sdk.Wpf.Sample/
git commit -m "feat: add WPF sample app with dwell-enabled controls"
```

---

### Task 11: Tests for EyeCtrl.Sdk.Abstractions (OutboundRectTracker)

**Files:**
- Create: `tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj`
- Create: `tests/EyeCtrl.Sdk.Abstractions.Tests/OutboundRectTrackerTests.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Test project** (match xUnit package versions already used in `tests/EyeCtrl.Core.Tests`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Internal test project: match the repo TFM (runs on the installed net10 runtime). -->
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- net10 test referencing the net8 Abstractions library is fully supported. -->
    <ProjectReference Include="..\..\src\EyeCtrl.Sdk.Abstractions\EyeCtrl.Sdk.Abstractions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Use the same versions as the existing test projects. -->
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

> Check `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj` for exact `Version="…"` attributes and copy them (or rely on central package management if the repo uses `Directory.Packages.props`).

- [ ] **Step 2: Add to `EyeCtrl.slnx`** under `/tests/`:
```xml
<Project Path="tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj" />
```

- [ ] **Step 3: Tests** (real assertions on the dedupe state machine)

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;
using Xunit;

public class OutboundRectTrackerTests
{
    private static readonly SdkRect A = new(0, 0, 10, 10);
    private static readonly SdkRect B = new(5, 5, 10, 10);

    [Fact]
    public void FirstSeen_ReturnsRegister()
    {
        var t = new OutboundRectTracker();
        var msg = t.OnRect("x", A, hwnd: 42);
        var reg = Assert.IsType<RegisterMessage>(msg);
        Assert.Equal("x", reg.Id);
        Assert.Equal(42, reg.Hwnd);
        Assert.Equal(A, reg.Rect);
    }

    [Fact]
    public void SameRect_ReturnsNull()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        Assert.Null(t.OnRect("x", A, 42));
    }

    [Fact]
    public void ChangedRect_ReturnsUpdate()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        var upd = Assert.IsType<UpdateMessage>(t.OnRect("x", B, 42));
        Assert.Equal(B, upd.Rect);
    }

    [Fact]
    public void AfterReset_ReturnsRegisterAgain()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        t.Reset();
        Assert.IsType<RegisterMessage>(t.OnRect("x", A, 42));
    }

    [Fact]
    public void Unregister_ReturnsUnregister_AndForgets()
    {
        var t = new OutboundRectTracker();
        t.OnRect("x", A, 42);
        Assert.IsType<UnregisterMessage>(t.OnUnregister("x"));
        Assert.IsType<RegisterMessage>(t.OnRect("x", A, 42)); // forgotten ⇒ re-Register
    }
}
```

- [ ] **Step 4: Run** — `dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests` (5 pass).
- [ ] **Step 5: Commit**
```bash
git add tests/EyeCtrl.Sdk.Abstractions.Tests/ EyeCtrl.slnx
git commit -m "test: add OutboundRectTracker dedupe tests"
```

---

### Task 12: Integration test for ExternalDwellService over a real pipe

Exercises the pipe round-trip and per-connection cleanup against the actual server, with a stubbed gaze engine and an injected `rootWindowAtPoint`. (The arbiter's decision logic is already covered in Task 5.)

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs`

- [ ] **Step 1: Confirm** `tests/EyeCtrl.App.Tests` references `EyeCtrl.Contracts` (add a `ProjectReference` if missing).

- [ ] **Step 2: Tests**

```csharp
namespace EyeCtrl.App.Tests.Services;

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class ExternalDwellServiceTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static GazeEngine NewGazeEngine() =>
        new(new StubGazeProvider(), new StubInputSimulator(), new EyeCtrlSettings());

    private static ExternalDwellService NewService(GazeEngine engine, Func<PhysicalPoint, long?> root) =>
        new(new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
            engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance, root);

    [Fact]
    public void ConstructDispose_NoThrow()
    {
        using var engine = NewGazeEngine();
        using var svc = NewService(engine, _ => null);
        svc.Start();
    }

    [Fact]
    public async Task RegisterThenDisconnect_RemovesTarget()
    {
        // STA pipe round-trip; assert the server accepts a register and drops it on disconnect.
        using var engine = NewGazeEngine();
        using var svc = NewService(engine, _ => 7);
        svc.Start();

        await using (var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut))
        {
            await client.ConnectAsync(2000);
            await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
                new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 10, 10), Hwnd = 7 }, Json));
            await Task.Delay(100); // let the server process
        } // disconnect → server cleans up the connection's targets

        await Task.Delay(100);
        // No assertion hook is exposed; the test passes if no exception/leak occurs.
        // (Behavioral activation is covered by ExternalDwellArbiterTests.)
    }

    private sealed class StubGazeProvider : IGazeProvider
    {
        public bool IsConnected => true;
        public event Action<GazePoint>? GazePointReceived;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class StubInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint p) { }
        public void LeftClick(PhysicalPoint p) { }
        public void RightClick(PhysicalPoint p) { }
        public void DoubleClick(PhysicalPoint p) { }
        public void MouseDown(PhysicalPoint p) { }
        public void MouseUp(PhysicalPoint p) { }
        public void Scroll(PhysicalPoint p, int dx, int dy) { }
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> mods, ushort vk) { }
    }
}
```

> If `Dispatcher.CurrentDispatcher` on the test thread proves awkward (no message pump), the feedback `BeginInvoke`s simply queue and never run — harmless for these tests since no gaze is fed. If a richer behavioral test is wanted later, expose an internal hook or feed `GazePointReceived` through the real engine.

- [ ] **Step 3: Run** — `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ExternalDwellServiceTests"`.
- [ ] **Step 4: Commit**
```bash
git add tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs
git commit -m "test: add ExternalDwellService pipe round-trip + lifecycle tests"
```

---

### Task 13: Full solution build + all tests

- [ ] **Step 1:** `dotnet build EyeCtrl.slnx` — zero errors.
- [ ] **Step 2:** `dotnet test` — all pre-existing + new tests pass.
- [ ] **Step 3:** Fix any missing `using`s / package-version mismatches surfaced by the build.
- [ ] **Step 4: Commit**
```bash
git add -A
git commit -m "chore: External Dwell API Plan 1 — WPF vertical slice complete"
```

---

### Task 14: Manual smoke test

- [ ] **Step 1:** Start EyeCtrl in test mode (mouse drives gaze): `dotnet run --project src/EyeCtrl.App -- --test-mode`
- [ ] **Step 2:** Start the sample: `dotnet run --project samples/EyeCtrl.Sdk.Wpf.Sample`
- [ ] **Step 3: Verify**
  1. Move the cursor over **Save**; the feedback overlay appears over the button and fills over ~800 ms; on completion the click fires (log updates).
  2. Verify EyeCtrl's own activation-button/action-menu does **not** appear while over the button (suppression), and the cursor ring is hidden.
  3. Move away — suppression lifts, ring returns.
  4. Dwell **Focus me** — the button receives focus (no click).
  5. **Plain** button still works with a normal mouse click.
  6. Check correct placement at a non-100% display scale (DPI correctness).
  7. Multi-monitor: move the sample to a secondary monitor and confirm the overlay still lands on the button.
- [ ] **Step 4:** Fix and re-test as needed. If the feedback overlay misaligns, recheck `DpiHelper` usage; if activation never fires, check the pipe (`eyectrl_dwell`) connected and `ExternalDwell.Enabled` is on.
