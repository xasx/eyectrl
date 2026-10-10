namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>Why the sub-pixel probe start was refused (W8). One member per precondition term.</summary>
internal enum ProbeRefusal
{
    /// <summary>The interaction is paused: resume first.</summary>
    Paused,

    /// <summary>The state is neither FreeLook nor Fixation (RepeatMode, Dragging, ScrollMode, …).</summary>
    NotAllowedState,

    /// <summary>The modes menu, the task switcher or the quick-actions grid is visible.</summary>
    MenusOpen,

    /// <summary>Eye care is enabled; it can raise its own topmost window.</summary>
    EyeCareOn,

    /// <summary>Adaptation is enabled; it can raise its own topmost window.</summary>
    AdaptationOn,

    /// <summary>Settings is open; the probe starts queued behind its close instead.</summary>
    SettingsOpen,

    /// <summary>A probe run is already live.</summary>
    AlreadyRunning,

    /// <summary>Calibration is running.</summary>
    CalibrationActive,
}

/// <summary>
/// C7 — the sub-pixel probe's coordinator (build plan C7, "how it works", in full).
///
/// <para>
/// <b>One run = one <see cref="ProbeSession"/>, passed explicitly.</b> Every continuation, timer,
/// handler and window event captures its own session, so a late callback from run 1 can never
/// affect run 2 (review 03 F3; W1 (d)).
/// </para>
///
/// <para>
/// <b>Start is queued.</b> C10's button arms <see cref="RequestStart"/> around the synchronous
/// Settings close (review 05 F8), the <c>SettingsOpenChanged(false)</c> handler posts
/// <see cref="BeginStart"/> at <see cref="DispatcherPriority.Background"/> so every line of the
/// close handling runs first (review 03 F1/04 E1), and a generation token makes a posted start
/// cancellable by any interlock (review 04 N3).
/// </para>
///
/// <para>
/// <b>The watchdog arms first</b> — before <c>ProbeOpen</c> is set — because the half-started state
/// has no End-test button and a gaze-only owner with supervision off has no other exit (review 04
/// N2). It runs on a <see cref="DispatcherPriority.Normal"/> dispatcher timer (review 05 E7) and
/// aborts on the show deadline, the per-session cap, the no-progress rule or the last-valid-sample
/// backstop.
/// </para>
///
/// <para>
/// <b>Restore marshals first, then is total and per-run idempotent.</b> It runs on the UI thread
/// whatever its trigger (review 04 N1), closes the window and tears the session down even when it
/// has already restored, then undoes the effects in the order that releases dwell suppression
/// before the surfaces that can throw (review 03 F4; review 05 F1/F3).
/// </para>
///
/// <para>
/// Every entry point's catch calls <see cref="Restore"/> and nothing runs after it: the start is a
/// sequence of posted continuations, each checking <c>Aborted</c>, so an exception or a re-entrant
/// <c>EndTest</c> can never leave an undischarged effect behind.
/// </para>
/// </summary>
internal sealed class SubPixelProbeCoordinator : CoordinatorBase
{
    /// <summary>How long the window may take to appear before the run is aborted.</summary>
    internal const long ShowDeadlineMs = 5_000;

    /// <summary>The per-session cap; it resets when session 2 starts (test design, Safety).</summary>
    internal const long SessionCapMs = 15 * 60 * 1000;

    /// <summary>Time without any phase progress before the run is aborted.</summary>
    internal const long NoProgressMs = 90_000;

    /// <summary>C7's own last-valid-sample backstop, independent of C5's eyes-closed exit.</summary>
    internal const long NoValidSampleMs = 12_000;

    /// <summary>The watchdog's tick interval.</summary>
    internal static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(250);

    private readonly ModalInteractionState _modal;
    private readonly IProbeHost _host;
    private readonly Func<long> _nowMs;
    private readonly Func<DispatcherPriority, TimeSpan, Action, IDisposable> _timerFactory;
    private readonly string _resultsDirectory;

    private int _generation;
    private int? _pendingStart;
    private SubPixelProbeStartMode _pendingStartMode = SubPixelProbeStartMode.FullRun;
    private SubPixelAbSession1Verdict? _pendingResumeVerdict;
    private bool _disposed;

    /// <summary>The live run, or <c>null</c>. The only coordinator-level run state there is.</summary>
    private ProbeSession? _session;

