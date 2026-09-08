namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;

/// <summary>
/// Sends mouse/keyboard input via Win32 SendInput.
/// <para>
/// SINGLE-PRIMARY-MONITOR ASSUMPTION: absolute mouse coordinates are normalized against
/// SM_CXSCREEN/SM_CYSCREEN, which describe the PRIMARY monitor only. On multi-monitor
/// setups, gaze-driven cursor positioning is therefore only correct on the primary
/// display (matching the eye tracker's single-screen calibration). Extending to
/// multiple monitors would require MOUSEEVENTF_VIRTUALDESK + virtual-screen metrics.
/// </para>
/// </summary>
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

    public void MiddleClick(PhysicalPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_MIDDLEDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_MIDDLEUP);
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
        deltaX = ClampScrollDelta(deltaX);
        deltaY = ClampScrollDelta(deltaY);

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

    public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey)
    {
        uint flags = isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
        var down = CreateKeyboardInput(virtualKeyCode, flags);
        var up = CreateKeyboardInput(virtualKeyCode, flags | NativeMethods.KEYEVENTF_KEYUP);
        SendInputs(down, up);
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
        SendModifiedKeyPress(modifiers, virtualKeyCode, isExtendedKey: false);
    }

    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
    {
        var inputs = BuildModifiedKeySequence(modifiers, virtualKeyCode, isExtendedKey);
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// Convert a character to its virtual key code and required modifiers
    /// using the current keyboard layout. The required modifiers indicate
    /// which keys must be held to produce this character (e.g., '!' requires Shift,
    /// AltGr characters require Ctrl+Alt on some layouts).
    /// Returns (0, empty) if the character cannot be mapped.
    /// </summary>
    public static (ushort VirtualKeyCode, IReadOnlyList<ModifierKey> RequiredModifiers) CharToVirtualKey(char c)
    {
        short result = NativeMethods.VkKeyScanW((ushort)c);
        if (result == -1) return (0, Array.Empty<ModifierKey>());

        ushort vk = (ushort)(result & 0xFF);
        byte modifierBits = (byte)((result >> 8) & 0xFF);
        var required = new List<ModifierKey>(3);

        if ((modifierBits & 0x01) != 0)
            required.Add(ModifierKey.Shift);
        if ((modifierBits & 0x02) != 0)
            required.Add(ModifierKey.Ctrl);
        if ((modifierBits & 0x04) != 0)
            required.Add(ModifierKey.Alt);

        return (vk, required);
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

    internal static int ClampScrollDelta(int value)
    {
        // Clamp delta values to prevent overflow when multiplied by 120.
        // Max safe value: int.MaxValue / 120 = 17,895,697.
        const int maxSafeDelta = 17_000_000;
        return Math.Clamp(value, -maxSafeDelta, maxSafeDelta);
    }

    internal static NativeMethods.INPUT[] BuildModifiedKeySequence(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
    {
        var modifierVks = new List<ushort>();
        if (modifiers.Contains(ModifierKey.Ctrl))
            modifierVks.Add(NativeMethods.VK_CONTROL);
        if (modifiers.Contains(ModifierKey.Alt))
            modifierVks.Add(NativeMethods.VK_MENU);
        if (modifiers.Contains(ModifierKey.Win))
            modifierVks.Add(NativeMethods.VK_LWIN);
        if (modifiers.Contains(ModifierKey.Shift))
            modifierVks.Add(NativeMethods.VK_SHIFT);

        var inputs = new NativeMethods.INPUT[modifierVks.Count * 2 + 2];
        int idx = 0;

        // Modifier key-downs (extended key flag for Win key)
        foreach (var vk in modifierVks)
        {
            uint flags = vk == NativeMethods.VK_LWIN ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
            inputs[idx++] = CreateKeyboardInput(vk, flags);
        }

        // Main key (with extended key flag if specified)
        uint mainKeyFlags = isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
        inputs[idx++] = CreateKeyboardInput(virtualKeyCode, mainKeyFlags);
        inputs[idx++] = CreateKeyboardInput(virtualKeyCode, mainKeyFlags | NativeMethods.KEYEVENTF_KEYUP);

        // Modifier key-ups (reverse order, with extended key flag)
        for (int i = modifierVks.Count - 1; i >= 0; i--)
        {
            uint flags = modifierVks[i] == NativeMethods.VK_LWIN
                ? NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY
                : NativeMethods.KEYEVENTF_KEYUP;
            inputs[idx++] = CreateKeyboardInput(modifierVks[i], flags);
        }

        return inputs;
    }

    private static readonly int s_inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    // MoveCursor runs per gaze frame (30-60 Hz); a [ThreadStatic] one-element buffer
    // makes the single-input path allocation-free. ThreadStatic (not a shared static)
    // because clicks can be sent from the UI thread while gaze moves on the poll thread.
    [ThreadStatic]
    private static NativeMethods.INPUT[]? t_singleInput;

    private static void SendSingleInput(NativeMethods.INPUT input)
    {
        var buffer = t_singleInput ??= new NativeMethods.INPUT[1];
        buffer[0] = input;
        NativeMethods.SendInput(1, buffer, s_inputSize);
    }

    private static void SendInputs(params NativeMethods.INPUT[] inputs)
    {
        NativeMethods.SendInput((uint)inputs.Length, inputs, s_inputSize);
    }
}
