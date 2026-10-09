namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>Which of the test's two sessions a state machine runs.</summary>
public enum SubPixelProbeSessionKind
{
    /// <summary>The drift block (decides session 1) then the static block (supporting evidence).</summary>
    Session1,

    /// <summary>The live-gaze block; run only after a session-1 conviction.</summary>
    Session2,
}

/// <summary>The phase of the current trial presentation.</summary>
public enum SubPixelProbePhase
{
    /// <summary>Nothing is running: before <see cref="SubPixelProbeViewModel.Start"/>, between two blocks, or after the session ended.</summary>
    Idle,

    /// <summary>Interval 1: the first condition, 3 s (live: 4 s).</summary>
    Interval1,

    /// <summary>The 0.8 s blank gap between the intervals.</summary>
    Gap,

    /// <summary>Interval 2: the other condition, same motion.</summary>
    Interval2,

    /// <summary>The answer screen: up to 60 s for a completed answer dwell.</summary>
    Answer,

    /// <summary>The session has ended, whether by completing its blocks or by an exit.</summary>
    Ended,
}

/// <summary>Condition A (whole-pixel) or B (sub-pixel) as drawn by an interval.</summary>
public enum SubPixelAbCondition
{
    /// <summary>The production-like whole-pixel ring.</summary>
    A,

    /// <summary>The exact sub-pixel ring.</summary>
    B,
}

/// <summary>Why a scored trial was voided and must be repeated at the end of its block.</summary>
public enum SubPixelProbeVoidReason
{
    /// <summary>Gaze stayed invalid for more than 500 ms inside an interval.</summary>
    InvalidGaze,

    /// <summary>The view reported an interval render gap above 50 ms.</summary>
    RenderGap,
}

/// <summary>Why a session ended.</summary>
public enum SubPixelProbeEndReason
{
    /// <summary>Every block of the session ran to its end; a void-capped block counts.</summary>
    BlocksCompleted,

    /// <summary>The owner dwelled End test, or another component ended the test.</summary>
    EndTest,

    /// <summary>10 s passed with no valid gaze sample: closed eyes, or a silent tracker.</summary>
    EyesClosed,

    /// <summary>No answer within 60 s of the answer screen appearing.</summary>
    NoAnswer,
}

/// <summary>
/// One presented scored trial's outcome. A voided trial carries <see cref="VoidReason"/> and no
/// answer; it is repeated at the end of its block.
/// </summary>
/// <param name="Trial">The trial that was presented, with its order and motion parameters.</param>
/// <param name="Answer">The condition the owner picked, or <c>null</c> when the trial was voided.</param>
/// <param name="VoidReason">Why the trial was voided, or <c>null</c> when it was answered.</param>
/// <param name="ResponseTimeMs">Answer latency from answer-screen onset; 0 for a voided trial.</param>
/// <param name="LongestInvalidGazeMs">Longest continuous invalid-gaze run observed during the trial's intervals.</param>
/// <param name="LongestRenderGapMs">Largest render gap the view reported during the trial's intervals.</param>
public sealed record SubPixelProbeTrialOutcome(
    SubPixelAbTrial Trial,
    SubPixelAbCondition? Answer,
    SubPixelProbeVoidReason? VoidReason,
    long ResponseTimeMs,
    long LongestInvalidGazeMs,
    long LongestRenderGapMs);

/// <summary>A finished block: its tally and every presentation it ran, in order (voids included).</summary>
/// <param name="Block">The block that finished.</param>
/// <param name="Tally">Its answer tally and void count, ready for <see cref="SubPixelAbVerdict"/>.</param>
/// <param name="Trials">Every presentation, in run order; a repeated trial appears once per presentation.</param>
public sealed record SubPixelProbeBlockOutcome(
    SubPixelAbBlock Block,
    SubPixelAbBlockTally Tally,
    IReadOnlyList<SubPixelProbeTrialOutcome> Trials);