    /// <summary>
    /// Creates the coordinator. Effects go through <paramref name="host"/>; the context supplies the
    /// messenger, dispatcher, engine, interaction state and settings at <see cref="CoordinatorBase.Initialize"/>.
    /// </summary>
    /// <param name="modal">The shared modal flags (ProbeOpen, SettingsOpen and their events).</param>
    /// <param name="host">The narrow effects seam (C14).</param>
    /// <param name="nowMs">The clock the watchdog reads; tests inject a fake.</param>
    /// <param name="timerFactory">The watchdog's timer factory; production wraps DispatcherTimer.</param>
    /// <param name="resultsDirectory">Where C11 writes results; tests pass a temp directory.</param>
    public SubPixelProbeCoordinator(
        ModalInteractionState modal,
        IProbeHost host,
        Func<long>? nowMs = null,
        Func<DispatcherPriority, TimeSpan, Action, IDisposable>? timerFactory = null,
        string? resultsDirectory = null)
    {
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _timerFactory = timerFactory ?? CreateDispatcherTimer;
        _resultsDirectory = resultsDirectory ?? AppBootstrapper.DefaultAppDataDir;
    }

    /// <summary>The live session, for tests (W1 (d)/(g), W7).</summary>
    internal ProbeSession? LiveSessionForTest => _session;

    /// <summary>The current generation, for tests.</summary>
    internal int GenerationForTest => _generation;

    /// <summary>
    /// Starts synchronously as the posted path would, for tests that drive the continuations
    /// directly (W1/W7). CaptureAndShow is still posted at ContextIdle, so a test can observe the
    /// half-started state by not pumping.
    /// </summary>
    internal void StartForTest() => StartForTest(SubPixelProbeStartMode.FullRun, null);

    /// <summary>Starts synchronously with an explicit run mode, for tests.</summary>
    /// <param name="startMode">The run to start.</param>
    /// <param name="resumedSession1Verdict">The persisted verdict a resume continues from.</param>
    internal void StartForTest(
        SubPixelProbeStartMode startMode, SubPixelAbSession1Verdict? resumedSession1Verdict)
    {
        _generation++;
        BeginStart(_generation, startMode, resumedSession1Verdict);
    }

    /// <summary>
    /// Records one phase-progress tick, for W7 (c): the cap/reset case must keep the 90 s rule quiet
    /// without waiting out C5's real phase timers.
    /// </summary>
    /// <param name="session">The run to stamp.</param>
    internal void MarkProgressForTest(ProbeSession session) => OnPhaseAdvanced(session);

    protected override void OnInitialize()
    {
        var context = Context!;

        // The queued start waits here. A false edge without a PendingStart starts nothing — the
        // disarm happens on true and in every interlock via the generation bump.
        _modal.SettingsOpenChanged += OnSettingsOpenChanged;

        context.Messenger.Register<SubPixelProbeCoordinator, SettingsRequestedMessage>(this,
            (_, _) => OnInterlock());
        context.Messenger.Register<SubPixelProbeCoordinator, StateChangedMessage>(this,
            (_, m) => OnStateChanged(m));
        context.Messenger.Register<SubPixelProbeCoordinator, GazeCursorMovedMessage>(this,
            (_, m) => OnGazeCursorMoved(m));

        context.EscapeHatch.EscapePressed += OnEscapePressed;
    }

    /// <summary>
    /// C10's request path (build plan C7 step 1): arms <c>PendingStart</c> around the synchronous
    /// Settings close, so a throw from the close still clears it (review 05 F8; W3 (f)).
    /// </summary>
    /// <param name="closeSettings">
    /// Closes the Settings window synchronously — during the call its <c>Closed</c> handler raises
    /// <c>SettingsOpenChanged(false)</c>, which posts the start.
    /// </param>
    /// <param name="startMode">The run to start; <c>ResumeSession2</c> continues a persisted session 1.</param>
    /// <param name="resumedSession1Verdict">The persisted convicting session-1 verdict a resume needs.</param>
    internal void RequestStart(
        Action closeSettings,
        SubPixelProbeStartMode startMode = SubPixelProbeStartMode.FullRun,
        SubPixelAbSession1Verdict? resumedSession1Verdict = null)
    {
        ArgumentNullException.ThrowIfNull(closeSettings);
        try
        {
            _pendingStart = ++_generation;
            _pendingStartMode = startMode;
            _pendingResumeVerdict = resumedSession1Verdict;
            closeSettings();
        }
        finally
        {
            _pendingStart = null;
            _pendingStartMode = SubPixelProbeStartMode.FullRun;
            _pendingResumeVerdict = null;
        }
    }

    /// <summary>
    /// The C10 button's precondition probe (build plan C10 / review 03 F11): <c>null</c> when the
    /// run may start, otherwise the localized one-line reason. Evaluated on activation, never as a
    /// live label.
    /// <para>
    /// <b>The SettingsOpen term is deliberately absent.</b> The button lives inside Settings, which
    /// holds <c>SettingsOpen = true</c> for as long as it is open, so the full precondition would
    /// refuse every press with "Close Settings first". The request closes Settings synchronously,
    /// and <see cref="BeginStart"/> re-checks the full precondition — SettingsOpen included — once
    /// the queued start runs, so a re-opened window still refuses (review 04 N3; fixup 01 B1).
    /// </para>
    /// </summary>
    internal string? GetBlockReason()
    {
        var refusal = ButtonRefusal();
        return refusal is null ? null : ReasonText(refusal.Value);
    }

