namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Diagnostics;
using System.Threading;

/// <summary>
/// R2's whole-run time cap (requirement h, review 01 M4): a background thread that kills the
/// process when the run outlives <see cref="Cap"/>, so the owner's screen cannot keep a topmost
/// window past it. The per-frame timeout in the harness bounds every normal path; this bounds the
/// process.
///
/// <para>
/// It fires only when nothing disposes it in time. The harness arms it before the window exists and
/// disposes it on the way out — success or exception — so the ordinary run never trips it. A hard
/// process exit destroys the window with it, which is what makes (h) hold by construction rather
/// than by the unwind path.
/// </para>
/// </summary>
internal sealed class ProbeRingRunWatchdog : IDisposable
{
    /// <summary>The whole-run cap: about 30 s, as (h) allows.</summary>
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    private readonly ManualResetEventSlim _finished = new(false);

    /// <summary>Arms the watchdog for the production cap.</summary>
    /// <param name="onTimeout">Invoked once on the watchdog thread when the cap expires.</param>
    internal ProbeRingRunWatchdog(Action onTimeout) : this(Cap, onTimeout)
    {
    }

    /// <summary>Arms the watchdog for <paramref name="cap"/>; the seam the cap-logic test drives.</summary>
    /// <param name="cap">How long the run may take.</param>
    /// <param name="onTimeout">Invoked once on the watchdog thread when the cap expires.</param>
    internal ProbeRingRunWatchdog(TimeSpan cap, Action onTimeout)
    {
        ArgumentNullException.ThrowIfNull(onTimeout);

        // Not stored: the thread is fire-and-forget and Dispose only signals it. Keeping the
        // reference would be an assigned-but-unread private field, which the build warns on.
        var thread = new Thread(() =>
        {
            if (!_finished.Wait(cap))
                onTimeout();
        })
        {
            IsBackground = true,
            Name = "R2 whole-run watchdog",
        };
        thread.Start();
    }

    /// <summary>
    /// The production timeout action: no window on the owner's screen outlives the cap, so the
    /// whole process goes.
    ///
    /// <para>
    /// <b><c>Kill</c>, not <c>Environment.FailFast</c> (orchestrator decision, fixup 02 F2).</b>
    /// FailFast goes through Windows Error Reporting, and this machine has a
    /// <c>WER\LocalDumps</c> key: a dump write delays termination, and WER can put up an error
    /// dialog — a surface the owner, who drives the PC by gaze alone, would have to dismiss with
    /// the very input the watchdog exists to free. <c>Kill</c> terminates immediately and silently.
    /// An exception would be worse still: the test runner can swallow one.
    /// </para>
    /// </summary>
    internal static void KillOnTimeout() => Process.GetCurrentProcess().Kill();

    /// <summary>
    /// Disarms the watchdog. Only signals — the event is not disposed, because the watchdog thread
    /// may still be inside <c>Wait</c> and disposing under it races; the event dies with the process.
    /// </summary>
    public void Dispose() => _finished.Set();
}
