namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the quick-actions hot-edge summon surface of <see cref="EventCoordinator"/>: the
/// <see cref="QuickActionsSummonController"/> lifecycle, the gaze feed, the effective-summon-edge
/// resolution (conflict avoidance against the docked toolbar), and the bidirectional
/// controller↔grid-VM visibility sync. Exposes <see cref="SetSummonSuspended"/> as the seam the
/// keyboard/settings coordinators call when a modal opens. Extracted from <c>EventCoordinator</c> as
/// part of the sub-coordinator decomposition (cluster 5).
/// </summary>
internal sealed class QuickActionsCoordinator : CoordinatorBase
{
    private QuickActionsSummonController? _summon;
    private Action<GazePoint>? _quickActionsGazeHandler;       // keep the SOURCE name — moved bodies use it verbatim
    private PropertyChangedEventHandler? _quickActionsSettingsHandler;
    // True once the toolbar-settings + grid-VM PropertyChanged handlers are attached (create path
    // only). Gates the matching detach in Dispose.
    private bool _vmSubscriptionsAttached;
    protected override void OnInitialize()
    {
        var context = Context!;
        SetupQuickActionsSummon();
    }

    /// <summary>Suspend/resume the hot-edge summon (called by Keyboard/Settings coordinators when a
    /// modal opens). Wraps _summon.SetSuspended.</summary>
    public void SetSummonSuspended(bool suspended) => _summon?.SetSuspended(suspended);

    // Test seams (production never calls these): inject a pre-built controller so the suspend/resume
    // path can be exercised without the full QuickActionsGridVM/GazeEngine creation path, and read
    // back the suspended state without reflection.
    internal void InjectSummonForTesting(QuickActionsSummonController summon) => _summon = summon;
    internal bool IsSummonSuspended => _summon?.IsSuspended ?? true;

    private void UpdateQuickActionsEffectiveEdge()
    {
        if (Context is null) return;
        Context.QuickActionsGridVM.EffectiveSummonEdge = ComputeActualSummonEdge(Context.Settings.QuickActions.SummonEdge);
    }

    private void AttachQuickActionsSettingsHandler()
    {
        if (_quickActionsSettingsHandler != null || Context is null) return;

        _quickActionsSettingsHandler = (_, e) =>
        {
            if (Context is null) return;

            if (e.PropertyName == nameof(QuickActionsSettings.Enabled))
            {
                if (Context.Settings.QuickActions.Enabled)
                {
                    if (_summon == null)
                        SetupQuickActionsSummon();
                    else
                        _summon.SetSuspended(false);
                }
                else
                {
                    _summon?.SetSuspended(true);
                }
                return;
            }

            if (_summon == null || e.PropertyName != nameof(QuickActionsSettings.SummonEdge)) return;
            UpdateQuickActionsEffectiveEdge();
            _summon.UpdateEdge(Context.QuickActionsGridVM.EffectiveSummonEdge);
        };
        Context.Settings.QuickActions.PropertyChanged += _quickActionsSettingsHandler;
    }

