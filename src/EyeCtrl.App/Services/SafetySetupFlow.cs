namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Whether the one-time safety setup should be raised at startup, and the raising itself. The
/// decision is pure, so the three conditions are pinned without a window: the prompt shows only
/// while it has never been answered to the end, never on a Supervisor recovery relaunch
/// (<c>--paused</c>: the person may be mid-crash and must not meet five questions), and never in
/// <c>--test-mode</c>.
/// </summary>
internal static class SafetySetupStartup
{
    /// <param name="safetySetupDone"><see cref="GeneralSettings.SafetySetupDone"/>.</param>
    /// <param name="isPausedLaunch"><c>App.IsPausedLaunch</c>.</param>
    /// <param name="isTestMode"><c>App.IsTestMode</c>.</param>
    internal static bool ShouldPrompt(bool safetySetupDone, bool isPausedLaunch, bool isTestMode) =>
        !safetySetupDone && !isPausedLaunch && !isTestMode;

    /// <summary>The startup hook: sends <see cref="SafetySetupRequestedMessage"/> exactly when
    /// <see cref="ShouldPrompt"/> says so. One call at the App's call site, so the decision cannot
    /// be inverted there without a test seeing it.</summary>
    /// <returns>Whether the request was sent.</returns>
    internal static bool MaybeRequest(
        bool safetySetupDone, bool isPausedLaunch, bool isTestMode, IMessenger messenger)
    {
        if (!ShouldPrompt(safetySetupDone, isPausedLaunch, isTestMode)) return false;
        messenger.Send(new SafetySetupRequestedMessage());
        return true;
    }
}

/// <summary>
/// The coordinator's handling of <see cref="SafetySetupRequestedMessage"/>, without the coordinator:
/// open the Settings window when none is open, then start the chain on it. A chain whose window the
/// request opened closes that window when it ends, so the owner gets normal use back (A2 fixup-01
/// M3); a window that was already open stays open.
/// </summary>
internal static class SafetySetupLaunch
{
    /// <param name="windowOpen">Whether a Settings window is already open.</param>
    /// <param name="openWindow">Opens a fresh Settings window.</param>
    /// <param name="start">Starts the chain on the (now open) window; the argument is
    /// <c>closeWhenDone</c>.</param>
    internal static void Run(bool windowOpen, Action openWindow, Action<bool> start)
    {
        if (!windowOpen) openWindow();
        start(!windowOpen);
    }
}

/// <summary>
/// The Settings window's side of the chain, WPF-free so it is headlessly testable: one flow at a
/// time, never started over a modal that is already up (that modal is never replaced; a first-run
/// request then asks again at the next start because the flag is still false), abandoned when the
/// window closes, and — when the chain's start opened the window — closing the window when the
/// chain ends, by its last answer or by "Later".
/// </summary>
internal sealed class SafetySetupHost
{
    private readonly Func<bool> _isModalVisible;
    private readonly Func<Action<bool>, SafetySetupFlow> _createFlow;
    private readonly Action _closeWindow;

    private SafetySetupFlow? _flow;
    private bool _closeWhenDone;

    /// <param name="isModalVisible">Whether the window's modal is up right now.</param>
    /// <param name="createFlow">Builds a flow that reports its end (true = answered to the end,
    /// false = "Later") to the given callback.</param>
    /// <param name="closeWindow">Closes the window (deferred: the end is reported from inside a
    /// modal choice's dispatch).</param>
    internal SafetySetupHost(
        Func<bool> isModalVisible, Func<Action<bool>, SafetySetupFlow> createFlow, Action closeWindow)
    {
        _isModalVisible = isModalVisible;
        _createFlow = createFlow;
        _closeWindow = closeWindow;
    }

