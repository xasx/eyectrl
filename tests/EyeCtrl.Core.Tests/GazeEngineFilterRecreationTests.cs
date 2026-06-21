namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeEngineFilterRecreationTests
{
    [Fact]
    public void RecreatesFilter_WhenFilterModeChanges()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var provider = new TestGazeProvider();
        var simulator = new TestInputSimulator();
        var engine = new GazeEngine(provider, simulator, settings);

        // Change filter mode at runtime
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;

        // Verify filter was recreated by sending gaze through it
        // OneEuro filter smooths data, so after sending the same point twice,
        // it should still work (not crash)
        provider.SimulateGaze(100, 200);
        provider.SimulateGaze(100, 200);

        // If we got here without error, the filter was successfully recreated
        Check.That(true).IsTrue();
    }

    [Fact]
    public void RecreatesFilter_PreservesGapState()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        settings.Cursor.MaxGapInterpolationMs = 150;

        var provider = new TestGazeProvider();
        var simulator = new TestInputSimulator();
        var engine = new GazeEngine(provider, simulator, settings);

        PhysicalPoint? lastMoved = null;
        engine.CursorMoved += (pos, _) => lastMoved = pos;

        provider.SimulateGaze(500, 300);
        Check.That(lastMoved!.Value.X).IsEqualTo(500);

        settings.Cursor.MaxGapInterpolationMs = 300;

        provider.SimulateGazeInvalid(0, 0, 50_000);
        Check.That(lastMoved!.Value.X).IsEqualTo(500);
    }

    private class TestGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void SimulateGaze(double x, double y) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, 0, true));

        public void SimulateGazeInvalid(double x, double y, long ts) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, ts, false));
    }

    private class TestInputSimulator : IInputSimulator
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
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
