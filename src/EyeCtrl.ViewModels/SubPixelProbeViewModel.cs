namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>Which of the test's two sessions is running.</summary>
public enum SubPixelProbeSessionKind
{
    /// <summary>The drift block (decides session 1) then the static block (supporting evidence).</summary>
    Session1,

    /// <summary>The live-gaze block; run only after a session-1 conviction.</summary>
    Session2,
}

/// <summary>Which run a state machine starts.</summary>
public enum SubPixelProbeStartMode
{
    /// <summary>The full run: Intro → session 1 → Ask/Result → session 2 → Result.</summary>
    FullRun,

    /// <summary>
    /// A session-2 resume: Intro → session-2 (live) blocks → Result. Used when session 1 already
    /// convicted and its result was persisted; the caller supplies that verdict.
    /// </summary>
    ResumeSession2,
}

/// <summary>The phase the run is in.</summary>
public enum SubPixelProbePhase
{
    /// <summary>Nothing is running: before <see cref="SubPixelProbeViewModel.Start"/>, between blocks, or after the run ended.</summary>
    Idle,

    /// <summary>The Intro screen: how to answer and how to leave, until Start.</summary>
    Intro,

    /// <summary>Interval 1: the first condition, 3 s (live: 4 s).</summary>
    Interval1,

    /// <summary>The 0.8 s blank gap between the intervals.</summary>
    Gap,

    /// <summary>Interval 2: the other condition, same motion.</summary>
    Interval2,

    /// <summary>The answer screen: up to 60 s for a completed answer dwell.</summary>
    Answer,

    /// <summary>
    /// A void has just been recorded: the next trial waits until a valid sample arrives, so one eye
    /// closure costs one trial (Q2). Only the 10 s eyes-closed exit runs, and it ends the run.
    /// </summary>
    VoidHold,

    /// <summary>The "Continue to the live-gaze part now?" screen, shown only after a session-1 conviction.</summary>
    Ask,

    /// <summary>The Result screen, shown until it is acknowledged (or its 60 s timeout).</summary>
    Result,

    /// <summary>The run has ended, whether normally or through an exit.</summary>
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
    /// <summary>No valid gaze for more than 500 ms inside an interval, with invalid samples arriving.</summary>
    InvalidGaze,

    /// <summary>The view reported an interval render gap above 50 ms.</summary>
    RenderGap,

    /// <summary>No valid gaze for more than 500 ms inside an interval, with no samples arriving.</summary>
    Silence,
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

    /// <summary>No answer within 60 s of the screen appearing.</summary>
    NoAnswer,
}

/// <summary>Why the whole run ended.</summary>
public enum SubPixelProbeRunEndReason
{
    /// <summary>The run reached its normal end: the Result screen was acknowledged.</summary>
    Completed,

    /// <summary>End test ended the run.</summary>
    EndTest,

    /// <summary>10 s passed with no valid gaze sample.</summary>
    EyesClosed,

    /// <summary>A screen timeout expired (Intro, answer or Ask).</summary>
    NoAnswer,

    /// <summary>The Result screen's timeout expired: the run was already complete (F14).</summary>
    ResultTimeout,
}

/// <summary>
/// One presented scored trial's outcome. A voided trial carries <see cref="VoidReason"/> and no
/// answer; it is repeated at the end of its block.
/// </summary>
/// <param name="Trial">The trial that was presented, with its order and motion parameters.</param>
/// <param name="Answer">The condition the owner picked, or <c>null</c> when the trial was voided.</param>
/// <param name="VoidReason">Why the trial was voided, or <c>null</c> when it was answered.</param>
/// <param name="ResponseTimeMs">Answer latency from answer-screen onset; 0 for a voided trial.</param>
/// <param name="LongestInvalidGazeMs">Longest continuous invalid-gaze run observed inside the trial's intervals.</param>
/// <param name="LongestRenderGapMs">Largest render gap the view reported inside the trial's intervals.</param>
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
/// <c>true</c> when an exit (End test, closed eyes, no answer) stopped the session mid-run; a
/// session that ran out its blocks — including a void-capped block — is <c>false</c>.
/// </param>
/// <param name="Blocks">
/// The blocks of this session that finished, in order. A session aborted mid-block still carries the
/// blocks that completed before the abort, so an abort never loses a completed result; only a
/// session aborted inside its first block ends with none.
/// </param>
public sealed record SubPixelProbeSessionOutcome(
    SubPixelProbeSessionKind Session,
    SubPixelProbeEndReason Reason,
    bool Aborted,
    IReadOnlyList<SubPixelProbeBlockOutcome> Blocks);

