namespace EyeCtrl.Core.Input;

using System.Globalization;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>One observed blocked-input state, raised on every blocked↔unblocked transition.</summary>
/// <param name="IsBlocked">True after a block transition, false after a clear.</param>
/// <param name="Reason">R1's refinement of the cause, or <see cref="BlockedInputReason.Unknown"/>
/// when only R2 has evidence.</param>
/// <param name="Trigger">The R2 rule that produced the transition, for the log and tests.</param>
/// <param name="TargetPoint">The last commanded or clicked point, in physical pixels.</param>
/// <param name="CursorBefore">The cursor just before the triggering injection.</param>
/// <param name="CursorAfter">The cursor just after it.</param>
/// <param name="Foreground">The foreground process's facts at the transition.</param>
/// <param name="Target">The target process's facts at the transition.</param>
public sealed record BlockedInputState(
    bool IsBlocked,
    BlockedInputReason Reason,
    string Trigger,
    PhysicalPoint? TargetPoint,
    PhysicalPoint? CursorBefore,
    PhysicalPoint? CursorAfter,
    ProcessTokenFacts Foreground,
    ProcessTokenFacts Target);

/// <summary>
/// R2, the theory-free detector: Windows can accept every <c>SendInput</c> call and still discard
/// the events (UIPI), so the only honest check is whether the cursor obeyed. A block needs
/// consecutive evidence, so an external cursor writer (TD Control, Gaze Point) cannot raise it:
/// that writer moves the cursor, which is never a "static while commanded" observation.
///
/// <para>
/// Two rules, both debounced by <see cref="ActionHitsRequired"/> /
/// <see cref="SamplerHitsRequired"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Action verification</b> (<see cref="ReportAction"/>): a click or pointed scroll batch starts
/// with an absolute move to P. If the cursor after equals the cursor before (within
/// <see cref="StaticTolerancePixels"/>) while the before position was far from P (beyond
/// <see cref="TargetTolerancePixels"/>), the move did not happen — one hit, counted only once the
/// delayed confirmation's pre-move AND post-move reads both still show the old spot (L3). A
/// subsequent action whose cursor lands within the target tolerance is a verified move and CLEARS
/// the state. A cursor that moved somewhere other than P is an external writer: it neither raises
/// nor clears.
/// </item>
/// <item>
/// <b>FreeLook sampler</b> (<see cref="ReportMoveCommand(PhysicalPoint)"/>): every
/// <see cref="SampleIntervalMilliseconds"/> of commanded moves, the cursor is read once. If EyeCtrl
/// commanded at least one point far from the sample's starting cursor and the cursor never left
/// its starting neighbourhood, the moves are being dropped — one hit. A cursor that reaches a
/// recently commanded point is a verified move and clears the state.
/// </item>
/// </list>
///
/// <para>
/// Thread-safe: <c>MoveCursor</c> runs on the gaze thread while clicks run on the UI thread. The
/// event is raised outside the lock, so a handler may call back into the detector.
/// </para>
/// </summary>
public sealed class BlockedInputDetector
{
    /// <summary>Consecutive failed actions that establish a block (the first failed click already
    /// did nothing, so two is the smallest number that cannot be a single dropped event).</summary>
    public const int ActionHitsRequired = 2;

    /// <summary>Consecutive one-second samples that establish a block in FreeLook.</summary>
    public const int SamplerHitsRequired = 3;

    /// <summary>The sampler's window length, in milliseconds (about one second).</summary>
    public const long SampleIntervalMilliseconds = 1000;

    /// <summary>How far the cursor may drift and still count as "did not move", in physical pixels.</summary>
    public const int StaticTolerancePixels = 3;

    /// <summary>How near a target the cursor must land to count as "the move happened", in physical
    /// pixels. Generous on purpose: the absolute-coordinate encoding truncates, and a 200 % display
    /// doubles the encoding error.</summary>
    public const int TargetTolerancePixels = 14;

    /// <summary>How long after a failed action the detector re-reads the cursor before counting a
    /// miss, in milliseconds. Windows documents neither way whether <c>GetCursorPos</c> already shows
    /// the move when <c>SendInput</c> returns, so a single immediate read would turn ordinary clicks
    /// into false blocks. A real UIPI block leaves the cursor frozen for seconds, so this delay costs
    /// nothing when it matters.</summary>
    public const int MissConfirmationDelayMilliseconds = 40;

