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
/// while it has never been answered to the end, never on a PAUSED launch (<c>--paused</c>: the
/// FallenBack retry's inert relaunch, where the person may be mid-crash and must not meet five
/// questions), and never in <c>--test-mode</c>.
/// <para>
/// A NORMAL crash-recovery relaunch is not paused (<c>paused: false</c>), so a crash before the
/// last answer re-opens the intro card. Accepted (A7/M1): the setup is not done, "Later" closes it,
/// and the Start/Later answer is latched, so it never strands anyone.
/// </para>
/// </summary>
internal static class SafetySetupStartup
{
    /// <param name="safetySetupDone"><see cref="GeneralSettings.SafetySetupDone"/>.</param>
    /// <param name="isPausedLaunch"><c>App.IsPausedLaunch</c>.</param>
    /// <param name="isTestMode"><c>App.IsTestMode</c>.</param>
    internal static bool ShouldPrompt(bool safetySetupDone, bool isPausedLaunch, bool isTestMode) =>
        !safetySetupDone && !isPausedLaunch && !isTestMode;

    /// <summary>The startup hook: sends <see cref="FirstRunGuideRequestedMessage"/> exactly when
    /// <see cref="ShouldPrompt"/> says so. One call at the App's call site, so the decision cannot
    /// be inverted there without a test seeing it.</summary>
    /// <returns>Whether the request was sent.</returns>
    internal static bool MaybeRequest(
        bool safetySetupDone, bool isPausedLaunch, bool isTestMode, IMessenger messenger)
    {
        if (!ShouldPrompt(safetySetupDone, isPausedLaunch, isTestMode)) return false;
        messenger.Send(new FirstRunGuideRequestedMessage());
        return true;
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

    /// <summary>Runs the question chain WITHOUT the intro card — the first-run guide shows its own
    /// transition card and then drives the questions (spec §7 step 7). Same initialization as
    /// <see cref="Start"/> minus the intro modal: the chain begins at question 0 and ends with the
    /// same onEnded semantics. Accepted residual (r1 S3): a "Yes" while a Disabling flight is in
    /// flight does not attach and leaves supervision off — the Settings row remains the remedy.</summary>
    internal void StartQuestions()
    {
        _active = true;
        _pendingFailure = null;
        Advance(0);
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

        // r1 F9 / r3 F5: an unrelated flight (the startup ensure, a disable) must never be
        // mistaken for this answer's enable. Already-on is a no-op success; only an in-flight
        // ENABLE is awaited. (RequestEnable itself no-ops on _inFlight || Enabled.)
        if (_supervision.Enabled && !_supervision.InFlight) return;
        _supervision.RequestEnable();
        if (_supervision.IsEnablingInFlight)
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
    internal static IDisposable DispatcherDelay(TimeSpan delay, Action action)
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