/// <summary>
/// C5 — the sub-pixel visibility test's run state machine.
/// <para>
/// One run owns every screen: <c>Intro → session-1 blocks → Ask (only on a session-1 conviction) →
/// session-2 blocks → Result</c>. A <see cref="SubPixelProbeStartMode.ResumeSession2"/> run skips
/// session 1 and its Ask — Intro → session-2 blocks → Result — continuing from a persisted,
/// convicting session-1 verdict. Within a block the flow is demo pair → interval 1 → 0.8 s gap →
/// interval 2 → answer → next. Takes <see cref="GazeCursorMovedMessage"/> for dwell and positions,
/// and validity through an injected callback because the message carries no validity flag.
/// </para>
/// <para>
/// <b>Exits (every phase):</b> the End test button, the 10 s time-since-last-valid-sample eyes-closed
/// exit, and a screen timeout on the screens that wait for input (Intro, answer, Ask, Result):
/// <see cref="IntroTimeoutMs"/> (80 s, SP-4c D5) on the Intro, <see cref="ScreenTimeoutMs"/> (60 s)
/// elsewhere. A session that is live when an exit fires first raises <see cref="SessionCompleted"/>
/// for it, then <see cref="RunEnded"/> for the run.
/// </para>
/// <para>
/// <b>Voids</b> (scored trials, inside interval 1 or 2 only): more than 500 ms with <b>no valid
/// gaze</b> — invalid samples and silence count as one run (F13) — or a view-reported render gap
/// above 50 ms. A voided trial is repeated at the end of its block, and the run then holds in
/// <see cref="SubPixelProbePhase.VoidHold"/> until a valid sample arrives, so one eye closure costs
/// one trial (Q2); the eyes-closed exit keeps running during the hold and ends the run as an abort.
/// A block's 4th void makes it incomplete and ends it (a void-capped drift block skips the static
/// block).
/// </para>
/// <para>
/// <b>Single-threaded (review 05 F4b).</b> The injected <see cref="TimeProvider"/> must deliver its
/// timer callbacks on the same thread as <see cref="Receive"/> — in production a dispatcher-backed
/// adapter (SP-4b). The gaze thread never enters: <see cref="Receive"/> is messenger-delivered on
/// the UI thread.
/// </para>
/// <para>
/// Every lasting phase change goes through <see cref="TransitionTo"/>, which assigns
/// <see cref="Phase"/>, then stops if a handler disposed or ended the run, then raises
/// <see cref="PhaseAdvanced"/> and stops again — so nothing is raised after <see cref="RunEnded"/>,
/// after a dispose, or after an <c>EndTest</c> made inside any handler, including
/// <c>PropertyChanged</c> on <see cref="Phase"/> (F9). <see cref="Dispose"/> stops all four timers
/// and unregisters from the messenger.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Validity is the latest sample's, not the message's (review 01 F5).</b> The validity callback
/// reads the tracker's newest published state, while the message being processed was marshalled
/// earlier, so under UI backlog a message can be judged by a slightly newer sample. That is
/// acceptable for the 500 ms / 10 s thresholds and must not be read as per-message validity.
/// </para>
/// <para>
/// The timers are a one-shot phase timer re-armed per phase, a one-shot eyes-closed timer re-armed
/// on every valid sample, a one-shot screen timer armed at Intro/answer/Ask/Result, and a one-shot
/// silence guard armed inside each interval (it re-arms to the exact millisecond the current silence
/// would cross 500 ms).
/// </para>
/// </remarks>
public partial class SubPixelProbeViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IDwellRegistrar
{
    /// <summary>Target id of the button answering "1" (condition shown in interval 1).</summary>
    public const string AnswerOneTargetId = "subpixel_answer_1";

    /// <summary>Target id of the button answering "2" (condition shown in interval 2).</summary>
    public const string AnswerTwoTargetId = "subpixel_answer_2";

    /// <summary>Target id of the always-present End test button.</summary>
    public const string EndTestTargetId = "subpixel_end_test";

    /// <summary>Target id of the Intro screen's Start button.</summary>
    public const string IntroStartTargetId = "subpixel_intro_start";

    /// <summary>Target id of the Ask screen's Yes button ("continue to live gaze").</summary>
    public const string AskYesTargetId = "subpixel_ask_yes";

    /// <summary>Target id of the Ask screen's Later button.</summary>
    public const string AskLaterTargetId = "subpixel_ask_later";

    /// <summary>Target id of the Result screen's Done button.</summary>
    public const string ResultDoneTargetId = "subpixel_result_done";

    /// <summary>Interval 1 and interval 2 duration of the scripted blocks, in milliseconds.</summary>
    public const int ScriptedIntervalMs = 3000;

    /// <summary>Interval 1 and interval 2 duration of the live block, in milliseconds.</summary>
    public const int LiveIntervalMs = 4000;

    /// <summary>The blank gap between the two intervals, in milliseconds.</summary>
    public const int GapMs = 800;