/// <summary>The end of a session, for the coordinator and the results writer.</summary>
/// <param name="Session">Which session ended.</param>
/// <param name="Reason">What ended it.</param>
/// <param name="Aborted">
/// <c>true</c> when an exit (End test, closed eyes, no answer) stopped the run mid-session; a
/// session that exhausted its blocks — including a void-capped block — is <c>false</c>.
/// </param>
/// <param name="Blocks">The blocks that finished, in order; empty when the run aborted early.</param>
public sealed record SubPixelProbeSessionOutcome(
    SubPixelProbeSessionKind Session,
    SubPixelProbeEndReason Reason,
    bool Aborted,
    IReadOnlyList<SubPixelProbeBlockOutcome> Blocks);

/// <summary>
/// C5 — the trial state machine of the sub-pixel visibility test.
/// <para>
/// One run = one session: demo → interval 1 → gap → interval 2 → answer → next, repeated for every
/// scored trial of every block the session runs (session 1: drift then static; session 2: live).
/// Takes <see cref="GazeCursorMovedMessage"/> for dwell and positions, and validity through an
/// injected callback because the message carries no validity flag. Owns the three exits: the 10 s
/// time-since-last-valid-sample exit, the 60 s no-answer abort and the End test button, plus the two
/// void rules (> 500 ms invalid gaze in an interval, a view-reported render gap > 50 ms). Voided
/// trials are repeated at the end of their block; a block's 4th void makes it incomplete and ends it.
/// </para>
/// <para>
/// <b>Single-threaded (review 05 F4b).</b> The injected <see cref="TimeProvider"/> must deliver its
/// timer callbacks on the same thread as <see cref="Receive"/> — in production a dispatcher-backed
/// adapter (SP-4b). The gaze thread never enters: <see cref="Receive"/> is messenger-delivered on
/// the UI thread. <see cref="Func{Boolean}"/> validity is queried inside <see cref="Receive"/>, so
/// it must also be safe on the UI thread.
/// </para>
/// <para>
/// After raising any of its events the view model checks <c>IsDisposed</c> and stops: a handler may
/// dispose it (End test, a coordinator teardown), and no timer may be armed or event raised after
/// that. <see cref="Dispose"/> stops all three timers and unregisters from the messenger.
/// </para>
/// </summary>
/// <remarks>
/// The timers come from <see cref="TimeProvider"/>: a one-shot phase timer re-armed per phase, a
/// one-shot eyes-closed timer re-armed on every valid sample, and a one-shot answer timeout re-armed
/// at every answer onset.
/// </remarks>
public class SubPixelProbeViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IDwellRegistrar
{
    /// <summary>Target id of the button answering "1" (condition shown in interval 1).</summary>
    public const string AnswerOneTargetId = "subpixel_answer_1";

    /// <summary>Target id of the button answering "2" (condition shown in interval 2).</summary>
    public const string AnswerTwoTargetId = "subpixel_answer_2";

    /// <summary>Target id of the always-present End test button.</summary>
    public const string EndTestTargetId = "subpixel_end_test";

    /// <summary>Interval 1 and interval 2 duration of the scripted blocks, in milliseconds.</summary>
    public const int ScriptedIntervalMs = 3000;

    /// <summary>Interval 1 and interval 2 duration of the live block, in milliseconds.</summary>
    public const int LiveIntervalMs = 4000;

    /// <summary>The blank gap between the two intervals, in milliseconds.</summary>
    public const int GapMs = 800;

    /// <summary>How long the answer screen waits for a dwell before aborting the session.</summary>
    public const int AnswerTimeoutMs = 60_000;

    /// <summary>Invalid-gaze time inside one interval that voids the trial; more than this voids.</summary>
    public const int InvalidGazeVoidMs = 500;

    /// <summary>View-reported render gap that voids the trial; more than this voids.</summary>
    public const int RenderGapVoidMs = 50;

    /// <summary>Time since the last valid sample after which the session ends (closed eyes, silence).</summary>
    public const int EyesClosedExitMs = 10_000;

