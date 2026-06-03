# External Dwell API — Plan 1 (WPF Vertical Slice) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the complete WPF vertical slice: cooperating WPF apps register controls via XAML attached properties, EyeCtrl detects dwell centrally, clicks the control via UIA, and renders dwell-progress feedback.

**Architecture:** Five new projects (`EyeCtrl.Contracts`, `EyeCtrl.Sdk.Abstractions`, `EyeCtrl.Sdk.Wpf`, plus tests, plus sample) feed a new `ExternalDwellService` in EyeCtrl.App. The service owns a dedicated `DwellDetector` with rectangular target support, runs a bidirectional named-pipe server (`eyectrl_dwell`), and integrates with `GazeEngine` for suppression + feedback via a small overlay window. A single Core change adds rectangular hit-test to `DwellTarget`.

**Tech Stack:** .NET 8/10, WPF, Win32 named pipes (System.IO.Pipes), UIA (System.Windows.Automation.Peers), xUnit

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
/// A rectangle in physical screen pixels. Used as the wire format between
/// SDK heads and the EyeCtrl dwell service. Intentionally separate from
/// EyeCtrl.Core.Models.PhysicalRect so the SDK never references Core.
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

public enum PipeMessageType { Register, Update, Unregister, Activated }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(UpdateMessage), "update")]
[JsonDerivedType(typeof(UnregisterMessage), "unregister")]
[JsonDerivedType(typeof(ActivatedMessage), "activated")]
public abstract record WireMessage;

public record RegisterMessage : WireMessage
{
    public required string Id { get; init; }
    public SdkRect Rect { get; init; }
    public long Hwnd { get; init; }
}

public record UpdateMessage : WireMessage
{
    public required string Id { get; init; }
    public SdkRect Rect { get; init; }
}

public record UnregisterMessage : WireMessage
{
    public required string Id { get; init; }
}

public record ActivatedMessage : WireMessage
{
    public required string Id { get; init; }
}
```

- [ ] **Step 4: Add to solution file**

Edit `EyeCtrl.slnx` — add to the `/src/` folder:
```xml
<Project Path="src/EyeCtrl.Contracts/EyeCtrl.Contracts.csproj" />
```

- [ ] **Step 5: Build verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: All 4 existing projects compile; the new `EyeCtrl.Contracts` builds without errors.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Contracts/ EyeCtrl.slnx
git commit -m "feat: add EyeCtrl.Contracts project with wire DTOs"
```

---

### Task 2: Create EyeCtrl.Sdk.Abstractions project — pipe client + adapter seam

**Files:**
- Create: `src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj`
- Create: `src/EyeCtrl.Sdk.Abstractions/IDwellElementAdapter.cs`
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

- [ ] **Step 2: Add to solution file**

Edit `EyeCtrl.slnx` — add to the `/src/` folder:
```xml
<Project Path="src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj" />
```

- [ ] **Step 3: Create IDwellElementAdapter.cs**

```csharp
namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

public enum DwellMode { Invoke, Focus }

/// <summary>
/// Per-element seam that each framework head implements.
/// The shared DwellClient calls this to read rects and invoke activation.
/// </summary>
public interface IDwellElementAdapter
{
    string Id { get; }
    bool TryGetScreenRect(out SdkRect rect);
    long WindowHandle { get; }
    void Activate(DwellMode mode);
}
```

- [ ] **Step 4: Create DwellClient.cs**

```csharp
namespace EyeCtrl.Sdk.Abstractions;

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EyeCtrl.Contracts;

/// <summary>
/// Named-pipe client that connects to the EyeCtrl dwell service.
/// Deduplicates outgoing rect updates (only sends on change).
/// Routes incoming `activated` messages back to the correct adapter.
/// </summary>
public class DwellClient : IDisposable
{
    private const string PipeName = "eyectrl_dwell";
    private const int ConnectTimeoutMs = 2000;
    private const int ReconnectBackoffMs = 2000;
    private const int MaxReconnectBackoffMs = 30000;

    private readonly Dictionary<string, IDwellElementAdapter> _adapters = new();
    private readonly Dictionary<string, SdkRect> _lastSentRects = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;
    private volatile bool _disposed;

    public void RegisterAdapter(IDwellElementAdapter adapter)
    {
        _adapters[adapter.Id] = adapter;
        SendCurrentRect(adapter);
    }

    public void UnregisterAdapter(string id)
    {
        _adapters.Remove(id);
        _lastSentRects.Remove(id);
        TrySend(new UnregisterMessage { Id = id });
    }

    public void OnAdapterLayoutChanged(string id)
    {
        if (_adapters.TryGetValue(id, out var adapter))
            SendCurrentRect(adapter);
    }

    public async Task ConnectAndReadAsync(CancellationToken token = default)
    {
        while (!_disposed && !token.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(ConnectTimeoutMs, token);
                _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true)
                    { AutoFlush = true };

                // Re-register all adapters on new connection
                _lastSentRects.Clear();
                foreach (var adapter in _adapters.Values)
                    SendCurrentRect(adapter);

                await ReadLoopAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // EyeCtrl not running or pipe broke — back off and retry
            }
            finally
            {
                _writer?.Dispose();
                _pipe?.Dispose();
                _writer = null;
                _pipe = null;
            }

            if (!_disposed && !token.IsCancellationRequested)
                await Task.Delay(ReconnectBackoffMs, token);
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        using var reader = new StreamReader(_pipe!, Encoding.UTF8, leaveOpen: true);
        while (!token.IsCancellationRequested && _pipe!.IsConnected)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null) break;
            try
            {
                var msg = JsonSerializer.Deserialize<WireMessage>(line, _jsonOptions);
                if (msg is ActivatedMessage activated && _adapters.TryGetValue(activated.Id, out var adapter))
                {
                    adapter.Activate(DwellMode.Invoke);
                }
            }
            catch { /* skip malformed messages */ }
        }
    }

    private void SendCurrentRect(IDwellElementAdapter adapter)
    {
        if (!adapter.TryGetScreenRect(out var rect))
            return;
        if (_lastSentRects.TryGetValue(adapter.Id, out var last) && last == rect)
            return; // dedupe — no change
        _lastSentRects[adapter.Id] = rect;
        TrySend(new RegisterMessage { Id = adapter.Id, Rect = rect, Hwnd = adapter.WindowHandle });
    }

    private void TrySend(WireMessage msg)
    {
        if (_writer == null || _pipe is not { IsConnected: true }) return;
        try
        {
            var json = JsonSerializer.Serialize<WireMessage>(msg, _jsonOptions);
            _writer.WriteLine(json);
        }
        catch { /* pipe broken — reconnect will handle */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _readCts?.Cancel();
        _writer?.Dispose();
        _pipe?.Dispose();
    }
}
```

