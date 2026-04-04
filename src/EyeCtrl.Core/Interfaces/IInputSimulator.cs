namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Interface for simulating mouse and keyboard input.
/// All point coordinates are in physical screen pixels.
/// </summary>
public interface IInputSimulator
{
    void MoveCursor(PhysicalPoint point);
    void LeftClick(PhysicalPoint point);
    void RightClick(PhysicalPoint point);
    void DoubleClick(PhysicalPoint point);
    void MouseDown(PhysicalPoint point);
    void MouseUp(PhysicalPoint point);
    void Scroll(PhysicalPoint point, int deltaX, int deltaY);
    void SendKeyPress(ushort virtualKeyCode);
    void SendKeyDown(ushort virtualKeyCode);
    void SendKeyUp(ushort virtualKeyCode);
    void SendText(string text);
}