    private readonly TimeProvider _timeProvider;
    private readonly SubPixelAbSchedule _schedule;
    private readonly SubPixelProbeSessionKind _session;
    private readonly DwellSettings _dwellSettings;
    private readonly Func<bool> _isValidSample;
    private readonly DwellDetector _targets;
    private readonly Dictionary<string, Action> _actions = new();
    private readonly List<SubPixelProbeBlockOutcome> _completedBlocks = new();
    private readonly List<SubPixelProbeTrialOutcome> _currentTrials = new();
    private readonly Queue<SubPixelAbBlock> _blocks;

    private readonly ITimer _phaseTimer;
    private readonly ITimer _eyesClosedTimer;
    private readonly ITimer _answerTimer;

    // The current block's presentations that have not run yet; a voided trial is appended at the end.
    private List<SubPixelAbTrial> _pending = new();
    private int _pendingIndex;

    private SubPixelProbePhase _phase = SubPixelProbePhase.Idle;
    private SubPixelAbBlock _currentBlock;
    private SubPixelAbTrial? _currentTrial;
    private SubPixelAbMotion? _currentMotion;
    private bool _currentTrialIsAFirst;
    private bool _isDemo;
    private bool _started;
    private bool _ended;
    private int _voids;
    private int _aPicks;
    private int _bPicks;
    private long _phaseStartTimestamp;
    private long _lastValidTimestamp;
    private long? _invalidSince;
    private long _longestInvalidMs;
    private long _longestRenderGapMs;
    private long _answerStartTimestamp;

    /// <summary>
    /// Creates the state machine. It is inert until <see cref="Start"/>.
    /// </summary>
    /// <param name="messenger">Messenger the view model receives <see cref="GazeCursorMovedMessage"/> from.</param>
    /// <param name="timeProvider">
    /// Clock and timer source. All timer callbacks must run on the UI thread: production injects a
    /// dispatcher-backed adapter (SP-4b), tests a fake advanced together with the gaze timestamps.
    /// </param>
    /// <param name="schedule">The session's trial plan, from <see cref="SubPixelAbSchedule.Create"/>.</param>
    /// <param name="session">Which session to run.</param>
    /// <param name="dwellSettings">Dwell configuration; the answer dwell is read from it live at registration.</param>
    /// <param name="isValidSample">
    /// Validity of the tracker's most recent sample, fed by <c>GazeSampled</c>. Queried on the UI
    /// thread for every gaze message; <c>false</c> means the gaze is currently invalid. A missing
    /// sample is silence, which is handled by the eyes-closed timer and needs no callback call.
    /// </param>
    public SubPixelProbeViewModel(
        IMessenger messenger,
        TimeProvider timeProvider,
        SubPixelAbSchedule schedule,
        SubPixelProbeSessionKind session,
        DwellSettings dwellSettings,
        Func<bool> isValidSample)
        : base(messenger)
    {
        _timeProvider = timeProvider;
        _schedule = schedule;
        _session = session;
        _dwellSettings = dwellSettings;
        _isValidSample = isValidSample;

        _targets = new DwellDetector(dwellSettings.DwellCooldownMs);
        _targets.TargetActivated += OnTargetActivated;

        _phaseTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnPhaseElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _eyesClosedTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnEyesClosedElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _answerTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnAnswerElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _blocks = session == SubPixelProbeSessionKind.Session1
            ? new Queue<SubPixelAbBlock>([SubPixelAbBlock.Drift, SubPixelAbBlock.Static])
            : new Queue<SubPixelAbBlock>([SubPixelAbBlock.Live]);
    }

    /// <summary>Raised when a block finishes: every trial presented, or the 4th void capped it.</summary>
    public event Action<SubPixelProbeBlockOutcome>? BlockCompleted;

    /// <summary>Raised exactly once when the session ends, normally or through an exit.</summary>
    public event Action<SubPixelProbeSessionOutcome>? SessionCompleted;

    /// <summary>Raised at every phase transition, answer onsets included. The coordinator's progress signal.</summary>
    public event Action? PhaseAdvanced;

    /// <summary>The phase the state machine is in.</summary>
    public SubPixelProbePhase Phase => _phase;

