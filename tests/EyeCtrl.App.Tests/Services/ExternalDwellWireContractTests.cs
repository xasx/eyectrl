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
using EyeCtrl.Sdk.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

// End-to-end contract tests for the eyectrl_dwell wire protocol: a real DwellClient (with its
// OutboundRectTracker dedup, reconnect loop, and adapter routing) speaking newline-framed JSON
// over a real named pipe to a real ExternalDwellService server. The unit-sliced tests in
// EyeCtrl.Sdk.Abstractions.Tests cover each side in isolation; these pin the CONTRACT — that id,
// rect, and hwnd flow correctly both directions, that dwell timing works through the pipe, and
// that the two sides cannot drift. Like ExternalDwellServiceTests, each instance binds a unique
// GUID-suffixed pipe (issue #76: pipe names are machine-global) and joins the same collection to
// stay off the parallel path within this process.
// Also a DpiHelper reader: dwell progress on a live target calls SetFeedback, which calls
// DwellFeedbackOverlay.ShowAt, which reads PhysicalToLogicalX/Y. It cannot also join
// "DpiHelper Tests (App)" - one collection per class - and it does not need to: "ExternalDwellPipe"
// is itself DisableParallelization, and xUnit v3 runs such a collection exclusively, never
// alongside another collection. So no DpiHelper writer can run while this class does. (This
// comment previously claimed the opposite, from xUnit v2 semantics.) See
// AppDpiHelperTestCollection's doc for the reach map and NonParallelCollectionPin for the pin.
[Collection("ExternalDwellPipe")]
public class ExternalDwellWireContractTests
{
    private readonly string _pipeName = ExternalDwellService.DefaultPipeName + "_test_" + Guid.NewGuid().ToString("N");

