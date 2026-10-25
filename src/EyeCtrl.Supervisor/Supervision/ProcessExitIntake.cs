namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The supervisor's intake filter for <see cref="ProcessExited"/> (A7 fixup-02): decides whether an
/// exit may be fed to the state machine.
/// </summary>
/// <remarks>
/// <para><b>Bound identity.</b> An exit is accepted only for the process the controller is bound to
/// (A7 fixup-01/S1): after a relaunch or an adopt, a marshalled <c>Exited</c> callback for the
/// previous instance must not strike the new one. The binding is compared by HANDLE REFERENCE, so a
/// new binding resets the intake even when Windows reuses the same pid.</para>
/// <para><b>One exit per binding.</b> Two feeders observe the same process exit: the controller's
/// <c>Exited</c> callback (<c>OnBoundProcessExited</c>, marshalled to the UI thread) and the
/// wire-time race guard (<see cref="WireTimeExit"/>). If the process dies between <c>Bind</c> and
/// the wire-time check, both report it. The second report would take the FallenBack arm of an
/// already-escalated machine — an extra strike and a second, identical dialog (A7 fixup-02/D1). The
/// first accepted exit consumes the binding's pid, so the repeat is dropped.</para>
/// </remarks>
internal sealed class ProcessExitIntake
{
    private IProcessHandle? _boundProcess;
    private int? _consumedExitPid;

    /// <summary>True when <paramref name="exit"/> may be fed to the machine; false for an exit
    /// owned by a previous binding, or a duplicate for the current one.</summary>
    /// <param name="exit">The exit to vet.</param>
    /// <param name="boundProcess">The process the controller currently has bound; null after a
    /// stand-down.</param>
    internal bool Accepts(ProcessExited exit, IProcessHandle? boundProcess)
    {
        if (!ReferenceEquals(boundProcess, _boundProcess))
        {
            // A different binding is a different exit identity — even for a reused Windows pid.
            _boundProcess = boundProcess;
            _consumedExitPid = null;
        }

        if (!exit.Matches(boundProcess?.Id)) return false;

        // An untagged exit (probes and older machine tests) carries no identity to compare.
        if (exit.Pid is not int pid) return true;

        if (pid == _consumedExitPid) return false;
        _consumedExitPid = pid;
        return true;
    }
}