    /// <summary>The block being presented (or last presented).</summary>
    public SubPixelAbBlock CurrentBlock => _currentBlock;

    /// <summary>The scored trial being presented; <c>null</c> during a block's demo pair.</summary>
    public SubPixelAbTrial? CurrentTrial => _currentTrial;

    /// <summary>The motion the current presentation runs; a demo previews the first pending trial's motion.</summary>
    public SubPixelAbMotion? CurrentMotion => _currentMotion;

    /// <summary>Whether the current presentation is the block's unscored demo pair.</summary>
    public bool IsDemo => _isDemo;

    /// <summary>The condition interval 1 draws for the current presentation.</summary>
    public SubPixelAbCondition Interval1Condition =>
        _currentTrialIsAFirst ? SubPixelAbCondition.A : SubPixelAbCondition.B;

    /// <summary>The condition interval 2 draws for the current presentation.</summary>
    public SubPixelAbCondition Interval2Condition =>
        _currentTrialIsAFirst ? SubPixelAbCondition.B : SubPixelAbCondition.A;

    /// <summary>Milliseconds elapsed in the current phase, on the injected clock.</summary>
    public long PhaseElapsedMs =>
        _phase == SubPixelProbePhase.Idle ? 0 : (long)_timeProvider.GetElapsedTime(_phaseStartTimestamp).TotalMilliseconds;

    /// <summary>Voids recorded in the current block, against the per-block cap of 3.</summary>
    internal int CurrentBlockVoids => _voids;

    /// <summary>Answers recorded in the current block so far; 10 for a completed block.</summary>
    internal int ScoredInCurrentBlock => _aPicks + _bPicks;

    /// <summary>Condition-A picks in the current block so far.</summary>
    internal int CurrentAPicks => _aPicks;

    /// <summary>Condition-B picks in the current block so far.</summary>
    internal int CurrentBPicks => _bPicks;

    /// <summary>Presentations of the current block so far, in run order (voids included).</summary>
    internal IReadOnlyList<SubPixelProbeTrialOutcome> CurrentBlockTrialOutcomes => _currentTrials;

    /// <summary>
    /// Starts the first block: its demo pair, then its trials. Idempotent; a disposed view model
    /// does nothing.
    /// </summary>
    public void Start()
    {
        if (IsDisposed || _started) return;
        _started = true;
        _lastValidTimestamp = _timeProvider.GetTimestamp();
        _eyesClosedTimer.Change(TimeSpan.FromMilliseconds(EyesClosedExitMs), Timeout.InfiniteTimeSpan);
        BeginNextBlock();
    }

    /// <summary>
    /// Ends the session as if the owner had dwelled End test: stops every timer, stops scoring the
    /// current trial (it is not voided and not recorded), and raises
    /// <see cref="SessionCompleted"/> once with <see cref="SubPixelProbeEndReason.EndTest"/>.
    /// </summary>
    public void EndTest() => EndSession(SubPixelProbeEndReason.EndTest);

    /// <summary>
    /// Reports an interval render gap measured by the view. A gap above 50 ms inside interval 1 or
    /// 2 voids the current scored trial; the demo pair and the other phases ignore it.
    /// </summary>
    /// <param name="gapMs">The gap between two presented frames, in milliseconds.</param>
    public void ReportRenderGap(long gapMs)
    {
        if (IsDisposed || _ended) return;

        if (gapMs > _longestRenderGapMs) _longestRenderGapMs = gapMs;

        if (_isDemo) return;
        if (_phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2
            && gapMs > RenderGapVoidMs)
        {
            VoidCurrentTrial(SubPixelProbeVoidReason.RenderGap);
        }
    }

