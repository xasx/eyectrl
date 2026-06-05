namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Contracts;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
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

    private static void RunOnStaThread(Action action)
    {
        using var done = new ManualResetEventSlim();
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
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
            RunPipeClient().GetAwaiter().GetResult();
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

            Assert.True(engine.SuppressFixation);
        });
    }

    private static async Task RunPipeClient()
    {
        await using var client = new NamedPipeClientStream(".", "eyectrl_dwell", PipeDirection.InOut);
        await client.ConnectAsync(2000);
        await using var w = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync(JsonSerializer.Serialize<WireMessage>(
            new RegisterMessage { Id = "btn", Rect = new SdkRect(0, 0, 10, 10), Hwnd = 7 }, Json));
        await Task.Delay(100);
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