    /// <summary>Starts the chain, unless a modal is up.</summary>
    /// <param name="closeWhenDone">Close the window when the chain ends (the request opened it).</param>
    /// <returns>Whether the chain started.</returns>
    internal bool Start(bool closeWhenDone)
    {
        if (_isModalVisible()) return false;

        _flow?.Abandon();
        _closeWhenDone = closeWhenDone;
        _flow = _createFlow(OnEnded);
        _flow.Start();
        return true;
    }

    /// <summary>The window closed: a pending crash-recovery result must not reach its view model.</summary>
    internal void OnWindowClosed()
    {
        _flow?.Abandon();
        _flow = null;
    }

    private void OnEnded(bool completed)
    {
        if (_closeWhenDone) _closeWindow();
    }
}

/// <summary>
/// The one-time safety setup: an intro card, then five questions chained through the Settings
/// window's dwell modal, each answer applied at once through the path the matching Settings row uses.
/// "Later" on the intro, closing the window, or any other way of abandoning the chain simply stops
/// it: the answers already given stay applied and <see cref="GeneralSettings.SafetySetupDone"/> stays
/// false, so the next start asks again.
///
/// <para>
/// <b>No card answers itself.</b> The question cards follow one another at the same place, so a gaze
/// still resting on the last answer sits on the next card's button. The Settings window's modal
/// holds every newly shown choice disarmed until the gaze has been outside it
/// (<c>LeaveBeforeArmLatch</c> in <see cref="SettingsWindowViewModel"/>); the flow relies on that.
/// </para>
///
/// <para>
/// <b>Crash recovery can fail after the chain has moved on</b> (<c>RequestEnable</c> is asynchronous
/// and rolls back; the bring-up polls for up to <see cref="SupervisorBringUp.StartWait"/>). The
/// questions never wait for it: the existing failure text is held and shown, with one
/// acknowledgement button, at the next boundary between the chain's own cards. But the chain does not
/// END while the bring-up is still in flight (A2 review 02 N2): after the last answer it shows a
/// "Checking crash recovery" card with no choices and ends only on the result — at once on success,
/// after the failure card's OK on failure. A ceiling of <see cref="CrashRecoveryCeiling"/> from the
/// request turns a result that never comes into a "could not be confirmed" failure, so the window
/// cannot stay up forever. Abandoning the chain stops the wait, so a result never lands on a modal
/// that is not the chain's.
/// </para>
/// </summary>
internal sealed class SafetySetupFlow
{
    /// <summary>How many questions the chain asks.</summary>
    internal const int QuestionCount = 5;

    /// <summary>The longest the chain waits for crash recovery's bring-up, from the request: the
    /// bring-up's own poll plus 5 s of slack.</summary>
    internal static readonly TimeSpan CrashRecoveryCeiling = SupervisorBringUp.StartWait + TimeSpan.FromSeconds(5);

    // Dwell-target ids, not text: constants, so the text-literal ratchet reads them as ids.
    private const string StartId = "safety_start";
    private const string LaterId = "safety_later";
    private const string OkId = "safety_ok";
    private const string YesId = "safety_yes";
    private const string NoId = "safety_no";
    private const string BasicId = "safety_basic";
    private const string AllId = "safety_all";

    private readonly EyeCtrlSettings _settings;
    private readonly SupervisionToggleService? _supervision;
    private readonly Action<string, IReadOnlyList<ModalChoice>> _showModal;
    private readonly Action _applyAndSave;
    private readonly Action<bool> _setSimpleSettingsUi;
    private readonly Action<bool> _onEnded;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private string? _pendingFailure;
    private bool _active;
    private bool _waitingForSupervision;
    private bool _checking;
    private IDisposable? _ceiling;