    /// <summary>
    /// The C10 reason's precondition: everything <see cref="RefusalFor"/> checks except
    /// SettingsOpen, because the button is pressed from inside Settings.
    /// </summary>
    internal static ProbeRefusal? ButtonRefusalFor(
        InteractionState state,
        bool menusVisible,
        bool eyeCareEnabled,
        bool adaptationEnabled,
        bool sessionLive,
        bool calibrationActive) =>
        RefusalFor(state, menusVisible, eyeCareEnabled, adaptationEnabled,
            settingsOpen: false, sessionLive, calibrationActive);

    /// <summary>
    /// The precondition, as a pure function so W8 can kill each term in isolation (the SP-4a gate
    /// pattern). Order of tests decides which reason wins when several are true.
    /// </summary>
    internal static ProbeRefusal? RefusalFor(
        InteractionState state,
        bool menusVisible,
        bool eyeCareEnabled,
        bool adaptationEnabled,
        bool settingsOpen,
        bool sessionLive,
        bool calibrationActive)
    {
        if (state == InteractionState.Paused) return ProbeRefusal.Paused;
        if (state is not (InteractionState.FreeLook or InteractionState.Fixation)) return ProbeRefusal.NotAllowedState;
        if (menusVisible) return ProbeRefusal.MenusOpen;
        if (eyeCareEnabled) return ProbeRefusal.EyeCareOn;
        if (adaptationEnabled) return ProbeRefusal.AdaptationOn;
        if (settingsOpen) return ProbeRefusal.SettingsOpen;
        if (sessionLive) return ProbeRefusal.AlreadyRunning;
        if (calibrationActive) return ProbeRefusal.CalibrationActive;
        return null;
    }

    private static string ReasonText(ProbeRefusal refusal) => StringTable.Current.T(refusal switch
    {
        ProbeRefusal.Paused => "settings.diagnostics.subpixel.reason.paused",
        ProbeRefusal.NotAllowedState => "settings.diagnostics.subpixel.reason.state",
        ProbeRefusal.MenusOpen => "settings.diagnostics.subpixel.reason.menus",
        ProbeRefusal.EyeCareOn => "settings.diagnostics.subpixel.reason.eye_care",
        ProbeRefusal.AdaptationOn => "settings.diagnostics.subpixel.reason.adaptation",
        ProbeRefusal.SettingsOpen => "settings.diagnostics.subpixel.reason.settings",
        ProbeRefusal.AlreadyRunning => "settings.diagnostics.subpixel.reason.running",
        ProbeRefusal.CalibrationActive => "settings.diagnostics.subpixel.reason.calibration",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown refusal."),
    });

    /// <summary>
    /// C10's activation (fixup 02 H2): when the newest persisted result convicts session 1 and has
    /// no session 2, the button resumes session 2 with that verdict; otherwise it starts a full run.
    /// A missing or unreadable result falls back to a full run.
    /// </summary>
    /// <param name="closeSettings">Closes the Settings window synchronously.</param>
    internal void RequestStartAuto(Action closeSettings)
    {
        var verdict = ReadResumeVerdict();
        RequestStart(
            closeSettings,
            SubPixelProbeViewModel.CanResumeSession2(verdict)
                ? SubPixelProbeStartMode.ResumeSession2
                : SubPixelProbeStartMode.FullRun,
            verdict);
    }

    /// <summary>The persisted session-1 verdict a resume would continue from, or <c>null</c>.</summary>
    internal SubPixelAbSession1Verdict? ReadResumeVerdict() =>
        SubPixelAbResultReader.ReadNewestConvictingSession1(_resultsDirectory);

    /// <summary>C10's label predicate: whether the newest persisted result opens a session-2 resume.</summary>
    internal bool CanResumeFromResults() =>
        SubPixelProbeViewModel.CanResumeSession2(ReadResumeVerdict());

    private ProbeRefusal? CurrentRefusal()
    {
        if (Context is null) return ProbeRefusal.SettingsOpen;

        return RefusalFor(
            Context.InteractionManager.CurrentState,
            AnyMenuVisible(),
            Context.Settings.EyeCare.Enabled,
            Context.Settings.Adaptation.Enabled,
            _modal.SettingsOpen,
            _session is not null,
            Context.CalibrationService.IsSessionActive);
    }

