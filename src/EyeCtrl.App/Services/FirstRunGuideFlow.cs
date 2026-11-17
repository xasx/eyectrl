namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>The guide's eight steps, in order (spec §6). Calibrate and ToolbarTask are omitted
/// when their preconditions fail (§7 steps 4/5).</summary>
internal enum FirstRunGuideStep
{
    Welcome, MeetDwell, DwellTime, Calibrate, ToolbarTask, Theme, Safety, Done,
}

/// <summary>Why the guide closed. Abandoned = Later/chrome Close/window closed — the flag stays
/// false unless the safety block completed; Finished = the Done card's Finish.</summary>
internal enum GuideExitReason { Finished, Abandoned }

/// <summary>The flow's window surface (spec §6): the flow knows nothing WPF; the wiring's
/// GuideSurface implements it and drives the window + view model.</summary>
internal interface IGuideSurface
{
    void ShowStep(FirstRunGuideStep step);
    /// <summary>The safety chain's showModal lands here (the flow passes it itself — ONE route).</summary>
    void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices);
    void HideCard();
    void ShowCard();
    void Close();
}

/// <summary>
/// The first-run guide's headless state machine (the SafetySetupFlow pattern). Owns the step
/// list, the skip policy, the task-success transitions, and the absorbed safety chain — whose
/// showModal the flow routes to the surface itself, so tests and production share one path.
/// </summary>
internal sealed class FirstRunGuideFlow
{
    private readonly EyeCtrlSettings _settings;
    private readonly bool _trackerPresent;
    private readonly bool _toolbarTaskAvailable;
    private readonly IGuideSurface _surface;
    private readonly Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow> _createSafetyFlow;
    private SafetySetupFlow? _safetyFlow;
    private FirstRunGuideStep _step;
    private bool _closed;

    /// <summary>Raised after each step change, BEFORE the surface shows it — the wiring toggles
    /// the toolbar hold (§5 claim 6) and the ring lifecycle (spec §5) here.</summary>
    public event Action<FirstRunGuideStep>? StepEntered;
    public event Action? ToolbarTaskEntered;
    public event Action? ToolbarTaskExited;
    /// <summary>Raised once when the guide ends.</summary>
    public event Action<GuideExitReason>? Closed;

    public FirstRunGuideFlow(EyeCtrlSettings settings, bool trackerPresent, bool toolbarTaskAvailable,
        IGuideSurface surface,
        Func<Action<string, IReadOnlyList<ModalChoice>>, Action<bool>, SafetySetupFlow> createSafetyFlow)
    {
        _settings = settings;
        _trackerPresent = trackerPresent;
        _toolbarTaskAvailable = toolbarTaskAvailable;
        _surface = surface;
        _createSafetyFlow = createSafetyFlow;
    }

    public void Start() => Enter(FirstRunGuideStep.Welcome);

    public void Advance()
    {
        switch (_step)
        {
            case FirstRunGuideStep.Welcome: Enter(Next(FirstRunGuideStep.MeetDwell)); break;
            case FirstRunGuideStep.DwellTime: Enter(Next(FirstRunGuideStep.Calibrate)); break;
            // The toolbar task's Continue (S1): without this the press cleared every chrome
            // target (the VM clears before dispatching) and re-entered nothing — the card sat
            // target-less under all the guide's claims, with no gaze way out.
            case FirstRunGuideStep.ToolbarTask: Enter(Next(FirstRunGuideStep.Theme)); break;
            case FirstRunGuideStep.Theme: Enter(Next(FirstRunGuideStep.Safety)); break;
            case FirstRunGuideStep.Done: Finish(); break;   // the Done card's exit (r1 F4)
        }
    }

    public void Skip()
    {
        switch (_step)
        {
            case FirstRunGuideStep.MeetDwell: Enter(Next(FirstRunGuideStep.DwellTime)); break;
            case FirstRunGuideStep.Calibrate: Enter(Next(FirstRunGuideStep.ToolbarTask)); break;
            case FirstRunGuideStep.ToolbarTask: Enter(Next(FirstRunGuideStep.Theme)); break;
        }
    }