    /// <param name="settings">The live settings the answers are written to.</param>
    /// <param name="supervision">The crash-recovery switch; null on a degraded host, which skips
    /// nothing — the question is still asked and its Yes is simply a no-op.</param>
    /// <param name="showModal">The Settings window's modal: one message, N dwell choices.</param>
    /// <param name="applyAndSave">The window's live-apply + debounced save.</param>
    /// <param name="setSimpleSettingsUi">The window's mode switch (re-renders the pages and the
    /// toggle label), true for Basic.</param>
    /// <param name="onEnded">Told once when the chain ends: true after the last answer (and any held
    /// failure's acknowledgement, and crash recovery's result), false on "Later".</param>
    /// <param name="schedule">Runs an action once after a delay on the UI thread and returns its
    /// cancellation; the crash-recovery ceiling uses it. Defaults to a <c>DispatcherTimer</c> on the
    /// current dispatcher.</param>
    internal SafetySetupFlow(
        EyeCtrlSettings settings,
        SupervisionToggleService? supervision,
        Action<string, IReadOnlyList<ModalChoice>> showModal,
        Action applyAndSave,
        Action<bool> setSimpleSettingsUi,
        Action<bool>? onEnded = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _schedule = schedule ?? DispatcherDelay;
        _settings = settings;
        _supervision = supervision;
        _showModal = showModal;
        _applyAndSave = applyAndSave;
        _setSimpleSettingsUi = setSimpleSettingsUi;
        _onEnded = onEnded ?? (_ => { });
    }

    /// <summary>Starts (or restarts) the chain from the intro card, whatever the flag says.</summary>
    internal void Start()
    {
        _active = true;
        _pendingFailure = null;
        _showModal(StringTable.Current.T("settings.safety.intro.message", QuestionCount),
        [
            new ModalChoice(StartId, T("settings.safety.start"), ModalChoiceKind.Affirmative, () => Advance(0)),
            new ModalChoice(LaterId, T("settings.safety.later"), ModalChoiceKind.Negative, () => End(false)),
        ]);
    }

    /// <summary>Stops listening for a pending crash-recovery result (the window closed, or the chain
    /// was restarted). Answers already given stay applied; nothing is undone.</summary>
    internal void Abandon()
    {
        _active = false;
        _checking = false;
        StopWaiting();
    }

    /// <summary>Stops listening for crash recovery's result and cancels the ceiling.</summary>
    private void StopWaiting()
    {
        _waitingForSupervision = false;
        if (_supervision is not null)
            _supervision.StateChanged -= OnSupervisionStateChanged;
        _ceiling?.Dispose();
        _ceiling = null;
    }

    private static string T(string key) => StringTable.Current.T(key);

    private static string Question(string key, int index) =>
        StringTable.Current.T(key, index + 1, QuestionCount);

    /// <summary>The boundary between cards: a held failure first, then the next question, or the end.</summary>
    private void Advance(int index)
    {
        if (_pendingFailure is { } failure)
        {
            _pendingFailure = null;
            _showModal(failure, [new ModalChoice(OkId, T("settings.safety.ok"), ModalChoiceKind.Affirmative,
                () => Advance(index))]);
            return;
        }

        if (index >= QuestionCount)
        {
            // Never end while crash recovery is still being brought up: the owner said "Yes" to it,
            // and a startup run closes the window at the end (N2). No choices: the result, or the
            // ceiling, moves the chain on.
            if (_waitingForSupervision)
            {
                _checking = true;
                _showModal(T("settings.safety.checking"), []);
                return;
            }

            End(true);
            return;
        }

        Ask(index);
    }

    private void End(bool completed)
    {
        if (!_active) return;
        _active = false;
        _onEnded(completed);
    }

