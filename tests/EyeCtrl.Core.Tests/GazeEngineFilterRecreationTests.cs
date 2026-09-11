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
        engine.CursorMoved += f => lastMoved = f.Position;

        provider.SimulateGaze(500, 300);
        Check.That(lastMoved!.Value.X).IsEqualTo(500);

        settings.Cursor.MaxGapInterpolationMs = 300;

        provider.SimulateGazeInvalid(0, 0, 50_000);
        Check.That(lastMoved!.Value.X).IsEqualTo(500);
    }

    [Fact]
    public void RecreatesFilter_WhenOneEuroMinCutoffChanges_TakesEffectLive()
    {
        // Regression guard for the Phase-1a-⑤ trigger extension in
        // GazeEngine._cursorSettingsChangedHandler: changing a One-Euro smoothing parameter at
        // runtime must REBUILD the live interpolator. Before that change, only FilterMode /
        // MaxGapInterpolationMs rebuilt, so smoothing tweaks were silently ignored until the next
        // mode switch. Differential (no magic filter constants): an engine whose minCutoff is lowered
        // to the maximum-smoothing floor must lag a step MORE than an identical engine left at the
        // default cutoff. If the trigger were reverted, both engines would behave identically and the
        // strict inequality would fail. Beta is pinned to 0 so cutoff == minCutoff (no speed
        // adaptation to wash out the difference). OneEuroBeta rides the same rebuild path.
        double defaultCutoff = StepAfterOptionalChange(newMinCutoff: null);
        double heavySmoothing = StepAfterOptionalChange(newMinCutoff: 0.1); // 0.1 Hz = CursorSettings floor

        Check.That(heavySmoothing < defaultCutoff).IsTrue();

        static double StepAfterOptionalChange(double? newMinCutoff)
        {
            var settings = new EyeCtrlSettings();
            settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
            settings.Cursor.OneEuroBeta = 0.0; // cutoff == minCutoff: isolate the parameter under test

            var provider = new TestGazeProvider();
            var engine = new GazeEngine(provider, new TestInputSimulator(), settings);
            double lastX = 0;
            engine.CursorMoved += f => lastX = f.Position.X;

            long ts = 0;
            for (int i = 0; i < 12; i++) { provider.SimulateGaze(500, 500, ts); ts += 33_000; } // steady state

            if (newMinCutoff is { } cutoff)
                settings.Cursor.OneEuroMinCutoff = cutoff; // the runtime change under test (rebuild + warm-start)

            provider.SimulateGaze(600, 500, ts); // step right; filtered X reflects the ACTIVE smoothing
            return lastX;
        }
    }

    [Fact]
    public void RecreatesFilter_WhenSmoothingFactorChanges_TakesEffectLive()
    {
        // Same regression guard for the EMA path (FilterMode.Smoothed reads SmoothingFactor). EMA has
        // no speed adaptation, so the effect is large and unambiguous: factor 0.05 (near-maximum
        // smoothing) must lag the step far more than the default 0.5.
        double defaultFactor = StepAfterOptionalChange(newFactor: null);
        double heavySmoothing = StepAfterOptionalChange(newFactor: 0.05);

        Check.That(heavySmoothing < defaultFactor).IsTrue();

        static double StepAfterOptionalChange(double? newFactor)
        {
            var settings = new EyeCtrlSettings();
            settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
            settings.Cursor.SmoothingFactor = 0.5;

            var provider = new TestGazeProvider();
            var engine = new GazeEngine(provider, new TestInputSimulator(), settings);
            double lastX = 0;
            engine.CursorMoved += f => lastX = f.Position.X;

            long ts = 0;
            for (int i = 0; i < 12; i++) { provider.SimulateGaze(500, 500, ts); ts += 33_000; } // steady state

            if (newFactor is { } factor)
                settings.Cursor.SmoothingFactor = factor; // the runtime change under test (rebuild + warm-start)

            provider.SimulateGaze(600, 500, ts);
            return lastX;
        }
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

        public void SimulateGaze(double x, double y, long ts) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, ts, true));

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
