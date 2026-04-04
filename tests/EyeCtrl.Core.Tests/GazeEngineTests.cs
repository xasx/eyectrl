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
        public List<(string Action, PhysicalPoint Point)> Actions { get; } = new();
        public void MoveCursor(PhysicalPoint p) => Actions.Add(("Move", p));
        public void LeftClick(PhysicalPoint p) => Actions.Add(("LeftClick", p));
        public void RightClick(PhysicalPoint p) => Actions.Add(("RightClick", p));
        public void DoubleClick(PhysicalPoint p) => Actions.Add(("DoubleClick", p));
        public void MouseDown(PhysicalPoint p) => Actions.Add(("MouseDown", p));
        public void MouseUp(PhysicalPoint p) => Actions.Add(("MouseUp", p));
        public void Scroll(PhysicalPoint p, int dx, int dy) => Actions.Add(("Scroll", p));
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
    public void ActionRequested_LeftClick_FiresEvent()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        // Subscribe to ActionRequested (App.xaml.cs handles execution via message bus)
        InteractionAction? firedAction = null;
        engine.InteractionManager.ActionRequested += (action, _) => firedAction = action;

        // Directly trigger action through the interaction manager
        engine.InteractionManager.OnFixationStarted(new PhysicalPoint(200, 200));
        engine.InteractionManager.OnActivationButtonDwelled();
        engine.InteractionManager.OnActionSelected(InteractionAction.LeftClick);

        Assert.Equal(InteractionAction.LeftClick, firedAction);
    }
}
