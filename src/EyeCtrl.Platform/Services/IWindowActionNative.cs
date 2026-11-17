namespace EyeCtrl.Platform.Services;

using EyeCtrl.Platform.Input;

/// <summary>
/// The Win32 calls the window actions need, behind a seam.
///
/// <para>
/// These methods run on <see cref="WindowActionScheduler"/>'s worker, never on the callers' thread,
/// and a test must be able to make one of them hang without touching a real window. The production
/// implementation (<see cref="Win32WindowActionNative"/>) forwards to the real P/Invokes; tests
/// substitute a fake that records the call order and can block. No test ever reaches
/// <c>SendInput</c>.
/// </para>
/// </summary>
internal interface IWindowActionNative
{
    /// <summary><c>IsWindow</c>: re-checks a one-shot snapshot handle at the point of use.</summary>
    bool IsWindow(IntPtr hwnd);

    /// <summary><c>IsIconic</c>: reads whether the target is minimised.</summary>
    bool IsIconic(IntPtr hwnd);

    /// <summary><c>IsZoomed</c>: reads whether the target is maximised.</summary>
    bool IsZoomed(IntPtr hwnd);

    /// <summary><c>ShowWindow</c>: synchronous on the calling (worker) thread.</summary>
    bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>
    /// <c>ShowWindowAsync</c>: posts the request without waiting for the target's thread. The
    /// Quick Actions minimise/maximise use this directly; they need no ordering or foreground
    /// grant, so they need no worker either.
    /// </summary>
    bool ShowWindowAsync(IntPtr hwnd, int command);

    /// <summary><c>SendInput</c>: the ALT foreground grant, one atomic down+up pair.</summary>
    uint SendInput(uint inputCount, NativeMethods.INPUT[] inputs, int size);

    /// <summary><c>SetForegroundWindow</c>: the switch itself.</summary>
    bool SetForegroundWindow(IntPtr hwnd);

    /// <summary><c>GetForegroundWindow</c>: the re-check after a switch (and the fallback's poll).</summary>
    IntPtr GetForegroundWindow();

    /// <summary><c>GetShellWindow</c>: identifies the desktop shell, which the escape path never
    /// minimises or restores.</summary>
    IntPtr GetShellWindow();
}
