namespace EyeCtrl.Platform.Tests;

using System.Diagnostics;

/// <summary>
/// The clock seam for the window-action tests: it starts PAUSED and is
/// <see cref="Resume"/>d once the test has observed the worker inside its first native call.
///
/// <para>
/// <see cref="EyeCtrl.Platform.Services.WindowActionScheduler"/>.Enqueue stamps the request
/// BEFORE its worker exists, then starts a DEDICATED thread (<c>new Thread(...)</c> — never the
/// pool) and measures both the start deadline and the completion budget from that stamp. The
/// worker's first clock read therefore includes thread creation, OS scheduling, JIT and any GC
/// pause — machine load, nothing the product controls. On a busy shared box that latency passed
/// the 100 ms start deadline of the drop test: the first request was dropped before its body
/// ran, the fake's <c>HangEntered</c> never fired, and the test went red although the product
/// was healthy (the 778d7585 sighting — 2 failures in 4 runs right after a full-solution
/// rebuild, 0 in 3 solo runs; the same waits passed in the same runs).
/// </para>
///
/// <para>
/// While paused, the clock reads a constant, so the start-deadline check sees zero elapsed
/// however late the machine schedules the worker: the request always runs once scheduled, and
/// the test's wait only has to cover that latency. <see cref="Resume"/> is called only after
/// the worker has been observed parked inside the fake's hang, where it touches no clock again
/// until the test releases it — so resuming cannot race a deadline the test means to keep.
/// From that moment time flows at the real rate (a plain offset of the real Stopwatch), and
/// every budget and deadline the test then measures — the second request's start deadline, the
/// completion budget that must expire, the one that must not — starts from a point the test
/// controls. What each test proves is unchanged; the machine stops being a variable in it.
/// </para>
///
/// <para>
/// This is the #386 family's shape (a wait on a worker that had not started under load), but
/// the fix is deliberately NOT a pool floor (<c>ThreadPoolFloorFixture</c>): a floor cannot
/// move a dedicated thread. Freezing the deadline's clock does. Pool-blocked work items leave
/// these tests green and CPU saturation is what reproduces the failure — both verified when
/// this seam was introduced.
/// </para>
/// </summary>
internal sealed class PausableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private bool _running;
    private long _virtualNow;
    private long _realAtResume;

    /// <summary>Time is frozen at zero until <see cref="Resume"/> is called.</summary>
    internal PausableTimeProvider()
    {
        // Virtual timestamps live in the Stopwatch tick domain, so the base class's
        // GetElapsedTime arithmetic (Stopwatch.Frequency) stays exact for them.
        _virtualNow = 0;
    }

    /// <summary>
    /// Let time flow at the real rate from now on. Idempotent. Call it only once the worker has
    /// been observed parked in the fake's hang (see the class doc for why that ordering is the
    /// race-free one).
    /// </summary>
    internal void Resume()
    {
        lock (_gate)
        {
            if (_running)
                return;

            _running = true;
            _realAtResume = Stopwatch.GetTimestamp();
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            if (!_running)
                return _virtualNow;

            return _virtualNow + (Stopwatch.GetTimestamp() - _realAtResume);
        }
    }
}
