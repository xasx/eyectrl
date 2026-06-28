namespace EyeCtrl.Core.Tests.EyeCare;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeEngineSampledTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private sealed class FakeInputSimulator : IInputSimulator
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
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }

    [Fact]
    public void GazeSampled_FiresWithRawPoint_BeforeInterpolation()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);

        GazePoint? seen = null;
        engine.GazeSampled += p => seen = p;

        var raw = new GazePoint(100, 200, 5_000_000, IsValid: true);
        provider.SimulateGaze(raw);

        Check.That(seen).IsNotNull();
        Check.That(seen!.IsValid).IsTrue();
        Check.That(seen!.X).IsEqualTo(100.0);
        Check.That(seen!.Y).IsEqualTo(200.0);
    }

    [Fact]
    public void GazeSampled_CarriesInvalidFlag_WhenEyesLost()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);

        bool? sawValid = null;
        engine.GazeSampled += p => sawValid = p.IsValid;

        provider.SimulateGaze(new GazePoint(0, 0, 6_000_000, IsValid: false));

        Check.That(sawValid).IsNotNull();
        Check.That(sawValid!.Value).IsFalse();
    }
}