    /// <summary>How long a screen that waits for input (answer, Ask, Result) waits before aborting.</summary>
    public const int ScreenTimeoutMs = 60_000;

    /// <summary>
    /// How long the Intro screen waits before aborting (SP-4c D5). Longer than
    /// <see cref="ScreenTimeoutMs"/> because the owner watches the Intro's motion demo first —
    /// 80 s is several demo loops. It must stay below C7's 90 s no-progress watchdog
    /// (<c>SubPixelProbeCoordinator.NoProgressMs</c>), which stamps only phase transitions: with a
    /// longer Intro the watchdog would abort the run first and this timer could never fire. Pinned
    /// by <c>SubPixelProbeIntroTimeoutTests</c>.
    /// </summary>
    public const int IntroTimeoutMs = 80_000;

    /// <summary>
    /// The pause grace every probe dwell target gets (SP-4c D4): leaving the hit circle, or a
    /// dropout (invalid or blink sample) for up to this long, pauses the button's dwell progress
    /// instead of resetting it. Opt-in at the detector; no other surface passes it.
    /// </summary>
    public const int DwellPauseGraceMs = 300;

    /// <summary>Invalid-gaze (or silence) time inside one interval that voids the trial; more than this voids.</summary>
    public const int InvalidGazeVoidMs = 500;

    /// <summary>View-reported render gap that voids the trial; more than this voids.</summary>
    public const int RenderGapVoidMs = 50;

    /// <summary>Time since the last valid sample after which the run ends (closed eyes, silence).</summary>
    public const int EyesClosedExitMs = 10_000;

    private readonly SubPixelProbeStartMode _startMode;
    private readonly TimeProvider _timeProvider;
    private readonly SubPixelAbSchedule _schedule;
    private readonly DwellSettings _dwellSettings;
    private readonly Func<bool> _isValidSample;
    private readonly DwellDetector _targets;
    private readonly Dictionary<string, Action> _actions = new();
    private readonly List<SubPixelProbeBlockOutcome> _sessionBlocks = new();
    private readonly List<SubPixelProbeTrialOutcome> _currentTrials = new();

    private readonly ITimer _phaseTimer;
    private readonly ITimer _eyesClosedTimer;
    private readonly ITimer _screenTimer;
    private readonly ITimer _voidGuardTimer;

    // The current block's presentations that have not run yet; a voided trial is appended at the end.
    private List<SubPixelAbTrial> _pending = new();
    private int _pendingIndex;

    // The current session's blocks and its position in them.
    private List<SubPixelAbBlock> _blocks = new();
    private int _blockIndex;

    /// <summary>The current phase. An observable property, so SP-4b can bind to <c>Phase</c> and sees every change (review 01 F8).</summary>
    [ObservableProperty]
    private SubPixelProbePhase _phase = SubPixelProbePhase.Idle;

    private SubPixelProbeSessionKind? _currentSession;
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
    private long _voidGuardArmedAt;
    private bool _runSawInvalidSample;
    private long _longestInvalidMs;
    private long _longestRenderGapMs;
    private long _screenStartTimestamp;

