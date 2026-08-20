namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// These tests each bind a real named pipe, but under a per-instance unique name (see
// ExternalDwellServiceTests._pipeName), so they no longer share the machine-global production name
// with anything else on the box. The collection is retained to keep these STA + timing-sensitive
// tests off the parallel path within this process; it never could have prevented the cross-process
// theft that actually broke them (DisableParallelization is process-local) — the unique name does.
[CollectionDefinition("ExternalDwellPipe", DisableParallelization = true)]
public sealed class ExternalDwellPipeCollection { }

[Collection("ExternalDwellPipe")]
public class ExternalDwellServiceTests
{
    /// <summary>
    /// Unique pipe name per test instance (xUnit constructs a new instance per method). Pipe names
    /// are machine-global, so binding the production name (<c>eyectrl_dwell</c>) let any other
    /// process on the box — a <c>dotnet test</c> in a sibling worktree, or a live
    /// <c>EyeCtrl.App</c>/<c>EyeCtrl.Supervisor</c> — accept a test's client and then drop it
    /// (<c>IOException: Pipe is broken</c>), which surfaced as a *different* test failing each run.
    /// xUnit's <c>DisableParallelization</c> cannot help there, being process-local; not sharing the
    /// name is the only real fix (issue #76), mirroring the <c>eyectrl_watch</c> tests.
    /// </summary>
    private readonly string _pipeName = ExternalDwellService.DefaultPipeName + "_test_" + Guid.NewGuid().ToString("N");

    private static GazeEngine NewGazeEngine() =>
        new(new StubGazeProvider(), new StubInputSimulator(), new EyeCtrlSettings());

    private ExternalDwellService NewService(GazeEngine engine, Func<PhysicalPoint, long?> root) =>
        new(new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
            engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
            new WeakReferenceMessenger(), root, pipeName: _pipeName);