    /// <summary>Receives the gaze stream: validity, then dwell. Inert until <see cref="Start"/>.</summary>
    /// <param name="message">The per-sample gaze message.</param>
    public void Receive(GazeCursorMovedMessage message)
    {
        // The recipient is registered at construction (MessengerViewModelBase), and gaze messages
        // flow from the moment the engine runs; before Start there is no session to time or score.
        if (!_started || IsDisposed || _ended) return;

        if (_isValidSample())
        {
            _lastValidTimestamp = _timeProvider.GetTimestamp();
            _invalidSince = null;
            _eyesClosedTimer.Change(TimeSpan.FromMilliseconds(EyesClosedExitMs), Timeout.InfiniteTimeSpan);
        }
        else
        {
            _invalidSince ??= _timeProvider.GetTimestamp();
            TrackInvalidGaze();
            // Invalid gaze never advances dwell: the position is unreliable, and a held position
            // must not complete an answer or the End test button (same rule as a held gap sample).
            return;
        }

        // Held (blink) samples never advance dwell: the position is frozen.
        if (message.IsInGap) return;

        // The detector runs on every screen so End test stays dwellable everywhere. Answer targets
        // are deliberately NOT filtered out here: the answer screen's onset reset is the single
        // no-carry-over guard (a filter would mask it — review 02 N5e), and an activation delivered
        // outside the answer screen is discarded in OnTargetActivated.
        _targets.Update(message.Position, message.TimestampMicroseconds);
    }

    private void TrackInvalidGaze()
    {
        if (_invalidSince is not { } since) return;

        long runMs = (long)_timeProvider.GetElapsedTime(since).TotalMilliseconds;
        if (runMs > _longestInvalidMs) _longestInvalidMs = runMs;

        if (_isDemo) return;
        if (_phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2
            && runMs > InvalidGazeVoidMs)
        {
            VoidCurrentTrial(SubPixelProbeVoidReason.InvalidGaze);
        }
    }

    private void OnEyesClosedElapsed()
    {
        if (IsDisposed || _ended) return;

        // The timer is re-armed on every valid sample, so it normally fires exactly 10 s after the
        // last one. The re-check keeps the guard true if a sample lands in the same dispatch batch.
        TimeSpan sinceLastValid = _timeProvider.GetElapsedTime(_lastValidTimestamp);
        if (sinceLastValid < TimeSpan.FromMilliseconds(EyesClosedExitMs)) return;

        EndSession(SubPixelProbeEndReason.EyesClosed);
    }

    private void OnAnswerElapsed()
    {
        if (IsDisposed || _ended) return;
        if (_phase != SubPixelProbePhase.Answer) return;
        EndSession(SubPixelProbeEndReason.NoAnswer);
    }

    private void OnPhaseElapsed()
    {
        if (IsDisposed || _ended) return;

        switch (_phase)
        {
            case SubPixelProbePhase.Interval1:
                _phase = SubPixelProbePhase.Gap;
                _phaseStartTimestamp = _timeProvider.GetTimestamp();
                if (!RaisePhaseAdvanced()) return;
                _phaseTimer.Change(TimeSpan.FromMilliseconds(GapMs), Timeout.InfiniteTimeSpan);
                break;

            case SubPixelProbePhase.Gap:
                _phase = SubPixelProbePhase.Interval2;
                _phaseStartTimestamp = _timeProvider.GetTimestamp();
                if (!RaisePhaseAdvanced()) return;
                _phaseTimer.Change(IntervalDuration, Timeout.InfiniteTimeSpan);
                break;

            case SubPixelProbePhase.Interval2:
                if (_isDemo) BeginNextScoredTrial();
                else BeginAnswer();
                break;
        }
    }

    private TimeSpan IntervalDuration =>
        _currentBlock == SubPixelAbBlock.Live
            ? TimeSpan.FromMilliseconds(LiveIntervalMs)
            : TimeSpan.FromMilliseconds(ScriptedIntervalMs);

    private void BeginNextBlock()
    {
        _currentBlock = _blocks.Dequeue();
        _pending = [.. TrialsFor(_currentBlock)];
        _pendingIndex = 0;
        _voids = 0;
        _aPicks = 0;
        _bPicks = 0;
        _currentTrials.Clear();
        BeginDemo();
    }

    private IReadOnlyList<SubPixelAbTrial> TrialsFor(SubPixelAbBlock block) => block switch
    {
        SubPixelAbBlock.Drift => _schedule.DriftTrials,
        SubPixelAbBlock.Static => _schedule.StaticTrials,
        SubPixelAbBlock.Live => _schedule.LiveTrials,
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Unknown block."),
    };