    /// <summary>The C10 reason's live-inputs form; see <see cref="ButtonRefusalFor"/>.</summary>
    private ProbeRefusal? ButtonRefusal()
    {
        if (Context is null) return ProbeRefusal.SettingsOpen;

        return ButtonRefusalFor(
            Context.InteractionManager.CurrentState,
            AnyMenuVisible(),
            Context.Settings.EyeCare.Enabled,
            Context.Settings.Adaptation.Enabled,
            _session is not null,
            Context.CalibrationService.IsSessionActive);
    }

    private bool AnyMenuVisible() =>
        Context!.ModesMenuVM.IsVisible
        || Context.TaskSwitcherVM.IsVisible
        || Context.QuickActionsGridVM.IsVisible;

    private void OnSettingsOpenChanged(bool open)
    {
        if (open)
        {
            // Reopening cancels any posted start: its generation is stale now (review 04 N3; W3 (d)).
            _generation++;
            return;
        }

        if (_pendingStart is not { } generation || Context is null) return;

        // Background: every line of OnSettingsWindowClosed and everything it queues runs first
        // (measured, review 04 E1). The posted start re-checks its generation and the precondition.
        var startMode = _pendingStartMode;
        var resumeVerdict = _pendingResumeVerdict;
        Context.Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => BeginStart(generation, startMode, resumeVerdict));
    }

    /// <summary>
    /// The interlock every mid-test trigger shares: the running session ends first, then the
    /// original action continues. Running synchronously inside the trigger's handler is the
    /// ordering mechanism — the messenger guarantees no delivery order (review 04 N7).
    /// </summary>
    private void OnInterlock()
    {
        _generation++;
        if (_session is { } session) Restore(session);
    }

    /// <summary>
    /// Tray "Show toolbar" (W3 (e), W9 (b)): the generation bump is the only guard against a queued
    /// start, and the live run is ended before <paramref name="continueWith"/> runs. The caller
    /// supplies the forced show so W9 can observe the order.
    /// </summary>
    /// <param name="continueWith">The force-show continuation, run after the interlock.</param>
    internal void InterlockForceShowToolbar(Action continueWith)
    {
        ArgumentNullException.ThrowIfNull(continueWith);
        OnInterlock();
        continueWith();
    }

    private void OnStateChanged(StateChangedMessage message)
    {
        if (message.NewState != InteractionState.Paused) return;
        OnInterlock();
    }

    private void OnGazeCursorMoved(GazeCursorMovedMessage message)
    {
        if (_session is { Aborted: false, Window: { } window }) window.UpdateGaze(message.Position);
    }

    /// <summary>
    /// Esc, for a caregiver. Arrives on a thread-pool thread (the hook's contract), so it marshals
    /// and guards inside the dispatched action; the flag-first check keeps the global hook inert
    /// while no probe is up (review 03 F8).
    /// </summary>
    private void OnEscapePressed()
    {
        var dispatcher = Context?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_modal.ProbeOpen && _session is { } session) Restore(session);
        });
    }

    /// <summary>
    /// The queued start. Re-checks its generation, the precondition and the live-session slot; a
    /// refusal is logged so a completed dwell never reads as a missed one (review 04 N3).
    /// </summary>
    /// <param name="generation">The generation the start was posted with.</param>
    internal void BeginStart(int generation) =>
        BeginStart(generation, SubPixelProbeStartMode.FullRun, null);

    /// <summary>Starts, with an explicit run mode (a resume continues a persisted session 1).</summary>
    /// <param name="generation">The generation the start was posted with.</param>
    /// <param name="startMode">The run to start.</param>
    /// <param name="resumedSession1Verdict">The persisted convicting verdict a resume continues from.</param>
    internal void BeginStart(
        int generation, SubPixelProbeStartMode startMode, SubPixelAbSession1Verdict? resumedSession1Verdict)
    {
        if (_disposed || Context is null) return;
        if (generation != _generation) return;
        if (_session is not null) return;

        if (startMode == SubPixelProbeStartMode.ResumeSession2
            && !SubPixelProbeViewModel.CanResumeSession2(resumedSession1Verdict))
        {
            Log.Information("Sub-pixel probe resume refused: session 1 did not convict");
            return;
        }

        var refusal = CurrentRefusal();
        if (refusal is not null)
        {
            Log.Information("Sub-pixel probe start refused: {Refusal}", refusal.Value);
            return;
        }

        var session = CreateSession(startMode, resumedSession1Verdict);
        _session = session;

        // The watchdog arms first, before ProbeOpen is set (review 04 N2; W7 (d)): from here the
        // half-started state always has an exit, whatever a later step does.
        ArmWatchdog(session);

        // Steps 1-6 (review 05 F1): each checks Aborted, each catch restores and returns. Nothing
        // runs after a step's catch, or after a step that re-entered EndTest.
        if (!RunStep(session, () => _host.SetProbeOpen(true))) return;                        // 1
        if (!RunStep(session, _host.HideRing)) return;                                        // 2
        if (!RunStep(session, _host.AcquireProbeSuppression)) return;                         // 3
        if (!RunStep(session, _host.DeactivateToolbar)) return;                               // 4
        if (!RunStep(session, () =>                                                          // 5
        {
            session.KeyboardWasVisible = _host.KeyboardVisible;
            session.KeyboardTouched = true;
            _host.HideKeyboard();
        })) return;
        if (!RunStep(session, () =>                                                          // 6
        {
            session.SummonSuspendedByUs = true;
            _host.SuspendSummon();
        })) return;

        if (session.Aborted) return;
        Context.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            try
            {
                CaptureAndShow(session);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The sub-pixel probe's show sequence threw; restoring");
                Restore(session);
            }
        });
    }

    private ProbeSession CreateSession(
        SubPixelProbeStartMode startMode, SubPixelAbSession1Verdict? resumedSession1Verdict)
    {
        long now = _nowMs();
        var session = new ProbeSession
        {
            StartedMs = now,
            CapStartMs = now,
            LastValidMs = now,
            LastProgressMs = now,
            StartMode = startMode,
            ResumedSession1Verdict = resumedSession1Verdict,
        };

        session.TimeProvider = new DispatcherTimeProvider(Context!.Dispatcher);
        int seed = Random.Shared.Next();
        session.Schedule = SubPixelAbSchedule.Create(seed, PrimaryScreenBoundsPhys());
        session.Writer = CreateWriter(session);
        return session;
    }

    private SubPixelAbResultWriter CreateWriter(ProbeSession session)
    {
        var context = Context!;
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return new SubPixelAbResultWriter(
            Path.Combine(_resultsDirectory, "subpixel-ab-" + stamp + ".json"),
            new SubPixelAbRunInfo(
                session.Schedule!.Seed,
                SubPixelAbRunInfo.CaptureRing(context.Settings.Cursor),
                context.Settings.Dwell.ToolbarButtonDwellMs,
                DpiHelper.ScaleX,
                PrimaryScreenBoundsPhys(),
                DisplayRefreshHz: ProbeDisplayInfo.TryGetRefreshRateHz()),
            session.StartMode,
            session.ResumedSession1Verdict?.LogLine);
    }

    private static PhysicalRect PrimaryScreenBoundsPhys() => new(
        0,
        0,
        DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth),
        DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight));

    private void ArmWatchdog(ProbeSession session)
    {
        session.GazeHandler = point => OnSessionGaze(session, point);
        Context!.GazeEngine.GazeSampled += session.GazeHandler;
        session.WatchdogTimer = _timerFactory(DispatcherPriority.Normal, WatchdogInterval, () => Check(session));
    }

    private void OnSessionGaze(ProbeSession session, GazePoint point)
    {
        // The mean sample interval the results file records (fixup 02 M3). Written on the gaze
        // thread, read at write time; the accumulation is interlocked so the read is safe.
        long previousUs = Interlocked.Exchange(ref session.LastSampleTimestampUs, point.TimestampMicroseconds);
        if (previousUs != 0)
        {
            long deltaUs = point.TimestampMicroseconds - previousUs;
            if (deltaUs > 0)
            {
                Interlocked.Add(ref session.SampleIntervalSumUs, deltaUs);
                Interlocked.Increment(ref session.SampleIntervalCount);
            }
        }

        if (point.IsValid)
        {
            Volatile.Write(ref session.LastSampleWasValid, true);
            Volatile.Write(ref session.LastValidMs, _nowMs());
        }
        else
        {
            Volatile.Write(ref session.LastSampleWasValid, false);
        }
    }

    /// <summary>
    /// Runs one start step. Returns <c>false</c> when the caller must stop: the session was already
    /// aborted, the step threw (its catch restores), or the step re-entered <c>EndTest</c>.
    /// </summary>
    private bool RunStep(ProbeSession session, Action step)
    {
        if (session.Aborted) return false;
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "A sub-pixel probe start step threw; restoring");
            Restore(session);
            return false;
        }

        return !session.Aborted;
    }

    /// <summary>
    /// Steps 2-10 of the start: re-assert the toolbar, re-check the menus, capture the backdrop,
    /// build C5 and C6, show. Every stage is followed by an <c>Aborted</c> check; the whole method
    /// runs under the posted continuation's catch (review 05 F3).
    /// </summary>
    private void CaptureAndShow(ProbeSession session)
    {
        if (session.Aborted) return;
        if (Context is null) return;

        // 2. In-flight gaze-thread decisions can land after BeginStart's posts (review 04 N4):
        // re-assert toolbar inactivity synchronously and abort if a menu appeared.
        _host.AssertToolbarInactive();
        if (_host.AnyMenuVisible)
        {
            Restore(session);
            return;
        }

        // 3. DwmFlush + capture (inside the host).
        session.Backdrop = _host.CaptureBackdrop();

        // 4. The after-capture check (review 05 F3; W1 (c1)).
        if (session.Aborted) return;

        // 5. Construct C5 and assign at once. The local keeps the after-construct check the ONLY
        // barrier: with the step-8 branch removed, `vm.Start()` below still has a live reference and
        // the fake would record Show() (W1-08; fixup 02 L1).
        var vm = _host.CreateViewModel(
            session.Schedule!, session.TimeProvider!,
            () => Volatile.Read(ref session.LastSampleWasValid),
            session.StartMode, session.ResumedSession1Verdict);
        session.Vm = vm;

        // 6.
        if (session.Aborted)
        {
            Teardown(session);
            return;
        }

        HookVm(session);

        // 7. Construct C6 into the session, its close wired to THIS session (review 05 F4; W1 (d)).
        session.Window = _host.CreateWindow(
            session.Backdrop, vm, Context.Settings.Cursor, () => Restore(session));

        // 8.
        if (session.Aborted)
        {
            Teardown(session);
            CloseWindow(session);
            return;
        }

        vm.Start();

        // 9.
        session.Window.Show();
        session.ShownMs = _nowMs();
        session.WindowShown = true;

        // 10. After-Show: a swallowed close during Show() is repaired here (review 05 F4; W1 (c3)).
        if (session.Aborted)
        {
            CloseWindow(session);
            Restore(session);
        }
    }

    private void HookVm(ProbeSession session)
    {
        var vm = session.Vm!;
        session.BlockHandler = o => OnBlockCompleted(session, o);
        session.SessionHandler = o => OnSessionCompleted(session, o);
        session.PhaseHandler = () => OnPhaseAdvanced(session);
        session.RunEndedHandler = r => OnRunEnded(session, r);
        vm.BlockCompleted += session.BlockHandler;
        vm.SessionCompleted += session.SessionHandler;
        vm.PhaseAdvanced += session.PhaseHandler;
        vm.RunEnded += session.RunEndedHandler;
    }

    private static void UnhookVm(SubPixelProbeViewModel vm, ProbeSession session)
    {
        if (session.BlockHandler is { } block) vm.BlockCompleted -= block;
        if (session.SessionHandler is { } done) vm.SessionCompleted -= done;
        if (session.PhaseHandler is { } phase) vm.PhaseAdvanced -= phase;
        if (session.RunEndedHandler is { } ended) vm.RunEnded -= ended;
    }

    private void OnBlockCompleted(ProbeSession session, SubPixelProbeBlockOutcome outcome)
    {
        // Events are dropped once the session aborted: a leaked write would append blocks to the
        // verdict's file for a run that was abandoned (review 05 F3; W1 (g) sibling).
        if (session.Aborted) return;

        try
        {
            SyncWriterCadence(session);
            session.Writer?.WriteBlock(outcome);
        }
        catch (Exception ex) { Log.Error(ex, "Could not write a sub-pixel probe block result"); }
    }

    private void OnSessionCompleted(ProbeSession session, SubPixelProbeSessionOutcome outcome)
    {
        if (session.Aborted) return;

        try
        {
            SyncWriterCadence(session);
            session.Writer?.WriteSession(
                outcome,
                session.Vm?.Session1Verdict,
                session.Vm?.Session2Verdict);
        }
        catch (Exception ex) { Log.Error(ex, "Could not write a sub-pixel probe session result"); }
    }

    private void SyncWriterCadence(ProbeSession session) =>
        session.Writer?.SetSampleCadenceMs(session.MeanSampleIntervalMs);

    private void OnPhaseAdvanced(ProbeSession session)
    {
        Volatile.Write(ref session.LastProgressMs, _nowMs());

        // The per-session cap restarts when session 2 starts (test design, Safety): CurrentSession
        // is C5's own signal for the boundary (SP-3 handoff).
        if (!session.CapResetForSession2
            && session.Vm?.CurrentSession == SubPixelProbeSessionKind.Session2)
        {
            session.CapResetForSession2 = true;
            Volatile.Write(ref session.CapStartMs, _nowMs());
        }
    }

    private void OnRunEnded(ProbeSession session, SubPixelProbeRunEndReason reason)
    {
        try
        {
            SyncWriterCadence(session);
            session.Writer?.WriteRunEnd(reason);
        }
        catch (Exception ex) { Log.Error(ex, "Could not write the sub-pixel probe run result"); }

        // A C5-triggered end is posted: Restore disposes C5, and that must never happen inside
        // C5's own call stack (review 05 F4b).
        if (session.Aborted) return;
        Context!.Dispatcher.BeginInvoke(() => Restore(session));
    }

    /// <summary>
    /// The watchdog's check, callable directly by tests (W7). Aborts the run when the window did
    /// not appear, when the per-session cap or the no-progress rule is exceeded, or when no valid
    /// sample arrived for 12 s.
    /// </summary>
    /// <param name="session">The run to check.</param>
    internal void Check(ProbeSession session)
    {
        if (_disposed || session.Aborted) return;

        long now = _nowMs();

        if (!session.WindowShown && now - session.StartedMs >= ShowDeadlineMs)
        {
            Abort(session, "the window was not shown within 5 s of the start");
            return;
        }

        if (now - session.CapStartMs >= SessionCapMs)
        {
            Abort(session, "the 15-minute session cap expired");
            return;
        }

        if (now - Volatile.Read(ref session.LastProgressMs) >= NoProgressMs)
        {
            Abort(session, "no phase progress for 90 s");
            return;
        }

        if (now - Volatile.Read(ref session.LastValidMs) >= NoValidSampleMs)
        {
            Abort(session, "no valid gaze sample for 12 s");
        }
    }

    private void Abort(ProbeSession session, string reason)
    {
        Log.Warning("Sub-pixel probe aborting: {Reason}", reason);
        Restore(session);
    }

    /// <summary>
    /// The one restore path: marshals first (review 04 N1), closes the window and tears the session
    /// down even when a previous restore already ran, then undoes the effects per-run, each in its
    /// own guard, most dangerous first (review 03 F4).
    /// </summary>
    /// <param name="session">The run to restore.</param>
    internal void Restore(ProbeSession session)
    {
        var context = Context;
        if (context is null) return;

        // 1. Marshal first, before any flag is touched. The only gaze-thread entry is C5's
        // validity callback; the watchdog's GazeSampled handler only stamps a time.
        if (!context.Dispatcher.CheckAccess())
        {
            context.Dispatcher.BeginInvoke(() => Restore(session));
            return;
        }

        // 2. Abort, close and tear down — deliberately BEFORE the restored guard, so a second
        // restore still closes a window that appeared after the first one ran (review 05 F3/F4).
        session.Aborted = true;
        try { CloseWindow(session); }
        catch (Exception ex) { Log.Error(ex, "Closing the sub-pixel probe window failed"); }
        try { Teardown(session); }
        catch (Exception ex) { Log.Error(ex, "Tearing the sub-pixel probe session down failed"); }

        // 3. Undo the rest once per run, each step isolated so one failure cannot skip the next
        // (review 03 F4; W1 (b)).
        if (session.Restored) return;

        // A coordinator-initiated abort (watchdog, Esc, interlock, a failed show) must still leave a
        // reason in the file; a C5-triggered end has already written its own (fixup 02 M3).
        try
        {
            SyncWriterCadence(session);
            session.Writer?.WriteRunEndIfAbsent(SubPixelProbeRunEndReason.EndTest);
        }
        catch (Exception ex) { Log.Error(ex, "Could not write the sub-pixel probe abort result"); }

        session.Restored = true;

        RestoreStep(() => _host.ReleaseProbeSuppression(), "release suppression");           // 1
        RestoreStep(() => _host.SetProbeOpen(false), "clear ProbeOpen");                     // 2
        if (session.SummonSuspendedByUs)
            RestoreStep(_host.ResumeSummon, "resume the summon");                            // 3
        if (session.KeyboardTouched)
            RestoreStep(() => _host.RestoreKeyboard(session.KeyboardWasVisible), "restore the keyboard"); // 4
        RestoreStep(_host.ReassertToolbarVisibility, "reassert the toolbar");                // 5
        RestoreStep(_host.ShowRingIfVisible, "show the ring");                               // 6

        if (ReferenceEquals(_session, session)) _session = null;                             // 7
    }

    private void RestoreStep(Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sub-pixel probe restore: could not {What}", what);
        }
    }

    /// <summary>
    /// Idempotent teardown of a session's own machinery: the watchdog timer, the GazeSampled
    /// handler, C5 and its dispatcher-backed clock. Ignores <c>Restored</c> (review 05 F3).
    /// <para>
    /// The timer/handler/clock half runs once. The C5 half runs whenever a view model is currently
    /// assigned: a construction that re-entered <c>EndTest</c> and was followed by the
    /// after-construct branch must still dispose the view model it assigned after the first
    /// teardown ran (build plan C7 step 4; W1 (c2)).
    /// </para>
    /// </summary>
    private void Teardown(ProbeSession session)
    {
        if (!session.TornDown)
        {
            session.TornDown = true;

            try { session.WatchdogTimer?.Dispose(); }
            catch (Exception ex) { Log.Error(ex, "Stopping the sub-pixel probe watchdog failed"); }
            session.WatchdogTimer = null;

            try
            {
                if (session.GazeHandler is { } handler && Context is not null)
                    Context.GazeEngine.GazeSampled -= handler;
            }
            catch (Exception ex) { Log.Error(ex, "Detaching the sub-pixel probe gaze handler failed"); }
            session.GazeHandler = null;

            try { session.TimeProvider?.Dispose(); }
            catch (Exception ex) { Log.Error(ex, "Disposing the sub-pixel probe clock failed"); }
            session.TimeProvider = null;
        }

        try
        {
            if (session.Vm is { } vm)
            {
                UnhookVm(vm, session);
                _host.DisposeViewModel(vm);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Disposing the sub-pixel probe view model failed"); }
        session.Vm = null;
    }

    /// <summary>
    /// Closes the session's window unless its <c>Closed</c> was observed. Ignores <c>Restored</c>,
    /// so the after-Show path can still close a window that appeared after a restore ran
    /// (review 05 F3; W1 (c3)). Never called from a <c>Closing</c> handler.
    /// </summary>
    private void CloseWindow(ProbeSession session)
    {
        if (session.Window is not { } window || window.ClosedObserved) return;
        window.Close();
    }

    protected override void OnDispose()
    {
        // The generation bump retires any queued start, and the live session is restored (plan C7).
        _generation++;
        _disposed = true;

        if (Context is not null)
        {
            _modal.SettingsOpenChanged -= OnSettingsOpenChanged;
            Context.EscapeHatch.EscapePressed -= OnEscapePressed;
        }

        if (_session is { } session) Restore(session);
    }

    private static IDisposable CreateDispatcherTimer(DispatcherPriority priority, TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(priority) { Interval = interval };
        EventHandler handler = (_, _) => tick();
        timer.Tick += handler;
        timer.Start();
        return new DispatcherTimerHandle(timer, handler);
    }

    private sealed class DispatcherTimerHandle : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly EventHandler _handler;
        private bool _disposed;

        public DispatcherTimerHandle(DispatcherTimer timer, EventHandler handler)
        {
            _timer = timer;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Tick -= _handler;
            _timer.Stop();
        }
    }
}