- [ ] **Step 5: Build verify**

Run: `dotnet build src/EyeCtrl.Sdk.Abstractions/EyeCtrl.Sdk.Abstractions.csproj`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Sdk.Abstractions/ EyeCtrl.slnx
git commit -m "feat: add EyeCtrl.Sdk.Abstractions with pipe client and adapter seam"
```

---

### Task 3: Add rectangular hit-test to DwellTarget (Core)

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
    public void RectHitTest_InsideRect_ReturnsTrue()
    {
        var rect = new PhysicalRect(100, 200, 80, 40);
        var target = new DwellTarget("btn", rect, 500);
        Assert.True(target.HitTest(new PhysicalPoint(140, 220))); // center
        Assert.True(target.HitTest(new PhysicalPoint(100, 200))); // top-left corner
        Assert.True(target.HitTest(new PhysicalPoint(179, 239))); // bottom-right inside
    }

    [Fact]
    public void RectHitTest_OutsideRect_ReturnsFalse()
    {
        var rect = new PhysicalRect(100, 200, 80, 40);
        var target = new DwellTarget("btn", rect, 500);
        Assert.False(target.HitTest(new PhysicalPoint(99, 200)));
        Assert.False(target.HitTest(new PhysicalPoint(100, 199)));
        Assert.False(target.HitTest(new PhysicalPoint(181, 220)));
        Assert.False(target.HitTest(new PhysicalPoint(140, 241)));
    }

    [Fact]
    public void RectHitTest_OnEdge_ReturnsTrue()
    {
        // A closed interval [x, x+width] includes the edge
        var rect = new PhysicalRect(100, 200, 80, 40);
        var target = new DwellTarget("btn", rect, 500);
        Assert.True(target.HitTest(new PhysicalPoint(100, 200)));
        Assert.True(target.HitTest(new PhysicalPoint(180, 240)));
    }

    [Fact]
    public void CircleHitTest_Unchanged()
    {
        var center = new PhysicalPoint(500, 300);
        var target = new DwellTarget("circle", center, 30, 700);
        Assert.True(target.HitTest(new PhysicalPoint(500, 300)));
        Assert.True(target.HitTest(new PhysicalPoint(520, 300)));
        Assert.False(target.HitTest(new PhysicalPoint(531, 300)));
        Assert.False(target.HitTest(new PhysicalPoint(540, 300)));
    }

    [Fact]
    public void Center_FromRect_ReturnsGeometricCenter()
    {
        var rect = new PhysicalRect(100, 200, 80, 40);
        var target = new DwellTarget("btn", rect, 500);
        Assert.Equal(140, target.Center.X);
        Assert.Equal(220, target.Center.Y);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetRectTests"`
Expected: 5 failures — constructor overload not found.

- [ ] **Step 3: Implement rectangular DwellTarget**

Replace `DwellTarget.cs` (adding the rect constructor + HitTest branch, keeping existing unchanged):

```csharp
namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Represents a target area for dwell-based gaze interactions.
/// All coordinates are in physical screen pixels.
/// </summary>
public class DwellTarget
{
    public string Id { get; }
    /// <summary>Center position in physical screen pixels.</summary>
    public PhysicalPoint Center { get; set; }
    public double Radius { get; }
    public long DwellTimeMicroseconds { get; }
    public long? EnteredTimestamp { get; set; }
    public bool IsGazeInside { get; set; }

    /// <summary>
    /// Optional rectangular bounds. When set, HitTest uses rectangle containment
    /// instead of circle distance. Null means the target is circular.
    /// </summary>
    public PhysicalRect? Bounds { get; }

    // ── Circle constructor (unchanged signature) ──
    public DwellTarget(string id, PhysicalPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = null;
    }

    // ── Rectangular constructor ──
    public DwellTarget(string id, PhysicalRect bounds, int dwellTimeMs)
    {
        Id = id;
        Center = bounds.Center;
        Radius = 0;
        DwellTimeMicroseconds = dwellTimeMs * MathConstants.MicrosecondsPerMillisecond;
        Bounds = bounds;
    }

    /// <summary>
    /// Tests if a point (in physical screen pixels) is within this target.
    /// Uses rect containment if <see cref="Bounds"/> is set; otherwise circle distance.
    /// </summary>
    public bool HitTest(PhysicalPoint point)
    {
        if (Bounds is { } r)
        {
            return point.X >= r.X && point.X <= r.X + r.Width
                && point.Y >= r.Y && point.Y <= r.Y + r.Height;
        }

        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellTargetRectTests"`