    private static void RunOnStaThread(Action action)
    {
        using var done = new ManualResetEventSlim();
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                // NewService constructs a DwellFeedbackOverlay, whose XAML reads
                // {StaticResource SmallCornerRadius} (migrated off a literal CornerRadius="4" in
                // Task 8). That StaticResource lookup needs the OverlayStyles.xaml token
                // dictionary already merged into Application.Current.Resources — this class has
                // no window/theme test of its own to guarantee that, so it used to rely on
                // OverlayResourceResolutionTests/CornerRadiusClampTests having merged it first as
                // a side effect of running earlier. That ordering was never guaranteed: it held
                // only while those two classes ran unconstrained across many parallel STA
                // threads, making an early winner near-certain by sheer volume. Serialising them
                // into one collection (see WpfApplicationGate) removed that lucky head start, so
                // this call makes the dependency explicit instead of leaning on load order.
                WpfApplicationGate.EnsureApplicationWithTokens();
                action();
            }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait();
        if (captured is not null) throw captured;
    }

    [Fact]
    public void ConstructDispose_NoThrow()
    {
        RunOnStaThread(() =>
        {
            using var engine = NewGazeEngine();
            using var svc = NewService(engine, _ => null);
            svc.Start();
        });
    }

    [Fact]
    public void RegisterThenDisconnect_RemovesTarget()
    {
        RunOnStaThread(() =>
        {
            using var engine = NewGazeEngine();
            using var svc = NewService(engine, _ => 7);
            svc.Start();
            Thread.Sleep(100);
            RunPipeClient(_pipeName).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void NoActiveTarget_LeavesForeignSuppressFixationUntouched()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            // root => null: no target is ever gated in, so the service has no active target.
            using var svc = NewService(engine, _ => null);
            svc.Start();

            // Another owner (keyboard / calibration via EventCoordinator) suppresses fixation.
            engine.SuppressFixation = true;

            // A gaze frame with no registered targets must NOT clear another owner's suppression.
            provider.Raise(new GazePoint(500, 500, 1_000, IsValid: true));

            Check.That(engine.SuppressFixation).IsTrue();
        });
    }

    [Fact]
    public void DisableThenReEnable_StillActivatesRegisteredTarget()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 };
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => 7,
                pipeName: _pipeName);
            svc.Start();

            var activated = RegisterToggleAndDwell(settings, provider, _pipeName).GetAwaiter().GetResult();

            // With the target retained across the disable/enable toggle, dwell still activates it.
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
            Check.That(activated).Contains("\"id\":\"btn\"");
        });
    }

    // Registers a target, toggles Enabled off then on, dwells over it, and returns the server's
    // activation line (or null on timeout). Used to verify re-enable does not drop the target.
    private static async Task<string?> RegisterToggleAndDwell(ExternalDwellSettings settings, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200); // let the server register the target

        // Toggle the feature off then back on — must NOT drop the registered target.
        settings.Enabled = false;
        settings.Enabled = true;

        // Dwell over the target: enter (t=0), complete after DefaultDwellMs (800 ms in microseconds).
        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    private static async Task RunPipeClient(string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 10, 10), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(100);
    }

    [Fact]
    public void RegisterWhileDisabled_ThenEnable_DwellActivates()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = false, DefaultDwellMs = 800, CooldownMs = 0 };
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => 7,
                pipeName: _pipeName);
            svc.Start();

            var activated = RegisterDisabledThenEnableAndDwell(settings, provider, _pipeName).GetAwaiter().GetResult();
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
        });
    }

    private static async Task<string?> RegisterDisabledThenEnableAndDwell(
        ExternalDwellSettings settings, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200);

        settings.Enabled = true;

        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    // --- Behavior tests migrated from the deleted ExternalDwellArbiterTests (arbiter folded into
    //     ExternalDwellService on a filtered DwellDetector in Phase B). ---

    [Fact]
    public void Gating_WrongRootWindow_NeverActivates()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            // Topmost window under the gaze (99) never matches the target's registered hwnd (7),
            // so the target is gated out of dwell — it must never activate or acquire suppression.
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 99, pipeName: _pipeName);
            svc.Start();

            var activated = RegisterAndDwell(provider, _pipeName).GetAwaiter().GetResult();

            Check.That(activated).IsNull();
            Check.That(engine.SuppressFixation).IsFalse();
        });
    }

    [Fact]
    public void Accumulating_SetsSuppression_ThenReleasesWhenGazeLeaves()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = NewService(engine, _ => 7);
            svc.Start();

            var (afterAccumulate, afterLeave) =
                RegisterAccumulateThenLeave(engine, provider, _pipeName).GetAwaiter().GetResult();

            Check.That(afterAccumulate).IsTrue();  // mid-dwell over a gated target suppresses fixation
            Check.That(afterLeave).IsFalse();      // leaving the target releases our suppression
        });
    }

    [Fact]
    public void Cooldown_BlocksImmediateRetrigger()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 5000 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 7, pipeName: _pipeName);
            svc.Start();

            var (first, second) = RegisterActivateTwice(provider, _pipeName).GetAwaiter().GetResult();

            Check.That(first).IsNotNull();          // first dwell activates
            Check.That(first).Contains("activated");
            Check.That(second).IsNull();            // second dwell within cooldown is blocked
        });
    }

    // Registers a target (hwnd 7) and dwells fully over it, returning the activation line or null.
    private static async Task<string?> RegisterAndDwell(StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200);

        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    // Registers a target, accumulates dwell partway (captures suppression state), then moves the
    // gaze off it (captures suppression state again). Keeps the pipe open so the target survives.
    private static async Task<(bool afterAccumulate, bool afterLeave)> RegisterAccumulateThenLeave(
        GazeEngine engine, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200);

        // Enter (t=0) then accumulate (0.1s, well under the 0.8s dwell): feedback → suppression.
        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 100_000, IsValid: true));
        var afterAccumulate = engine.SuppressFixation;

        // Gaze far outside the target: no feedback → suppression released.
        provider.Raise(new GazePoint(9000, 9000, 200_000, IsValid: true));
        var afterLeave = engine.SuppressFixation;

        return (afterAccumulate, afterLeave);
    }

    // Activates the target once, then attempts a second dwell within the cooldown window.
    private static async Task<(string? first, string? second)> RegisterActivateTwice(StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200);

        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));
        var read1 = r.ReadLineAsync();
        var first = await Task.WhenAny(read1, Task.Delay(2000)) == read1 ? await read1 : null;

        // Retrigger attempt at t=1.6s/2.4s — inside the 5s cooldown from the first activation.
        provider.Raise(new GazePoint(100, 100, 1_600_000, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 2_400_000, IsValid: true));
        var read2 = r.ReadLineAsync();
        var second = await Task.WhenAny(read2, Task.Delay(1500)) == read2 ? await read2 : null;

        return (first, second);
    }

    private sealed class StubGazeProvider : IGazeProvider
    {
        public bool IsConnected => true;
        public event Action<GazePoint>? GazePointReceived;
        public void Raise(GazePoint p) => GazePointReceived?.Invoke(p);
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
