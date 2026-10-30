namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Services;
using EyeCtrl.Platform.Input;

public sealed class ProcessController
{
    private IProcessHandle? _process;
    public IProcessHandle? BoundProcess => _process;

    /// <summary>
    /// Captured at launch/adopt. RESERVED for a future strict-mode <see cref="Ipc.ClientIdentityBinder"/>
    /// upgrade (PID-reuse protection via creation-time comparison). Currently captured-but-unread.
    /// </summary>
    public DateTime? StartTimeUtc { get; private set; }
    public string? ExePath { get; private set; }

    /// <summary>
    /// True when the supervisor itself initiated the most recent <see cref="ForceKill"/>. The Exited-event
    /// handler checks this to avoid double-counting: a supervisor-initiated kill is the EXPECTED consequence
    /// of a hang/gaze-death/startup-grace recovery that ALREADY booked a strike via GoRecovering. Feeding a
    /// second ProcessExited for it would count the same incident twice and halve the crash budget.
    /// Cleared on the next Launch/Adopt.
    /// </summary>
    public bool KillInitiated { get; private set; }

    /// <summary>Raised when the bound process exits. Detached on <see cref="Unbind"/>/rebind, so an
    /// exit of a process the supervisor no longer watches is not reported (ticket 06-B).</summary>
    public event Action<IProcessHandle>? BoundProcessExited;

    private Action? _onExited;

    private readonly Func<ProcessStartInfo, IProcessHandle?> _processFactory;
    private readonly Func<int, IProcessHandle?> _adoptFactory;

    /// <summary>
    /// C3: releases a drag button the killed App left held. Null in the test constructor on
    /// purpose — these tests call <see cref="ForceKill"/>, and a default that touched the real
    /// marker and <c>SendInput</c> would let a fake-driven unit test release the owner's physical
    /// mouse. Production binds the real marker + sink in the parameterless constructor.
    /// </summary>
    private readonly HeldButtonRelease? _heldButtonRelease;

    /// <summary>
    /// C3 (fixup-01 F1): true when this controller is wired to release a held drag button. The
    /// production binding used to be observable only by sending input, so a refactor that passed
    /// null through the constructor chain stayed green. This is the pin: constructing the
    /// parameterless controller is IO-free and input-free (the marker stores a path, the simulator
    /// stores a mapper), and a test asserts this is true WITHOUT ever calling
    /// <see cref="ForceKill"/> on that instance.
    /// </summary>
    internal bool HasHeldButtonRelease => _heldButtonRelease is not null;

    public ProcessController()
        // Process.Start is documented to return null when no new process was started; the null-tolerant
        // factory signature keeps that out of Launch rather than turning it into a NullReferenceException.
        : this(psi => Process.Start(psi) is { } started ? new RealProcessHandle(started) : null,
               pid =>
               {
                   try { return new RealProcessHandle(Process.GetProcessById(pid)); }
                   catch { return null; }
               },
               // C3: the SAME marker file the App's DragCoordinator writes and its startup backstop
               // reads. A force-kill is the one path that runs none of the App's release code.
               new HeldButtonRelease(
                   new FileHeldButtonMarker(),
                   new Win32InputSimulator(new MouseCoordinateMapper()),
                   LeftButtonProbe.IsLeftButtonDown))
    {
    }

    public ProcessController(Func<ProcessStartInfo, IProcessHandle?> processFactory, Func<int, IProcessHandle?> adoptFactory)
        // No held-button recovery: see _heldButtonRelease. Production uses the constructor above;
        // tests inject one with fakes through the three-argument overload.
        : this(processFactory, adoptFactory, heldButtonRelease: null)
    {
    }

    /// <summary>The full constructor: lets tests observe the held-button recovery with a fake marker
    /// and a recording input sink (never <c>SendInput</c>).</summary>
    public ProcessController(
        Func<ProcessStartInfo, IProcessHandle?> processFactory,
        Func<int, IProcessHandle?> adoptFactory,
        HeldButtonRelease? heldButtonRelease)
    {
        _processFactory = processFactory;
        _adoptFactory = adoptFactory;
        _heldButtonRelease = heldButtonRelease;
    }

    /// <summary>Starts the App and binds it.</summary>
    /// <param name="exePath">The App exe.</param>
    /// <param name="paused">Adds <c>--paused</c> (the FallenBack retry).</param>
    /// <param name="uiLanguage">A language the caretaker chose while the App was down (T05b): adds
    /// <c>--ui-language &lt;token&gt;</c>, which the App saves at startup. A launch that loses the
    /// App's single-instance guard exits before it reads the flag, so the caller keeps the choice
    /// pending until a re-read finds it saved.</param>
    public void Launch(string exePath, bool paused, UiLanguage? uiLanguage = null)
    {
        ReleasePrevious();
        KillInitiated = false;
        // The path we were handed IS the launched image; no need to ask the OS for it (and
        // Process.MainModule on a just-started process is one more thing that can throw).
        ExePath = exePath;
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (paused) psi.ArgumentList.Add("--paused");
        if (uiLanguage is { } language)
        {
            // T01's single token mapping; the App parses it with the same table.
            psi.ArgumentList.Add("--ui-language");
            psi.ArgumentList.Add(UiLanguageTokens.ToCommandLine(language));
        }
        Bind(_processFactory(psi));
        StartTimeUtc = _process?.StartTime;
    }