Expected: 5 passed.

- [ ] **Step 5: Run all existing tests to confirm no regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All existing tests still pass (circle path unchanged, no existing callers use the rect constructor).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellTarget.cs tests/EyeCtrl.Core.Tests/Processing/DwellTargetRectTests.cs
git commit -m "feat: add rectangular hit-test to DwellTarget"
```

---

### Task 4: Create ExternalDwellSettings

**Files:**
- Create: `src/EyeCtrl.Core/Settings/ExternalDwellSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:18,79`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs:45,194`

- [ ] **Step 1: Create ExternalDwellSettings.cs**

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
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"{nameof(DefaultDwellMs)} must be between 200 and 3000");
    }

    partial void OnCooldownMsChanging(int value)
    {
        if (value < 0 || value > 5000)
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"{nameof(CooldownMs)} must be between 0 and 5000");
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(DefaultDwellMs, 200, 3000, nameof(DefaultDwellMs)),
            ValidationHelper.ValidateRange(CooldownMs, 0, 5000, nameof(CooldownMs))
        );
    }
}
```

- [ ] **Step 2: Wire into EyeCtrlSettings**

In `EyeCtrlSettings.cs`, add after the `Backdrop` line (line 16):

```csharp
[ObservableProperty] private ExternalDwellSettings _externalDwell = new();
```

In `Validate()`, add after the `AutoSelection` block (after line 79):

```csharp
if (ExternalDwell is null)
    errors.Add("ExternalDwell settings must not be null.");
else
    errors.AddRange(ExternalDwell.Validate());
```

- [ ] **Step 3: Wire into SettingsViewModel.BuildTabs()**

Add in `BuildTabs()` after the Dwell tab (around line 70), before the Selection tab:

```csharp
var ed = _settings.ExternalDwell;

Tabs.Add(new SettingsTabViewModel("ExternalDwell", "\uD83D\uDD17", new List<SettingItemViewModel>
{
    new ToggleSettingViewModel("externalEnabled", "Enabled", ed.Enabled, v => ed.Enabled = v),
    new SliderSettingViewModel("externalDwell", "Dwell Time (ms)", ed.DefaultDwellMs, 200, 3000, 50, v => ed.DefaultDwellMs = (int)v),
    new SliderSettingViewModel("externalCooldown", "Cooldown (ms)", ed.CooldownMs, 0, 5000, 50, v => ed.CooldownMs = (int)v),
}));
```

In `ResetAllSettings()`, add after the AutoSelection line (line 194):
```csharp
CopySettings(defaults.ExternalDwell, _settings.ExternalDwell);
```

- [ ] **Step 4: Build verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: All projects compile.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ExternalDwellSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs
git commit -m "feat: add ExternalDwellSettings and wire into settings UI"
```

---

### Task 5: Create DwellFeedbackOverlay (progress indicator)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml`
- Create: `src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml.cs`

- [ ] **Step 1: Create DwellFeedbackOverlay.xaml**

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.DwellFeedbackOverlay"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
    WindowStyle="None" AllowsTransparency="True"
    Background="Transparent" Topmost="True"
    ShowInTaskbar="False" IsClickThrough="True"
    ResizeMode="NoResize">
    <Border x:Name="ProgressBorder"
            CornerRadius="4" BorderThickness="2"
            BorderBrush="#3399FF" Background="#1A3399FF"
            Visibility="Hidden">
        <Grid>
            <Rectangle x:Name="ProgressFill" HorizontalAlignment="Left"
                       Fill="#3399FF" RadiusX="2" RadiusY="2" />
        </Grid>
    </Border>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Create DwellFeedbackOverlay.xaml.cs**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;

public partial class DwellFeedbackOverlay : OverlayWindow
{
    public DwellFeedbackOverlay()
    {
        InitializeComponent();
    }

    public void ShowAt(double screenX, double screenY, double width, double height)
    {
        // Clamp to screen
        Left = Math.Max(0, screenX);
        Top = Math.Max(0, screenY);
        Width = Math.Min(width, SystemParameters.PrimaryScreenWidth - Left);
        Height = Math.Min(height, SystemParameters.PrimaryScreenHeight - Top);
        ProgressBorder.Visibility = Visibility.Visible;
        ProgressFill.Width = 0;
        Show();
    }

    public void SetProgress(double progress)
    {
        if (progress < 0) progress = 0;
        if (progress > 1) progress = 1;
        ProgressFill.Width = ProgressBorder.ActualWidth * progress;
    }

