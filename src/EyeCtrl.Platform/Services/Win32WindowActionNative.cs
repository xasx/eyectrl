namespace EyeCtrl.Platform.Services;

using EyeCtrl.Platform.Input;

/// <summary>
/// The production <see cref="IWindowActionNative"/>: the real user32 P/Invokes. Nothing here is
/// called from a test; the seam exists so a fake can stand in.
/// </summary>
internal sealed class Win32WindowActionNative : IWindowActionNative
{
    /// <inheritdoc />
    public bool IsWindow(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    /// <inheritdoc />
    public bool IsIconic(IntPtr hwnd) => NativeMethods.IsIconic(hwnd);

    /// <inheritdoc />
    public bool IsZoomed(IntPtr hwnd) => NativeMethods.IsZoomed(hwnd);

    /// <inheritdoc />
    public bool ShowWindow(IntPtr hwnd, int command) => NativeMethods.ShowWindow(hwnd, command);

    /// <inheritdoc />
    public bool ShowWindowAsync(IntPtr hwnd, int command)
        => NativeMethods.ShowWindowAsync(hwnd, command);

    /// <inheritdoc />
    public uint SendInput(uint inputCount, NativeMethods.INPUT[] inputs, int size)
        => NativeMethods.SendInput(inputCount, inputs, size);

    /// <inheritdoc />
    public bool SetForegroundWindow(IntPtr hwnd) => NativeMethods.SetForegroundWindow(hwnd);
}