    /// <summary>How many unconfirmed miss candidates may be queued (a burst of clicks).</summary>
    private const int MaxPendingMisses = 4;

    private readonly ICursorPositionProbe _cursor;
    private readonly IInputIntegrityProbe _integrity;
    private readonly ILogger<BlockedInputDetector>? _logger;
    private readonly Func<long> _nowMilliseconds;
    private readonly Func<(int Width, int Height)>? _screenMetrics;

    private readonly object _gate = new();

    // Recent commanded points, newest last. Three is one frame of lag plus slack; using the whole
    // window would let a stale point near the cursor defeat the static test.
    private readonly List<PhysicalPoint> _recentCommands = new(3);

    // A miss candidate is confirmed by a re-read after MissConfirmationDelayMilliseconds. The
    // click's own UI path is never blocked on that read; the queue is drained from the gaze stream.
    private readonly List<PendingMiss> _pendingMisses = new(MaxPendingMisses);

    private int _actionMisses;
    private int _samplerMisses;

    private PhysicalPoint? _sampleCursor;
    private long _sampleStartedAtMs;
    private bool _sampleWindowOpen;
    private bool _windowHadFarCommand;

    private BlockedInputState _state = new(
        false, BlockedInputReason.Unknown, "none", null, null, null,
        ProcessTokenFacts.Unknown, ProcessTokenFacts.Unknown);

    /// <summary>Creates the detector over its two OS seams.</summary>
    /// <param name="cursor">Reads the OS cursor; a test passes a fake.</param>
    /// <param name="integrity">R1's token read; a test passes a fake.</param>
    /// <param name="logger">Receives one warning per transition.</param>
    /// <param name="nowMilliseconds">The clock seam; defaults to <see cref="Environment.TickCount64"/>.</param>
    /// <param name="screenMetrics">The primary screen's size, so off-screen gaze/target points are
    /// clamped exactly as the injection clamps them. Null clamps only negative coordinates (all the
    /// top/left edge needs); production passes the live <c>SM_CXSCREEN</c>/<c>SM_CYSCREEN</c>.</param>
    public BlockedInputDetector(
        ICursorPositionProbe cursor,
        IInputIntegrityProbe integrity,
        ILogger<BlockedInputDetector>? logger = null,
        Func<long>? nowMilliseconds = null,
        Func<(int Width, int Height)>? screenMetrics = null)
    {
        _cursor = cursor;
        _integrity = integrity;
        _logger = logger;
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
        _screenMetrics = screenMetrics;
    }

    /// <summary>Whether the detector currently believes injected input is being discarded.</summary>
    public bool IsBlocked
    {
        get
        {
            lock (_gate)
                return _state.IsBlocked;
        }
    }

    /// <summary>The latest state (the initial value is an unblocked, unknown state).</summary>
    public BlockedInputState CurrentState
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>Raised once per blocked↔unblocked transition, outside the detector's lock.</summary>
    public event EventHandler<BlockedInputState>? StateChanged;