    /// <summary>Later (welcome) / chrome Close (any step) / the window closing externally.
    /// Idempotent — the window's Closed event re-enters after surface.Close().</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _safetyFlow?.Abandon();
        _surface.Close();
        Closed?.Invoke(GuideExitReason.Abandoned);
    }

    /// <summary>The Done card's Finish (spec §7 step 8): the mirror of Close with the Finished
    /// reason — without it the last card strands the user under all seven claims (r1 F4).</summary>
    public void Finish()
    {
        if (_closed) return;
        _closed = true;
        _surface.Close();
        Closed?.Invoke(GuideExitReason.Finished);
    }

    public void NotifyTaskSucceeded()
    {
        switch (_step)
        {
            case FirstRunGuideStep.MeetDwell: Enter(Next(FirstRunGuideStep.DwellTime)); break;
            case FirstRunGuideStep.ToolbarTask: Enter(Next(FirstRunGuideStep.Theme)); break;
        }
    }

    /// <summary>Calibration returned to Idle/Cancelled (NOT CompletedMessage — spec §7 step 4).</summary>
    public void NotifyCalibrationEnded()
    {
        if (_step == FirstRunGuideStep.Calibrate)
            Enter(Next(FirstRunGuideStep.ToolbarTask));
    }

    public void NotifySafetyEnded(bool completed)
    {
        if (_step != FirstRunGuideStep.Safety) return;
        _safetyFlow = null;
        Enter(Next(FirstRunGuideStep.Done));
    }

    /// <summary>The wiring forwards a fired chain choice's OnChosen (Yes/No/Basic/All). No-op
    /// outside the safety block.</summary>
    public void ForwardSafetyChoice(Action choice)
    {
        if (_step != FirstRunGuideStep.Safety) return;
        choice();
    }

    /// <summary>The transition card's Begin: starts the absorbed question chain (spec §7 step 7).
    /// No-op unless the guide is on the Safety step. Starting inside Enter(Safety) would register
    /// question 0's targets and ShowStep would then wipe them (r2 F8).</summary>
    public void BeginSafetyQuestions()
    {
        if (_step != FirstRunGuideStep.Safety) return;
        _safetyFlow?.StartQuestions();
    }

    /// <summary>The window's own Closed event (external close = abandon). Its OWN path, not
    /// Close(): the window is already closing, and routing through Close() would call the
    /// surface's Close() — a Window.Close() re-entered from inside that window's own Closed
    /// event. Same latch + abandon + Abandoned reason, minus the surface call.</summary>
    public void WindowClosed()
    {
        if (_closed) return;
        _closed = true;
        _safetyFlow?.Abandon();
        Closed?.Invoke(GuideExitReason.Abandoned);
    }

    /// <summary>Step id for the close log line (ids only — privacy constraint).</summary>
    public string LastStepId => _step.ToString();

    /// <summary>Whether the guide is on <paramref name="step"/> right now (the summon handler's
    /// step gate — r1 F21).</summary>
    public bool IsOnStep(FirstRunGuideStep step) => _step == step;

    private FirstRunGuideStep Next(FirstRunGuideStep candidate)
    {
        // Spec §7: Calibrate omitted without a real tracker; ToolbarTask omitted floating/mouse.
        if (candidate == FirstRunGuideStep.Calibrate && !_trackerPresent)
            candidate = FirstRunGuideStep.ToolbarTask;
        if (candidate == FirstRunGuideStep.ToolbarTask && !_toolbarTaskAvailable)
            candidate = FirstRunGuideStep.Theme;
        return candidate;
    }

    private void Enter(FirstRunGuideStep step)
    {
        var previous = _step;
        _step = step;
        StepEntered?.Invoke(step);

        if (previous == FirstRunGuideStep.ToolbarTask && step != FirstRunGuideStep.ToolbarTask)
            ToolbarTaskExited?.Invoke();
        if (step == FirstRunGuideStep.ToolbarTask && previous != FirstRunGuideStep.ToolbarTask)
            ToolbarTaskEntered?.Invoke();

        if (step == FirstRunGuideStep.Safety)
        {
            // The flow owns the chain's showModal route (ONE path for tests and production). The
            // chain is created here but NOT started — the transition card's Begin calls
            // BeginSafetyQuestions() (r2 F8).
            _safetyFlow = _createSafetyFlow(
                (message, choices) => _surface.ShowSafetyCard(message, choices),
                completed => NotifySafetyEnded(completed));
        }

        _surface.ShowStep(step);
    }
}
