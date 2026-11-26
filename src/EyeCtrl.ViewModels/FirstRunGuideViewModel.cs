namespace EyeCtrl.ViewModels;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Binding surface for the first-run guide's current step (spec §5/§6). The chrome detector
/// (base class) dwells at ToolbarButtonDwellMs and carries EVERY card button (step choices, the
/// chrome Close, the theme chips, the dwell ±) via the Settings-modal latch protocol: targets
/// register AT THE ORIGIN and are HELD (Hold(id), SettingsWindowViewModel.Register :186-195); the
/// window pushes each real centre through UpdateChoiceCentre (→ UpdateTargetCenter +
/// latch.Positioned, :208-212); the latch's filter rides every DwellDetector.Update (:166); ALL
/// chrome targets clear before a fired id dispatches (:317-318) — the wiring re-renders the card
/// when the press did not change the step. The practice target carries its OWN detector at
/// ActivationButtonDwellMs (two-knob split) with the forgiving-dwell grace, re-timed live by
/// SetDwellTime. Gaze routes through SampleMayDwell (gates !IsInGap, NOT IsPaused) —
/// DwellActiveWhilePaused is true, so the guide stays usable under user-pause (§9).
/// </summary>
public sealed partial class FirstRunGuideViewModel : DwellOverlayViewModelBase
{
    /// <summary>The choice cards' radius, in DIP (Settings' ModalBtnRadius is an App-internal —
    /// the guide declares its own, r2 F3). Computed PER REGISTRATION: a static would freeze the
    /// DPI (r2 F18).</summary>
    private const double ChoiceRadiusDip = 70;

    /// <summary>The practice target's unscaled radius, in DIP — matches the 220-DIP ring.</summary>
    private const double PracticeRingRadiusDip = 110;

    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _practiceDetector;
    private readonly LeaveBeforeArmLatch _latch;
    private readonly Dictionary<string, double> _choiceRadiiDip = new();
    private double _surfaceScale = 1.0;
    private PhysicalPoint _practiceCentre;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private bool _chromeCloseVisible;
    [ObservableProperty] private bool _practiceVisible;
    [ObservableProperty] private bool _sliderVisible;
    [ObservableProperty] private IReadOnlyList<ModalChoice>? _safetyChoices;

    /// <summary>The practice target fired (its own ActivationButtonDwellMs detector).</summary>
    public event Action? PracticeTargetActivated;
    /// <summary>Practice dwell progress, 0..1 (the window's fill element binds to this).</summary>
    public event Action<double>? PracticeProgress;
    /// <summary>The chrome Close (or Later) fired.</summary>
    public event Action? CloseRequested;
    /// <summary>A chrome target fired (a choice id, guide_close, chips, dwell ±).</summary>
    public event Action<string>? ChoiceActivated;
    /// <summary>Chrome dwell progress per id, 0..1 (the window's progress bars bind to this).</summary>
    public event Action<string, double>? ChoiceProgress;
    /// <summary>The dwell-time changed (clamped 200–2000 ms).</summary>
    public event Action<int>? DwellTimeChanged;
    /// <summary>A theme chip fired (theme name).</summary>
    public event Action<string>? ThemeChosen;

    /// <summary>The guide is the overlay whose only dismissal is a dwell (the chrome Close), so it
    /// stays usable under user-pause (§9) — the base's opt-in; effects gate on their own sample.</summary>
    protected override bool DwellActiveWhilePaused => true;

    internal bool DwellActiveWhilePausedForTest => DwellActiveWhilePaused;

    /// <summary>The practice ring's dedicated detector, exposed for headless geometry tests.</summary>
    internal DwellDetector PracticeDetectorForTest => _practiceDetector;

    /// <summary>Uniform surface scale set by the fullscreen window (<c>min(1, W/1920, H/1200)</c>),
    /// always in <c>(0, 1]</c>. Scales unscaled layout DIP radii and the leave-before-arm release
    /// margin so dwell catchments match the visually scaled controls 1:1 (issue #372).</summary>
    public double SurfaceScale => _surfaceScale;

    /// <summary>Whether the chrome detector may accumulate on <paramref name="id"/> right now —
    /// the latch's filter, exposed for tests.</summary>
    internal bool ChoiceMayDwell(string id) =>
        _latch.Filter is null || _latch.Filter(new DwellTarget(id, new PhysicalPoint(0, 0), 1, 0));