    private void Ask(int index)
    {
        switch (index)
        {
            case 0:
                YesNo("settings.safety.crash.message", yes =>
                {
                    // "No" is deliberately a no-op, even while crash recovery is on: a stray dwell
                    // must never remove the safety net. Turning it off stays in Settings → General.
                    if (yes) RequestCrashRecovery();
                }, index);
                break;
            case 1:
                YesNo("settings.safety.boot.message", yes =>
                {
                    _settings.General.StartOnBoot = yes;
                    _applyAndSave();
                }, index);
                break;
            case 2:
                YesNo("settings.safety.cue.message", yes =>
                {
                    _settings.Dwell.CueEnabled = yes;
                    _applyAndSave();
                }, index);
                break;
            case 3:
                YesNo("settings.safety.track.message", yes =>
                {
                    _settings.TrackStatus.Enabled = yes;
                    _applyAndSave();
                }, index);
                break;
            default:
                _showModal(Question("settings.safety.mode.message", index),
                [
                    new ModalChoice(BasicId, T("settings.safety.mode.basic"), ModalChoiceKind.Affirmative,
                        () => Answered(index, () => _setSimpleSettingsUi(true))),
                    new ModalChoice(AllId, T("settings.safety.mode.all"), ModalChoiceKind.Neutral,
                        () => Answered(index, () => _setSimpleSettingsUi(false))),
                ]);
                break;
        }
    }

    private void YesNo(string messageKey, Action<bool> apply, int index) =>
        _showModal(Question(messageKey, index),
        [
            new ModalChoice(YesId, T("settings.modal.yes"), ModalChoiceKind.Affirmative,
                () => Answered(index, () => apply(true))),
            new ModalChoice(NoId, T("settings.modal.no"), ModalChoiceKind.Negative,
                () => Answered(index, () => apply(false))),
        ]);

    /// <summary>Applies one answer, marks the chain done after the last, then moves on.</summary>
    private void Answered(int index, Action apply)
    {
        apply();
        if (index == QuestionCount - 1)
        {
            _settings.General.SafetySetupDone = true;
            _applyAndSave();
        }

        Advance(index + 1);
    }

    /// <summary>
    /// "Yes" to crash recovery: the switch's own asynchronous bring-up. A synchronous outcome (the
    /// Supervisor is not installed; a fake host) is read straight away; an in-flight one is awaited
    /// on <see cref="SupervisionToggleService.StateChanged"/> without holding the chain.
    /// </summary>
    private void RequestCrashRecovery()
    {
        if (_supervision is null) return;

        _supervision.RequestEnable();
        if (_supervision.InFlight)
        {
            _waitingForSupervision = true;
            _supervision.StateChanged += OnSupervisionStateChanged;
            _ceiling = _schedule(CrashRecoveryCeiling, OnCrashRecoveryCeiling);
            return;
        }

        HoldFailureIfRolledBack();
    }

    private void OnSupervisionStateChanged()
    {
        if (!_waitingForSupervision || _supervision is null || _supervision.InFlight) return;

        StopWaiting();
        HoldFailureIfRolledBack();
        ResumeAfterCrashRecovery();
    }

    /// <summary>The bring-up never reported back: treat it as a failure the owner must see.</summary>
    private void OnCrashRecoveryCeiling()
    {
        if (!_waitingForSupervision) return;

        StopWaiting();
        _pendingFailure = T("settings.safety.unconfirmed.message");
        ResumeAfterCrashRecovery();
    }

    /// <summary>Crash recovery's outcome is known: leave the checking card if it is up, else keep a
    /// failure for the next boundary.</summary>
    private void ResumeAfterCrashRecovery()
    {
        // Otherwise a question is on screen: a held failure waits for the next boundary. The chain
        // cannot have ended — it does not end while waiting, and abandoning it stops the wait — so a
        // result never lands on a modal that is not the chain's (A2 review L1).
        if (_checking)
        {
            _checking = false;
            Advance(QuestionCount);
        }
    }

    /// <summary>The production <c>schedule</c>: a one-shot <c>DispatcherTimer</c> on this thread's
    /// dispatcher (the chain runs on the UI thread).</summary>
    private static IDisposable DispatcherDelay(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new StopOnDispose(timer.Stop);
    }

    private sealed class StopOnDispose(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    /// <summary>A request that ended with supervision still off failed (or rolled back): its status
    /// text is then the failure, verbatim.</summary>
    private void HoldFailureIfRolledBack()
    {
        if (_supervision is { Enabled: false })
            _pendingFailure = _supervision.StatusText;
    }
}