    /// <summary>
    /// Reports one completed click, double-click or pointed scroll: the cursor read just before the
    /// injection and just after it. An unreadable cursor is ignored (no evidence either way). A miss
    /// is only a CANDIDATE here: it is counted after a re-read
    /// (<see cref="MissConfirmationDelayMilliseconds"/>) shows the cursor still static, so a cursor
    /// read that lags <c>SendInput</c> cannot raise a false block. When the FreeLook stream also
    /// supplies its pre-move read, that read AND the post-move read must both stay static (L3).
    /// </summary>
    /// <param name="target">The point the batch's leading absolute move aimed at.</param>
    /// <param name="cursorBefore">The cursor before the batch, or null when unreadable.</param>
    /// <param name="cursorAfter">The cursor after the batch, or null when unreadable.</param>
    public void ReportAction(PhysicalPoint target, PhysicalPoint? cursorBefore, PhysicalPoint? cursorAfter)
    {
        if (cursorBefore is not { } beforeRaw || cursorAfter is not { } afterRaw)
            return;

        var now = _nowMilliseconds();
        BlockedInputState? transition;
        lock (_gate)
        {
            transition = ConfirmDueMissesLocked(now, cursorOverride: null, hasCursorOverride: false);

            var targetPoint = ClampToScreen(target);
            var before = ClampToScreen(beforeRaw);
            var after = ClampToScreen(afterRaw);
            var moved = after.DistanceTo(before) > StaticTolerancePixels;

            if (moved && after.DistanceTo(targetPoint) <= TargetTolerancePixels)
            {
                // F5: a clear needs an observed move. A click that lands where the frozen cursor
                // already sits is not evidence that input works again.
                _actionMisses = 0;
                _pendingMisses.Clear();
                transition = ClearIfBlocked("verified action move", targetPoint, before, after) ?? transition;
            }
            else if (!moved && before.DistanceTo(targetPoint) > TargetTolerancePixels)
            {
                // Candidate only; the delayed re-read decides. Two candidates can queue in a burst,
                // but a dwell-driven click is hundreds of ms apart and the first confirms first.
                _pendingMisses.Add(new PendingMiss(targetPoint, before, now + MissConfirmationDelayMilliseconds));
                if (_pendingMisses.Count > MaxPendingMisses)
                    _pendingMisses.RemoveAt(0);
            }
            else if (!_state.IsBlocked)
            {
                // The cursor moved but not to the commanded point: an external writer, not a drop.
                // It also proves the pointer is not frozen, so any candidate still waiting for its
                // confirmation is dropped rather than counted later.
                _actionMisses = 0;
                _pendingMisses.Clear();
            }
        }

        Raise(transition);
    }

    /// <summary>
    /// Reports one commanded absolute cursor move (the FreeLook/gaze stream). At most one cursor
    /// read happens per <see cref="SampleIntervalMilliseconds"/>; the intervening calls only record
    /// what EyeCtrl asked for. Off-screen commands are clamped to the primary screen before any
    /// comparison, because the injection clamps them the same way: a gaze 30 px above the top edge is
    /// a perfectly ordinary look, not a dropped move.
    /// </summary>
    /// <param name="commanded">The point EyeCtrl just asked the OS to move to.</param>
    public void ReportMoveCommand(PhysicalPoint commanded) =>
        ReportMoveCommand(commanded, cursorBeforeMove: null, hasCursorBeforeMove: false);

    /// <summary>
    /// Reports one commanded move together with the cursor read taken BEFORE the move was written.
    /// That read is the evidence N1 needs: a click that lands BY the next frame's pre-move read is
    /// visible here, while a read taken after the move would see only the gaze point this call just
    /// wrote (and would call a landed click a miss when the gaze is still on the button).
    ///
    /// <para>
    /// L3: the due-miss confirmation needs BOTH this pre-move read and the post-move read taken just
    /// after this move, each still within <see cref="StaticTolerancePixels"/> of the click's
    /// <c>before</c>. A click that lands later than this read is overwritten by this frame's own
    /// FreeLook move, so the post-move read sees our gaze point rather than <c>before</c> and the
    /// candidate is dropped. Only a gaze that itself rests within tolerance of <c>before</c> leaves
    /// both reads static; that is the remaining one-frame window. The overload with no pre-move
    /// read keeps the single post-move read for direct callers.
    /// </para>
    /// </summary>
    /// <param name="commanded">The point EyeCtrl just asked the OS to move to.</param>
    /// <param name="cursorBeforeMove">The cursor before this move, or null when unreadable. A null
    /// read is still a supplied observation: no due candidate may be confirmed from it.</param>
    public void ReportMoveCommand(PhysicalPoint commanded, PhysicalPoint? cursorBeforeMove) =>
        ReportMoveCommand(commanded, cursorBeforeMove, hasCursorBeforeMove: true);