    private void BeginDemo()
    {
        _isDemo = true;
        _currentTrial = null;
        // The demo previews the first pending trial's motion and order, labelled, unscored.
        _currentMotion = _pending[_pendingIndex].Motion;
        _currentTrialIsAFirst = _pending[_pendingIndex].Order == SubPixelAbOrder.AFirst;
        EnterInterval1();
    }

    private void BeginNextScoredTrial()
    {
        if (_pendingIndex >= _pending.Count)
        {
            CompleteBlock();
            return;
        }

        _isDemo = false;
        _currentTrial = _pending[_pendingIndex++];
        _currentMotion = _currentTrial.Motion;
        _currentTrialIsAFirst = _currentTrial.Order == SubPixelAbOrder.AFirst;
        EnterInterval1();
    }

    private void EnterInterval1()
    {
        if (IsDisposed || _ended) return;

        _phase = SubPixelProbePhase.Interval1;
        _phaseStartTimestamp = _timeProvider.GetTimestamp();
        _invalidSince = null;
        _longestInvalidMs = 0;
        _longestRenderGapMs = 0;

        if (!RaisePhaseAdvanced()) return;
        _phaseTimer.Change(IntervalDuration, Timeout.InfiniteTimeSpan);
    }

    private void BeginAnswer()
    {
        _phase = SubPixelProbePhase.Answer;
        _phaseStartTimestamp = _timeProvider.GetTimestamp();
        _answerStartTimestamp = _phaseStartTimestamp;

        // The buttons go live only now: the reset is what guarantees a fresh dwell, and it is what
        // the boundary test kills — removing it lets dwell accumulated during interval 2 (or the
        // gap) deliver an answer at the first answer-phase sample.
        _targets.ResetAllState();
        _targets.ResetCooldown();

        if (!RaisePhaseAdvanced()) return;
        _answerTimer.Change(TimeSpan.FromMilliseconds(AnswerTimeoutMs), Timeout.InfiniteTimeSpan);
    }

    private bool RaisePhaseAdvanced()
    {
        PhaseAdvanced?.Invoke();
        return !IsDisposed && !_ended;
    }

    private void OnTargetActivated(string id)
    {
        if (IsDisposed || _ended) return;

        if (id == EndTestTargetId)
        {
            InvokeAction(id);
            EndTest();
            return;
        }

        if (id is AnswerOneTargetId or AnswerTwoTargetId)
        {
            // Dwell accumulated outside the answer screen is discarded: the buttons are live only
            // there, and the onset reset makes the answer dwell start from onset.
            if (_phase != SubPixelProbePhase.Answer || _currentTrial is not { } trial) return;

            SubPixelAbCondition picked = id == AnswerOneTargetId
                ? Interval1Condition
                : Interval2Condition;

            if (picked == SubPixelAbCondition.B) _bPicks++;
            else _aPicks++;

            _answerTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _currentTrials.Add(new SubPixelProbeTrialOutcome(
                trial, picked, null,
                (long)_timeProvider.GetElapsedTime(_answerStartTimestamp).TotalMilliseconds,
                _longestInvalidMs, _longestRenderGapMs));

            InvokeAction(id);
            if (IsDisposed || _ended) return;

            BeginNextScoredTrial();
            return;
        }

        InvokeAction(id);
    }

    private void VoidCurrentTrial(SubPixelProbeVoidReason reason)
    {
        if (_currentTrial is not { } trial || _isDemo) return;

        _currentTrials.Add(new SubPixelProbeTrialOutcome(
            trial, null, reason, 0, _longestInvalidMs, _longestRenderGapMs));
        _voids++;

        if (_voids > SubPixelAbVerdict.MaxVoidsPerBlock)
        {
            CompleteBlock();
            return;
        }

        _pending.Add(_schedule.Requeue(trial));
        BeginNextScoredTrial();
    }