    private void SetupQuickActionsSummon()
    {
        var s = Context!.Settings.QuickActions;
        AttachQuickActionsSettingsHandler();

        if (_summon != null)
        {
            _summon.SetSuspended(!s.Enabled);
            return;
        }

        if (!s.Enabled) return;

        // Hot-edge summon is a gaze gesture — disabled in mouse mode (the OS clamps the cursor to
        // the screen so it can't go "past" an edge, and on multi-monitor a Right/Bottom edge could
        // misfire on cursor travel). The toolbar QuickActions button remains the mouse-mode path.
        if (EyeCtrl.App.App.ActiveProviderType == Core.Enums.GazeProviderType.Mouse) return;

        // Resolve actual summon edge (avoid conflict with main toolbar)
        var actualEdge = ComputeActualSummonEdge(s.SummonEdge);

        // Match the toolbar's screen-size source (ToolbarLifecycleCoordinator.SetupToolbarAutoHide): WPF SystemParameters
        // are logical DIPs, so ×ScaleX/×ScaleY yields physical px. Do NOT use WinForms
        // Screen.Bounds — it is ALREADY physical under PerMonitorV2, so ×ScaleX double-scales
        // (would break Right/Bottom summon edges on scaled displays; the default "Left" hides it).
        var controller = new QuickActionsSummonController(
            actualEdge,
            System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY,
            s.SummonThresholdMs,
            s.DismissTimeoutMs,
            calibrationActive: () => Context.CalibrationService.IsSessionActive);

        // Gaze-thread → UI-thread marshal: both events hop through the dispatcher
        // before touching the VM (mirrors ToolbarAutoHideController's visibility adapter).
        var gridVM = Context.QuickActionsGridVM;
        // Keep the VM's effective edge in sync with the resolved edge (the controller watches the
        // resolved edge via ComputeActualSummonEdge; the grid window must anchor there too, or it
        // renders at the raw configured edge which may differ when it collides with the toolbar dock).
        UpdateQuickActionsEffectiveEdge();
        controller.SummonRequested += () =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                // Toolbar button / mouse-mode may have already shown it; only show when hidden.
                if (!gridVM.IsVisible)
                {
                    UpdateQuickActionsEffectiveEdge();   // edge may have changed since last show
                    gridVM.Receive(new QuickActionsToggledMessage()); // → Reload + show
                    // Do NOT call EnterSummoned here: Feed already set the controller to Summoned
                    // with the gaze-away dismiss timer armed (_summonOpened=true). EnterSummoned
                    // would clear that flag and disable auto-dismiss for hot-edge summons.
                }
            });
        controller.DismissRequested += () =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                if (gridVM.IsVisible)
                {
                    gridVM.HideOverlay();      // hide WITHOUT round-tripping through Receive/toggle
                    controller.ResetToIdle();  // explicit-state hook (no re-entrant DismissRequested)
                }
            });

        // Feed takes a GazePoint (validity packed inside) — no unwrap/rewrap.
        _quickActionsGazeHandler = sample =>
            controller.Feed(sample, sample.TimestampMicroseconds);
        Context.GazeEngine.GazeSampled += _quickActionsGazeHandler;

        _summon = controller;

        // Both subscriptions below use NAMED handlers so Dispose can -= them. They were anonymous
        // lambdas before the decomposition and were never detached; each one rooted this coordinator
        // through a settings object / VM that outlives EventCoordinator. (Same technique the T10
        // split applied to TaskSwitcherVM.PropertyChanged.) Assigning _summon above first lets the
        // handlers read the field instead of capturing the `controller` local.
        //
        // React to toolbar edge changes — re-evaluate conflict (not just pass-through)
        Context.Settings.Toolbar.PropertyChanged += OnToolbarSettingsPropertyChanged;

        // H1 fix — bidirectional controller↔VM sync. Subscribe to grid VM IsVisible so that
        // toolbar-button shows (which set IsVisible=true without going through the controller)
        // and any other VM-driven show/hide keep the controller honest.
        gridVM.PropertyChanged += OnGridVmPropertyChanged;

        // Gate the Dispose-side detach: these two run only on the create-a-controller path, so a
        // suspend-only Initialize (or InjectSummonForTesting, whose context leaves Settings/GridVM
        // null!) must not have Dispose dereference them.
        _vmSubscriptionsAttached = true;
    }

    private void OnToolbarSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;
        if (e.PropertyName == nameof(ToolbarSettings.DockedEdge))
        {
            UpdateQuickActionsEffectiveEdge();
            _summon?.UpdateEdge(Context.QuickActionsGridVM.EffectiveSummonEdge);
        }
    }

    private void OnGridVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;
        if (e.PropertyName != nameof(QuickActionsGridViewModel.IsVisible)) return;

        var gridVM = Context.QuickActionsGridVM;
        Context.Dispatcher.BeginInvoke(() =>
        {
            // _summon is assigned before this handler is subscribed and never nulled, so it is
            // non-null in practice; the local keeps the dispatched closure independent of the field.
            var controller = _summon;
            if (controller == null) return;

            if (gridVM.IsVisible)
            {
                // Only mark VM-driven shows (toolbar button, etc.), which leave the controller
                // Idle. A hot-edge summon already set it to Summoned WITH the dismiss timer
                // armed; calling EnterSummoned again would clear _summonOpened and kill
                // auto-dismiss. So sync only when the controller didn't initiate the show.
                if (controller.State == QuickActionsSummonController.SummonState.Idle)
                    controller.EnterSummoned();
            }
            else
            {
                controller.ResetToIdle();
            }
        });
    }

    private string ComputeActualSummonEdge(string configuredEdge)
    {
        if (configuredEdge == Context!.Settings.Toolbar.DockedEdge)
            return configuredEdge switch
            {
                "Left" => "Right", "Right" => "Left",
                "Top" => "Bottom", "Bottom" => "Top",
                _ => "Left"
            };
        return configuredEdge;
    }

    protected override void OnDispose()
    {
        if (_quickActionsGazeHandler != null) Context!.GazeEngine.GazeSampled -= _quickActionsGazeHandler;
        if (_quickActionsSettingsHandler != null) Context!.Settings.QuickActions.PropertyChanged -= _quickActionsSettingsHandler;
        if (_vmSubscriptionsAttached)
        {
            Context!.Settings.Toolbar.PropertyChanged -= OnToolbarSettingsPropertyChanged;
            Context!.QuickActionsGridVM.PropertyChanged -= OnGridVmPropertyChanged;
        }
    }
}
