namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Whether the one-time safety setup should be raised at startup. The whole decision, pure, so the
/// three conditions are pinned without a window: the prompt shows only while it has never been
/// answered to the end, never on a Supervisor recovery relaunch (<c>--paused</c>: the person may be
/// mid-crash and must not meet five questions), and never in <c>--test-mode</c>.
/// </summary>
internal static class SafetySetupStartup
{
    /// <param name="safetySetupDone"><see cref="GeneralSettings.SafetySetupDone"/>.</param>
    /// <param name="isPausedLaunch"><c>App.IsPausedLaunch</c>.</param>
    /// <param name="isTestMode"><c>App.IsTestMode</c>.</param>
    internal static bool ShouldPrompt(bool safetySetupDone, bool isPausedLaunch, bool isTestMode) =>
        !safetySetupDone && !isPausedLaunch && !isTestMode;
}

/// <summary>
/// The one-time safety setup: five questions chained through the Settings window's dwell modal, each
/// answer applied at once through the path the matching Settings row uses. Closing the window (or
/// any other way of abandoning the chain) simply stops it: the answers already given stay applied
/// and <see cref="GeneralSettings.SafetySetupDone"/> stays false, so the next start asks again.
///
/// <para>
/// <b>A "step" card precedes every question.</b> The modal's dwell targets arm the moment they are
/// registered, so a gaze still resting on the previous answer's button would otherwise answer the
/// next question — a silent "Yes" to start-at-sign-in. The step card's single button sits in the
/// middle; the two answer buttons sit 97 DIP either side of it with a 70 DIP hit circle, so no gaze
/// point lies in both: a gaze that is still on an answer button cannot fire the step card, and a
/// gaze that is still on the step card cannot fire an answer.
/// </para>
///
/// <para>
/// <b>Crash recovery can fail after the chain has moved on</b> (<c>RequestEnable</c> is asynchronous
/// and rolls back). The chain never waits for it: the existing failure text is held and shown, with
/// one acknowledgement button, at the next boundary between cards (or when it arrives after the last
/// answer).
/// </para>
/// </summary>
internal sealed class SafetySetupFlow
{
    /// <summary>How many questions the chain asks.</summary>
    internal const int QuestionCount = 5;

    // Dwell-target ids, not text: constants, so the text-literal ratchet reads them as ids.
    private const string NextId = "safety_next";
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

    private string? _pendingFailure;
    private bool _finished;
    private bool _waitingForSupervision;

    /// <param name="settings">The live settings the answers are written to.</param>
    /// <param name="supervision">The crash-recovery switch; null on a degraded host, which skips
    /// nothing — the question is still asked and its Yes is simply a no-op.</param>
    /// <param name="showModal">The Settings window's modal: one message, N dwell choices.</param>
    /// <param name="applyAndSave">The window's live-apply + debounced save.</param>
    /// <param name="setSimpleSettingsUi">The window's mode switch (re-renders the pages and the
    /// toggle label), true for Basic.</param>
    internal SafetySetupFlow(
        EyeCtrlSettings settings,
        SupervisionToggleService? supervision,
        Action<string, IReadOnlyList<ModalChoice>> showModal,
        Action applyAndSave,
        Action<bool> setSimpleSettingsUi)
    {
        _settings = settings;
        _supervision = supervision;
        _showModal = showModal;
        _applyAndSave = applyAndSave;
        _setSimpleSettingsUi = setSimpleSettingsUi;
    }

    /// <summary>Starts (or restarts) the chain from the first question, whatever the flag says.</summary>
    internal void Start()
    {
        _finished = false;
        _pendingFailure = null;
        Advance(0);
    }

    /// <summary>Stops listening for a pending crash-recovery result (the window closed, or the chain
    /// was restarted). Answers already given stay applied; nothing is undone.</summary>
    internal void Abandon()
    {
        _waitingForSupervision = false;
        if (_supervision is not null)
            _supervision.StateChanged -= OnSupervisionStateChanged;
    }

    private static string T(string key) => StringTable.Current.T(key);

    /// <summary>The boundary between cards: a held failure first, then the next step card.</summary>
    private void Advance(int index)
    {
        if (_pendingFailure is { } failure)
        {
            _pendingFailure = null;
            _showModal(failure, [Acknowledge(() => Advance(index))]);
            return;
        }

        if (index >= QuestionCount) return;

        _showModal(
            StringTable.Current.T("settings.safety.step.message", index + 1, QuestionCount),
            [new ModalChoice(NextId, T("settings.safety.next"), ModalChoiceKind.Affirmative,
                () => Ask(index))]);
    }

    private static ModalChoice Acknowledge(Action then) =>
        new(OkId, T("settings.safety.ok"), ModalChoiceKind.Affirmative, then);

    private void Ask(int index)
    {
        switch (index)
        {
            case 0:
                YesNo("settings.safety.crash.message", yes =>
                {
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
                _showModal(T("settings.safety.mode.message"),
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
        _showModal(T(messageKey),
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
            _finished = true;
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
            return;
        }

        HoldFailureIfRolledBack();
    }

    private void OnSupervisionStateChanged()
    {
        if (!_waitingForSupervision || _supervision is null || _supervision.InFlight) return;

        _waitingForSupervision = false;
        _supervision.StateChanged -= OnSupervisionStateChanged;
        HoldFailureIfRolledBack();

        // Arrived after the last card: nothing else will flush it.
        if (_finished && _pendingFailure is { } failure)
        {
            _pendingFailure = null;
            _showModal(failure, [Acknowledge(() => { })]);
        }
    }

    /// <summary>A request that ended with supervision still off failed (or rolled back): its status
    /// text is then the failure, verbatim.</summary>
    private void HoldFailureIfRolledBack()
    {
        if (_supervision is { Enabled: false })
            _pendingFailure = _supervision.StatusText;
    }
}