    /// <summary>
    /// Binds an externally-launched process by PID. Returns false if the PID is not running / cannot be opened.
    /// </summary>
    /// <remarks>
    /// <para>Must not throw: the caller is a <c>Dispatcher.BeginInvoke</c> heartbeat callback
    /// (<c>Program.OnHeartbeatReceived</c>), where an escaping exception becomes a
    /// DispatcherUnhandledException and tears down the supervisor itself. Both hazards are contained
    /// below the call: <see cref="Process.GetProcessById(int)"/> throws <see cref="ArgumentException"/>
    /// if the process isn't running — swallowed by the adopt factory; <see cref="Process.MainModule"/>
    /// throws on a 64/32-bit bitness mismatch or access denied — swallowed by
    /// <c>RealProcessHandle.ExePath</c>, which yields null instead. A null <see cref="ExePath"/> is
    /// acceptable because the binder does its own exe-path authentication via
    /// <c>QueryFullProcessImageName</c>.</para>
    /// </remarks>
    public bool Adopt(int pid)
    {
        ReleasePrevious();
        KillInitiated = false;
        Bind(_adoptFactory(pid));
        if (_process != null)
        {
            ExePath = _process.ExePath;
            StartTimeUtc = _process.StartTime;
        }
        return _process != null;
    }

    public bool IsAlive => _process is { HasExited: false };

    public void ForceKill()
    {
        // Set BEFORE the HasExited check: recovery is an INTENT, not "did the kill actually execute."
        // If the process exits on its own between stale-detection and this call, HasExited is already
        // true and the kill is skipped — but the late Process.Exited (marshaled via BeginInvoke, so it
        // runs after this synchronous block) would otherwise double-count. KillInitiated covers that.
        KillInitiated = true;
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                // The kill can throw (the process raced natural exit between the HasExited check
                // and Kill, or access was denied). ForceKill runs on the supervisor's dispatcher,
                // which has no other handler — the WATCHDOG would die exactly when it is supposed
                // to be recovering the app. Raced natural exit still feeds ProcessExited via the
                // late Process.Exited; ACCESS-DENIED does not (no Exited will arrive, and
                // KillInitiated stays set until the next Launch/Adopt clears it, suppressing the
                // next strike) — unreachable for a same-user child in practice.
                Serilog.Log.Error(ex, "ForceKill: Process.Kill threw (raced natural exit or access denied).");
            }
        }

        ReleaseHeldButtonAfterKill();
    }

    /// <summary>
    /// C3: a force-kill is <c>TerminateProcess</c>-fast — the App's drop/cancel/dispose/fatal-exit
    /// releases, and even the crash handlers, never run — so a drag in progress leaves the OS left
    /// button down. If the cross-process marker the App writes on press is still there, the killed
    /// process owed exactly one button-up: send it in place, clear the marker, and log one line.
    /// Runs even when the process had already exited on its own (<see cref="KillInitiated"/> still
    /// marks this as the recovery path) — a hard crash stranded the button the same way.
    /// <para>
    /// Never throws into the supervisor's dispatcher: a failed release leaves the marker for the
    /// App's startup backstop, which runs moments later on the relaunch.
    /// </para>
    /// </summary>
    private void ReleaseHeldButtonAfterKill()
    {
        if (_heldButtonRelease is null)
            return;

        try
        {
            var result = _heldButtonRelease.ReleaseIfMarked();
            if (!result.MarkerWasSet)
                return;

            // Fixup-01 F5: a clear failure is not a failed release — the button-up was sent. The
            // stale marker only means a later startup may send one more, inert button-up.
            if (result.MarkerClearFailure is { } clearFailure)
            {
                Serilog.Log.Warning(
                    "ForceKill: sent one in-place left-button-up, but clearing the marker failed ({ClearFailure}); "
                    + "a later startup may send one inert button-up (GetAsyncKeyState VK_LBUTTON={LeftButtonWasDown}).",
                    clearFailure.GetType().Name, result.LeftButtonWasDown);
                return;
            }

            Serilog.Log.Information(
                "ForceKill: a drag button was held, so sent one in-place left-button-up and cleared the marker "
                + "(GetAsyncKeyState VK_LBUTTON={LeftButtonWasDown}).",
                result.LeftButtonWasDown);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex,
                "ForceKill: the held-drag-button release failed; the marker is kept for the App's startup backstop.");
        }
    }

    /// <summary>Binds a handle and wires its exit to <see cref="BoundProcessExited"/>.</summary>
    private void Bind(IProcessHandle? handle)
    {
        _process = handle;
        if (handle is null) return;
        _onExited = () => BoundProcessExited?.Invoke(handle);
        handle.Exited += _onExited;
    }

    /// <summary>
    /// Releases the bound process without killing it (ticket 06-B stand-down). The exit handler is
    /// detached, so a later exit of the released process books nothing. Idempotent.
    /// </summary>
    public void Unbind()
    {
        ReleasePrevious();
        KillInitiated = false;
    }

    /// <summary>Detaches the exit handler and disposes the previously-bound handle before a relaunch/adopt.</summary>
    private void ReleasePrevious()
    {
        if (_process is not null && _onExited is not null)
        {
            try { _process.Exited -= _onExited; } catch { }
        }
        _onExited = null;
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
}
