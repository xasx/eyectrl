namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;

public class Win32InputSimulator : IInputSimulator
{
    private static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
    private static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

    public void MoveCursor(ScreenPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        SendSingleInput(input);
    }

    public void LeftClick(ScreenPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendInputs(down, up);
    }

    public void RightClick(ScreenPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTUP);
        SendInputs(down, up);
    }

    public void DoubleClick(ScreenPoint point)
    {
        LeftClick(point);
        LeftClick(point);
    }

    public void MouseDown(ScreenPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        SendSingleInput(input);
    }

    public void MouseUp(ScreenPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendSingleInput(input);
    }

    public void Scroll(ScreenPoint point, int deltaX, int deltaY)
    {
        // Clamp delta values to prevent overflow when multiplied by 120
        // Max safe value: int.MaxValue / 120 = 17,895,697
        const int maxSafeDelta = 17_000_000;
        deltaX = Math.Clamp(deltaX, -maxSafeDelta, maxSafeDelta);
        deltaY = Math.Clamp(deltaY, -maxSafeDelta, maxSafeDelta);

        if (deltaY != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_WHEEL);
            input.Union.Mouse.MouseData = deltaY * 120;
            SendSingleInput(input);
        }
        if (deltaX != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_HWHEEL);
            input.Union.Mouse.MouseData = deltaX * 120;
            SendSingleInput(input);
        }
    }

    public void SendKeyPress(ushort virtualKeyCode)
    {
        SendKeyDown(virtualKeyCode);
        SendKeyUp(virtualKeyCode);
    }

    public void SendKeyDown(ushort virtualKeyCode)
    {
        var input = CreateKeyboardInput(virtualKeyCode, 0);
        SendSingleInput(input);
    }

    public void SendKeyUp(ushort virtualKeyCode)
    {
        var input = CreateKeyboardInput(virtualKeyCode, NativeMethods.KEYEVENTF_KEYUP);
        SendSingleInput(input);
    }

    public void SendText(string text)
    {
        foreach (char c in text)
        {
            var down = new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Union = new NativeMethods.INPUTUNION
                {
                    Keyboard = new NativeMethods.KEYBDINPUT
                    {
                        VirtualKey = 0,
                        ScanCode = c,
                        Flags = NativeMethods.KEYEVENTF_UNICODE
                    }
                }
            };
            var up = down;
            up.Union.Keyboard.Flags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
            SendInputs(down, up);
        }
    }

    private static NativeMethods.INPUT CreateMouseInput(ScreenPoint point, uint flags)
    {
        // Validate coordinates are finite (not NaN or Infinity)
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("Screen coordinates must be finite numbers.", nameof(point));

        // Get screen dimensions and validate they're positive
        var screenWidth = ScreenWidth;
        var screenHeight = ScreenHeight;
        if (screenWidth <= 0 || screenHeight <= 0)
            throw new InvalidOperationException("Unable to get valid screen dimensions.");

        // Clamp coordinates to valid screen bounds (0 to screen size)
        // This prevents overflow and ensures cursor stays on screen
        var clampedX = Math.Clamp(point.X, 0, screenWidth);
        var clampedY = Math.Clamp(point.Y, 0, screenHeight);

        // Normalize to 0-65535 range with rounding for better accuracy
        // Using Math.Round instead of truncating cast
        int normalizedX = (int)Math.Round((clampedX * 65535) / screenWidth);
        int normalizedY = (int)Math.Round((clampedY * 65535) / screenHeight);

        // Final clamp to ensure we're in the valid range after rounding
        normalizedX = Math.Clamp(normalizedX, 0, 65535);
        normalizedY = Math.Clamp(normalizedY, 0, 65535);

        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            Union = new NativeMethods.INPUTUNION
            {
                Mouse = new NativeMethods.MOUSEINPUT { Dx = normalizedX, Dy = normalizedY, Flags = flags }
            }
        };
    }

    private static NativeMethods.INPUT CreateKeyboardInput(ushort vk, uint flags)
    {
        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Union = new NativeMethods.INPUTUNION
            {
                Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = vk, Flags = flags }
            }
        };
    }

    private static void SendSingleInput(NativeMethods.INPUT input)
    {
        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendInputs(params NativeMethods.INPUT[] inputs)
    {
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