    /// <summary>
    /// Creates the run state machine. It is inert until <see cref="Start"/>.
    /// </summary>
    /// <param name="messenger">Messenger the view model receives <see cref="GazeCursorMovedMessage"/> from.</param>
    /// <param name="timeProvider">
    /// Clock and timer source. All timer callbacks must run on the UI thread: production injects a
    /// dispatcher-backed adapter (SP-4b), tests a fake advanced together with the gaze timestamps.
    /// </param>
    /// <param name="schedule">The run's trial plan, from <see cref="SubPixelAbSchedule.Create"/>.</param>
    /// <param name="dwellSettings">Dwell configuration; the button dwell is read from it live at registration.</param>
    /// <param name="isValidSample">
    /// Validity of the tracker's most recent sample, fed by <c>GazeSampled</c>. Queried on the UI
    /// thread for every gaze message; <c>false</c> means the gaze is currently invalid. A missing
    /// sample is silence, handled by the silence guard and the eyes-closed timer.
    /// </param>
    /// <param name="startMode">The run to start: the full run, or a session-2 resume.</param>
    /// <param name="resumedSession1Verdict">
    /// The persisted session-1 verdict a <see cref="SubPixelProbeStartMode.ResumeSession2"/> run
    /// continues from. Required, and must convict, for a resume; ignored for a full run.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="startMode"/> is a session-2 resume whose
    /// <paramref name="resumedSession1Verdict"/> is missing or does not convict. Callers should
    /// gate on <see cref="CanResumeSession2"/> and surface their own refusal reason.
    /// </exception>
    public SubPixelProbeViewModel(
        IMessenger messenger,
        TimeProvider timeProvider,
        SubPixelAbSchedule schedule,
        DwellSettings dwellSettings,
        Func<bool> isValidSample,
        SubPixelProbeStartMode startMode = SubPixelProbeStartMode.FullRun,
        SubPixelAbSession1Verdict? resumedSession1Verdict = null)
        : base(messenger)
    {
        if (startMode == SubPixelProbeStartMode.ResumeSession2 && !CanResumeSession2(resumedSession1Verdict))
        {
            throw new ArgumentException(
                "A session-2 resume needs a convicting session-1 verdict.", nameof(resumedSession1Verdict));
        }

        _startMode = startMode;
        if (startMode == SubPixelProbeStartMode.ResumeSession2)
            Session1Verdict = resumedSession1Verdict;

        _timeProvider = timeProvider;
        _schedule = schedule;
        _dwellSettings = dwellSettings;
        _isValidSample = isValidSample;

        _targets = new DwellDetector(
            dwellSettings.DwellCooldownMs, pauseGraceMs: DwellPauseGraceMs);
        _targets.TargetActivated += OnTargetActivated;

        _phaseTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnPhaseElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _eyesClosedTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnEyesClosedElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _screenTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnScreenElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _voidGuardTimer = timeProvider.CreateTimer(
            static state => ((SubPixelProbeViewModel)state!).OnVoidGuardElapsed(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised when a session's blocks all ran or its exit stopped it; before Ask/Result on a normal end.</summary>
    public event Action<SubPixelProbeBlockOutcome>? BlockCompleted;

    /// <summary>Raised exactly once per session that started, normally or through an abort.</summary>
    public event Action<SubPixelProbeSessionOutcome>? SessionCompleted;

    /// <summary>Raised at every phase transition, including the Idle between blocks and the terminal Ended (fixup 04 HR7).</summary>
    public event Action? PhaseAdvanced;

    /// <summary>The terminal event: raised once, last, when the run ends. C7 restores on it.</summary>
    public event Action<SubPixelProbeRunEndReason>? RunEnded;

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

    /// <summary>Session 1's verdict, once session 1 has ended; <c>null</c> before and during it.</summary>
    public SubPixelAbSession1Verdict? Session1Verdict { get; private set; }

    /// <summary>Session 2's verdict, once the live block has ended; <c>null</c> otherwise.</summary>
    public SubPixelAbSession2Verdict? Session2Verdict { get; private set; }

    /// <summary>
    /// The session whose blocks are live, or <c>null</c> on Intro/Ask/Result and after an abort.
    /// C7's watchdog resets its per-session cap when this becomes
    /// <see cref="SubPixelProbeSessionKind.Session2"/>.
    /// </summary>
    public SubPixelProbeSessionKind? CurrentSession => _currentSession;

    /// <summary>Milliseconds elapsed in the current phase, on the injected clock.</summary>
    public long PhaseElapsedMs =>
        Phase == SubPixelProbePhase.Idle ? 0 : (long)_timeProvider.GetElapsedTime(_phaseStartTimestamp).TotalMilliseconds;

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
    /// Starts the run: the Intro screen first. Idempotent; a disposed view model does nothing.
    /// </summary>
    public void Start()
    {
        if (IsDisposed || _started || _ended) return;
        _started = true;
        _lastValidTimestamp = _timeProvider.GetTimestamp();
        _eyesClosedTimer.Change(TimeSpan.FromMilliseconds(EyesClosedExitMs), Timeout.InfiniteTimeSpan);
        EnterScreen(SubPixelProbePhase.Intro);
    }

    /// <summary>
    /// Leaves the Intro screen and starts the run's blocks: session 1 on a full run, session 2 on
    /// a resume. Ignored outside Intro.
    /// </summary>
    public void BeginTrials()
    {
        if (IsDisposed || _ended || Phase != SubPixelProbePhase.Intro) return;
        _screenTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        BeginSession(_startMode == SubPixelProbeStartMode.ResumeSession2
            ? SubPixelProbeSessionKind.Session2
            : SubPixelProbeSessionKind.Session1);
    }

    /// <summary>
    /// Whether a session-2 resume may start from <paramref name="session1Verdict"/>: only a
    /// session 1 that <see cref="SubPixelAbOutcome.Convicts"/> opens the live-gaze part. SP-4b
    /// gates the resume entry on this and can show its own refusal reason.
    /// </summary>
    /// <param name="session1Verdict">The persisted session-1 verdict, or <c>null</c> if none was found.</param>
    /// <returns><c>true</c> when the resume is allowed.</returns>
    public static bool CanResumeSession2(SubPixelAbSession1Verdict? session1Verdict) =>
        session1Verdict?.Outcome == SubPixelAbOutcome.Convicts;

    /// <summary>Ask → Yes: starts session 2 in the same run. Ignored outside Ask.</summary>
    public void ContinueToLiveGaze()
    {
        if (IsDisposed || _ended || Phase != SubPixelProbePhase.Ask) return;
        _screenTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        BeginSession(SubPixelProbeSessionKind.Session2);
    }

    /// <summary>Ask → Later: goes to the Result screen. Ignored outside Ask.</summary>
    public void DeclineLiveGaze()
    {
        if (IsDisposed || _ended || Phase != SubPixelProbePhase.Ask) return;
        _screenTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        EnterScreen(SubPixelProbePhase.Result);
    }

    /// <summary>Result → Done: ends the run with <see cref="SubPixelProbeRunEndReason.Completed"/>.</summary>
    public void AcknowledgeResult()
    {
        if (IsDisposed || _ended || Phase != SubPixelProbePhase.Result) return;
        EndRun(SubPixelProbeRunEndReason.Completed);
    }

    /// <summary>
    /// Ends the run as the End test button does: a live session is closed first (its
    /// <see cref="SessionCompleted"/> carries <see cref="SubPixelProbeEndReason.EndTest"/>), then
    /// <see cref="RunEnded"/> is raised once with the same reason.
    /// </summary>
    public void EndTest() =>
        AbortRun(SubPixelProbeEndReason.EndTest, SubPixelProbeRunEndReason.EndTest);

    /// <summary>
    /// Reports an interval render gap measured by the view. A gap inside interval 1 or 2 is recorded
    /// for the trial's statistics, and above 50 ms voids the current scored trial; other phases
    /// ignore it.
    /// </summary>
    /// <param name="gapMs">The gap between two presented frames, in milliseconds.</param>
    public void ReportRenderGap(long gapMs)
    {
        if (IsDisposed || _ended) return;
        if (Phase is not (SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2) || _isDemo) return;

        if (gapMs > _longestRenderGapMs) _longestRenderGapMs = gapMs;

        if (gapMs > RenderGapVoidMs)
            VoidCurrentTrial(SubPixelProbeVoidReason.RenderGap);
    }

    /// <summary>
    /// Receives the gaze stream: validity, the void hold's release, the interval guard, then dwell.
    /// Inert until <see cref="Start"/>.
    /// </summary>
    /// <param name="message">The per-sample gaze message.</param>
    public void Receive(GazeCursorMovedMessage message)
    {
        // The recipient is registered at construction (MessengerViewModelBase), and gaze messages
        // flow from the moment the engine runs; before Start there is no run to time or score.
        if (!_started || IsDisposed || _ended) return;

        bool valid = _isValidSample();
        if (valid)
        {
            _lastValidTimestamp = _timeProvider.GetTimestamp();
            _eyesClosedTimer.Change(TimeSpan.FromMilliseconds(EyesClosedExitMs), Timeout.InfiniteTimeSpan);
        }
        else if (Phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2 && !_isDemo)
        {
            // Part of the trial's "no valid gaze" run; the void guard's deadline does the timing.
            _runSawInvalidSample = true;
        }

        // Q2: after a void the next trial waits for a valid sample. The eyes-closed exit keeps
        // running throughout, so a long closure ends the run as an abort instead of burning trials.
        if (Phase == SubPixelProbePhase.VoidHold)
        {
            if (!valid) return;
            BeginNextScoredTrial();
            return;
        }

        // Invalid gaze never advances dwell: the position is unreliable, and a held position must
        // not complete an answer or the End test button (same rule as a held gap sample).
        if (!valid) return;

        if (Phase is SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2 && !_isDemo)
        {
            // A valid sample ends the current "no valid gaze" run and starts the next one's clock.
            long runMs = (long)_timeProvider.GetElapsedTime(_voidGuardArmedAt).TotalMilliseconds;
            if (runMs > _longestInvalidMs) _longestInvalidMs = runMs;
            _runSawInvalidSample = false;
            ArmVoidGuard();
        }

        // Held (blink) samples never advance dwell: the position is frozen.
        if (message.IsInGap) return;

        // The detector runs on every screen so End test stays dwellable everywhere. Answer targets
        // are deliberately NOT filtered out here: the answer screen's onset reset is the single
        // no-carry-over guard (a filter would mask it), and an activation delivered outside the
        // answer screen is discarded in OnTargetActivated.
        _targets.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnEyesClosedElapsed()
    {
        if (IsDisposed || _ended) return;

        // The timer is re-armed on every valid sample, so it normally fires exactly 10 s after the
        // last one. The re-check keeps the guard true if a sample lands in the same dispatch batch.
        TimeSpan sinceLastValid = _timeProvider.GetElapsedTime(_lastValidTimestamp);
        if (sinceLastValid < TimeSpan.FromMilliseconds(EyesClosedExitMs)) return;

        AbortRun(SubPixelProbeEndReason.EyesClosed, SubPixelProbeRunEndReason.EyesClosed);
    }

    private void OnScreenElapsed()
    {
        if (IsDisposed || _ended) return;

        // Every session has already reported its end at the Result screen, so its timeout is not an
        // abort: it gets its own "already complete" reason (F14).
        AbortRun(
            SubPixelProbeEndReason.NoAnswer,
            Phase == SubPixelProbePhase.Result
                ? SubPixelProbeRunEndReason.ResultTimeout
                : SubPixelProbeRunEndReason.NoAnswer);
    }

    /// <summary>
    /// The unified void rule (F13): a scored interval with no valid sample for more than 500 ms is
    /// voided. The guard is armed at the interval's first instant and re-armed by every valid
    /// sample, so its due time is the deadline; invalid samples and silence count alike.
    /// </summary>
    private void OnVoidGuardElapsed()
    {
        if (IsDisposed || _ended) return;
        if (Phase is not (SubPixelProbePhase.Interval1 or SubPixelProbePhase.Interval2) || _isDemo) return;

        long runMs = (long)_timeProvider.GetElapsedTime(_voidGuardArmedAt).TotalMilliseconds;
        if (runMs > _longestInvalidMs) _longestInvalidMs = runMs;

        VoidCurrentTrial(_runSawInvalidSample
            ? SubPixelProbeVoidReason.InvalidGaze
            : SubPixelProbeVoidReason.Silence);
    }

    private void OnPhaseElapsed()
    {
        if (IsDisposed || _ended) return;

        switch (Phase)
        {
            case SubPixelProbePhase.Interval1:
                DisarmVoidGuard();
                _phaseStartTimestamp = _timeProvider.GetTimestamp();
                if (!TransitionTo(SubPixelProbePhase.Gap)) return;
                _phaseTimer.Change(TimeSpan.FromMilliseconds(GapMs), Timeout.InfiniteTimeSpan);
                break;

            case SubPixelProbePhase.Gap:
                // The interval-2 void run starts here: no-valid-gaze time that began in the gap (or
                // in interval 1) is not charged to interval 2 (review 01 F1).
                _phaseStartTimestamp = _timeProvider.GetTimestamp();
                if (!TransitionTo(SubPixelProbePhase.Interval2)) return;
                _phaseTimer.Change(IntervalDuration, Timeout.InfiniteTimeSpan);
                ArmVoidGuard();
                break;

            case SubPixelProbePhase.Interval2:
                DisarmVoidGuard();
                if (_isDemo) BeginNextScoredTrial();
                else EnterScreen(SubPixelProbePhase.Answer);
                break;
        }
    }

    private TimeSpan IntervalDuration =>
        _currentBlock == SubPixelAbBlock.Live
            ? TimeSpan.FromMilliseconds(LiveIntervalMs)
            : TimeSpan.FromMilliseconds(ScriptedIntervalMs);

    private void BeginSession(SubPixelProbeSessionKind kind)
    {
        _currentSession = kind;
        _sessionBlocks.Clear();
        _blocks = kind == SubPixelProbeSessionKind.Session1
            ? [SubPixelAbBlock.Drift, SubPixelAbBlock.Static]
            : [SubPixelAbBlock.Live];
        _blockIndex = 0;

        BeginNextBlock();
    }

    private void BeginNextBlock()
    {
        _currentBlock = _blocks[_blockIndex++];
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

    /// <summary>
    /// Begins a presentation's interval 1. Callers have already checked the view model is live; the
    /// guarded transition is what stops a disposed or ended run (or one a handler ends during the
    /// raise) from arming timers.
    /// </summary>
    private void EnterInterval1()
    {
        _phaseStartTimestamp = _timeProvider.GetTimestamp();
        _longestInvalidMs = 0;
        _longestRenderGapMs = 0;

        if (!TransitionTo(SubPixelProbePhase.Interval1)) return;
        _phaseTimer.Change(IntervalDuration, Timeout.InfiniteTimeSpan);
        ArmVoidGuard();
    }

    private void EnterScreen(SubPixelProbePhase phase)
    {
        _phaseStartTimestamp = _timeProvider.GetTimestamp();
        _screenStartTimestamp = _phaseStartTimestamp;

        // A fresh dwell for the screen's buttons: no state carried between screens.
        _targets.ResetAllState();
        _targets.ResetCooldown();
        DisarmVoidGuard();

        if (!TransitionTo(phase)) return;
        _screenTimer.Change(
            TimeSpan.FromMilliseconds(phase == SubPixelProbePhase.Intro ? IntroTimeoutMs : ScreenTimeoutMs),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Arms (or re-arms) the unified void guard (F13): its due time is the instant the current
    /// "no valid gaze" run would cross 500 ms. The deadline starts at the interval's first instant,
    /// so a dropout that began in the gap is counted from the interval, not from the gap.
    /// </summary>
    private void ArmVoidGuard()
    {
        _voidGuardArmedAt = _timeProvider.GetTimestamp();
        _voidGuardTimer.Change(
            TimeSpan.FromMilliseconds(InvalidGazeVoidMs + 1), Timeout.InfiniteTimeSpan);
    }

    private void DisarmVoidGuard() =>
        _voidGuardTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// The one guarded phase transition (F9): assigns <see cref="Phase"/> (raising
    /// <c>PropertyChanged</c>), stops if a handler disposed or ended the run, then raises
    /// <see cref="PhaseAdvanced"/> and stops again if a handler did. Returns <c>false</c> when the
    /// caller must not continue; nothing is raised after a dispose or an end.
    /// </summary>
    private bool TransitionTo(SubPixelProbePhase phase)
    {
        Phase = phase;
        if (IsDisposed || _ended) return false;

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

        if (id == IntroStartTargetId)
        {
            if (Phase != SubPixelProbePhase.Intro) return;
            InvokeAction(id);
            BeginTrials();
            return;
        }

        if (id == AskYesTargetId)
        {
            if (Phase != SubPixelProbePhase.Ask) return;
            InvokeAction(id);
            ContinueToLiveGaze();
            return;
        }

        if (id == AskLaterTargetId)
        {
            if (Phase != SubPixelProbePhase.Ask) return;
            InvokeAction(id);
            DeclineLiveGaze();
            return;
        }

        if (id == ResultDoneTargetId)
        {
            if (Phase != SubPixelProbePhase.Result) return;
            InvokeAction(id);
            AcknowledgeResult();
            return;
        }

        if (id is AnswerOneTargetId or AnswerTwoTargetId)
        {
            // Dwell accumulated outside the answer screen is discarded: the buttons are live only
            // there, and the onset reset makes the answer dwell start from onset.
            if (Phase != SubPixelProbePhase.Answer || _currentTrial is not { } trial) return;

            SubPixelAbCondition picked = id == AnswerOneTargetId
                ? Interval1Condition
                : Interval2Condition;

            if (picked == SubPixelAbCondition.B) _bPicks++;
            else _aPicks++;

            _screenTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _currentTrials.Add(new SubPixelProbeTrialOutcome(
                trial, picked, null,
                (long)_timeProvider.GetElapsedTime(_screenStartTimestamp).TotalMilliseconds,
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

        DisarmVoidGuard();
        _currentTrials.Add(new SubPixelProbeTrialOutcome(
            trial, null, reason, 0, _longestInvalidMs, _longestRenderGapMs));
        _voids++;

        if (_voids > SubPixelAbVerdict.MaxVoidsPerBlock)
        {
            CompleteBlock();
            return;
        }

        _pending.Add(_schedule.Requeue(trial));
        HoldAfterVoid();
    }

    /// <summary>
    /// Q2: after a void the next trial waits for a valid sample. The phase timer and the void guard
    /// are stopped, so only the 10 s eyes-closed exit runs; it ends the run as an abort if the eyes
    /// stay closed, which is exactly the documented way out.
    /// </summary>
    private void HoldAfterVoid()
    {
        DisarmVoidGuard();
        _phaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (!TransitionTo(SubPixelProbePhase.VoidHold)) return;
    }

    private void CompleteBlock()
    {
        var outcome = new SubPixelProbeBlockOutcome(
            _currentBlock,
            new SubPixelAbBlockTally(_aPicks, _bPicks, _voids),
            [.. _currentTrials]);

        _sessionBlocks.Add(outcome);
        _currentTrial = null;
        _currentMotion = null;

        if (!TransitionTo(SubPixelProbePhase.Idle)) return;

        BlockCompleted?.Invoke(outcome);
        if (IsDisposed || _ended) return;

        // A void-capped drift block removes the session-1 verdict and skips the static block.
        if (_currentSession == SubPixelProbeSessionKind.Session1
            && _currentBlock == SubPixelAbBlock.Drift
            && outcome.Tally.Voids <= SubPixelAbVerdict.MaxVoidsPerBlock)
        {
            BeginNextBlock();
            return;
        }

        FinishSession();
    }

    private void FinishSession()
    {
        var kind = _currentSession!.Value;
        _currentSession = null;

        if (kind == SubPixelProbeSessionKind.Session1)
        {
            var drift = _sessionBlocks.First(block => block.Block == SubPixelAbBlock.Drift);
            var staticBlock = _sessionBlocks.FirstOrDefault(block => block.Block == SubPixelAbBlock.Static);
            Session1Verdict = SubPixelAbVerdict.Session1(drift.Tally, staticBlock?.Tally);
        }
        else
        {
            var live = _sessionBlocks.First(block => block.Block == SubPixelAbBlock.Live);
            Session2Verdict = SubPixelAbVerdict.Session2(live.Tally);
        }

        // SessionCompleted is raised before Ask/Result so C11 writes the result first.
        SessionCompleted?.Invoke(new SubPixelProbeSessionOutcome(
            kind, SubPixelProbeEndReason.BlocksCompleted, Aborted: false, [.. _sessionBlocks]));
        if (IsDisposed || _ended) return;

        if (kind == SubPixelProbeSessionKind.Session1)
        {
            EnterScreen(Session1Verdict!.Outcome == SubPixelAbOutcome.Convicts
                ? SubPixelProbePhase.Ask
                : SubPixelProbePhase.Result);
            return;
        }

        EnterScreen(SubPixelProbePhase.Result);
    }

    private void AbortRun(SubPixelProbeEndReason sessionReason, SubPixelProbeRunEndReason runReason)
    {
        if (IsDisposed || _ended) return;

        RaiseAbortedSessionCompleted(sessionReason);
        if (IsDisposed) return;

        EndRun(runReason);
    }

    private void RaiseAbortedSessionCompleted(SubPixelProbeEndReason reason)
    {
        if (_currentSession is not { } kind) return;

        _currentSession = null;
        SessionCompleted?.Invoke(new SubPixelProbeSessionOutcome(
            kind, reason, Aborted: true, [.. _sessionBlocks]));
    }

    private void EndRun(SubPixelProbeRunEndReason reason)
    {
        if (IsDisposed || _ended) return;

        // The run is terminal from here: setting _ended first makes a re-entrant call from any
        // handler a no-op. The terminal sequence is spelled out instead of going through
        // TransitionTo because _ended is already set and must not suppress the PhaseAdvanced.
        _ended = true;
        _currentTrial = null;
        _currentMotion = null;
        StopTimers();
        _targets.ClearTargets();

        Phase = SubPixelProbePhase.Ended;
        if (IsDisposed) return;

        // PhaseAdvanced on the terminal transition (F8), then RunEnded last; a dispose in either
        // handler stops the sequence (F12).
        PhaseAdvanced?.Invoke();
        if (IsDisposed) return;

        RunEnded?.Invoke(reason);
    }

    private void StopTimers()
    {
        _phaseTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _eyesClosedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _screenTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _voidGuardTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Runs a registered target's action and publishes the app's dwell-completion cue first
    /// (SP-4c D1): every honoured activation plays the standard <c>DwellCueService</c> sound, the
    /// same message the toolbar and the other cue surfaces send. A discarded activation (an answer
    /// outside the answer screen, Start outside the Intro) never reaches here, so it stays silent.
    /// </summary>
    private void InvokeAction(string id)
    {
        if (_actions.TryGetValue(id, out var action))
        {
            Messenger.Send(new DwellActivationMessage());
            action();
        }
    }

    /// <summary>
    /// Triggers a registered target's activation logic directly, without a real dwell. Used by the
    /// view's mouse fallbacks and by unit tests.
    /// </summary>
    /// <param name="id">Target id, one of the C5 constants.</param>
    public void SimulateActivated(string id) => OnTargetActivated(id);

    /// <inheritdoc/>
    /// <remarks>
    /// C5 knows its own target ids; for them the dwell time is
    /// <see cref="DwellSettings.ToolbarButtonDwellMs"/> read from the injected settings at
    /// registration, so the owner's live value applies. Unknown ids use <paramref name="dwellMs"/>.
    /// The hit radius is the caller's to compute (the answer/End test buttons'
    /// half-shorter-side, in physical px). All categories share C5's one detector;
    /// <paramref name="category"/> is accepted for <see cref="IDwellRegistrar"/> compatibility.
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
    /// <returns><c>true</c> for the answer, End test and screen buttons.</returns>
    public static bool IsKnownTarget(string id) =>
        id is AnswerOneTargetId or AnswerTwoTargetId or EndTestTargetId
            or IntroStartTargetId or AskYesTargetId or AskLaterTargetId or ResultDoneTargetId;

    /// <summary>Whether a dwell target with this id is registered; the view's registration oracle (C6).</summary>
    /// <param name="id">Target id to test.</param>
    /// <returns><c>true</c> when <see cref="Register"/> has added the target.</returns>
    public bool HasRegisteredTarget(string id) => _actions.ContainsKey(id);

    /// <summary>Stops every timer and clears the dwell targets. The messenger unregisters in the base.</summary>
    protected override void OnDispose()
    {
        StopTimers();
        _targets.ClearTargets();
        _phaseTimer.Dispose();
        _eyesClosedTimer.Dispose();
        _screenTimer.Dispose();
        _voidGuardTimer.Dispose();
    }
}