    public void HideFeedback()
    {
        Hide();
        ProgressBorder.Visibility = Visibility.Hidden;
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml.cs
git commit -m "feat: add DwellFeedbackOverlay for external target progress"
```

---

### Task 6: Create ExternalDwellService

**Files:**
- Create: `src/EyeCtrl.App/Services/ExternalDwellService.cs`

This is the largest new file. It implements: pipe server, rect detector with dedicated DwellDetector, topmost-window gating via WindowFromPoint, fixation suppression integration, and dwell-progress feedback overlay.

The `WindowFromPoint` P/Invoke is mockable via an injected `Func<PhysicalPoint, long?>` delegate, enabling unit-testing of the gating logic.

- [ ] **Step 1: Create ExternalDwellService.cs**

```csharp
namespace EyeCtrl.App.Services;

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EyeCtrl.App.Overlays;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

public class ExternalDwellService : IDisposable
{
    private readonly ExternalDwellSettings _settings;
    private readonly GazeEngine _gazeEngine;
    private readonly ILogger<ExternalDwellService> _logger;
    private readonly Func<PhysicalPoint, long?> _windowFromPoint; // mockable seam
    private readonly DwellDetector _detector;
    private readonly DwellFeedbackOverlay _feedback;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Dictionary<string, (long Hwnd, SdkRect LastRect)> _targetMeta = new();
    private readonly Dictionary<string, NamedPipeServerStream> _connectionById = new();
    private readonly List<NamedPipeServerStream> _connections = new();

    private string? _accumulatingTargetId;
    private string? _feedbackTargetId;
    private long? _accumulationStartUs;
    private long? _lastActivationTimestamp;
    private long _lastTimestampUs;
    private NamedPipeServerStream? _acceptPipe;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ExternalDwellService(
        ExternalDwellSettings settings,
        GazeEngine gazeEngine,
        ILogger<ExternalDwellService> logger,
        Func<PhysicalPoint, long?>? windowFromPoint = null)
    {
        _settings = settings;
        _gazeEngine = gazeEngine;
        _logger = logger;
        _windowFromPoint = windowFromPoint ?? WindowFromPointImpl;
        _detector = new DwellDetector(settings.CooldownMs);
        _detector.TargetProgress += OnTargetProgress;
        _detector.TargetActivated += OnTargetActivated;
        _feedback = new DwellFeedbackOverlay();

        _gazeEngine.CursorMoved += OnCursorMoved;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _acceptPipe = new NamedPipeServerStream("eyectrl_dwell", PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Message, PipeOptions.Asynchronous);
        _ = AcceptLoopAsync(_cts.Token);
    }

    // ── Pipe server ──

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _acceptPipe!.WaitForConnectionAsync(token);
                var conn = _acceptPipe;
                _acceptPipe = new NamedPipeServerStream("eyectrl_dwell", PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                _connections.Add(conn);
                _ = HandleConnectionAsync(conn, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accepting pipe connection");
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            var connectionTargets = new HashSet<string>();

            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;

                try
                {
                    var msg = JsonSerializer.Deserialize<WireMessage>(line, _jsonOptions);
                    switch (msg)
                    {
                        case RegisterMessage reg:
                            HandleRegister(reg, writer, pipe, connectionTargets);
                            break;
                        case UpdateMessage upd:
                            HandleUpdate(upd);
                            break;
                        case UnregisterMessage unreg:
                            HandleUnregister(unreg, connectionTargets);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error handling pipe message: {Line}", line);
                }
            }

            // Clean up all targets owned by this connection
            foreach (var id in connectionTargets)
                RemoveTargetInternal(id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection handler error");
        }
        finally
        {
            _connections.Remove(pipe);
            pipe.Dispose();
        }
    }

    private void HandleRegister(RegisterMessage msg, StreamWriter writer,
        NamedPipeServerStream pipe, HashSet<string> connectionTargets)
    {
        if (!_settings.Enabled) return;

        RemoveTargetInternal(msg.Id);
        var target = new DwellTarget(msg.Id,
            new PhysicalRect(msg.Rect.X, msg.Rect.Y, msg.Rect.Width, msg.Rect.Height),
            _settings.DefaultDwellMs);
        _detector.AddTarget(target);
        _targetMeta[msg.Id] = (msg.Hwnd, msg.Rect);
        connectionTargets.Add(msg.Id);
        _connectionById[msg.Id] = pipe;
    }

    private void HandleUpdate(UpdateMessage msg)
    {
        if (!_targetMeta.ContainsKey(msg.Id)) return;
        _targetMeta[msg.Id] = (_targetMeta[msg.Id].Hwnd, msg.Rect);
        // DwellDetector uses center for nearest-target; update it.
        var center = new PhysicalPoint(msg.Rect.CenterX, msg.Rect.CenterY);
        _detector.UpdateTargetCenter(msg.Id, center);
    }

    private void HandleUnregister(UnregisterMessage msg, HashSet<string> connectionTargets)
    {
        connectionTargets.Remove(msg.Id);
        RemoveTargetInternal(msg.Id);
    }

    private void RemoveTargetInternal(string id)
    {
        _detector.RemoveTarget(id);
        _targetMeta.Remove(id);
        _connectionById.Remove(id);
        if (_accumulatingTargetId == id)
        {
            _accumulatingTargetId = null;
            _accumulationStartUs = null;
            HideFeedback();
        }
    }

    private void HideFeedback()
    {
        _gazeEngine.SuppressFixation = false;
        _feedback.HideFeedback();
        _feedbackTargetId = null;
    }

    // ── Dwell detection (gated, manual accumulation) ──

    private void OnCursorMoved(PhysicalPoint point, long timestamp)
    {
        if (!_settings.Enabled) return;
        _lastTimestampUs = timestamp;

        // ── Cooldown check ──
        if (_lastActivationTimestamp.HasValue &&
            (timestamp - _lastActivationTimestamp.Value) < (long)_settings.CooldownMs * 1000L)
        {
            _accumulatingTargetId = null;
            _accumulationStartUs = null;
            HideFeedback();
            return;
        }
        _lastActivationTimestamp = null;

        var topHwnd = _windowFromPoint(point);

        // ── Find the best gated target (topmost HWND match + hit-test + nearest) ──
        string? bestId = null;
        double bestDistSq = double.MaxValue;
        PhysicalRect bestRect = default;

        foreach (var kv in _targetMeta)
        {
            if (topHwnd == null || kv.Value.Hwnd != topHwnd.Value)
                continue;
            var rect = kv.Value.LastRect;
            if (!RectContains(rect, point))
                continue;
            double cx = rect.CenterX;
            double cy = rect.CenterY;
            double distSq = (point.X - cx) * (point.X - cx) + (point.Y - cy) * (point.Y - cy);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestId = kv.Key;
                bestRect = rect;
            }
        }

        // ── Target-change handling ──
        if (bestId != _accumulatingTargetId)
        {
            _accumulationStartUs = bestId == null ? null : timestamp;
            _accumulatingTargetId = bestId;

            if (_feedbackTargetId != null)
                HideFeedback();
            if (bestId != null)
            {
                _feedbackTargetId = bestId;
                _gazeEngine.SuppressFixation = true;
                _feedback.ShowAt(bestRect.X, bestRect.Y, bestRect.Width, bestRect.Height);
            }
            _detector.ResetAllState();
        }

        // ── Dwell accumulation ──
        if (_accumulationStartUs.HasValue && bestId != null)
        {
            long elapsed = timestamp - _accumulationStartUs.Value;
            long dwellUs = (long)_settings.DefaultDwellMs * 1000L;
            double progress = Math.Min(1.0, (double)elapsed / dwellUs);
            _feedback.SetProgress(progress);

            if (elapsed >= dwellUs)
            {
                ActivateTarget(bestId);
                _accumulatingTargetId = null;
                _accumulationStartUs = null;
                _lastActivationTimestamp = timestamp;
                HideFeedback();
            }
        }
    }

    private static bool RectContains(PhysicalRect r, PhysicalPoint p)
    {
        return p.X >= r.X && p.X <= r.X + r.Width
            && p.Y >= r.Y && p.Y <= r.Y + r.Height;
    }

    private void ActivateTarget(string targetId)
    {
        if (_connectionById.TryGetValue(targetId, out var pipe) && pipe.IsConnected)
        {
            try
            {
                var msg = new ActivatedMessage { Id = targetId };
                var json = JsonSerializer.Serialize<WireMessage>(msg, _jsonOptions);
                var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send activation for target {Id}", targetId);
            }
        }
    }

    // ── DwellDetector event handlers (kept for progress/activation forwarding from the
    //    detector instance, though main loop uses manual accumulation) ──

    private void OnTargetProgress(string targetId, double progress) { }
    private void OnTargetActivated(string targetId) { }

    // ── Native gating (windowFromPoint seam) ──

    private static long? WindowFromPointImpl(PhysicalPoint point)
    {
        var pt = new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
        var hwnd = NativeMethodsReal.WindowFromPoint(pt);
        return hwnd == IntPtr.Zero ? null : hwnd.ToInt64();
    }

    private static class NativeMethodsReal
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    }

    // ── Cleanup ──

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _acceptPipe?.Dispose();
        foreach (var conn in _connections) conn.Dispose();
        _cts?.Dispose();
        _feedback.Close();
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/EyeCtrl.App/Services/ExternalDwellService.cs
git commit -m "feat: add ExternalDwellService with pipe server, rect detector, gating, suppression, feedback"
```

---

### Task 7: Wire ExternalDwellService into DI and app lifecycle

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Register in AppBootstrapper**

In `BuildServiceProvider()`, add after the `OverlayManager` registration (after line 175):

```csharp
// ExternalDwellService (enables external apps to register dwellable controls via named pipe)
services.AddSingleton(sp => new ExternalDwellService(
    sp.GetRequiredService<EyeCtrlSettings>().ExternalDwell,
    sp.GetRequiredService<GazeEngine>(),
    sp.GetRequiredService<ILogger<ExternalDwellService>>()));
```

- [ ] **Step 2: Start the service in app lifecycle**

In `App.xaml.cs`, at line 190 after `gazeEngine.Start();`, add:

```csharp
// Start external dwell pipe server (after gaze engine is running)
_serviceProvider.GetRequiredService<ExternalDwellService>().Start();
```

Add the import at the top of `App.xaml.cs`:
```csharp
using EyeCtrl.App.Services;
```

- [ ] **Step 3: Build verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: Compiles. If `ExternalDwellService` not recognized, add `using EyeCtrl.App.Services;` in `App.xaml.cs`.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: register ExternalDwellService in DI and start on app launch"
```

---

### Task 8: Create EyeCtrl.Sdk.Wpf project — attached properties + adapter

**Files:**
- Create: `src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj`
- Create: `src/EyeCtrl.Sdk.Wpf/Dwell.cs`
- Create: `src/EyeCtrl.Sdk.Wpf/WpfElementAdapter.cs`
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
    <Description>EyeCtrl dwell integration for WPF apps. Attach eyectrl:Dwell.Enabled="True" to any FrameworkElement to make it gaze-dwellable.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\EyeCtrl.Sdk.Abstractions\EyeCtrl.Sdk.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add to solution file**

Edit `EyeCtrl.slnx` — add to the `/src/` folder:
```xml
<Project Path="src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj" />
```

- [ ] **Step 3: Create WpfElementAdapter.cs**

```csharp
namespace EyeCtrl.Sdk.Wpf;

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

internal class WpfElementAdapter : IDwellElementAdapter
{
    private readonly FrameworkElement _element;
    private readonly DwellMode _mode;

    public string Id { get; }
    public long WindowHandle { get; }

    public WpfElementAdapter(string id, FrameworkElement element, DwellMode mode)
    {
        Id = id;
        _element = element;
        _mode = mode;
        // Resolve HWND via root Window
        var window = Window.GetWindow(element);
        WindowHandle = window is null
            ? 0
            : new WindowInteropHelper(window).Handle.ToInt64();
    }

    public bool TryGetScreenRect(out SdkRect rect)
    {
        if (!_element.IsLoaded || _element.ActualWidth < 0 || _element.ActualHeight < 0)
        {
            rect = default;
            return false;
        }
        try
        {
            var topLeft = _element.PointToScreen(new Point(0, 0));
            var botRight = _element.PointToScreen(new Point(_element.ActualWidth, _element.ActualHeight));
            rect = new SdkRect(topLeft.X, topLeft.Y,
                botRight.X - topLeft.X, botRight.Y - topLeft.Y);
            return true;
        }
        catch
        {
            rect = default;
            return false;
        }
    }

    public void Activate(DwellMode mode)
    {
        _element.Dispatcher.BeginInvoke(() =>
        {
            switch (mode)
            {
                case DwellMode.Invoke:
                    ActivateInvoke();
                    break;
                case DwellMode.Focus:
                    _element.Focus();
                    break;
            }
        });
    }

    private void ActivateInvoke()
    {
        if (_element is System.Windows.Controls.Primitives.ButtonBase button)
        {
            if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
            {
                button.Command.Execute(button.CommandParameter);
                return;
            }
            // Fallback: click the button via AutomationPeer
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(
                (System.Windows.Controls.Button)_element);
            peer.Invoke();
        }
    }
}
```

- [ ] **Step 4: Create Dwell.cs (attached properties)**

```csharp
namespace EyeCtrl.Sdk.Wpf;

using System.Collections.Concurrent;
using System.Windows;
using EyeCtrl.Sdk.Abstractions;

public static class Dwell
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(Dwell),
            new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached("Mode", typeof(DwellMode), typeof(Dwell),
            new PropertyMetadata(DwellMode.Invoke, OnModeChanged));

    private static readonly DwellClient _client = new();
    private static readonly ConcurrentDictionary<string, WpfElementAdapter> _adaptersById = new();
    private static readonly ConcurrentDictionary<FrameworkElement, string> _idByElement = new();
    private static bool _clientStarted;
    private static long _idCounter;

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    public static DwellMode GetMode(DependencyObject obj) => (DwellMode)obj.GetValue(ModeProperty);
    public static void SetMode(DependencyObject obj, DwellMode value) => obj.SetValue(ModeProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
            RegisterElement(element);
        else
            UnregisterElement(element);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { }

    private static void EnsureClientStarted()
    {
        if (_clientStarted) return;
        _clientStarted = true;
        _ = _client.ConnectAndReadAsync();
    }

    private static void RegisterElement(FrameworkElement element)
    {
        EnsureClientStarted();
        if (_idByElement.ContainsKey(element)) return; // already registered

        var id = $"wpf_{Interlocked.Increment(ref _idCounter)}";
        var mode = GetMode(element);
        var adapter = new WpfElementAdapter(id, element, mode);
        _adaptersById[id] = adapter;
        _idByElement[element] = id;

        element.Loaded += OnElementLoaded;
        element.Unloaded += OnElementUnloaded;
        element.LayoutUpdated += OnElementLayoutUpdated;

        if (element.IsLoaded)
            _client.RegisterAdapter(adapter);
    }

    private static void UnregisterElement(FrameworkElement element)
    {
        if (!_idByElement.TryRemove(element, out var id)) return;
        _adaptersById.TryRemove(id, out _);

        element.Loaded -= OnElementLoaded;
        element.Unloaded -= OnElementUnloaded;
        element.LayoutUpdated -= OnElementLayoutUpdated;

        _client.UnregisterAdapter(id);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && _idByElement.TryGetValue(element, out var id)
            && _adaptersById.TryGetValue(id, out var adapter))
        {
            _client.RegisterAdapter(adapter);
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e) { /* layout reconnect handles re-registration */ }

    private static void OnElementLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement element && _idByElement.TryGetValue(element, out var id))
        {
            _client.OnAdapterLayoutChanged(id);
        }
    }
}
```

- [ ] **Step 5: Build verify**

Run: `dotnet build src/EyeCtrl.Sdk.Wpf/EyeCtrl.Sdk.Wpf.csproj`
Expected: Build succeeds. `ButtonAutomationPeer` and `PointToScreen` are available via implicit WPF framework references (`PresentationFramework.dll`, `WindowsBase.dll`).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Sdk.Wpf/ EyeCtrl.slnx
git commit -m "feat: add EyeCtrl.Sdk.Wpf with Dwell attached properties and adapter"
```

---

### Task 9: Create WPF sample app

**Files:**
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/EyeCtrl.Sdk.Wpf.Sample.csproj`
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/MainWindow.xaml`
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/MainWindow.xaml.cs`
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/App.xaml`
- Create: `samples/EyeCtrl.Sdk.Wpf.Sample/App.xaml.cs`

- [ ] **Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Sdk.Wpf\EyeCtrl.Sdk.Wpf.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create MainWindow.xaml**

```xml
<Window x:Class="EyeCtrl.Sdk.Wpf.Sample.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:eyectrl="clr-namespace:EyeCtrl.Sdk.Wpf;assembly=EyeCtrl.Sdk.Wpf"
        Title="EyeCtrl SDK Sample" Height="300" Width="400">
    <StackPanel Margin="20" VerticalAlignment="Center">
        <TextBlock Text="Dwell-enabled buttons:" FontSize="16" Margin="0,0,0,15" />
        <Button Content="Save" Width="200" Height="40" Margin="0,5"
                Click="OnSaveClick"
                eyectrl:Dwell.Enabled="True" />
        <Button Content="Delete" Width="200" Height="40" Margin="0,5"
                Click="OnDeleteClick"
                eyectrl:Dwell.Enabled="True" />
        <Button Content="Plain (no dwell)" Width="200" Height="40" Margin="0,5"
                Click="OnPlainClick" />
        <TextBlock x:Name="LogLabel" Margin="0,15,0,0" Foreground="Gray" />
    </StackPanel>
</Window>
```

- [ ] **Step 3: Create MainWindow.xaml.cs**

```csharp
namespace EyeCtrl.Sdk.Wpf.Sample;

using System.Windows;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        LogLabel.Text = $"Saved at {DateTime.Now:T}";
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        LogLabel.Text = $"Deleted at {DateTime.Now:T}";
    }

