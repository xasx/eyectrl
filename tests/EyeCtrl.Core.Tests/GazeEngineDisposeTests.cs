namespace EyeCtrl.Core.Tests;

using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeEngineDisposeTests
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

    private static int PropertyChangedSubscriberCount(CursorSettings settings)
    {
        var field = typeof(ObservableObject).GetField(
            nameof(ObservableObject.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (Delegate?)field!.GetValue(settings);
        return handler?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void Dispose_UnsubscribesFromCursorSettingsPropertyChanged()
    {
        var settings = new EyeCtrlSettings();
        int before = PropertyChangedSubscriberCount(settings.Cursor);

        var engine = new GazeEngine(new FakeGazeProvider(), new FakeInputSimulator(), settings);
        Check.That(PropertyChangedSubscriberCount(settings.Cursor)).IsStrictlyGreaterThan(before);

        engine.Dispose();
        Check.That(PropertyChangedSubscriberCount(settings.Cursor)).IsEqualTo(before);
    }
}
