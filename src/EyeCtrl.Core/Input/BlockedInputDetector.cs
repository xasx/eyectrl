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
/// <see cref="TargetTolerancePixels"/>), the move did not happen — one hit. A subsequent action
/// whose cursor lands within the target tolerance is a verified move and CLEARS the state. A cursor
/// that moved somewhere other than P is an external writer: it neither raises nor clears.
/// </item>
/// <item>
/// <b>FreeLook sampler</b> (<see cref="ReportMoveCommand"/>): every
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

    private readonly ICursorPositionProbe _cursor;
    private readonly IInputIntegrityProbe _integrity;
    private readonly ILogger<BlockedInputDetector>? _logger;
    private readonly Func<long> _nowMilliseconds;

    private readonly object _gate = new();

    // Recent commanded points, newest last. Three is one frame of lag plus slack; using the whole
    // window would let a stale point near the cursor defeat the static test.
    private readonly List<PhysicalPoint> _recentCommands = new(3);

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
    public BlockedInputDetector(
        ICursorPositionProbe cursor,
        IInputIntegrityProbe integrity,
        ILogger<BlockedInputDetector>? logger = null,
        Func<long>? nowMilliseconds = null)
    {
        _cursor = cursor;
        _integrity = integrity;
        _logger = logger;
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
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
    /// injection and just after it. An unreadable cursor is ignored (no evidence either way).
    /// </summary>
    /// <param name="target">The point the batch's leading absolute move aimed at.</param>
    /// <param name="cursorBefore">The cursor before the batch, or null when unreadable.</param>
    /// <param name="cursorAfter">The cursor after the batch, or null when unreadable.</param>
    public void ReportAction(PhysicalPoint target, PhysicalPoint? cursorBefore, PhysicalPoint? cursorAfter)
    {
        if (cursorBefore is not { } before || cursorAfter is not { } after)
            return;

        BlockedInputState? transition = null;
        lock (_gate)
        {
            if (after.DistanceTo(target) <= TargetTolerancePixels)
            {
                _actionMisses = 0;
                transition = ClearIfBlocked("verified action move", target, before, after);
            }
            else if (after.DistanceTo(before) <= StaticTolerancePixels
                     && before.DistanceTo(target) > TargetTolerancePixels)
            {
                _actionMisses++;
                if (!_state.IsBlocked && _actionMisses >= ActionHitsRequired)
                    transition = Block("cursor static while a click/scroll move was commanded", target, before, after);
            }
            else if (!_state.IsBlocked)
            {
                // The cursor moved, but not to the commanded point: an external writer, not a drop.
                _actionMisses = 0;
            }
        }

        Raise(transition);
    }

    /// <summary>
    /// Reports one commanded absolute cursor move (the FreeLook/gaze stream). At most one cursor
    /// read happens per <see cref="SampleIntervalMilliseconds"/>; the intervening calls only record
    /// what EyeCtrl asked for.
    /// </summary>
    /// <param name="commanded">The point EyeCtrl just asked the OS to move to.</param>
    public void ReportMoveCommand(PhysicalPoint commanded)
    {
        BlockedInputState? transition = null;
        lock (_gate)
        {
            if (!_sampleWindowOpen || _sampleCursor is not { } sample)
            {
                StartSampleWindow(ReadCursor() ?? commanded);
                return;
            }

            if (commanded.DistanceTo(sample) > TargetTolerancePixels)
                _windowHadFarCommand = true;

            _recentCommands.Add(commanded);
            if (_recentCommands.Count > 3)
                _recentCommands.RemoveAt(0);

            var now = _nowMilliseconds();
            if (now - _sampleStartedAtMs < SampleIntervalMilliseconds)
                return;

            var cursor = ReadCursor();
            if (cursor is not { } current)
            {
                if (!_state.IsBlocked)
                    _samplerMisses = 0; // a gap breaks the consecutive streak
                StartSampleWindow(sample);
                return;
            }

            if (!_windowHadFarCommand && !_state.IsBlocked)
                _samplerMisses = 0; // nothing was commanded away from the cursor: no evidence

            var staticCursor = current.DistanceTo(sample) <= StaticTolerancePixels;
            var reached = _recentCommands.Exists(p => p.DistanceTo(current) <= TargetTolerancePixels);

            if (reached)
            {
                _samplerMisses = 0;
                transition = ClearIfBlocked("verified sampler move", _recentCommands[^1], sample, current);
            }
            else if (staticCursor && _windowHadFarCommand)
            {
                _samplerMisses++;
                if (!_state.IsBlocked && _samplerMisses >= SamplerHitsRequired)
                {
                    transition = Block(
                        "cursor static while FreeLook kept moving it", _recentCommands[^1], sample, current);
                }
            }
            else if (!_state.IsBlocked)
            {
                // Moved but not to anything we asked for: an external writer broke the streak.
                _samplerMisses = 0;
            }

            StartSampleWindow(current);
        }

        Raise(transition);
    }

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
        _sampleCursor = start;
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