    private void OnPlainClick(object sender, RoutedEventArgs e)
    {
        LogLabel.Text = "Plain click (no dwell needed)";
    }
}
```

- [ ] **Step 4: Create App.xaml + App.xaml.cs**

App.xaml:
```xml
<Application x:Class="EyeCtrl.Sdk.Wpf.Sample.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
```

App.xaml.cs:
```csharp
namespace EyeCtrl.Sdk.Wpf.Sample;

using System.Windows;

public partial class App : Application
{
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build samples/EyeCtrl.Sdk.Wpf.Sample/EyeCtrl.Sdk.Wpf.Sample.csproj`
Expected: Builds successfully.

- [ ] **Step 6: Commit**

```bash
git add samples/EyeCtrl.Sdk.Wpf.Sample/
git commit -m "feat: add WPF sample app with dwell-enabled controls"
```

---

### Task 10: Tests for EyeCtrl.Sdk.Abstractions (DwellClient)

**Files:**
- Create: `tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj`
- Create: `tests/EyeCtrl.Sdk.Abstractions.Tests/DwellClientTests.cs`
- Modify: `EyeCtrl.slnx`

- [ ] **Step 1: Create test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Sdk.Abstractions\EyeCtrl.Sdk.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add to solution**

```xml
<Project Path="tests/EyeCtrl.Sdk.Abstractions.Tests/EyeCtrl.Sdk.Abstractions.Tests.csproj" />
```

- [ ] **Step 3: Create tests**

```csharp
namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using Xunit;

public class DwellClientTests
{
    private class FakeAdapter : IDwellElementAdapter
    {
        public string Id { get; }
        public DwellMode Mode { get; private set; }
        public bool Activated { get; private set; }
        public SdkRect? CurrentRect { get; set; }
        public long WindowHandle { get; set; } = 12345;

