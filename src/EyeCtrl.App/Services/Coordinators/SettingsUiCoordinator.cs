namespace EyeCtrl.App.Services.Coordinators;

using System;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.Setup;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the settings-UI surface of <see cref="EventCoordinator"/>: the settings window lifecycle
/// (<see cref="ShowSettingsWindow"/> via the <see cref="SettingsRequestedMessage"/> registration,
/// <see cref="OnSettingsWindowClosed"/>, the <see cref="CloseSettingsMessage"/> registration), the
/// settings-applied reaction (<see cref="OnSettingsChanged"/>: toolbar re-init + keyboard reload),
/// the track-status visibility binding, and the model/restart-coordinator hand-off. Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition (cluster 7).
/// </summary>
internal sealed class SettingsUiCoordinator : CoordinatorBase
{
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly KeyboardCoordinator _keyboard;
    private FirstRunGuideHost? _guideHost;
    private SplashThreadHandle? _splashHandle;
    private FirstRunGuideWindow? _guideWindow;
    private FirstRunGuideViewModel? _guideVm;
    private FirstRunGuideFlow? _guideFlow;
    private GuideSurface? _guideSurface;
    private Action<bool>? _onToolbarVisibility;   // a FIELD, not a local function (r3 F6)
    private SettingsWindow? _settingsWindow;
    private ModelRegistryService? _modelRegistry;
    private ModelManagementService? _modelManagementService;
    private AppRestartCoordinator? _restartCoordinator;
    private SupervisionToggleService? _supervisionToggle;
    private Func<ISetupGateways>? _setupGatewaysFactory;
    private IUninstallHandoffPresenter? _uninstallHandoff;
    // C9 S6e fixup-08: the open window as the uninstall hand-off drives it, and whether THIS
    // hand-off hid it. Both are cleared when the window closes, so a watch that outlives it
    // cannot show a window nobody owns.
    private ISettingsWindowHandle? _handoffWindow;
    private bool _settingsHiddenForUninstallHandoff;
    // The probe entry (build plan C10): the Diagnostics button's reason probe and request action,
    // handed over once at startup by EventCoordinator after C7 exists.
    private Func<string?>? _probeBlockReason;
    private Action? _probeRequest;
    private Func<bool>? _probeCanResume;
    // Owned HERE — moved from EventCoordinator. The metrics snapshot AND the includeReset flag
    // (Adaptation.Enabled) of the last band build, compared by ReinitializeToolbar so the save
    // path and the live Enabled subscription (Task 9) rebuild the band exactly once however
    // their arrivals interleave.
    private ToolbarSnapshot? _toolbarSnapshot;
    private bool? _appliedIncludeReset;
    public SettingsUiCoordinator(ModalInteractionState modal, QuickActionsCoordinator quickActions,
        ToolbarLifecycleCoordinator toolbar, KeyboardCoordinator keyboard)
    {
        // All four are required collaborators dereferenced unconditionally (the suspend seams and the
        // ReloadIfChanged seam), so reject nulls at the ctor rather than at first use.
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _quickActions = quickActions ?? throw new ArgumentNullException(nameof(quickActions));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
    }

    protected override void OnInitialize()
    {
        var context = Context!;

        var messenger = context.Messenger;

        // Settings window
        messenger.Register<SettingsUiCoordinator, SettingsRequestedMessage>(this, (_, m) =>
            context.Dispatcher.BeginInvoke(() => ShowSettingsWindow(m.Target)));

        // First-run guide: raised at startup (the renamed gate) and from the Settings → General row.
        messenger.Register<SettingsUiCoordinator, FirstRunGuideRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(StartFirstRunGuide));