    private void CompleteBlock()
    {
        var outcome = new SubPixelProbeBlockOutcome(
            _currentBlock,
            new SubPixelAbBlockTally(_aPicks, _bPicks, _voids),
            [.. _currentTrials]);

        _completedBlocks.Add(outcome);
        _phase = SubPixelProbePhase.Idle;
        _currentTrial = null;
        _currentMotion = null;

        BlockCompleted?.Invoke(outcome);
        if (IsDisposed) return;

        // A void-capped drift block removes the session-1 verdict and skips the static block.
        if (_session == SubPixelProbeSessionKind.Session1
            && _currentBlock == SubPixelAbBlock.Drift
            && outcome.Tally.Voids <= SubPixelAbVerdict.MaxVoidsPerBlock)
        {
            BeginNextBlock();
            return;
        }

        EndSession(SubPixelProbeEndReason.BlocksCompleted);
    }

    private void EndSession(SubPixelProbeEndReason reason)
    {
        if (IsDisposed || _ended) return;

        _ended = true;
        _phase = SubPixelProbePhase.Ended;
        _currentTrial = null;
        StopTimers();
        _targets.ClearTargets();

        // SessionCompleted is the terminal raise: every timer is already stopped and nothing runs
        // after it, so a dispose inside a handler needs no post-raise guard here. The raises that do
        // have a continuation (PhaseAdvanced, BlockCompleted) check IsDisposed explicitly.
        SessionCompleted?.Invoke(new SubPixelProbeSessionOutcome(
            _session,
            reason,
            Aborted: reason != SubPixelProbeEndReason.BlocksCompleted,
            [.. _completedBlocks]));
    }

    private void StopTimers()
    {
        _phaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _eyesClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _answerTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void InvokeAction(string id)
    {
        if (_actions.TryGetValue(id, out var action)) action();
    }

    /// <summary>
    /// Triggers a registered target's activation logic directly, without a real dwell. Used by the
    /// view's mouse fallbacks and by unit tests.
    /// </summary>
    /// <param name="id">Target id, one of the C5 constants.</param>
    public void SimulateActivated(string id) => OnTargetActivated(id);

    /// <inheritdoc/>
    /// <remarks>
    /// C5 knows its three target ids; for them the dwell time is
    /// <see cref="DwellSettings.ToolbarButtonDwellMs"/> read from the injected settings at
    /// registration, so the owner's live value applies. Unknown ids use <paramref name="dwellMs"/>.
    /// The hit radius is the caller's to compute (the answer buttons' half-shorter-side, in
    /// physical px). All categories share C5's one detector; <paramref name="category"/> is accepted
    /// for <see cref="IDwellRegistrar"/> compatibility.
    /// </remarks>
    public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated)
    {
        if (IsDisposed) return;

        _actions[id] = onActivated;
        int effectiveDwellMs = IsKnownTarget(id) ? _dwellSettings.ToolbarButtonDwellMs : dwellMs;
        _targets.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0), radiusPhysical, effectiveDwellMs));
    }

    /// <inheritdoc/>
    public void Unregister(string id, DwellCategory category)
    {
        _actions.Remove(id);
        _targets.RemoveTarget(id);
    }

    /// <inheritdoc/>
    public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) =>
        _targets.UpdateTargetCenter(id, center);

    /// <inheritdoc/>
    public double GetProgress(string id, DwellCategory category) => _targets.GetProgress(id);

    /// <summary>Whether <paramref name="id"/> is one of C5's own dwell targets.</summary>
    /// <param name="id">Target id to test.</param>
    /// <returns><c>true</c> for the two answer buttons and the End test button.</returns>
    public static bool IsKnownTarget(string id) =>
        id is AnswerOneTargetId or AnswerTwoTargetId or EndTestTargetId;

    /// <summary>Stops every timer and clears the dwell targets. The messenger unregisters in the base.</summary>
    protected override void OnDispose()
    {
        StopTimers();
        _targets.ClearTargets();
        _phaseTimer.Dispose();
        _eyesClosedTimer.Dispose();
        _answerTimer.Dispose();
    }
}
