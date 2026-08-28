namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

public class FakeGazeProvider : IGazeProvider
{
    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }
    public void Start() => IsConnected = true;
    public void Stop() => IsConnected = false;
    public void Dispose() => Stop();
    public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
}

public class FakeInputSimulator : IInputSimulator
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
    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
}
