namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;

public class Win32InputSimulator : IInputSimulator
{
    private static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
    private static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

    private readonly IMouseCoordinateMapper _coordinateMapper;

    public Win32InputSimulator(IMouseCoordinateMapper coordinateMapper)
    {
        _coordinateMapper = coordinateMapper;
    }

    public void MoveCursor(PhysicalPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        SendSingleInput(input);
    }

    public void LeftClick(PhysicalPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendInputs(down, up);
    }

    public void RightClick(PhysicalPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTUP);
        SendInputs(down, up);
    }

    public void DoubleClick(PhysicalPoint point)
    {
        LeftClick(point);
        LeftClick(point);
    }

    public void MouseDown(PhysicalPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        SendSingleInput(input);
    }

    public void MouseUp(PhysicalPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendSingleInput(input);
    }

    public void Scroll(PhysicalPoint point, int deltaX, int deltaY)
    {
        // Clamp delta values to prevent overflow when multiplied by 120
        // Max safe value: int.MaxValue / 120 = 17,895,697
        const int maxSafeDelta = 17_000_000;
        deltaX = Math.Clamp(deltaX, -maxSafeDelta, maxSafeDelta);
        deltaY = Math.Clamp(deltaY, -maxSafeDelta, maxSafeDelta);

        if (deltaY != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_WHEEL);
            input.Union.Mouse.MouseData = deltaY * PlatformConstants.MouseWheelDelta;
            SendSingleInput(input);
        }
        if (deltaX != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_HWHEEL);
            input.Union.Mouse.MouseData = deltaX * PlatformConstants.MouseWheelDelta;
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

    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode)
    {
        // Build list of modifier VK codes to press
        var modifierVks = new List<ushort>();
        if (modifiers.Contains(ModifierKey.Ctrl))
            modifierVks.Add(NativeMethods.VK_CONTROL);
        if (modifiers.Contains(ModifierKey.Alt))
            modifierVks.Add(NativeMethods.VK_MENU);
        if (modifiers.Contains(ModifierKey.Win))
            modifierVks.Add(NativeMethods.VK_LWIN);
        if (modifiers.Contains(ModifierKey.Shift))
            modifierVks.Add(NativeMethods.VK_SHIFT);

        // Build input array: modifier downs + key down + key up + modifier ups (reverse)
        var inputs = new NativeMethods.INPUT[modifierVks.Count * 2 + 2];
        int idx = 0;

        // Modifier key-downs
        foreach (var vk in modifierVks)
            inputs[idx++] = CreateKeyboardInput(vk, 0);

        // Main key
        inputs[idx++] = CreateKeyboardInput(virtualKeyCode, 0);
        inputs[idx++] = CreateKeyboardInput(virtualKeyCode, NativeMethods.KEYEVENTF_KEYUP);

        // Modifier key-ups (reverse order)
        for (int i = modifierVks.Count - 1; i >= 0; i--)
            inputs[idx++] = CreateKeyboardInput(modifierVks[i], NativeMethods.KEYEVENTF_KEYUP);

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// Convert a character to its virtual key code and required modifier state
    /// using the current keyboard layout.
    /// Returns (0, false) if the character cannot be mapped.
    /// </summary>
    public static (ushort VirtualKeyCode, bool RequiresShift) CharToVirtualKey(char c)
    {
        short result = NativeMethods.VkKeyScanW((ushort)c);
        if (result == -1) return (0, false);
        ushort vk = (ushort)(result & 0xFF);
        bool requiresShift = ((result >> 8) & 0x01) != 0;
        return (vk, requiresShift);
    }

    private NativeMethods.INPUT CreateMouseInput(PhysicalPoint point, uint flags)
    {
        // Get screen dimensions
        var screenWidth = ScreenWidth;
        var screenHeight = ScreenHeight;

        // Use coordinate mapper to normalize coordinates
        var (normalizedX, normalizedY) = _coordinateMapper.NormalizeScreenCoordinate(point, screenWidth, screenHeight);

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
