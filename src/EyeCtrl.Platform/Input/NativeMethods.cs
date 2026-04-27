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

    internal const ushort VK_SHIFT = VirtualKeys.Shift;
    internal const ushort VK_CONTROL = VirtualKeys.Control;
    internal const ushort VK_MENU = VirtualKeys.Alt;
    internal const ushort VK_LWIN = VirtualKeys.LWin;
    internal const ushort VK_BACK = VirtualKeys.Back;
    internal const ushort VK_TAB = VirtualKeys.Tab;
    internal const ushort VK_RETURN = VirtualKeys.Return;
    internal const ushort VK_SPACE = VirtualKeys.Space;
    internal const ushort VK_ESCAPE = VirtualKeys.Escape;

    // Function keys
    internal const ushort VK_F1 = VirtualKeys.F1;
    internal const ushort VK_F2 = VirtualKeys.F2;
    internal const ushort VK_F3 = VirtualKeys.F3;
    internal const ushort VK_F4 = VirtualKeys.F4;
    internal const ushort VK_F5 = VirtualKeys.F5;
    internal const ushort VK_F6 = VirtualKeys.F6;
    internal const ushort VK_F7 = VirtualKeys.F7;
    internal const ushort VK_F8 = VirtualKeys.F8;
    internal const ushort VK_F9 = VirtualKeys.F9;
    internal const ushort VK_F10 = VirtualKeys.F10;
    internal const ushort VK_F11 = VirtualKeys.F11;
    internal const ushort VK_F12 = VirtualKeys.F12;

    // Navigation keys
    internal const ushort VK_PRIOR = VirtualKeys.Prior;
    internal const ushort VK_NEXT = VirtualKeys.Next;
    internal const ushort VK_END = VirtualKeys.End;
    internal const ushort VK_HOME = VirtualKeys.Home;
    internal const ushort VK_LEFT = VirtualKeys.Left;
    internal const ushort VK_UP = VirtualKeys.Up;
    internal const ushort VK_RIGHT = VirtualKeys.Right;
    internal const ushort VK_DOWN = VirtualKeys.Down;
    internal const ushort VK_SNAPSHOT = VirtualKeys.Snapshot;
    internal const ushort VK_INSERT = VirtualKeys.Insert;
    internal const ushort VK_DELETE = VirtualKeys.Delete;

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