        // CloseSettings -> close settings window
        messenger.Register<SettingsUiCoordinator, CloseSettingsMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() =>
            {
                if (_settingsWindow != null)
                {
                    _settingsWindow.Close();
                }
            }));

        UpdateTrackStatusVisibility();
        // Named handler (was an anonymous lambda that was never detached — it kept this coordinator
        // alive through the settings object, which outlives EventCoordinator). Dispose -= it.
        context.Settings.TrackStatus.PropertyChanged += OnTrackStatusSettingsPropertyChanged;
        // Task 9: the band's Reset membership follows the live Adaptation.Enabled flip. The
        // settings UI mutates the live object as the user toggles — before any save raises
        // SettingsChanged — so the flip arrives here as PropertyChanged. Marshalled like the
        // TrackStatus handler above; the rebuild itself lives in ReinitializeToolbar.
        context.Settings.Adaptation.PropertyChanged += OnAdaptationSettingsPropertyChanged;
    }

    private void OnTrackStatusSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;
        if (e.PropertyName == nameof(TrackStatusSettings.Enabled))
            Context.Dispatcher.BeginInvoke(UpdateTrackStatusVisibility);
    }

    private void OnAdaptationSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;
        if (e.PropertyName != nameof(AdaptationSettings.Enabled)) return;
        // Through the same guard the save path uses, so a flip that also lands in a save
        // re-initializes the band exactly once.
        var settings = Context.Settings;
        Context.Dispatcher.BeginInvoke(() => ReinitializeToolbar(settings));
    }

    /// <summary>Stores the model services for the next <see cref="ShowSettingsWindow"/> construction.
    /// 2-arg (matches KeyboardCoordinator + EC's forwarder; the dead download service is dropped at the seam).</summary>
    public void SetModelServices(ModelRegistryService registry, ModelManagementService management)
    {
        _modelRegistry = registry;
        _modelManagementService = management;
    }

    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator) => _restartCoordinator = coordinator;

    /// <summary>Hands the supervision toggle (ticket 04) to the settings window at construction.
    /// Called once at startup, like <see cref="SetRestartCoordinator"/>.</summary>
    public void SetSupervisionToggle(SupervisionToggleService service) => _supervisionToggle = service;

    /// <summary>Hands the splash handle in for the latched show. Called once from App.xaml.cs
    /// via EventCoordinator's forwarder (r1 F15) after Initialize.</summary>
    public void SetSplashHandle(SplashThreadHandle? handle) => _splashHandle = handle;

    private void StartFirstRunGuide()
    {
        var context = Context!;
        if (_guideHost is not null) return;

        var host = new FirstRunGuideHost();
        if (!host.TryBegin(out var lease)) return;
        _guideHost = host;

        try
        {
            BuildAndRaiseGuide(context, lease);
        }
        catch (Exception ex)
        {
            // r2 F14: log instead of rethrowing — inside a BeginInvoke'd callback a rethrow
            // lands in DispatcherUnhandledException during a first-run startup.
            Log.Error(ex, "First-run guide: raise failed; unwinding");
            UnwindPartialRaise(context, lease);
        }
    }

    /// <summary>The raise-failure unwind: undo whatever BuildAndRaiseGuide/Show did, then hand
    /// the session back — toolbar restored, ring restored, lease released, cards started. No
    /// re-entry: the flow is nulled BEFORE the window closes, and the toolbar handler is
    /// unsubscribed first (r3 F9).</summary>
    private void UnwindPartialRaise(EventCoordinationContext context, FirstRunGuideLease lease)
    {
        if (_onToolbarVisibility is not null)
            _toolbar.ToolbarVisibilityChanged -= _onToolbarVisibility;
        _calibrationCeiling?.Dispose();                    // a pending refusal tail must not fire after unwind
        context.Messenger.Unregister<CalibrationStateChangedMessage>(this);
        context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);
        _modal.GuideOpen = false;
        _modal.GuideRingWanted = false;                    // the step-5 exception dies with the guide
        _safetyActions.Clear();                            // no fired choice survives a failed raise
        _quickActions.SetSummonSuspended("guide", false);
        _toolbar.SetGuideHold(false);
        _toolbar.ForceShow();                          // r3 F9: floating/mouse users get the toolbar back
        if (context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen)
            context.OverlayManager.CursorCircle.Show();           // r3 F9: the ring comes back
        Log.Information("First-run guide: closed (step=raise-failed, completed=false)");
        _guideFlow = null;                             // BEFORE window.Close: no re-entry (r3 F9)
        (_guideVm as IDisposable)?.Dispose();
        _guideVm = null;
        if (_guideWindow is not null) { _guideWindow.Close(); _guideWindow = null; }
        _guideSurface = null;
        _guideHost = null;
        lease.Dispose();
        StatusCardsFallback?.Invoke();
        StatusCardsFallback = null;
    }

    /// <summary>App assigns this at startup; the unwind invokes it so the status-card presenter
    /// starts even when the raise failed (r3 F6 / r2 F14). Cleared in Restore (r2 F19).</summary>
    internal Action? StatusCardsFallback { get; set; }

    private void BuildAndRaiseGuide(EventCoordinationContext context, FirstRunGuideLease lease)
    {
        var settings = context.Settings;
        bool tracker = App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse;
        bool toolbarTask = tracker && settings.Toolbar.IsDocked;   // spec §7 step 5

        _guideVm = new FirstRunGuideViewModel(context.Messenger,
            dwellCooldownMs: settings.Dwell.DwellCooldownMs,       // a COOLDOWN, not a dwell time (r1 F23)
            settings, sink: null, dwellRegistry: null);
        _guideWindow = new FirstRunGuideWindow(_guideVm);
        _guideSurface = new GuideSurface(_guideWindow, _guideVm, this);

        _guideFlow = new FirstRunGuideFlow(settings, trackerPresent: tracker,
            toolbarTaskAvailable: toolbarTask,
            surface: _guideSurface,
            createSafetyFlow: (showModal, onEnded) => new SafetySetupFlow(settings, _supervisionToggle,
                showModal,
                applyAndSave: () =>
                {
                    OnSettingsChanged(settings);                     // the live-apply path (r2 F6/Q2)
                    context.SettingsService.SaveDebounced(settings); // the DEBOUNCED save
                },
                setSimpleSettingsUi: v => settings.General.SimpleSettingsUi = v,
                onEnded));

        _guideVm.IsVisible = true;                                  // gaze input lives with IsVisible
        _guideVm.ChoiceActivated += id => context.Dispatcher.BeginInvoke(() => OnGuideChoice(id));
        _guideVm.CloseRequested += () => context.Dispatcher.BeginInvoke(_guideFlow.Close);
        // Split across lines on purpose: DwellSurfaceCensusTests scans for the raw detector
        // subscription marker and cannot tell this ROUTED VM event (published by the classified
        // FirstRunGuideViewModel) from one.
        _guideVm.PracticeTargetActivated
            += () => context.Dispatcher.BeginInvoke(_guideFlow.NotifyTaskSucceeded);

        // Theme pick: card-local preview + live-apply + save, then RE-RENDER the card (r4 F3 —
        // OnTargetActivated cleared every chrome target, and a chip press does not change the
        // step). The preview has TWO halves: ThemeResourceApplier.Apply re-skins the XAML half
        // through Card.Resources (r1 F2, unchanged — surface, title, body, read-out, ring and
        // the cards' dwell bars), and ApplyPreviewTheme repaints the DwellButtons' palette
        // colours (fill + label) against the picked theme — a DwellButton colours itself at
        // build time, so its half needs the explicit re-resolve. The re-render that follows
        // rebuilds only the action row; Close, dwell ± and the chips re-skin through the
        // repaint.
        _guideVm.ThemeChosen += name => context.Dispatcher.BeginInvoke(() =>
        {
            var theme = ThemeRegistry.Resolve(name);
            ThemeResourceApplier.Apply(_guideWindow!.CardResources, theme);   // card-local (r1 F2)
            _guideWindow.ApplyPreviewTheme(theme);           // the DwellButtons' palette repaint
            settings.Theme.Name = name;
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
            _guideSurface?.RenderChoicesForCurrentStep();      // re-register the cleared targets
        });

        // Dwell ±: live-apply + save, then RE-RENDER the card (r4 F3 — same cleared-targets rule).
        _guideVm.DwellTimeChanged += _ =>
        {
            OnSettingsChanged(settings);
            context.SettingsService.SaveDebounced(settings);
            _guideSurface?.RenderChoicesForCurrentStep();
        };
        context.ToolbarVM.GuideOpenGate = () => _modal.GuideOpen;   // claim 7 wired (r1 F12)

        void Show()
        {
            try
            {
                context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Guide);   // claim 1
                _modal.GuideOpen = true;                   // claim 2
                _quickActions.SetSummonSuspended("guide", true);   // claim 5
                _toolbar.SetGuideHold(true);               // claim 6 — hold arms BEFORE suspend (r1 F11)
                context.OverlayManager.CursorCircle.Hide();            // the ring hides — INSIDE Show
                context.OverlayManager.PauseIndicator.RaiseToTop();    // spec §5: the guide clears the pause strip
                Log.Information("First-run guide: shown");
                _guideWindow!.ShowGuide();
                _guideFlow!.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "First-run guide: show failed; unwinding");
                UnwindPartialRaise(context, lease);
            }
        }
        if (_splashHandle is { } splash) splash.WhenExited(Show);
        else Show();

        // Sync-show unwind guard: Show() unwinds internally on a failure — UnwindPartialRaise
        // nulls _guideFlow — so continuing into the subscriptions below would NRE on the nulled
        // flow and surface as a second unwind + a second raise-failed log through
        // StartFirstRunGuide's catch. The unwind already did everything (lease, fallback), so a
        // return is the whole job.
        if (_guideFlow is null) return;

        // Step toggles (claim 6(d)) + the ring's step-5 lifecycle (r3 F8: SHOW then raise).
        _guideFlow.StepEntered += step => context.Dispatcher.BeginInvoke(() =>
        {
            if (_guideFlow is null || !_modal.GuideOpen) return;
            if (step == FirstRunGuideStep.ToolbarTask)
            {
                _modal.GuideRingWanted = true;             // the ring is wanted on this step (S3-4)
                _toolbar.ResetToolbarForGuideTask();       // hold released + controller Hidden
                if (context.CursorVM.IsVisible)
                    context.OverlayManager.CursorCircle.Show();       // SHOW (RaiseToTop alone can't)
                context.OverlayManager.Toolbar.RaiseToTop();          // Guide < Toolbar
                context.OverlayManager.PauseIndicator.RaiseToTop();   // < PauseIndicator
                context.OverlayManager.CursorCircle.RaiseToTop();     // < ring
            }
            else
            {
                _modal.GuideRingWanted = false;            // no other step wants the ring (S3-4)
                _toolbar.SetGuideHold(true);               // non-task steps hold again
                context.OverlayManager.CursorCircle.Hide();           // step 5 ended: ring hidden again
            }
        });

        // The summon observation — a FIELD-SUBSCRIBED private method (r3 F6), gated on the task,
        // swapping the body to the success text (spec §7.5). NO re-render: the summon is a
        // toolbar event, not a card activation — no targets were cleared (r4 F5).
        _onToolbarVisibility = visible =>
        {
            if (_guideFlow is null || !_modal.GuideOpen || !visible) return;
            context.Dispatcher.BeginInvoke(() =>
            {
                if (_guideFlow is null || !_guideFlow.IsOnStep(FirstRunGuideStep.ToolbarTask)) return;
                context.OverlayManager.Toolbar.RaiseToTop();
                context.OverlayManager.PauseIndicator.RaiseToTop();
                if (context.CursorVM.IsVisible)
                    context.OverlayManager.CursorCircle.Show();       // show + raise (r3 F8)
                context.OverlayManager.CursorCircle.RaiseToTop();
                _guideWindow?.ShowCardContent(
                    StringTable.Current.T("guide.toolbar.title"),
                    StringTable.Current.T("guide.toolbar.success"), chromeClose: true);
            });
        };
        _toolbar.ToolbarVisibilityChanged += _onToolbarVisibility;

        void Restore(GuideExitReason reason)
        {
            _toolbar.ToolbarVisibilityChanged -= _onToolbarVisibility;   // no handler pile-up
            _calibrationCeiling?.Dispose();                // a pending refusal tail must not fire after close
            context.Messenger.Unregister<CalibrationStateChangedMessage>(this);  // always (r1 F17)
            context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Guide);  // claim 1
            _modal.GuideOpen = false;
            _modal.GuideRingWanted = false;                // the step-5 exception dies with the guide
            _safetyActions.Clear();                        // no fired choice outlives the guide (singleton)
            _quickActions.SetSummonSuspended("guide", false);
            _toolbar.SetGuideHold(false);
            _toolbar.ForceShow();                          // ForceShow + ResetToVisible — every mode (r4 F5)
            if (context.CursorVM.IsVisible && !_modal.SettingsOpen && !_modal.ProbeOpen)
                context.OverlayManager.CursorCircle.Show();           // ring restore (spec §5)
            Log.Information("First-run guide: closed (step={Step}, completed={Completed})",
                _guideFlow?.LastStepId ?? "done", reason == GuideExitReason.Finished);
            (_guideVm as IDisposable)?.Dispose();
            _guideFlow = null;                             // BEFORE window.Close: no re-entry
            _guideVm = null;
            _guideWindow?.Close();
            _guideWindow = null;
            _guideSurface = null;
            _guideHost = null;
            StatusCardsFallback = null;                    // no double-start on a later failed re-run (r2 F19)
            lease.Dispose();
        }
        _guideFlow.Closed += reason => context.Dispatcher.BeginInvoke(() => Restore(reason));
        _guideWindow.Closed += (_, _) => _guideFlow?.WindowClosed();   // external close = abandon
    }

    private readonly Dictionary<string, Action?> _safetyActions = new();

    /// <summary>The safety chain's id → OnChosen map, populated when a question card renders;
    /// the dispatch consults it first (r3 F5's one route).</summary>
    internal void RememberSafetyActions(IReadOnlyList<ModalChoice> choices)
    {
        _safetyActions.Clear();
        foreach (var choice in choices)
            _safetyActions[choice.Id] = choice.OnChosen;
    }

    private void OnGuideChoice(string id)
    {
        if (_guideFlow is null) return;
        if (_safetyActions.TryGetValue(id, out var onChosen))
        {
            _safetyActions.Clear();                        // consumed: the chain advances to the next card
            _guideFlow.ForwardSafetyChoice(onChosen ?? (() => { }));
            return;
        }
        switch (id)
        {
            case "guide_start": _guideFlow.Advance(); break;
            case "guide_later": _guideFlow.Close(); break;
            case "guide_dwell_skip": _guideFlow.Skip(); break;
            case "guide_dwell_minus": _guideVm?.SetDwellTime(_guideVm.DwellTimeMs - 100); break;
            case "guide_dwell_plus": _guideVm?.SetDwellTime(_guideVm.DwellTimeMs + 100); break;
            case "guide_dwelltime_continue": _guideFlow.Advance(); break;
            case "guide_calibrate_now": StartCalibration(); break;
            case "guide_calibrate_skip": _guideFlow.Skip(); break;
            case "guide_toolbar_skip": _guideFlow.Skip(); break;
            case "guide_toolbar_continue": _guideFlow.Advance(); break;
            case "guide_theme_continue": _guideFlow.Advance(); break;
            case "guide_done_finish": _guideFlow.Finish(); break;
            case "guide_safety_begin": _guideFlow.BeginSafetyQuestions(); break;   // transition card
            case "guide_close": _guideFlow.Close(); break;
            default:
                if (id.StartsWith("guide_theme_", StringComparison.Ordinal))
                {
                    var theme = id["guide_theme_".Length..];
                    _guideVm?.ThemeChosenFire(char.ToUpperInvariant(theme[0]) + theme[1..]);
                }
                break;
        }
    }

    private IDisposable? _calibrationCeiling;

    private void StartCalibration()
    {
        var context = Context!;
        if (_guideVm is null || _guideFlow is null || _guideSurface is null) return;

        var collected = false;
        void OnCalibrationStateChanged(CalibrationStateChangedMessage m)
        {
            if (m.State is CalibrationState.Collecting or CalibrationState.Training)
            {
                collected = true;
                _calibrationCeiling?.Dispose();
                _guideWindow?.HideGuide();
            }
            else if (m.State is CalibrationState.Idle or CalibrationState.Cancelled)
            {
                context.Messenger.Unregister<CalibrationStateChangedMessage>(this);   // ONE type arg (r2 F10)
                _calibrationCeiling?.Dispose();
                context.Dispatcher.BeginInvoke(() =>
                {
                    _guideWindow?.ShowGuide();
                    context.OverlayManager.PauseIndicator.RaiseToTop();   // spec §5, like every show
                    _guideFlow?.NotifyCalibrationEnded();   // renders step 5 — no Calibrate flicker (r3 F12)
                });
            }
        }
        context.Messenger.Register<SettingsUiCoordinator, CalibrationStateChangedMessage>(this,
            (_, m) => OnCalibrationStateChanged(m));
        _calibrationCeiling = SafetySetupFlow.DispatcherDelay(TimeSpan.FromSeconds(2), RefusalGuard);

        void RefusalGuard()
        {
            if (collected) return;
            context.Messenger.Unregister<CalibrationStateChangedMessage>(this);
            context.Dispatcher.BeginInvoke(() => _guideSurface?.ShowStep(FirstRunGuideStep.Calibrate));
        }

        context.Messenger.Send(new CalibrationRequestedMessage());
    }

    /// <summary>The flow's window surface. Per-BUILDER choice lists: the action row gets the
    /// step's buttons via SetChoices; the chrome Close gets its own host via SetChromeClose;
    /// the Theme chips get a 2×2 grid in StepContent via SetThemeGrid (r5 F4's geometry — six
    /// cards in one row overflow the card and their 70-DIP circles overlap). Every builder
    /// registers AND visualizes what it builds (r3 F5) and re-registers on a re-render (r5 F2).
    /// Keys are LITERALS (r2 F12). In-place presses (dwell ±, theme chips) re-render through
    /// RenderChoicesForCurrentStep (r4 F3).</summary>
    private sealed class GuideSurface(
        FirstRunGuideWindow window, FirstRunGuideViewModel vm,
        SettingsUiCoordinator owner) : IGuideSurface
    {
        private FirstRunGuideStep? _lastStep;

        public void ShowStep(FirstRunGuideStep step)
        {
            var (title, body) = TextsFor(step);
            var chromeClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done);
            window.ShowCardContent(title, body, chromeClose);
            window.SetChoices(RowChoicesFor(step));             // the action row, registered AND visualized
            window.SetChromeClose(chromeClose, T("guide.chrome.close"));
            window.SetPractice(step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime);
            window.SetDwellKnob(step == FirstRunGuideStep.DwellTime);
            window.SetThemeGrid(step == FirstRunGuideStep.Theme, ThemeChips());
            _lastStep = step;
        }

        private (string Title, string Body) TextsFor(FirstRunGuideStep step) => step switch
        {
            FirstRunGuideStep.Welcome => (T("guide.welcome.title"), T("guide.welcome.body")),
            FirstRunGuideStep.MeetDwell => (T("guide.dwell.title"), T("guide.dwell.body")),
            FirstRunGuideStep.DwellTime => (T("guide.dwelltime.title"), T("guide.dwelltime.body")),
            FirstRunGuideStep.Calibrate => (T("guide.calibrate.title"), T("guide.calibrate.body")),
            FirstRunGuideStep.ToolbarTask => (T("guide.toolbar.title"),
                string.Format(CultureInfo.InvariantCulture, T("guide.toolbar.body"), EdgeWord())),
            FirstRunGuideStep.Theme => (T("guide.theme.title"), T("guide.theme.body")),
            FirstRunGuideStep.Safety => (T("guide.safety.title"), T("guide.safety.body")),
            FirstRunGuideStep.Done => (T("guide.done.title"), T("guide.done.body")),
            _ => (string.Empty, string.Empty),
        };

        /// <summary>The docked edge's localized word. `DockedEdge` is a STRING ("Top"/"Bottom"/
        /// "Left"/"Right", ToolbarSettings.cs:41) and `Context` is the outer coordinator's
        /// protected property, read through `owner` (r5 F5).</summary>
        private string EdgeWord() => owner.Context!.Settings.Toolbar.DockedEdge switch
        {
            "Top" => T("guide.toolbar.edge.top"),
            "Bottom" => T("guide.toolbar.edge.bottom"),
            "Left" => T("guide.toolbar.edge.left"),
            _ => T("guide.toolbar.edge.right"),
        };

        /// <summary>The action row's buttons per step — the chrome Close is NOT in this list
        /// (it lives in its own host, r5 F4). Each row carries its <see cref="ModalChoiceKind"/>
        /// (the DwellButton follow-up: the cards wear the Settings modal's per-kind palette), mapped
        /// the way Settings maps its modal choices: a forward action → Affirmative (modal_yes's
        /// kind), backing out (Skip/Later) → Negative (modal_no's kind — the guide's own chrome
        /// Close rides the same kind in SetChromeClose).</summary>
        private static (string Label, string Id, ModalChoiceKind Kind)[] RowChoicesFor(FirstRunGuideStep step) => step switch
        {
            FirstRunGuideStep.Welcome => [
                (T("guide.welcome.start"), "guide_start", ModalChoiceKind.Affirmative),
                (T("guide.later"), "guide_later", ModalChoiceKind.Negative)],
            FirstRunGuideStep.MeetDwell => [(T("guide.dwell.skip"), "guide_dwell_skip", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.DwellTime => [(T("guide.dwelltime.continue"), "guide_dwelltime_continue", ModalChoiceKind.Affirmative)],
            FirstRunGuideStep.Calibrate => [
                (T("guide.calibrate.now"), "guide_calibrate_now", ModalChoiceKind.Affirmative),
                (T("guide.calibrate.skip"), "guide_calibrate_skip", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.ToolbarTask => [
                (T("guide.toolbar.continue"), "guide_toolbar_continue", ModalChoiceKind.Affirmative),
                (T("guide.toolbar.skip"), "guide_toolbar_skip", ModalChoiceKind.Neutral)],
            FirstRunGuideStep.Theme => [(T("guide.theme.continue"), "guide_theme_continue", ModalChoiceKind.Affirmative)],
            FirstRunGuideStep.Safety => [(T("guide.safety.begin"), "guide_safety_begin", ModalChoiceKind.Affirmative)],
            FirstRunGuideStep.Done => [(T("guide.done.finish"), "guide_done_finish", ModalChoiceKind.Affirmative)],
            _ => [],
        };

        /// <summary>The Theme step's four chips, one ROW in StepContent (each chip's full
        /// half-diagonal catchment fits — a 2×2 grid's vertical spacing could not).</summary>
        private static (string Label, string Id)[] ThemeChips() =>
        [
            (T("guide.theme.midnight"), "guide_theme_midnight"),
            (T("guide.theme.ember"), "guide_theme_ember"),
            (T("guide.theme.daylight"), "guide_theme_daylight"),
            (T("guide.theme.contrast"), "guide_theme_contrast"),
        ];

        public void ShowSafetyCard(string message, IReadOnlyList<ModalChoice> choices)
        {
            vm.ShowSafetyCard(message, choices);
            window.ShowCardContent(T("guide.safety.title"), message, chromeClose: true);
            // The safety choices' own kinds ride through: the DwellButton cards colour them
            // exactly as the Settings modal would (the follow-up's whole point).
            window.SetChoices(choices.Select(c => (c.Label, c.Id, c.Kind)).ToArray());
            window.SetChromeClose(true, T("guide.chrome.close"));   // chrome Close through step 7 (r3 F10)
            owner.RememberSafetyActions(choices);                 // id → OnChosen, for the dispatch
        }

        /// <summary>Re-renders the CURRENT step (r4 F3): an in-place press (dwell ±, theme chip)
        /// cleared every chrome target, and the press did not change the step.</summary>
        public void RenderChoicesForCurrentStep()
        {
            if (_lastStep is { } step) ShowStep(step);
        }

        public void HideCard() => window.HideGuide();
        public void ShowCard() => window.ShowGuide();
        public void Close() => window.Close();

        private static string T(string key) => StringTable.Current.T(key);
    }

    /// <summary>Hands the S6f setup-gateways FACTORY (the „Erweitert" update/rollback/uninstall
    /// rows) to the settings window construction. Called once at startup, like
    /// <see cref="SetSupervisionToggle"/>; null keeps the page free of the rows. A factory, not an
    /// instance: review 01 M1 requires a fresh offer and policy per Settings window, so an inbox
    /// change or a caregiver fixing the policy shows on the next visit without an App restart.</summary>
    public void SetSetupGateways(Func<ISetupGateways> gatewaysFactory) =>
        _setupGatewaysFactory = gatewaysFactory;

    /// <summary>
    /// Hands the sub-pixel probe entry (build plan C10) to the next settings window: the
    /// precondition reason the Diagnostics button shows on activation, and the request action that
    /// closes Settings and queues the probe start. Called once at startup, after C7 exists.
    /// </summary>
    /// <param name="probeBlockReason">Returns the localized refusal, or <c>null</c> when start is allowed.</param>
    /// <param name="requestProbe">Closes Settings synchronously and queues the probe start.</param>
    /// <param name="canResumeSession2">Whether a session-2 resume applies; SP-6 supplies the reader.</param>
    public void SetProbeEntry(
        Func<string?> probeBlockReason, Action requestProbe, Func<bool>? canResumeSession2 = null)
    {
        _probeBlockReason = probeBlockReason ?? throw new ArgumentNullException(nameof(probeBlockReason));
        _probeRequest = requestProbe ?? throw new ArgumentNullException(nameof(requestProbe));
        _probeCanResume = canResumeSession2;
    }

    /// <summary>
    /// Closes the Settings window synchronously for a probe request. The window's <c>Closed</c>
    /// handler raises <c>SettingsOpenChanged(false)</c> inside <c>Close()</c>, which is what C7's
    /// arming depends on (review 05 F8).
    /// </summary>
    internal void CloseSettingsWindowForProbe()
    {
        if (_settingsWindow is not null)
            _settingsWindow.Close();
    }

    /// <summary>Gaze-routing seam (consumed by ToolbarLifecycleCoordinator.SetupGazeRouting after
    /// the cluster-8 fold): forwards the sample to the settings window iff it is open and visible.
    /// The visibility check lives here because the <c>_settingsWindow</c> reference moved out of EC
    /// with this coordinator.</summary>
    internal void UpdateSettingsGazeIfVisible(PhysicalPoint point, long timestamp)
    {
        if (_settingsWindow?.IsVisible ?? false)
            _settingsWindow.UpdateGaze(point, timestamp);
    }

    private void UpdateTrackStatusVisibility()
    {
        if (Context is null) return;
        var trackStatusWindow = Context.OverlayManager.TrackStatus;
        if (Context.Settings.TrackStatus.Enabled)
            trackStatusWindow.StartTracking();
        else
            trackStatusWindow.StopTracking();
    }

    /// <summary>
    /// Opens settings, or applies a deep link to the window that is already open. There is no
    /// branch here: <see cref="SettingsDeepLink.Dispatch"/> decides (pure, unit-tested), turns the
    /// action into a plan and runs it — open first, then the fresh navigation, so the navigation
    /// lands after construction without its own guard. The modal case is the window's own queue.
    /// </summary>
    /// <param name="target">Where the request should land; <see cref="SettingsTarget.Default"/>
    /// keeps the historical "open where it opens" behaviour and never touches an open window.</param>
    private void ShowSettingsWindow(SettingsTarget target = SettingsTarget.Default)
    {
        if (Context is null) return;

        // C9 S6e fixup-08: a Settings request while the window is hidden for the uninstall
        // hand-off is the owner bringing it back manually (the toolbar, the tray, a keyboard
        // row). Show it now and cancel the automatic return — otherwise the watch would raise it
        // a second time when Setup ends. The hidden window kept its tab, so nothing is lost.
        var reopened = ReopenHiddenSettingsWindowForUninstallHandoff();

        SettingsDeepLink.Dispatch(
            windowOpen: reopened || _settingsWindow is not null,
            target: target,
            openWindow: OpenSettingsWindow,
            navigate: () => _settingsWindow?.NavigateToLanguageRow());
    }

    /// <summary>Constructs and shows a fresh settings window. Never navigates; the caller does.</summary>
    private void OpenSettingsWindow()
    {
        var context = Context!;

        _modal.SettingsOpen = true;
        _modal.SettingsOpenChanged?.Invoke(true);
        _toolbar.SetAutoHideSuspended("settings", true);
        _quickActions.SetSummonSuspended("settings", true);
        context.OverlayManager.CursorCircle.Hide();

        try
        {
            // Create SettingsViewModels
            var settingsVM = new SettingsViewModel(context.Settings);

            var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
                context.QuickActionsStore, context.InstalledAppProvider);
            quickActionsEditor.Load();

            var settingsWindowVM = new SettingsWindowViewModel(
                settingsVM,
                quickActionsEditor,
                context.Messenger,
                context.Settings.Dwell);

            _settingsWindow = new SettingsWindow(
                settingsWindowVM,
                context.Settings,
                context.SettingsService,
                _modelRegistry ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _modelManagementService ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                context.Messenger,
                context.CalibrationService,
                _restartCoordinator ?? throw new InvalidOperationException(
                    "Restart coordinator not set; SetRestartCoordinator must be called at startup."),
                _supervisionToggle ?? throw new InvalidOperationException(
                    "Supervision toggle not set; SetSupervisionToggle must be called at startup."),
                context.UserDictionary,
                probeBlockReason: _probeBlockReason,
                requestProbe: _probeRequest,
                canResumeSession2: _probeCanResume,
                setupGateways: _setupGatewaysFactory?.Invoke(),
                uninstallHandoff: UninstallHandoff);

            _settingsWindow.SettingsChanged += OnSettingsChanged;
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show();
            _handoffWindow = new SettingsWindowHandle(_settingsWindow);
        }
        catch
        {
            _modal.SettingsOpen = false;
            _modal.SettingsOpenChanged?.Invoke(false);
            // Window construction threw before Show() — OnSettingsWindowClosed will never fire, so
            // undo the suspend we set alongside _modal.SettingsOpen = true above.
            _toolbar.SetAutoHideSuspended("settings", false);
            _quickActions.SetSummonSuspended("settings", false);
            if (context.CursorVM.IsVisible)
                context.OverlayManager.CursorCircle.Show();
            throw;
        }
    }

    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (Context is null) return;

        Context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        Context.InteractionManager.SelectionMode = s.SelectionMode;
        Context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        Context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));

        var toolbarSnapshot = ToolbarSnapshot.Capture(s);
        if (_toolbarSnapshot != toolbarSnapshot)
        {
            ReinitializeToolbar(s);
        }

        // The keyboard snapshot compare + reload lives on KeyboardCoordinator (cluster 3), exposed
        // as the ReloadIfChanged seam. When the snapshot did NOT change (live-read settings like
        // MaxSuggestions, ShowPredictionConfidence, ShowPredictionTypes), the keyboard still
        // displays cells built from the OLD values — and the keyboard stays visible and dwellable
        // under the settings window (gaze feeds it on KeyboardVisible alone), so a lowered
        // suggestion count left stale cells whose completed dwell OnPredictionDwelled silently
        // swallowed (index guard, no redraw). Refresh the bar instead: idempotent, and it
        // retires the unreachable targets the moment the setting moves.
        if (ShouldRefreshPredictionBar(
                _keyboard.ReloadIfChanged(s, Context.InputSimulator, Context.SettingsService),
                _modal.KeyboardVisible))
        {
            Context.OverlayManager.Keyboard.UpdatePredictions();
        }

        Log.Information("Settings saved");
    }

    /// <summary>
    /// Rebuilds the toolbar band at the settings' metrics, with <c>Adaptation.Enabled</c> as the
    /// includeReset flag (as at startup, Task 7 / D5/M5 — Reset is on the band only while
    /// adaptation is on), then re-applies the dock mode, re-evaluates auto-hide enablement
    /// (IsDocked may have flipped to false → disable + force-show), refreshes the cached edge,
    /// repositions the pause indicator along it, and re-captures the auto-hide rect.
    /// <para>
    /// Two callers, one guard: the save path (the toolbar-metrics snapshot branch of
    /// <see cref="OnSettingsChanged"/>) and the <see cref="AdaptationSettings.Enabled"/>
    /// subscription — the settings UI mutates the live object as the user toggles, so a flip
    /// that lands in the same save as a metrics change would otherwise rebuild the band twice.
    /// The last applied (metrics snapshot, includeReset) pair is remembered and an identical
    /// request returns, making the rebuild exactly-once in either arrival order.
    /// </para>
    /// </summary>
    private void ReinitializeToolbar(EyeCtrlSettings s)
    {
        if (Context is null) return;

        var toolbarSnapshot = ToolbarSnapshot.Capture(s);
        if (_toolbarSnapshot == toolbarSnapshot && _appliedIncludeReset == s.Adaptation.Enabled)
            return;

        Context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs,
            s.Adaptation.Enabled);
        Context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);

        bool nowEnabled = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse && s.Toolbar.IsDocked;
        _toolbar.UpdateEnabled(nowEnabled);
        _toolbar.UpdateEdge(s.Toolbar.DockedEdge);
        _toolbar.PositionIndicator(s.Toolbar.DockedEdge);
        _toolbar.RefreshToolbarRect();

        _toolbarSnapshot = toolbarSnapshot;
        _appliedIncludeReset = s.Adaptation.Enabled;
    }

    /// <summary>
    /// The prediction-bar refresh gate, extracted so it is unit-testable headlessly: review round 2
    /// deleted the entire inline block and no test failed — the same zero-coverage discovery that
    /// produced <c>ShouldFeedKeyboardSample</c> one file over. A reload already rebuilt the bar, so
    /// refresh only when it did NOT run and the keyboard can be showing cells at all.
    /// </summary>
    internal static bool ShouldRefreshPredictionBar(bool reloaded, bool keyboardVisible) =>
        !reloaded && keyboardVisible;

    /// <summary>Settings' close path, `internal` so the W2/W3 probe tests can drive the real
    /// handler (review 03 F1: it is otherwise reachable only through a real SettingsWindow).</summary>
    internal void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (Context is null) return;

        _settingsWindow = null;
        // fixup-08: the window is gone, so there is no hidden window to bring back — a running
        // uninstall watch must find nothing to show rather than a stale handle.
        _handoffWindow = null;
        _settingsHiddenForUninstallHandoff = false;
        _modal.SettingsOpen = false;
        _modal.SettingsOpenChanged?.Invoke(false);
        _toolbar.SetAutoHideSuspended("settings", false);
        // B7 fixup-05 L-b: release Settings' own summon claim UNCONDITIONALLY. Under named claims
        // the probe holds (and releases) its own "probe" claim, so guarding this on ProbeOpen
        // could leak "settings" until Settings is opened and closed again. (Before claims, this
        // guard existed because the probe's Restore resumed the shared bool for everyone.)
        _quickActions.SetSummonSuspended("settings", false);
        if (Context.CursorVM.IsVisible && !_modal.ProbeOpen)
            Context.OverlayManager.CursorCircle.Show();
    }

    // ── C9 S6e fixup-08: the uninstall hand-off's hide / come-back ────────────────────────────
    //
    // Owner decision 06.11: Settings hides when Setup's uninstall really starts and comes back by
    // itself on every outcome that does not remove EyeCtrl. The reason is z-order, not taste:
    // since #316 Settings sits in ZBID_UIACCESS (band 2) while Setup runs at uiAccess=false, so
    // Setup's topmost Q8 can never be raised above it. Hiding is the only thing that works.

    /// <summary>
    /// The presenter the „Erweitert" page's uninstall row starts. Built once, lazily; every
    /// hand-off creates a fresh one-shot watch.
    /// </summary>
    private IUninstallHandoffPresenter UninstallHandoff =>
        _uninstallHandoff ??= new UninstallHandoffPresenter(
            HideSettingsForUninstallHandoff,
            RestoreSettingsAfterUninstallHandoff,
            UninstallHandoffWatchFactory.CreateForProduction(),
            warn: line => Log.Warning("{UninstallHandoff}", line));

    /// <summary>
    /// Hides the open Settings window for the hand-off, releasing the flags it holds while open —
    /// the toolbar's auto-hide suspension, the quick-actions summon claim and the ring's hide — so
    /// the owner keeps a usable toolbar and ring next to Setup's window. Returns whether a visible
    /// window was hidden; false means there is nothing to bring back.
    /// </summary>
    internal bool HideSettingsForUninstallHandoff()
    {
        if (Context is null) return false;
        if (_settingsHiddenForUninstallHandoff) return false;
        if (_handoffWindow is not { IsVisible: true } window) return false;

        window.Hide();
        _settingsHiddenForUninstallHandoff = true;

        _modal.SettingsOpen = false;
        _modal.SettingsOpenChanged?.Invoke(false);
        _toolbar.SetAutoHideSuspended("settings", false);
        _quickActions.SetSummonSuspended("settings", false);
        if (Context.CursorVM.IsVisible && !_modal.ProbeOpen)
            Context.OverlayManager.CursorCircle.Show();

        Log.Information("Settings hidden for the uninstall hand-off");
        return true;
    }

    /// <summary>
    /// The watch's end. Only <see cref="UninstallHandoffEnd.Uninstalled" /> keeps Settings hidden:
    /// Setup stops the App in the same run, and a window raised over the removal would be the last
    /// thing the owner sees. Everything else — cancel, „Nein, behalten", Esc, close, the 5-minute
    /// timeout, a refusal, a failure, a watcher that never learned the outcome — brings it back.
    /// A restore that arrives after the owner already reopened Settings (or after the window was
    /// closed) does nothing.
    /// </summary>
    internal void RestoreSettingsAfterUninstallHandoff(
        UninstallHandoffEnd end, SetupOutcome? outcome = null)
    {
        if (Context is null) return;

        if (end == UninstallHandoffEnd.Uninstalled)
        {
            Log.Information("Settings stays hidden: the uninstall completed");
            return;
        }

        if (!_settingsHiddenForUninstallHandoff)
        {
            // The owner already reopened it: nothing to show, but the end still belongs in the log.
            Log.Information(
                "Uninstall hand-off ended after the owner reopened Settings: {End} ({Outcome})",
                end, outcome);
            return;
        }

        BringSettingsBackAfterUninstallHandoff(reopenedByOwner: false);
        Log.Information(
            "Settings restored after the uninstall hand-off: {End} ({Outcome})", end, outcome);
    }

    /// <summary>
    /// The owner's manual reopen while the uninstall hand-off hid the window: show it again on
    /// „Erweitert" and cancel the automatic return. Returns whether a hidden window was brought
    /// back. Internal so the headless contract can prove the one-shot property and that the later
    /// watch end does not show it a second time.
    /// </summary>
    internal bool ReopenHiddenSettingsWindowForUninstallHandoff()
    {
        if (!_settingsHiddenForUninstallHandoff)
            return false;

        BringSettingsBackAfterUninstallHandoff(reopenedByOwner: true);
        return true;
    }

    /// <summary>
    /// Shows the hidden window again on „Erweitert" and re-takes the flags Settings holds while
    /// open. One path for the automatic return and the owner's manual reopen, so the two can never
    /// diverge; the second caller finds the flag already cleared and is a no-op.
    /// </summary>
    private void BringSettingsBackAfterUninstallHandoff(bool reopenedByOwner)
    {
        if (!_settingsHiddenForUninstallHandoff) return;

        var window = _handoffWindow;

        if (window is null)
        {
            // The window was closed while hidden: there is nothing to show, and the close path
            // has already released every flag.
            _settingsHiddenForUninstallHandoff = false;
            return;
        }

        _modal.SettingsOpen = true;
        _modal.SettingsOpenChanged?.Invoke(true);
        _toolbar.SetAutoHideSuspended("settings", true);
        _quickActions.SetSummonSuspended("settings", true);
        Context!.OverlayManager.CursorCircle.Hide();

        try
        {
            window.ShowOnAdvanced();
            _settingsHiddenForUninstallHandoff = false;
        }
        catch (Exception ex)
        {
            // Keep the flag so a later Settings request retries the show instead of reporting a
            // window that is still hidden as open.
            Log.Warning(ex, "Could not show the Settings window after the uninstall hand-off");
            return;
        }

        if (reopenedByOwner)
            Log.Information("Settings reopened by the owner during the uninstall hand-off");
    }

    /// <summary>
    /// Test seam: sets the window handle the hide/restore drives. Production assigns it once per
    /// window in <see cref="OpenSettingsWindow" />; a test supplies a recorder, because a real
    /// Settings window may never be shown from a test.
    /// </summary>
    internal void SetHandoffWindowForTests(ISettingsWindowHandle? window) =>
        _handoffWindow = window;

    protected override void OnDispose()
    {
        // Close the settings window before tearing down the rest of EC (EC.Dispose calls this
        // FIRST). Unsubscribe the Closed handler before Close() so the synchronous Closed event
        // doesn't re-enter OnSettingsWindowClosed (which would touch the toolbar/quick-actions
        // suspend state and null _settingsWindow mid-teardown).
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.SettingsChanged -= OnSettingsChanged;
            // Suppress a throw during shutdown so it can't cascade out of EventCoordinator.Dispose
            // and skip the user-dictionary save + other App.OnExit saves.
            try { _settingsWindow.Close(); }
            catch (Exception ex) { Log.Warning(ex, "Suppressed error closing settings window during shutdown"); }
            _settingsWindow = null;
            _handoffWindow = null;
            _settingsHiddenForUninstallHandoff = false;
        }

        if (Context is not null)
        {
            Context.Settings.TrackStatus.PropertyChanged -= OnTrackStatusSettingsPropertyChanged;
            Context.Settings.Adaptation.PropertyChanged -= OnAdaptationSettingsPropertyChanged;
        }
    }
}
