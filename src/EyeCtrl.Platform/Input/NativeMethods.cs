namespace EyeCtrl.Platform.Input;

using System.Runtime.InteropServices;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial short VkKeyScanW(ushort ch);

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12;    // Alt
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_SPACE = 0x20;
    internal const ushort VK_ESCAPE = 0x1B;

    // Function keys
    internal const ushort VK_F1 = 0x70;
    internal const ushort VK_F2 = 0x71;
    internal const ushort VK_F3 = 0x72;
    internal const ushort VK_F4 = 0x73;
    internal const ushort VK_F5 = 0x74;
    internal const ushort VK_F6 = 0x75;
    internal const ushort VK_F7 = 0x76;
    internal const ushort VK_F8 = 0x77;
    internal const ushort VK_F9 = 0x78;
    internal const ushort VK_F10 = 0x79;
    internal const ushort VK_F11 = 0x7A;
    internal const ushort VK_F12 = 0x7B;

    // Navigation keys
    internal const ushort VK_PRIOR = 0x21;     // Page Up
    internal const ushort VK_NEXT = 0x22;      // Page Down
    internal const ushort VK_END = 0x23;
    internal const ushort VK_HOME = 0x24;
    internal const ushort VK_LEFT = 0x25;      // Arrow Left
    internal const ushort VK_UP = 0x26;        // Arrow Up
    internal const ushort VK_RIGHT = 0x27;     // Arrow Right
    internal const ushort VK_DOWN = 0x28;      // Arrow Down
    internal const ushort VK_SNAPSHOT = 0x2C;  // Print Screen
    internal const ushort VK_INSERT = 0x2D;
    internal const ushort VK_DELETE = 0x2E;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