    // Direct (unsmoothed), deliberately. These tests pin the WIRE CONTRACT: that an id, a rect and an
    // hwnd survive the round trip and that dwell timing works through the pipe. They express that by
    // raising two synthetic samples 400-800 ms apart at an exact screen position and expecting the
    // server to see that position — which silently assumed the gaze filter was a pass-through.
    //
    // It was: at the pre-Stage-3 defaults a jump of any size drove the adaptive cutoff so high that
    // alpha reached ~1 and the raw sample came straight back out. That is the pathology the retune
    // removed, so the retuned filter now needs ~3 samples to cross a large jump and the 2-sample
    // stimulus lands the filtered point nowhere near the rect. Pinning the filter to Direct removes the
    // coupling rather than papering over it: nothing in this file is about smoothing, and a protocol
    // test that only passes while the filter refuses to filter is testing the wrong thing.
    private static GazeEngine NewGazeEngine(StubGazeProvider provider)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        return new GazeEngine(provider, new StubInputSimulator(), settings);
    }

    private ExternalDwellService NewService(GazeEngine engine, Func<PhysicalPoint, long?> root,
        ExternalDwellSettings settings) =>
        new(settings, engine, Dispatcher.CurrentDispatcher, NullLogger<ExternalDwellService>.Instance,
            new WeakReferenceMessenger(), root, pipeName: _pipeName);

    // Same STA-thread + WPF-application gate pattern as ExternalDwellServiceTests: the service ctor
    // builds a DwellFeedbackOverlay whose XAML needs the token dictionary in Application.Current.Resources.
    private static void RunOnStaThread(Action action)
    {
        using var done = new ManualResetEventSlim();
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
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

    // ─── Scenario 1: the core round trip ─────────────────────────────────────────────

    [Fact]
    public void Register_DwellOverRect_ActivationReachesClientAdapter()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            // Cooldown 5000ms makes a second activation synthetically impossible during the retry
            // loop below (every retry lands inside the cooldown), so the count is exactly 1.
            using var svc = NewService(engine, _ => 7,
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 5000 });
            svc.Start();

            using var client = new DwellClient(_pipeName);
            var fake = new FakeDwellAdapter("btn", hwnd: 7, new SdkRect(0, 0, 200, 200));
            client.RegisterAdapter(fake);
            client.Start();

            long t = 0;
            DwellUntilActivated(provider, fake, x: 100, y: 100, ref t);

            Check.That(fake.Activations).IsEqualTo(1);
        });
    }

    // ─── Scenario 2: rect update mid-dwell follows the new rect without a dwell reset ──

    [Fact]
    public void RectUpdate_MidDwell_FollowsMovedRect_WithoutDwellReset()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            using var svc = NewService(engine, _ => 7,
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 5000 });
            svc.Start();

            using var client = new DwellClient(_pipeName);
            var fake = new FakeDwellAdapter("btn", hwnd: 7, new SdkRect(0, 0, 200, 200));
            client.RegisterAdapter(fake);
            client.Start();

            long t = WaitUntilRegistered(engine, provider, x: 100, y: 100, tUs: 0);

            // Enter rect A (0,0–200,200) and accumulate to 50%.
            provider.Raise(new GazePoint(100, 100, t, IsValid: true));
            provider.Raise(new GazePoint(100, 100, t + 400_000, IsValid: true));
            Check.That(engine.SuppressFixation).IsTrue();   // mid-dwell over a gated target

            // The control moves to B (300,0–500,200); the layout-changed nudge must arrive as an
            // UpdateMessage, not a re-Register (a fresh DwellTarget would zero the accumulated dwell).
            fake.MoveTo(new SdkRect(300, 0, 200, 200));
            client.OnAdapterLayoutChanged("btn");
            Thread.Sleep(250);   // server applies the update (no ack in the protocol; bounded settle)

            // Gaze follows to B's center. 800ms since the ORIGINAL enter → must activate now. If the
            // update never arrived, this point is outside A and the dwell resets; if the update were
            // treated as a re-register, the target merely re-enters with zero progress. Both fail here.
            provider.Raise(new GazePoint(400, 100, t + 800_000, IsValid: true));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && fake.Activations == 0) Thread.Sleep(20);
            Check.That(fake.Activations).IsEqualTo(1);
        });
    }

    // ─── Scenario 3: unregister mid-dwell stops progress without activating ───────────

    [Fact]
    public void Unregister_MidDwell_StopsProgress_WithoutActivation()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            using var svc = NewService(engine, _ => 7,
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 });
            svc.Start();

            using var client = new DwellClient(_pipeName);
            var fake = new FakeDwellAdapter("btn", hwnd: 7, new SdkRect(0, 0, 200, 200));
            client.RegisterAdapter(fake);
            client.Start();

            long t = WaitUntilRegistered(engine, provider, x: 100, y: 100, tUs: 0);

            // Mid-dwell at 50%.
            provider.Raise(new GazePoint(100, 100, t, IsValid: true));
            provider.Raise(new GazePoint(100, 100, t + 400_000, IsValid: true));
            Check.That(engine.SuppressFixation).IsTrue();

            client.UnregisterAdapter("btn");

            // Poll for server truth: park off-target (clearing accumulated dwell), then re-enter
            // the OLD rect for 100ms — far short of completing. While the server still tracks the
            // target that shows progress (suppression true); once dropped, nothing accumulates.
            // The observation IS the barrier, so no fixed settle is needed. A re-enter that
            // COMPLETED would release suppression either way (activation releases it too), which
            // is why the accumulate must stay sub-completion — and the client-side adapter
            // removal would mask a lingering server target's activation, which is why the truth
            // channel is suppression, not Activations.
            long probe = t + 2_000_000;
            var probeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (true)
            {
                provider.Raise(new GazePoint(9000, 9000, probe, IsValid: true));
                provider.Raise(new GazePoint(100, 100, probe + 50_000, IsValid: true));
                provider.Raise(new GazePoint(100, 100, probe + 100_000, IsValid: true));
                probe += 1_000_000;
                if (!engine.SuppressFixation || DateTime.UtcNow >= probeDeadline) break;
                Thread.Sleep(50);
            }
            Check.That(engine.SuppressFixation).IsFalse();

            Thread.Sleep(300);   // grace for an activation line that must never come
            Check.That(fake.Activations).IsEqualTo(0);
        });
    }

    // ─── Scenario 4: the client survives a server restart and re-registers its rects ──

    [Fact]
    public void ServerRestart_ClientReconnects_AndReRegistersItsRects()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            var settings = new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 5000 };
            using var svc1 = NewService(engine, _ => 7, settings);
            svc1.Start();

            using var client = new DwellClient(_pipeName);
            var fake = new FakeDwellAdapter("btn", hwnd: 7, new SdkRect(0, 0, 200, 200));
            client.RegisterAdapter(fake);
            client.Start();

            // Phase 1: works before the restart. Cooldown 5000 is load-bearing here: with cooldown 0
            // every retry attempt of DwellUntilActivated can emit its own activation, so a second
            // one could still be in flight when beforeRestart is sampled below — and land during
            // phase 2, faking a reconnect activation (a false pass, the bad direction). With the
            // cooldown, at most ONE activation can be emitted before the loop observes it, so the
            // sample is quiescent. svc2's detector is fresh, so phase 2 is not cooldown-blocked.
            long t = 0;
            DwellUntilActivated(provider, fake, x: 100, y: 100, ref t);
            var beforeRestart = fake.Activations;
            Check.That(beforeRestart).IsStrictlyGreaterThan(0);

            // EyeCtrl "restarts": old server gone, new one on the same pipe, same gaze engine.
            svc1.Dispose();
            using var svc2 = NewService(engine, _ => 7, settings);
            svc2.Start();

            // Phase 2: the client's backoff loop reconnects and re-sends its adapters' rects (the
            // tracker reset path in RunAsync). Third-party apps depend on exactly this when EyeCtrl
            // restarts. The retry loop absorbs the ~1s MinBackoffMs before the reconnect lands.
            DwellUntilActivated(provider, fake, x: 100, y: 100, ref t);
            Check.That(fake.Activations).IsStrictlyGreaterThan(beforeRestart);
        });
    }

    // ─── Scenario 5: garbage on the wire must not kill the connection ─────────────────

    [Fact]
    public void MalformedLines_ConnectionSurvives_AndStillActivates()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            using var svc = NewService(engine, _ => 7,
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 5000 });
            svc.Start();

            var activated = SendGarbageThenRegisterAndDwell(provider, _pipeName).GetAwaiter().GetResult();

            Check.That(activated).IsNotNull();
            Check.That(activated).Contains("activated");
            Check.That(activated).Contains("\"id\":\"btn\"");
        });
    }

    private static async Task<string?> SendGarbageThenRegisterAndDwell(StubGazeProvider provider, string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var r = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        // JSON null (deserializes to null → the HandleMessage(null) no-op), an empty line and
        // non-JSON (JsonException), an unknown type discriminator, and truncated JSON. The server
        // must swallow each per-line and keep reading — a cooperating app that emits one bad line
        // must not lose its dwell channel.
        await w.WriteLineAsync("null");
        await w.WriteLineAsync("");
        await w.WriteLineAsync("not json {");
        await w.WriteLineAsync("""{"type":"nonsense"}""");
        await w.WriteLineAsync("""{"type":"register","id":"trunc""");
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 200, 200), Hwnd = 7 }, WireJson.Options));
        await Task.Delay(250);   // let the server register the target

        provider.Raise(new GazePoint(100, 100, 0, IsValid: true));
        provider.Raise(new GazePoint(100, 100, 800_000, IsValid: true));

        var read = r.ReadLineAsync();
        return await Task.WhenAny(read, Task.Delay(2000)) == read ? await read : null;
    }

    // ─── Scenario 6: cooldown suppresses the second dwell over the wire ──────────────

    [Fact]
    public void Cooldown_SecondDwellWithinWindow_DoesNotReactivate()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            using var svc = NewService(engine, _ => 7,
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 5000 });
            svc.Start();

            using var client = new DwellClient(_pipeName);
            var fake = new FakeDwellAdapter("btn", hwnd: 7, new SdkRect(0, 0, 200, 200));
            client.RegisterAdapter(fake);
            client.Start();

            long t = WaitUntilRegistered(engine, provider, x: 100, y: 100, tUs: 0);

            // First dwell completes → exactly one activation delivered to the adapter.
            provider.Raise(new GazePoint(100, 100, t, IsValid: true));
            provider.Raise(new GazePoint(100, 100, t + 800_000, IsValid: true));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && fake.Activations == 0) Thread.Sleep(20);
            Check.That(fake.Activations).IsEqualTo(1);

            // Second full dwell completing 1.6s after the first activation — inside the 5s cooldown,
            // so the server must not emit a second activated line.
            provider.Raise(new GazePoint(100, 100, t + 1_600_000, IsValid: true));
            provider.Raise(new GazePoint(100, 100, t + 2_400_000, IsValid: true));

            Thread.Sleep(700);
            Check.That(fake.Activations).IsEqualTo(1);
        });
    }

    // ─── Scenario 7: the hwnd flows over the wire — occlusion gating with the real client ──

    [Fact]
    public void Gating_RootWindowDiffersFromRegisteredHwnd_NeverActivates()
    {
        RunOnStaThread(() =>
        {
            var provider = new StubGazeProvider();
            using var engine = NewGazeEngine(provider);
            // Switchable occlusion root: 99 never matches the hwnd the client registers (7), then
            // the positive leg flips it to 7. Without that flip the test is vacuous — "no
            // activation, no suppression" cannot distinguish gated-out from never-registered
            // (with no targets the service short-circuits before the root lookup).
            long root = 99;
            using var svc = NewService(engine, _ => root,
                new ExternalDwellSettings { Enabled = true, DefaultDwellMs = 800, CooldownMs = 0 });
            svc.Start();

            using var client = new DwellClient(_pipeName);
            var fake = new FakeDwellAdapter("btn", hwnd: 7, new SdkRect(0, 0, 200, 200));
            client.RegisterAdapter(fake);
            client.Start();

            Thread.Sleep(400);   // connect + register settle (gating means WaitUntilRegistered cannot be used)

            long t = 0;
            provider.Raise(new GazePoint(100, 100, t, IsValid: true));
            provider.Raise(new GazePoint(100, 100, t + 800_000, IsValid: true));

            Thread.Sleep(600);   // grace for an activation line that must never come
            Check.That(fake.Activations).IsEqualTo(0);
            Check.That(engine.SuppressFixation).IsFalse();

            // Open the gate: the root now matches the registered hwnd, so the SAME target must
            // activate. This is also what pins hwnd flow — if the hwnd had arrived as 0 or
            // garbage, root 7 would not match and this leg would time out.
            root = 7;
            t += 2_000_000;
            DwellUntilActivated(provider, fake, x: 100, y: 100, ref t);
            Check.That(fake.Activations).IsStrictlyGreaterThan(0);
        });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until the server has the target registered AND gated in, polling with a tiny 50ms
    /// accumulate: as soon as the pipe delivered the Register, mid-dwell feedback acquires gaze
    /// suppression, which is observable synchronously on the raising thread. This replaces the
    /// fixed post-register sleeps elsewhere in the suite with a deterministic barrier. Afterwards
    /// the gaze is parked off-target (resetting the accumulated dwell and releasing suppression)
    /// and the next safe synthetic timestamp is returned.
    /// </summary>
    private static long WaitUntilRegistered(GazeEngine engine, StubGazeProvider provider, double x, double y, long tUs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline && !engine.SuppressFixation)
        {
            provider.Raise(new GazePoint(x, y, tUs, IsValid: true));
            provider.Raise(new GazePoint(x, y, tUs + 50_000, IsValid: true));
            tUs += 1_000_000;
            Thread.Sleep(50);
        }
        Check.WithCustomMessage("target never became dwellable — Register did not reach the server")
            .That(engine.SuppressFixation).IsTrue();

        provider.Raise(new GazePoint(9000, 9000, tUs + 100_000, IsValid: true));
        Check.That(engine.SuppressFixation).IsFalse();
        return tUs + 1_000_000;
    }

    /// <summary>
    /// Raises enter/complete gaze pairs with advancing synthetic timestamps until the adapter
    /// activates, so tests absorb connect/register/backoff latency without wall-clock coupling:
    /// dwell completion itself is driven by the synthetic timestamps, never by real elapsed time.
    /// </summary>
    private static void DwellUntilActivated(StubGazeProvider provider, FakeDwellAdapter adapter,
        double x, double y, ref long tUs)
    {
        var baseline = adapter.Activations;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline && adapter.Activations == baseline)
        {
            provider.Raise(new GazePoint(x, y, tUs, IsValid: true));
            provider.Raise(new GazePoint(x, y, tUs + 800_000, IsValid: true));
            tUs += 1_000_000;

            // Give the activated line time to travel back through the pipe and the client's read
            // loop before the next attempt (with a cooldown configured, every retry stays inside
            // it, so the activation count cannot overshoot).
            var inner = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
            while (DateTime.UtcNow < inner && adapter.Activations == baseline) Thread.Sleep(20);
        }
        Check.WithCustomMessage("adapter never activated — the round trip did not complete")
            .That(adapter.Activations).IsStrictlyGreaterThan(baseline);
    }

    /// <summary>
    /// Thread-agnostic IDwellElementAdapter fake. Activate() fires on the client's background read
    /// loop and TryGetScreenRect() on its reconnect loop, so the rect is lock-guarded and the
    /// activation count is interlocked.
    /// </summary>
    private sealed class FakeDwellAdapter : IDwellElementAdapter
    {
        private readonly object _rectLock = new();
        private SdkRect _rect;
        private int _activations;

        public string Id { get; }
        public DwellMode Mode => DwellMode.Invoke;
        public long WindowHandle { get; }
        public int Activations => Volatile.Read(ref _activations);

        public FakeDwellAdapter(string id, long hwnd, SdkRect rect)
        {
            Id = id;
            WindowHandle = hwnd;
            _rect = rect;
        }

        public bool TryGetScreenRect(out SdkRect rect)
        {
            lock (_rectLock) rect = _rect;
            return true;
        }

        public void Activate() => Interlocked.Increment(ref _activations);

        public void MoveTo(SdkRect rect)
        {
            lock (_rectLock) _rect = rect;
        }
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
