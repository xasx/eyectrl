namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
public interface IInputSimulator
{
    void MoveCursor(ScreenPoint point);
    void LeftClick(ScreenPoint point);
    void RightClick(ScreenPoint point);
    void DoubleClick(ScreenPoint point);
    void MouseDown(ScreenPoint point);
    void MouseUp(ScreenPoint point);
    void Scroll(ScreenPoint point, int deltaX, int deltaY);
    void SendKeyPress(ushort virtualKeyCode);
    void SendKeyDown(ushort virtualKeyCode);
    void SendKeyUp(ushort virtualKeyCode);
    void SendText(string text);
}