/// <summary>
/// One run of the sub-pixel probe: every flag, stamp, continuation and object C7 needs, so a late
/// callback from an earlier run can only ever touch its own session (review 03 F3).
/// </summary>
internal sealed class ProbeSession
{
    // Written by the gaze thread (the watchdog's GazeSampled handler), read on the UI thread.
    internal bool LastSampleWasValid;
    internal long LastValidMs;

    // Sample-cadence accumulation, written on the gaze thread with Interlocked (fixup 02 M3).
    internal long LastSampleTimestampUs;
    internal long SampleIntervalSumUs;
    internal int SampleIntervalCount;

    internal long StartedMs;
    internal long CapStartMs;
    internal long LastProgressMs;

    /// <summary>The mean interval between GazeSampled samples, in ms, or <c>null</c> before two arrivals.</summary>
    internal double? MeanSampleIntervalMs
    {
        get
        {
            int count = Volatile.Read(ref SampleIntervalCount);
            return count == 0
                ? null
                : Volatile.Read(ref SampleIntervalSumUs) / 1000.0 / count;
        }
    }

    /// <summary>0 until the window's <c>Show()</c> returned.</summary>
    internal long ShownMs;

    /// <summary>Set with <see cref="ShownMs"/>; a test clock may legitimately start at 0.</summary>
    internal bool WindowShown;

    internal bool Aborted;
    internal bool Restored;
    internal bool TornDown;
    internal bool CapResetForSession2;

    internal bool KeyboardTouched;
    internal bool KeyboardWasVisible;
    internal bool SummonSuspendedByUs;
    internal SubPixelProbeStartMode StartMode;
    internal SubPixelAbSession1Verdict? ResumedSession1Verdict;

    internal IProbeWindow? Window;
    internal SubPixelProbeViewModel? Vm;
    internal SubPixelAbSchedule? Schedule;
    internal BitmapSource? Backdrop;
    internal DispatcherTimeProvider? TimeProvider;
    internal SubPixelAbResultWriter? Writer;
    internal IDisposable? WatchdogTimer;
    internal Action<GazePoint>? GazeHandler;
    internal Action<SubPixelProbeBlockOutcome>? BlockHandler;
    internal Action<SubPixelProbeSessionOutcome>? SessionHandler;
    internal Action? PhaseHandler;
    internal Action<SubPixelProbeRunEndReason>? RunEndedHandler;
}