    public FirstRunGuideViewModel(IMessenger messenger, int dwellCooldownMs,
        EyeCtrlSettings settings, IStruggleTelemetrySink? sink,
        IDwellDetectorRegistry? dwellRegistry)
        : base(messenger, dwellCooldownMs, context: DwellContext.Other, sink: sink,
               dwellRegistry: dwellRegistry, pauseGraceSettings: settings.Dwell)
    {
        _settings = settings;
        _practiceDetector = new DwellDetector(_settings.Dwell.DwellCooldownMs, DwellContext.Other, sink,
            pauseGraceMs: _settings.Dwell.DwellPauseGraceMs) { OtherTargetReleasesHold = true };
        _practiceDetector.TargetActivated += _ =>
        {
            PracticeTargetActivated?.Invoke();
            Messenger.Send(new DwellActivationMessage());   // the base pattern (:96-101): cue the fire
        };
        _practiceDetector.TargetProgress += (_, p) => PracticeProgress?.Invoke(p);
        // Evaluated dynamically so both live DPI updates and viewport surface scaling apply to the
        // leave-before-arm hysteresis without freezing either at construction time.
        _latch = new LeaveBeforeArmLatch(DwellDetector,
            releaseMarginPhysical: () => DpiHelper.LogicalToPhysicalX(
                SettingsWindowViewModel.LatchReleaseMarginDip * _surfaceScale));
    }

