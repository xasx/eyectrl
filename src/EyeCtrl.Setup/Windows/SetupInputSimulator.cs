namespace EyeCtrl.Setup.Windows;

using System.Runtime.InteropServices;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The one input operation Setup needs: an in-place left-button-up for C3's marker. The engine
/// reuses Core's <see cref="HeldButtonRelease"/>; only the sink is this small Win32 sender, so the
/// 171 MB App/Platform stack is not pulled into Setup for one SendInput call.
/// <para>All other input members throw: a Setup path that tries to click or type is a bug and must
/// fail loudly rather than move the owner's cursor.</para>
/// </summary>
public sealed class SetupInputSimulator : IInputSimulator
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftUp = 0x0004;
    private const int VkLeftButton = 0x01;

    /// <summary>Reads the OS left-button state (the post-release settle probe).</summary>
    public static bool IsLeftButtonDown => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    /// <inheritdoc />
    public void ReleaseLeftButton()
    {
        var input = new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput { Flags = MouseEventLeftUp },
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    /// <inheritdoc />
    public void MoveCursor(PhysicalPoint point) => throw new NotSupportedException();

    /// <inheritdoc />
    public void LeftClick(PhysicalPoint point) => throw new NotSupportedException();

    /// <inheritdoc />
    public void RightClick(PhysicalPoint point) => throw new NotSupportedException();

    /// <inheritdoc />
    public void DoubleClick(PhysicalPoint point) => throw new NotSupportedException();

    /// <inheritdoc />
    public void MouseDown(PhysicalPoint point) => throw new NotSupportedException();

    /// <inheritdoc />
    public void MouseUp(PhysicalPoint point) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Scroll(PhysicalPoint point, int deltaX, int deltaY) => throw new NotSupportedException();

    /// <inheritdoc />
    public void SendKeyPress(ushort virtualKeyCode) => throw new NotSupportedException();

    /// <inheritdoc />
    public void SendKeyDown(ushort virtualKeyCode) => throw new NotSupportedException();

    /// <inheritdoc />
    public void SendKeyUp(ushort virtualKeyCode) => throw new NotSupportedException();

    /// <inheritdoc />
    public void SendText(string text) => throw new NotSupportedException();

    /// <inheritdoc />
    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) =>
        throw new NotSupportedException();

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}

/// <summary>Binds Core's <see cref="HeldButtonRelease"/> to the real marker and the in-place sink.</summary>
public sealed class CoreHeldButtonRecovery : EyeCtrl.Setup.Abstractions.IHeldButtonRecovery
{
    private readonly HeldButtonRelease _release;

    /// <summary>
    /// Creates the production binding over the SETUP profile's marker
    /// (<c>&lt;Setup AppDataRoot&gt;\drag-left-button-held</c>).
    /// </summary>
    /// <param name="paths">The Setup layout. A Release Setup is the installed product's
    /// maintenance tool wherever it runs from, so its <see cref="SetupPaths.AppDataRoot"/> is the
    /// installed App's profile — the same marker the App writes. The parameterless
    /// <see cref="FileHeldButtonMarker"/> default would take the APP rule and look in
    /// <c>EyeCtrl.Dev</c> from a flagless run copy, leaving the owner with a stuck left button.</param>
    public CoreHeldButtonRecovery(SetupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Marker = new FileHeldButtonMarker(
            System.IO.Path.Combine(paths.AppDataRoot, FileHeldButtonMarker.FileName));
        _release = new HeldButtonRelease(
            Marker,
            new SetupInputSimulator(),
            () => SetupInputSimulator.IsLeftButtonDown);
    }

    /// <summary>The marker this recovery reads — the Setup profile's. Internal so the wiring test
    /// can pin the path without touching the real app-data file.</summary>
    internal FileHeldButtonMarker Marker { get; }

    /// <inheritdoc />
    public HeldButtonReleaseResult ReleaseIfMarked() => _release.ReleaseIfMarked();
}
