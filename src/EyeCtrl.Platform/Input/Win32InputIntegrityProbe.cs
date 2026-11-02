namespace EyeCtrl.Platform.Input;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Interop;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// R1's Win32-backed, read-only token inspection. It answers three questions — our own token, the
/// foreground window's process, and the process under a target point — and caches each answer until
/// the underlying window changes, so a 30–60 Hz input path never re-queries a stable foreground.
///
/// <para>
/// All failures degrade to <see cref="ProcessTokenFacts.Unknown"/>; only a genuine
/// <c>ERROR_ACCESS_DENIED</c> is recorded as <see cref="ProcessTokenFacts.AccessDenied"/>, because
/// that is the one failure that itself proves the process sits above ours. A process that exited
/// between the window lookup and the token read must not be reported as elevated.
/// </para>
/// </summary>
public sealed class Win32InputIntegrityProbe : IInputIntegrityProbe
{
    private readonly object _gate = new();

    private bool _selfRead;
    private SelfTokenFacts _self;

    private bool _foregroundRead;
    private IntPtr _foregroundWindow;
    private ProcessTokenFacts _foregroundFacts;

    private bool _pointRead;
    private IntPtr _pointWindow;
    private ProcessTokenFacts _pointFacts;

    /// <inheritdoc />
    public SelfTokenFacts ReadSelf()
    {
        lock (_gate)
        {
            if (_selfRead) return _self;

            _selfRead = true;
            _self = ReadOwnToken();
            return _self;
        }
    }

    /// <inheritdoc />
    public ProcessTokenFacts InspectForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        lock (_gate)
        {
            if (_foregroundRead && hwnd == _foregroundWindow)
                return _foregroundFacts;
        }

        var facts = FromWindow(hwnd);

        lock (_gate)
        {
            _foregroundRead = true;
            _foregroundWindow = hwnd;
            _foregroundFacts = facts;
        }
        return facts;
    }

    /// <inheritdoc />
    public ProcessTokenFacts InspectPoint(PhysicalPoint point)
    {
        var raw = InputIntegrityNativeMethods.WindowFromPoint(
            new NativeMethods.POINT
            {
                X = (int)Math.Round(point.X, MidpointRounding.AwayFromZero),
                Y = (int)Math.Round(point.Y, MidpointRounding.AwayFromZero),
            });

        // Resolve an owned dialog/control to the top-level owner: its process is the one whose
        // token the user means by "the window I am clicking".
        var hwnd = raw == IntPtr.Zero
            ? IntPtr.Zero
            : InputIntegrityNativeMethods.GetAncestor(raw, InputIntegrityNativeMethods.GaRootOwner);

        lock (_gate)
        {
            if (_pointRead && hwnd == _pointWindow)
                return _pointFacts;
        }

        var facts = FromWindow(hwnd == IntPtr.Zero ? raw : hwnd);

        lock (_gate)
        {
            _pointRead = true;
            _pointWindow = hwnd;
            _pointFacts = facts;
        }
        return facts;
    }

    /// <inheritdoc />
    public ProcessTokenFacts InspectWindow(IntPtr hwnd) => FromWindow(hwnd);

    private static SelfTokenFacts ReadOwnToken()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            if (!InputIntegrityNativeMethods.OpenProcessToken(
                    process.Handle, InputIntegrityNativeMethods.TokenQuery, out var token))
            {
                return new SelfTokenFacts(null, false, false);
            }

            using (token)
            {
                return new SelfTokenFacts(
                    ReadIntegrityLevel(token),
                    ReadDword(token, InputIntegrityNativeMethods.TokenElevationInfo) is 1,
                    ReadDword(token, InputIntegrityNativeMethods.TokenUIAccessInfo) is 1);
            }
        }
        catch (Exception)
        {
            return new SelfTokenFacts(null, false, false);
        }
    }

    private static ProcessTokenFacts FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return ProcessTokenFacts.Unknown;

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? ProcessTokenFacts.Unknown : FromProcessId(pid);
        }
        catch (Exception)
        {
            return ProcessTokenFacts.Unknown;
        }
    }

    private static ProcessTokenFacts FromProcessId(uint pid)
    {
        using var process = ProcessIdentity.OpenProcess(
            InputIntegrityNativeMethods.ProcessQueryLimitedInformation, false, pid);

        if (process.IsInvalid)
            return DeniedOrUnknown();

        var name = TryProcessName(process);

        if (!InputIntegrityNativeMethods.OpenProcessToken(
                process.DangerousGetHandle(), InputIntegrityNativeMethods.TokenQuery, out var token))
        {
            var denied = Marshal.GetLastWin32Error() == InputIntegrityNativeMethods.ErrorAccessDenied;
            return new ProcessTokenFacts(name, null, null, null, denied);
        }

        using (token)
        {
            return new ProcessTokenFacts(
                name,
                ReadIntegrityLevel(token),
                ReadDword(token, InputIntegrityNativeMethods.TokenElevationInfo) is 1,
                ReadDword(token, InputIntegrityNativeMethods.TokenUIAccessInfo) is 1,
                AccessDenied: false);
        }
    }

    /// <summary>
    /// The one place a failed <c>OpenProcess</c> is classified. Only ACCESS_DENIED means "above us";
    /// a process that exited gives another error and stays Unknown.
    /// </summary>
    private static ProcessTokenFacts DeniedOrUnknown()
    {
        var error = Marshal.GetLastWin32Error();
        return error == InputIntegrityNativeMethods.ErrorAccessDenied
            ? new ProcessTokenFacts(null, null, null, null, AccessDenied: true)
            : ProcessTokenFacts.Unknown;
    }

    private static string? TryProcessName(SafeProcessHandle process)
    {
        try
        {
            var buffer = new char[1024];
            uint length = (uint)buffer.Length;
            if (!ProcessIdentity.QueryFullProcessImageName(
                    process.DangerousGetHandle(), 0, buffer, ref length))
            {
                return null;
            }

            return Path.GetFileName(new string(buffer, 0, (int)length));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ReadIntegrityLevel(SafeAccessTokenHandle token)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (!InputIntegrityNativeMethods.GetTokenInformation(
                    token, InputIntegrityNativeMethods.TokenIntegrityLevel,
                    buffer, 64, out _))
            {
                return null;
            }

            return InputIntegrityNativeMethods.ReadIntegrityLevel(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ReadDword(SafeAccessTokenHandle token, int informationClass)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            if (!InputIntegrityNativeMethods.GetTokenInformation(
                    token, informationClass, buffer, 4, out _))
            {
                return null;
            }

            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