    private void ReportMoveCommand(
        PhysicalPoint commanded, PhysicalPoint? cursorBeforeMove, bool hasCursorBeforeMove)
    {
        commanded = ClampToScreen(commanded);
        var now = _nowMilliseconds();
        BlockedInputState? transition;
        lock (_gate)
        {
            // N1: a pre-move read that has left the old spot drops the candidate. Near the commanded
            // click target it is a delayed landing (positive evidence, clears a live block); anywhere
            // else it is an external writer. Still at the old spot, the confirmation below decides.
            transition = DropPendingExplainedByPreMoveLocked(cursorBeforeMove);

            transition = ConfirmDueMissesLocked(
                now, cursorBeforeMove, hasCursorBeforeMove) ?? transition;

            if (!_sampleWindowOpen || _sampleCursor is not { } sample)
            {
                StartSampleWindow(ReadCursor() ?? commanded);
            }
            else
            {
                if (commanded.DistanceTo(sample) > TargetTolerancePixels)
                    _windowHadFarCommand = true;

                _recentCommands.Add(commanded);
                if (_recentCommands.Count > 3)
                    _recentCommands.RemoveAt(0);

                if (now - _sampleStartedAtMs >= SampleIntervalMilliseconds)
                {
                    var cursor = ReadCursor();
                    if (cursor is not { } raw)
                    {
                        if (!_state.IsBlocked)
                            _samplerMisses = 0; // a gap breaks the consecutive streak
                        StartSampleWindow(sample);
                    }
                    else
                    {
                        var current = ClampToScreen(raw);

                        if (!_windowHadFarCommand && !_state.IsBlocked)
                            _samplerMisses = 0; // nothing commanded away from the cursor: no evidence

                        var staticCursor = current.DistanceTo(sample) <= StaticTolerancePixels;
                        var moved = current.DistanceTo(sample) > StaticTolerancePixels;
                        // F5: a clear needs an OBSERVED move that lands on a commanded point. The
                        // frozen cursor merely lying on the gaze path (P4) must not clear a real block.
                        var reached = moved && _recentCommands.Exists(
                            p => p.DistanceTo(current) <= TargetTolerancePixels);

                        if (reached)
                        {
                            _samplerMisses = 0;
                            transition = ClearIfBlocked(
                                "verified sampler move", _recentCommands[^1], sample, current) ?? transition;
                        }
                        else if (staticCursor && _windowHadFarCommand)
                        {
                            _samplerMisses++;
                            if (!_state.IsBlocked && _samplerMisses >= SamplerHitsRequired)
                            {
                                transition = Block(
                                    "cursor static while FreeLook kept moving it",
                                    _recentCommands[^1], sample, current) ?? transition;
                            }
                        }
                        else if (!_state.IsBlocked)
                        {
                            // Moved but not to anything we asked for: an external writer broke it.
                            _samplerMisses = 0;
                        }

                        StartSampleWindow(current);
                    }
                }
            }
        }

        Raise(transition);
    }

    /// <summary>
    /// Drops candidates an EyeCtrl move already explains (N1). A pre-move read within
    /// <see cref="StaticTolerancePixels"/> of the pending candidate's <c>Before</c> is still the old
    /// spot and is left to the confirmation. Further away, the cursor moved for some other reason:
    /// at the commanded click target it is a delayed landing (positive evidence, clears a live
    /// block), elsewhere it is an external writer and no evidence of a drop. Runs under the lock.
    /// </summary>
    /// <param name="cursorBeforeMove">The cursor read taken before this FreeLook move, or null if
    /// unreadable (then nothing is dropped on this evidence).</param>
    /// <returns>A clear transition if a delayed landing ended a live block.</returns>
    private BlockedInputState? DropPendingExplainedByPreMoveLocked(PhysicalPoint? cursorBeforeMove)
    {
        if (cursorBeforeMove is not { } raw)
            return null;

        var before = ClampToScreen(raw);
        BlockedInputState? transition = null;

        for (var i = _pendingMisses.Count - 1; i >= 0; i--)
        {
            var pending = _pendingMisses[i];
            if (before.DistanceTo(pending.Before) <= StaticTolerancePixels)
                continue; // still at the old spot: the confirmation must decide

            _pendingMisses.RemoveAt(i);

            if (before.DistanceTo(pending.Target) <= TargetTolerancePixels)
            {
                // The click's move landed late: it was NOT dropped.
                _actionMisses = 0;
                transition = ClearIfBlocked(
                    "delayed verified action move", pending.Target, pending.Before, before) ?? transition;
            }
            else if (!_state.IsBlocked)
            {
                // Moved somewhere else: an external writer, not a drop.
                _actionMisses = 0;
            }
        }

        return transition;
    }

