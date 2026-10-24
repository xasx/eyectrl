namespace EyeCtrl.Supervisor.Supervision;

using System;

/// <summary>
/// The wire-time race guard (ticket 06 review F4): the bound process may already have exited
/// between <c>Launch()</c>/<c>Adopt()</c> and the subscription to its <c>Exited</c> event, in which
/// case that event has already fired (or never will). This finds that exit and feeds
/// <see cref="ProcessExited"/>.
/// </summary>
/// <remarks>
/// <para>A7/L3: the input is POSTED to the UI queue, never fed inline. The caller can be part of a
/// state-machine transition: <c>RetryNow</c> launches the FallenBack retry, wires the new process,
/// closes the episode's dialog and only then feeds <see cref="FallenBackRetry"/>. A synchronous
/// feed here re-enters <c>HandleInput</c> while the machine is still
/// <see cref="SupervisorState.FallenBack"/>, which re-escalates into a second
/// <see cref="SupervisorCommand.LaunchFallbackAt"/> and a second, countdown-less dialog. The retry
/// then closes the FIRST dialog and re-arms IdleWatch, leaving the second one stale on screen with
/// no way to run it. Posting lets the in-flight transition land first: the exit is then seen in
/// IdleWatch, where the machine deliberately ignores it — exactly like an exit that races the
/// retry rather than the wire.</para>
/// <para>Same Dispatcher-priority reasoning as the real <c>Exited</c> callback
/// (<c>OnBoundProcessExited</c>): the supervisor's tick runs at Background priority, so a posted
/// input at Normal runs before the next tick, and the launch outcome is never mistaken for a
/// stale one.</para>
/// </remarks>
internal static class WireTimeExit
{
    /// <summary>
    /// Posts <see cref="ProcessExited"/> when <paramref name="boundProcess"/> has already exited,
    /// instead of feeding it synchronously into <paramref name="feed"/>.
    /// </summary>
    /// <param name="boundProcess">The process the controller just bound; null when unbound.</param>
    /// <param name="post">The UI-thread poster (production: <c>Dispatcher.BeginInvoke</c>).</param>
    /// <param name="feed">The state-machine entry point the posted input is fed to.</param>
    /// <param name="now">Read at drain time for the input's timestamp, like the real
    /// <c>Exited</c> callback's <c>DateTime.UtcNow</c>.</param>
    /// <returns>The exited process's id when an exit was observed and posted; null otherwise.</returns>
    internal static int? PostIfExited(
        IProcessHandle? boundProcess,
        Action<Action> post,
        Action<SupervisionInput> feed,
        Func<DateTime> now)
    {
        if (boundProcess is null || !boundProcess.HasExited) return null;

        var pid = boundProcess.Id;
        post(() => feed(new ProcessExited(now())));
        return pid;
    }
}