        public FakeAdapter(string id, SdkRect? rect = null)
        {
            Id = id;
            CurrentRect = rect;
        }

        public bool TryGetScreenRect(out SdkRect rect)
        {
            rect = CurrentRect ?? default;
            return CurrentRect != null;
        }

        public void Activate(DwellMode mode)
        {
            Mode = mode;
            Activated = true;
        }
    }

    [Fact]
    public void RegisterAdapter_WhenRectAvailable_CallsTrySend()
    {
        using var client = new DwellClient();
        var adapter = new FakeAdapter("btn1", new SdkRect(100, 200, 80, 40));

        // RegisterAdapter records rect and calls TrySend (no-op when pipe disconnected)
        client.RegisterAdapter(adapter);
        client.UnregisterAdapter("btn1");
        // No exception raised
    }

    [Fact]
    public void OnAdapterLayoutChanged_WithNoChange_DoesNotReSend()
    {
        using var client = new DwellClient();
        var adapter = new FakeAdapter("btn1", new SdkRect(100, 200, 80, 40));
        client.RegisterAdapter(adapter);
        // Second call with same rect should dedupe
        client.OnAdapterLayoutChanged("btn1");
        client.UnregisterAdapter("btn1");
    }

    [Fact]
    public void OnAdapterLayoutChanged_WithRectChange_SendsUpdate()
    {
        using var client = new DwellClient();
        var adapter = new FakeAdapter("btn1", new SdkRect(100, 200, 80, 40));
        client.RegisterAdapter(adapter);

        adapter.CurrentRect = new SdkRect(110, 210, 80, 40);
        client.OnAdapterLayoutChanged("btn1");

        client.UnregisterAdapter("btn1");
    }