    /// <summary>
    /// Confirms every miss candidate whose re-read is due. L3: when the caller supplied a pre-move
    /// read, a candidate becomes a miss only when BOTH that read and the post-move read are still
    /// within <see cref="StaticTolerancePixels"/> of where the cursor was before the injection. The
    /// post-move read is taken here, after this FreeLook move was written: in a real block that move
    /// is dropped, so the cursor is still at <c>before</c>, while a click that landed in between was
    /// overwritten by our own move and reads as a cursor away from <c>before</c>. A read that is
    /// unreadable, or one that has left <c>before</c>, drops the candidate (the read lagged, or an
    /// external writer moved it). Without a supplied pre-move read (direct callers) the single
    /// post-move read decides, as before. Runs under the detector lock.
    /// </summary>
    /// <param name="now">The current clock reading.</param>
    /// <param name="cursorOverride">A cursor read supplied by the caller (the pre-move read), or
    /// null; only meaningful when <paramref name="hasCursorOverride"/> is true.</param>
    /// <param name="hasCursorOverride">True when the caller supplied the pre-move read. A supplied
    /// null then means "unreadable" and no candidate may be confirmed from it.</param>
    /// <returns>A block or clear transition caused by the confirmation, if any.</returns>
    private BlockedInputState? ConfirmDueMissesLocked(
        long now, PhysicalPoint? cursorOverride, bool hasCursorOverride)
    {
        BlockedInputState? transition = null;

        // Both reads serve every due candidate: they all describe the same instant. The pre-move
        // read is the caller's; the post-move read is taken here, after our own move.
        var preMove = hasCursorOverride && cursorOverride is { } rawPre
            ? ClampToScreen(rawPre)
            : (PhysicalPoint?)null;
        PhysicalPoint? postMove = null;
        var hasPostMoveRead = false;

        for (var i = _pendingMisses.Count - 1; i >= 0; i--)
        {
            var pending = _pendingMisses[i];
            if (now < pending.DueAtMs)
                continue;

            if (!hasPostMoveRead)
            {
                postMove = ReadCursor() is { } rawPost ? ClampToScreen(rawPost) : null;
                hasPostMoveRead = true;
            }

            _pendingMisses.RemoveAt(i);

            // L3, the pre-move half: a supplied null is "unreadable", and a read that has left
            // `before` was already explained by DropPending. Either way there is no miss here.
            var preStatic = !hasCursorOverride
                || (preMove is { } pre && pre.DistanceTo(pending.Before) <= StaticTolerancePixels);
            if (hasCursorOverride && !preStatic)
                continue;

            if (postMove is not { } current)
                continue; // unreadable: no evidence, drop the candidate

            // L3: BOTH halves must see the old spot. In a block every move is dropped, so both
            // stay frozen; a landing can leave only the post-move read away from `before`.
            var postStatic = current.DistanceTo(pending.Before) <= StaticTolerancePixels;
            var stillStatic = preStatic && postStatic;

            if (stillStatic)
            {
                _actionMisses++;
                if (!_state.IsBlocked && _actionMisses >= ActionHitsRequired)
                {
                    transition = Block(
                        "cursor still static after a click/scroll move (confirmed by a re-read)",
                        pending.Target, pending.Before, current);
                }
            }
            else
            {
                if (!_state.IsBlocked)
                    _actionMisses = 0;

                // A delayed landing after the read: the move DID happen, so a live block clears.
                if (_state.IsBlocked
                    && current.DistanceTo(pending.Target) <= TargetTolerancePixels)
                {
                    transition = ClearIfBlocked(
                        "delayed verified action move", pending.Target, pending.Before, current);
                }
            }
        }

        return transition;
    }

    /// <summary>
    /// Clamps a point to the primary screen the way the injection does. A gaze point above the top
    /// edge (the toolbar's summon band) or beyond a corner must compare equal to the clamped cursor,
    /// not as a 30 px miss. With no screen size supplied, the top/left edge is still known (negative
    /// coordinates are off-screen on every display).
    /// </summary>
    internal PhysicalPoint ClampToScreen(PhysicalPoint point)
    {
        if (_screenMetrics?.Invoke() is { Width: > 0, Height: > 0 } size)
        {
            return new PhysicalPoint(
                Math.Clamp(point.X, 0, size.Width - 1),
                Math.Clamp(point.Y, 0, size.Height - 1));
        }

        return new PhysicalPoint(Math.Max(0, point.X), Math.Max(0, point.Y));
    }

