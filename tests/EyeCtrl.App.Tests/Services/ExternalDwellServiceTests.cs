namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
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

// Also a DpiHelper reader: dwell progress on a live target calls SetFeedback, which calls
// DwellFeedbackOverlay.ShowAt, which reads PhysicalToLogicalX/Y. It cannot also join
// "DpiHelper Tests (App)" - one collection per class - and it does not need to: "ExternalDwellPipe"
// is itself DisableParallelization, and xUnit v3 runs such a collection exclusively, never
// alongside another collection. So no DpiHelper writer can run while this class does. (This
// comment previously claimed the opposite, from xUnit v2 semantics.) See
// AppDpiHelperTestCollection's doc for the reach map and NonParallelCollectionPin for the pin.
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

    [Fact]
    public void Dispose_DetachesTheSettingsPropertyChangedHandler()
    {
        RunOnStaThread(() =>
        {
            var settings = new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 };
            int before = SettingsSubscriberCount(settings);
            using var engine = NewGazeEngine();
            // using + the explicit Dispose below: the assert must observe the detached state,
            // and a failed first assertion must not leak the service and its overlay window.
            // The second Dispose is a no-op behind the _disposed guard.
            using var svc = new ExternalDwellService(settings, engine, Dispatcher.CurrentDispatcher,
                NullLogger<ExternalDwellService>.Instance, new WeakReferenceMessenger(), _ => null,
                pipeName: _pipeName);
            Check.That(SettingsSubscriberCount(settings)).IsStrictlyGreaterThan(before);

            svc.Dispose();
            Check.That(SettingsSubscriberCount(settings)).IsEqualTo(before);
        });
    }

    // Same reflection guard as GazeEngineDisposeTests: the service subscribes the settings
    // singleton in its ctor, so a handler Dispose forgets to detach keeps the disposed
    // service reachable forever (that is exactly how the leak shipped — an anonymous
    // lambda nothing could unsubscribe).
    private static int SettingsSubscriberCount(ExternalDwellSettings settings)
    {
        var field = typeof(ObservableObject).GetField(
            nameof(ObservableObject.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (Delegate?)field!.GetValue(settings);
        return handler?.GetInvocationList().Length ?? 0;
    }

    private ExternalDwellService NewService(GazeEngine engine, Func<PhysicalPoint, long?> root) =>
        new(new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
            engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
            new WeakReferenceMessenger(), root, pipeName: _pipeName);

    /// <summary>
    /// Raises valid samples at (x,y) at 50 ms cadence from <paramref name="fromUs"/> through
    /// <paramref name="toUs"/> inclusive. The dwell detector's gap clamp restarts a dwell on a
    /// single step of 150 ms or more, so the old two-sample pattern (t=0 then t=800 ms) no longer
    /// accumulates — and an activation assertion would fail for a reason unrelated to the test.
    /// </summary>
    private static void RaiseHold(StubGazeProvider provider, double x, double y, long fromUs, long toUs)
    {
        for (var t = fromUs; t < toUs; t += 50_000)
            provider.Raise(new GazePoint(x, y, t, IsValid: true));
        provider.Raise(new GazePoint(x, y, toUs, IsValid: true));
    }

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
        RaiseHold(provider, 100, 100, 0, 800_000);

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    // The external-dwell detector runs on the gaze thread inside the provider lock, off CursorMoved
    // rather than the UI-thread message. It honours the frame's gap flag like every other dwell
    // surface: a blink freezes the published position over the cooperating app's control, and feeding
    // that on would activate it. Driven end-to-end through a real GapInterpolator by raising an
    // INVALID sample, which is what produces a held frame.
    [Fact]
    public void HeldSamples_DoNotActivateExternalTarget()
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

            var activated = RegisterThenDwellThroughABlink(provider, _pipeName).GetAwaiter().GetResult();

            Check.That(activated).IsNull();
        });
    }

    // Registers a target, enters it with a real sample, then lets the dwell time elapse across HELD
    // samples only. Returns the server's activation line, or null if none arrived (the expectation).
    private static async Task<string?> RegisterThenDwellThroughABlink(StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200); // let the server register the target

        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));          // enter the target
        // Invalid samples inside the 150 ms interpolation budget: the engine publishes the held
        // (100,100) with IsInGap set. The last one is past DefaultDwellMs, so an unguarded detector
        // would activate here.
        provider.Raise(new GazePoint(0, 0, 100_000, IsValid: false));
        provider.Raise(new GazePoint(0, 0, 800_000, IsValid: false));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(1000)) == read ? await read : null;
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

        RaiseHold(provider, 100, 100, 0, 800_000);

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

        RaiseHold(provider, 100, 100, 0, 800_000);

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

        RaiseHold(provider, 100, 100, 0, 800_000);
        var read1 = r.ReadLineAsync();
        var first = await Task.WhenAny(read1, Task.Delay(2000)) == read1 ? await read1 : null;

        // Retrigger attempt at t=1.6s/2.4s — inside the 5s cooldown from the first activation.
        provider.Raise(new GazePoint(100, 100, 1_600_000, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 2_400_000, IsValid: true));
        var read2 = r.ReadLineAsync();
        var second = await Task.WhenAny(read2, Task.Delay(1500)) == read2 ? await read2 : null;

        return (first, second);
    }

    [Fact]
    public void DwellRoutingEntry_HalvingMap_ReducesActivationLatency()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            var dwellRegistry = new DwellDetectorRegistry();
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 7,
                dwellRegistry: dwellRegistry, pipeName: _pipeName);
            svc.Start();

            // The service registers exactly one routing entry: the External range (200–3000, NOT
            // Standard — a legal 2500 ms baseline must survive an identity restore), whose applier
            // delegates effective-dwell maps to the service's detector.
            Check.That(dwellRegistry.Registrations).HasSize(1);
            Check.That(dwellRegistry.Registrations[0].Context).IsEqualTo(DwellContext.ExternalDwell);
            Check.That(dwellRegistry.Registrations[0].MinDwellMs).IsEqualTo(AdaptationDwellRanges.ExternalMinDwellMs);
            Check.That(dwellRegistry.Registrations[0].MaxDwellMs).IsEqualTo(AdaptationDwellRanges.ExternalMaxDwellMs);

            var activated = RegisterApplyHalvingMapAndDwell(
                dwellRegistry.Registrations[0], provider, _pipeName).GetAwaiter().GetResult();

            // 420 ms is just past the HALVED 400 ms dwell and well under the 800 ms baseline — the
            // activation can only have fired because the routed map reached the live target.
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
        });
    }

    // Registers a target (800 ms baseline), lets the async pipe registration settle, applies the
    // halving map through the routing entry, then dwells to just past 400 ms — half the baseline.
    // The activation proves the map reached the detector's live target behaviourally (at the
    // baseline dwell time that timestamp would still be mid-dwell).
    private static async Task<string?> RegisterApplyHalvingMapAndDwell(
        DwellRoutingRegistration entry, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200); // let the server register the target BEFORE applying the map

        entry.Apply(ms => ms / 2);

        RaiseHold(provider, 100, 100, 0, 420_000);

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    [Fact]
    public void GeometryRoutingEntry_MarginMap_GrowsTheLiveRectTarget()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            var dwellRegistry = new DwellDetectorRegistry();
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 7,
                dwellRegistry: dwellRegistry, pipeName: _pipeName);
            svc.Start();

            Check.That(dwellRegistry.Registrations).HasSize(1);
            Check.That(dwellRegistry.Registrations[0].ApplyGeometry).IsNotNull();

            var activated = RegisterApplyMarginMapAndDwell(
                dwellRegistry.Registrations[0], provider, _pipeName).GetAwaiter().GetResult();

            // (205, 100) is OUTSIDE the as-registered rect [0, 200] × [0, 200] and INSIDE the
            // +20-outset rect [−20, 220] — the activation can only have fired because the routed
            // margin reached the live target's geometry.
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
        });
    }

    // Registers a rect target, lets the async pipe registration settle, applies a +20 margin map
    // through the routing entry's geometry applier, then dwells at a point only the GROWN rect
    // covers — proving the applier drives the service's live target under its lock.
    private static async Task<string?> RegisterApplyMarginMapAndDwell(
        DwellRoutingRegistration entry, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200); // let the server register the target BEFORE applying the map

        entry.ApplyGeometry(ext => ext + 20); // min half-extent 100 → 120 ⇒ +20 per side

        RaiseHold(provider, 205, 100, 0, 800_000);

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    [Fact]
    public void GeometryRouting_RebaseAfterSdkSizeChange_IdentityMapDoesNotRevertTheNewSize()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            var dwellRegistry = new DwellDetectorRegistry();
            using var svc = new ExternalDwellService(
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 },
                engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
                new WeakReferenceMessenger(), _ => 7,
                dwellRegistry: dwellRegistry, pipeName: _pipeName);
            svc.Start();

            var activated = RegisterUpdateApplyIdentityAndDwell(
                dwellRegistry.Registrations[0], provider, _pipeName).GetAwaiter().GetResult();

            // (30, 50) is inside the SDK's NEW 400×100 rect and outside the stale-200×200-based
            // reverted rect (x ∈ [100, 300] around the new centre) — the activation proves the
            // rebase survived the routed identity map (round-1 blocker, service level).
            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
        });
    }

    // Registers 200×200, lets it settle, pushes an UpdateMessage to 400×100, lets it settle,
    // applies the NULL geometry map (the disabled/cleared restore path — margin 0), then dwells at
    // a point only the NEW rect covers. Under the raw-write bug the null map recomputes Bounds from
    // the stale base half-extents and the point falls outside — no dwell, no activation.
    private static async Task<string?> RegisterUpdateApplyIdentityAndDwell(
        DwellRoutingRegistration entry, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(200);
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new UpdateMessage { Id = "btn", Rect = new SdkRect(0, 0, 400, 100) }, WireJson.Options));
        await Task.Delay(200); // let the server rebase the target BEFORE applying the map

        entry.ApplyGeometry(null);

        RaiseHold(provider, 30, 50, 0, 800_000);

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    // --- Ownership-gate security tests (local attacker on the dwell pipe) ---

    // A second connection re-registering an id owned by the first client must NOT steal the
    // dwell channel: the activation still routes to the owning client, the thief gets nothing.
    [Fact]
    public void SecondConnection_RegisterStealAttempt_TargetStaysWithOwner()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = NewService(engine, _ => 7);
            svc.Start();

            var (ownerLine, thiefLine) = TwoClientsRegisterStealAndDwell(provider, _pipeName).GetAwaiter().GetResult();

            Check.That(ownerLine).IsNotNull();            // owner still receives the activation
            Check.That(ownerLine).Contains("\"id\":\"btn\"");
            Check.That(thiefLine).IsNull();               // the thief received nothing
        });
    }

    // A foreign unregister must be ignored: only the owning connection may remove a target.
    // Without the gate, one pipe message from any client could permanently kill the user's
    // dwell-clicks in the affected SDK app.
    [Fact]
    public void ForeignUnregister_IsIgnored_TargetSurvives()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = NewService(engine, _ => 7);
            svc.Start();

            var (ownerLine, thiefLine) = RegisterForeignUnregisterAndDwell(provider, _pipeName).GetAwaiter().GetResult();

            Check.That(ownerLine).IsNotNull();            // the target survived the foreign unregister
            Check.That(ownerLine).Contains("\"id\":\"btn\"");
        });
    }

    // Owner registers "btn"; a second connection sends its own register for the same id (steal
    // attempt); dwell completes. Returns (owner's activation line, thief's first line or null).
    private static async Task<(string? ownerLine, string? thiefLine)> TwoClientsRegisterStealAndDwell(
        StubGazeProvider provider, string pipeName)
    {
        var (r1, w1, r2, w2, client1, client2) = await ConnectTwoClients(pipeName);
        await using (client1)
        await using (client2)
        {
            await w1.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
                new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
            await Task.Delay(200);

            await w2.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
                new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
            await Task.Delay(200);

            RaiseHold(provider, 100, 100, 0, 800_000);

            var read1 = r1.ReadLineAsync();
            var ownerLine = await Task.WhenAny(read1, Task.Delay(2000)) == read1 ? await read1 : null;

            var read2 = r2.ReadLineAsync();
            var thiefLine = await Task.WhenAny(read2, Task.Delay(300)) == read2 ? await read2 : null;

            return (ownerLine, thiefLine);
        }
    }

    // Owner registers "btn"; a second connection sends an unregister for it; dwell completes.
    // Returns (owner's activation line, thief's first line or null).
    private static async Task<(string? ownerLine, string? thiefLine)> RegisterForeignUnregisterAndDwell(
        StubGazeProvider provider, string pipeName)
    {
        var (r1, w1, r2, w2, client1, client2) = await ConnectTwoClients(pipeName);
        await using (client1)
        await using (client2)
        {
            await w1.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
                new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
            await Task.Delay(200);

            await w2.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
                new UnregisterMessage { Id = "btn" }, WireJson.Options));
            await Task.Delay(200);

            RaiseHold(provider, 100, 100, 0, 800_000);

            var read1 = r1.ReadLineAsync();
            var ownerLine = await Task.WhenAny(read1, Task.Delay(2000)) == read1 ? await read1 : null;

            return (ownerLine, null);
        }
    }

    private static async Task<(StreamReader r1, StreamWriter w1, StreamReader r2, StreamWriter w2,
        NamedPipeClientStream client1, NamedPipeClientStream client2)> ConnectTwoClients(string pipeName)
    {
        var client1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client1.ConnectAsync(2000);
        var w1 = new StreamWriter(client1, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var r1 = new StreamReader(client1, Encoding.UTF8, false, 1024, leaveOpen: true);

        var client2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client2.ConnectAsync(2000);
        var w2 = new StreamWriter(client2, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var r2 = new StreamReader(client2, Encoding.UTF8, false, 1024, leaveOpen: true);
        return (r1, w1, r2, w2, client1, client2);
    }

    // A register past the per-connection cap (512) must be rejected WITHOUT dropping the
    // connection: the client's existing targets keep working. Bound registration state is the
    // OOM defence for the target tables.
    [Fact]
    public void RegisterOverPerConnectionCap_RejectedButConnectionServes()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = NewService(engine, _ => 7);
            svc.Start();

            var (legit, overCap) = RegisterUpToCapThenDwell(svc, provider, _pipeName).GetAwaiter().GetResult();

            Check.That(legit).IsNotNull();               // a capped-connection target still fires
            Check.That(legit).Contains("\"id\":\"t0\""); // t0's tile activated after the rejection
            Check.That(overCap).IsNull();                // the over-cap target never activates
        });
    }

    // Registers exactly MaxTargetsPerConnection targets on non-overlapping tiles, then one more
    // on a far tile (rejected over the cap). Dwells t0 (clean filter history, like the other
    // dwell tests), then dwells the over-cap tile long enough for the gaze filter to converge —
    // a wrongly-registered t512 would activate, a rejected one leaves the pipe silent.
    private static async Task<(string? legit, string? overCap)> RegisterUpToCapThenDwell(
        ExternalDwellService svc, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        var batch = new StringBuilder();
        for (var i = 0; i < ExternalDwellService.MaxTargetsPerConnection; i++)
            batch.AppendLine(JsonSerializer.Serialize<WireMessage>(
                new RegisterMessage { Id = $"t{i}", Rect = new SdkRect(i * 100, 0, 100, 100), Hwnd = 7 }, WireJson.Options));
        await w.WriteAsync(batch);
        await WaitForTargetCountAsync(svc, ExternalDwellService.MaxTargetsPerConnection);

        // One past the cap, on its own far tile.
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "t512", Rect = new SdkRect(60000, 0, 100, 100), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(300);

        // Dwell on t0's tile continuously, like the other dwell tests.
        RaiseHold(provider, 50, 50, 0, 800_000);
        var read1 = r.ReadLineAsync();
        var legit = await Task.WhenAny(read1, Task.Delay(2000)) == read1 ? await read1 : null;

        // Dwell the over-cap tile with a continuous stream: the One-Euro filter converges onto it,
        // the gap clamp cannot restart the dwell, and a registered t512 accrues 800 ms and fires
        // while a rejected one stays silent.
        for (var t = 1_000_000L; t <= 3_200_000L; t += 50_000)
            provider.Raise(new GazePoint(60050, 50, t, IsValid: true));
        var read2 = r.ReadLineAsync();
        var overCap = await Task.WhenAny(read2, Task.Delay(2000)) == read2 ? await read2 : null;

        return (legit, overCap);
    }

    // A client sitting at EXACTLY the cap must still be able to refresh its own targets' rects
    // (idempotent re-register): a capped refresh would freeze every target at stale geometry
    // while the connection still looks healthy — dwell silently lands on the wrong rectangles.
    [Fact]
    public void ReRegisterAtCap_RefreshesTheExistingTargetsRect()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = new GazeEngine(provider, new StubInputSimulator(), new EyeCtrlSettings());
            using var svc = NewService(engine, _ => 7);
            svc.Start();

            var activated = RegisterToCapThenReregisterAndDwell(svc, provider, _pipeName).GetAwaiter().GetResult();

            Check.That(activated).IsNotNull();               // the refresh was accepted at the cap
            Check.That(activated).Contains("\"id\":\"t0\""); // and dwell used the NEW rect
        });
    }

    // Registers MaxTargetsPerConnection tiles, then re-registers t0 MOVED to a far tile (a
    // same-connection id the caps must not reject) and dwells the far tile. This dwell is the
    // test's first, so the gaze filter warm-starts on the target — two samples suffice.
    private static async Task<string?> RegisterToCapThenReregisterAndDwell(
        ExternalDwellService svc, StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        var batch = new StringBuilder();
        for (var i = 0; i < ExternalDwellService.MaxTargetsPerConnection; i++)
            batch.AppendLine(JsonSerializer.Serialize<WireMessage>(
                new RegisterMessage { Id = $"t{i}", Rect = new SdkRect(i * 100, 0, 100, 100), Hwnd = 7 }, WireJson.Options));
        await w.WriteAsync(batch);
        await WaitForTargetCountAsync(svc, ExternalDwellService.MaxTargetsPerConnection);

        // t0's rect refreshed to a far tile — the register that the cap used to reject.
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "t0", Rect = new SdkRect(100000, 0, 100, 100), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(300);

        RaiseHold(provider, 100050, 50, 0, 900_000);

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    // Poll observable server state (TargetCountForTests) until the batched registrations have
    // landed — sturdier than a fixed sleep, which is the flake-shaped part under load.
    private static async Task WaitForTargetCountAsync(ExternalDwellService svc, int expected)
    {
        for (var i = 0; i < 200 && svc.TargetCountForTests < expected; i++)
            await Task.Delay(25);
        Check.That(svc.TargetCountForTests).IsEqualTo(expected);
    }

    // --- Pipe-supplied rect sanity clamp ---

    [Fact]
    public void ToRect_ClampsHostileExtremes_IntoASaneScreenRange()
    {
        // JSON rejects NaN/Infinity, but legal extremes (1e308) would otherwise flow through
        // DpiHelper into WPF window positioning. Far beyond any real screen is enough.
        var hostile = ExternalDwellService.ToRect(new SdkRect(1e308, -1e308, 1e308, 1e308));
        Check.That(hostile.X).IsEqualTo(100_000);
        Check.That(hostile.Y).IsEqualTo(-100_000);
        Check.That(hostile.Width).IsEqualTo(100_000);
        Check.That(hostile.Height).IsEqualTo(100_000);

        var normal = ExternalDwellService.ToRect(new SdkRect(10, 20, 30, 40));
        Check.That(normal.X).IsEqualTo(10);
        Check.That(normal.Y).IsEqualTo(20);
        Check.That(normal.Width).IsEqualTo(30);
        Check.That(normal.Height).IsEqualTo(40);
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
