namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class GazeEngineTests
{
    private class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private class FakeInputSimulator : IInputSimulator
    {
        public List<(string Action, ScreenPoint Point)> Actions { get; } = new();
        public void MoveCursor(ScreenPoint p) => Actions.Add(("Move", p));
        public void LeftClick(ScreenPoint p) => Actions.Add(("LeftClick", p));
        public void RightClick(ScreenPoint p) => Actions.Add(("RightClick", p));
        public void DoubleClick(ScreenPoint p) => Actions.Add(("DoubleClick", p));
        public void MouseDown(ScreenPoint p) => Actions.Add(("MouseDown", p));
        public void MouseUp(ScreenPoint p) => Actions.Add(("MouseUp", p));
        public void Scroll(ScreenPoint p, int dx, int dy) => Actions.Add(("Scroll", p));
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
    }

    [Fact]
    public void ProcessGaze_MovesCursor()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        Assert.Contains(simulator.Actions, a => a.Action == "Move");
    }

    [Fact]
    public void ActionRequested_LeftClick_SimulatesClick()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        // Directly trigger action through the interaction manager
        engine.InteractionManager.OnFixationStarted(new ScreenPoint(200, 200));
        engine.InteractionManager.OnActivationButtonDwelled();
        engine.InteractionManager.OnActionSelected(InteractionAction.LeftClick);

        Assert.Contains(simulator.Actions, a => a.Action == "LeftClick");
    }
}