    /// <summary>One failed action waiting for its confirmation re-read.</summary>
    /// <param name="Target">The clamped point the injection aimed at.</param>
    /// <param name="Before">The clamped cursor before the injection.</param>
    /// <param name="DueAtMs">The clock reading at which the confirmation is due.</param>
    private readonly record struct PendingMiss(PhysicalPoint Target, PhysicalPoint Before, long DueAtMs);

    private PhysicalPoint? ReadCursor()
    {
        try
        {
            return _cursor.TryGetCursorPosition();
        }
        catch (Exception)
        {
            return null; // a probe fault is "no evidence", never a block
        }
    }

    private void StartSampleWindow(PhysicalPoint start)
    {
        _sampleCursor = ClampToScreen(start);
        _sampleStartedAtMs = _nowMilliseconds();
        _sampleWindowOpen = true;
        _windowHadFarCommand = false;
        _recentCommands.Clear();
    }

    private BlockedInputState Block(
        string trigger, PhysicalPoint target, PhysicalPoint? before, PhysicalPoint? after)
    {
        var self = ReadSelfSafely();
        var foreground = ReadSafely(() => _integrity.InspectForeground());
        var targetFacts = ReadSafely(() => _integrity.InspectPoint(target));
        var reason = InputIntegrityRules.ReasonFor(foreground, targetFacts, self);

        var state = new BlockedInputState(
            true, reason, trigger, target, before, after, foreground, targetFacts);
        _state = state;

        _logger?.LogWarning(
            "Blocked input: Windows is discarding EyeCtrl's injected input ({Trigger}). "
            + "Foreground={ForegroundProcess} IL={ForegroundIntegrity} Elevated={ForegroundElevated} "
            + "UIAccess={ForegroundUIAccess}; Target={TargetProcess} IL={TargetIntegrity} "
            + "Elevated={TargetElevated} at {TargetPoint}; CursorBefore={CursorBefore} CursorAfter={CursorAfter}",
            trigger,
            foreground.ProcessName ?? "unknown",
            InputIntegrityRules.IntegrityName(foreground.IntegrityLevel),
            foreground.Elevated,
            foreground.HasUIAccess,
            targetFacts.ProcessName ?? "unknown",
            InputIntegrityRules.IntegrityName(targetFacts.IntegrityLevel),
            targetFacts.Elevated,
            Describe(target),
            Describe(before),
            Describe(after));

        return state;
    }

    private BlockedInputState? ClearIfBlocked(
        string trigger, PhysicalPoint target, PhysicalPoint? before, PhysicalPoint? after)
    {
        if (!_state.IsBlocked)
            return null;

        var previous = _state;
        _state = new BlockedInputState(
            false, BlockedInputReason.Unknown, trigger, target, before, after,
            previous.Foreground, previous.Target);

        _logger?.LogWarning(
            "Blocked input cleared: injected input lands again ({Trigger}). "
            + "Foreground={ForegroundProcess} IL={ForegroundIntegrity}; Target={TargetProcess} at {TargetPoint}; "
            + "CursorBefore={CursorBefore} CursorAfter={CursorAfter}",
            trigger,
            previous.Foreground.ProcessName ?? "unknown",
            InputIntegrityRules.IntegrityName(previous.Foreground.IntegrityLevel),
            previous.Target.ProcessName ?? "unknown",
            Describe(target),
            Describe(before),
            Describe(after));

        return _state;
    }

    private SelfTokenFacts ReadSelfSafely()
    {
        try
        {
            return _integrity.ReadSelf();
        }
        catch (Exception)
        {
            return new SelfTokenFacts(null, false, false);
        }
    }

    private static ProcessTokenFacts ReadSafely(Func<ProcessTokenFacts> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return ProcessTokenFacts.Unknown;
        }
    }

    private void Raise(BlockedInputState? transition)
    {
        if (transition is not { } state)
            return;

        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            // The detector sits on the input path: a faulting subscriber must never break a click or
            // a gaze frame. The state is still recorded; the banner is only a notification.
            _logger?.LogWarning(ex, "A blocked-input state subscriber threw; the detector is unaffected.");
        }
    }

    /// <summary>Renders a point for the log as invariant whole pixels, or <c>(?)</c>.</summary>
    internal static string Describe(PhysicalPoint? point) =>
        point is { } p
            ? string.Format(CultureInfo.InvariantCulture, "({0:F0},{1:F0})", p.X, p.Y)
            : "(?)";
}