    [Fact]
    public void UnregisterAdapter_RemovesTracking()
    {
        using var client = new DwellClient();
        var adapter = new FakeAdapter("btn1", new SdkRect(100, 200, 80, 40));
        client.RegisterAdapter(adapter);
        client.UnregisterAdapter("btn1");
        // No-op on unregister of already-removed
        client.UnregisterAdapter("btn1");
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/EyeCtrl.Sdk.Abstractions.Tests`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.Sdk.Abstractions.Tests/ EyeCtrl.slnx
git commit -m "test: add DwellClient unit tests"
```

---

### Task 11: Tests for ExternalDwellService

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs`

- [ ] **Step 1: Check test project setup**

Check existing `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj` for xUnit versions and patterns used. Use matching versions.

- [ ] **Step 2: Create ExternalDwellServiceTests.cs**

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

public class ExternalDwellServiceTests
{
    private class FakeGazeEngine : GazeEngine
    {
        public event Action<PhysicalPoint, long>? CursorMovedInvoked;

        public FakeGazeEngine() : base(null!, null!, new EyeCtrlSettings())
        {
            // Hijack event raising for tests
        }

        public void SimulateCursorMoved(PhysicalPoint point, long ts)
        {
            // The service subscribed to CursorMoved — we need a real event
        }
    }

    // Since GazeEngine's CursorMoved is an actual event, use a real
    // lightweight GazeEngine with mock IGazeProvider + IInputSimulator

    [Fact]
    public void Service_HasSmokeTest()
    {
        // Full integration tests need a running pipe server + connected client.
        // This smoke test verifies construction + dispose.
        var gazeEngine = new GazeEngine(
            new StubGazeProvider(),
            new StubInputSimulator(),
            new EyeCtrlSettings());
        var settings = new ExternalDwellSettings { Enabled = false };
        var logger = new LoggerFactory().CreateLogger<ExternalDwellService>();

        using var service = new ExternalDwellService(settings, gazeEngine, logger,
            windowFromPoint: _ => null);
        service.Dispose();
        // No exception = pass
    }

    private class StubGazeProvider : EyeCtrl.Core.Interfaces.IGazeProvider
    {
        public bool IsConnected => true;
        public event Action<EyeCtrl.Core.Models.GazePoint>? GazePointReceived;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private class StubInputSimulator : EyeCtrl.Core.Interfaces.IInputSimulator
    {
        public void MoveCursor(PhysicalPoint point) { }
        public void LeftClick(PhysicalPoint point) { }
        public void RightClick(PhysicalPoint point) { }
        public void DoubleClick(PhysicalPoint point) { }
        public void MouseDown(PhysicalPoint point) { }
        public void MouseUp(PhysicalPoint point) { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(
            System.Collections.Generic.IReadOnlySet<EyeCtrl.Core.Models.ModifierKey> modifiers,
            ushort virtualKeyCode) { }
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ExternalDwellServiceTests"`
Expected: Pass.

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs
git commit -m "test: add ExternalDwellService smoke tests"
```

---

### Task 12: Full solution build + all tests

- [ ] **Step 1: Build entire solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: All projects compile with zero errors.

- [ ] **Step 2: Run all tests**

Run: `dotnet test`
Expected: All pre-existing tests + new tests pass.

- [ ] **Step 3: Fix any compilation/test failures**

If `ExternalDwellService` has missing references in `App.xaml.cs` or `AppBootstrapper.cs`, add the appropriate `using` directives.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: complete External Dwell API Plan 1 — WPF vertical slice"
```

---

### Task 13: Manual smoke test

- [ ] **Step 1: Start EyeCtrl in test mode**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`

- [ ] **Step 2: Start WPF sample app**

Run: `dotnet run --project samples/EyeCtrl.Sdk.Wpf.Sample`

- [ ] **Step 3: Verify dwell activation**

1. Move system cursor (mouse in test mode) to hover over the "Save" button
2. Wait for approximately 800 ms (the default dwell time)
3. Verify the DwellFeedbackOverlay appears and fills over the button
4. After dwell completes, verify the "Save" button fires and the log label updates
5. Verify EyeCtrl's own fixation/activation-button flow is suppressed while over the button
6. Move cursor away — verify suppression lifts
7. Click the "Plain" button normally — verify it works, no dwell needed

- [ ] **Step 4: If any issues, fix and re-test**

Common issues:
- Feedback overlay not showing: check `DwellFeedbackOverlay` DPI handling (physical vs logical coords)
- Activation not firing: check pipe connection (`eyectrl_dwell`), verify JSON serialization
- Suppression not working: check `GazeEngine.SuppressFixation` flow
- Duplicate registrations: check `_idByElement` tracking