    /// <summary>Updates the uniform surface scale (issue #372) used to convert unscaled layout DIP
    /// radii (choice catchments, the 110-DIP practice ring, and the leave-before-arm release
    /// margin) into physical screen pixels. Values above 1.0 clamp to 1.0 (never upscales past the
    /// 1920×1200 reference); non-positive or non-finite values throw. Re-scales any currently
    /// registered chrome and practice targets in place.</summary>
    public void SetSurfaceScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale,
                "Surface scale must be finite and strictly positive.");
        var clamped = Math.Min(1.0, scale);
        if (Math.Abs(clamped - _surfaceScale) < 1e-12) return;
        _surfaceScale = clamped;

        foreach (var target in DwellDetector.Targets)
        {
            if (_choiceRadiiDip.TryGetValue(target.Id, out var unscaledDip))
            {
                DwellDetector.AddTarget(new DwellTarget(
                    target.Id, target.Center,
                    DpiHelper.LogicalToPhysicalX(unscaledDip * _surfaceScale),
                    _settings.Dwell.ToolbarButtonDwellMs));
            }
        }
        ReTimePracticeTarget();
    }

    /// <summary>The two knob values the step-3 writes through (200–2000).</summary>
    public int DwellTimeMs => _settings.Dwell.ActivationButtonDwellMs;

    public void SetDwellTime(int ms)
    {
        _settings.Dwell.ActivationButtonDwellMs = Math.Clamp(ms, 200, 2000);
        ReTimePracticeTarget();
        DwellTimeChanged?.Invoke(_settings.Dwell.ActivationButtonDwellMs);
    }

    /// <summary>Titles the guide card. Named ShowGuideCard (not ShowCard) so the status-card
    /// slot gate's pin (StatusCardPresenterTests, <c>\.ShowCard\s*\(</c> over src/) keeps
    /// matching exactly one shower — the slot presenter itself.</summary>
    public void ShowGuideCard(string title, string body, bool chromeClose)
    {
        Title = title;
        Body = body;
        ChromeCloseVisible = chromeClose;
    }

    /// <summary>The safety chain's card (spec §7 step 7): the body becomes the message and the
    /// choices are published for the wiring's rendering.</summary>
    public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
    {
        Body = message;
        SafetyChoices = choices;
    }

    /// <summary>Registers a chrome card AT THE ORIGIN and HOLDS it (the Settings-modal pattern:
    /// a target registered under a resting gaze must not arm — r1 F7). The window pushes the
    /// real centre through <see cref="UpdateChoiceCentre"/>; a fired id clears ALL chrome targets
    /// before dispatching (the Settings precedent, :317-318) — the wiring re-renders the card
    /// when the press did not change the step (r4 F3). <paramref name="radiusDip"/> defaults to
    /// <see cref="ChoiceRadiusDip"/> (70, the modal fallback); the window passes each card's
    /// unscaled half-diagonal, which is multiplied by <see cref="SurfaceScale"/> so the WHOLE
    /// rendered card dwells without overlapping neighbours on smaller screens.</summary>
    public void RegisterChoice(string id, double? radiusDip = null)
    {
        var unscaledRadiusDip = radiusDip ?? ChoiceRadiusDip;
        _choiceRadiiDip[id] = unscaledRadiusDip;
        DwellDetector.AddTarget(new DwellTarget(id, new PhysicalPoint(0, 0),
            DpiHelper.LogicalToPhysicalX(unscaledRadiusDip * _surfaceScale),
            _settings.Dwell.ToolbarButtonDwellMs));
        _latch.Hold(id);
    }

    /// <summary>Removes one chrome target and its latch state (the dwell ± on a step that lost
    /// its knob).</summary>
    public void RemoveChromeChoice(string id)
    {
        _choiceRadiiDip.Remove(id);
        DwellDetector.RemoveTarget(id);
        _latch.Forget(id);
    }

    /// <summary>The view's layout/DPI push: real centre + the latch's positioned mark (the mark
    /// only sticks on an ALREADY-HELD target — so this must come after RegisterChoice; repeat on
    /// every DPI/display change, Q1).</summary>
    public void UpdateChoiceCentre(string id, PhysicalPoint centre)
    {
        DwellDetector.UpdateTargetCenter(id, centre);
        _latch.Positioned(id);
    }

    /// <summary>Clears every chrome target and the latch (the window calls this before
    /// re-registering a new card's set).</summary>
    public void ClearChoices()
    {
        _choiceRadiiDip.Clear();
        DwellDetector.ClearTargets();
        _latch.Clear();
    }

    private void ReTimePracticeTarget()
    {
        _practiceDetector.RemoveTarget("guide_practice");
        if (PracticeVisible)
            // The dwell radius must match the VISUAL: the owner-approved fullscreen ring is
            // 220 DIP (110-DIP radius) at scale 1.0, scaled uniformly with SurfaceScale on smaller
            // viewports so the catchment never outgrows the drawn ring.
            _practiceDetector.AddTarget(new DwellTarget("guide_practice", _practiceCentre,
                radius: DpiHelper.LogicalToPhysicalX(PracticeRingRadiusDip * _surfaceScale),
                dwellTimeMs: _settings.Dwell.ActivationButtonDwellMs));
    }

    /// <summary>The view pushes the practice target's real centre at layout and on DPI change
    /// (r1 F8 — an absolute top-left coordinate would park the target off the card).</summary>
    public void SetPracticeCentre(PhysicalPoint centre)
    {
        _practiceCentre = centre;
        ReTimePracticeTarget();
    }

    public void SetPracticeVisible(bool visible)
    {
        PracticeVisible = visible;
        ReTimePracticeTarget();
    }

    public void SetSliderVisible(bool visible) => SliderVisible = visible;

    internal void CloseRequestedFire() => CloseRequested?.Invoke();
    internal void ThemeChosenFire(string name) => ThemeChosen?.Invoke(name);

    /// <summary>The window's uniform chrome-activation route (r3 F4): every card button the
    /// window builds fires this with its id.</summary>
    internal void OnChromeChoice(string id) => ChoiceActivated?.Invoke(id);

    /// <summary>Per-sample feed: the base detector (chrome, latch-filtered) AND the practice
    /// detector, both through the shared gate — a gap-held sample accumulates nowhere.</summary>
    public override void Receive(GazeCursorMovedMessage message)
    {
        NoteSamplePaused(message.IsPaused);
        if (IsVisible && SampleMayDwell(message))
        {
            _latch.Update(message.Position);
            DwellDetector.Update(message.Position, message.TimestampMicroseconds, _latch.Filter);
            if (PracticeVisible)
                _practiceDetector.Update(message.Position, message.TimestampMicroseconds);
        }
    }

    protected override void OnTargetActivated(string targetId)
    {
        _choiceRadiiDip.Clear();
        DwellDetector.ClearTargets();       // Settings precedent: clear before dispatching (:317-318)
        _latch.Clear();
        if (targetId == "guide_close") { CloseRequested?.Invoke(); return; }
        ChoiceActivated?.Invoke(targetId);
    }

    /// <summary>Chrome dwell progress per id, 0..1 (the window's progress elements bind to this).</summary>
    protected override void OnTargetProgress(string targetId, double progress)
        => ChoiceProgress?.Invoke(targetId, progress);
}
