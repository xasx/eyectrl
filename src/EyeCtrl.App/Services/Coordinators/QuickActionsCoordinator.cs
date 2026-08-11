namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

internal sealed class QuickActionsCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private QuickActionsSummonController? _summon;
    private Action<GazePoint>? _quickActionsGazeHandler;       // keep the SOURCE name — moved bodies use it verbatim
    private PropertyChangedEventHandler? _quickActionsSettingsHandler;
    private bool _disposed;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
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
        if (_context == null) return;
        _context.QuickActionsGridVM.EffectiveSummonEdge = ComputeActualSummonEdge(_context.Settings.QuickActions.SummonEdge);
    }

    private void AttachQuickActionsSettingsHandler()
    {
        if (_quickActionsSettingsHandler != null || _context == null) return;

        _quickActionsSettingsHandler = (_, e) =>
        {
            if (_context == null) return;

            if (e.PropertyName == nameof(QuickActionsSettings.Enabled))
            {
                if (_context.Settings.QuickActions.Enabled)
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
            _summon.UpdateEdge(_context.QuickActionsGridVM.EffectiveSummonEdge);
        };
        _context.Settings.QuickActions.PropertyChanged += _quickActionsSettingsHandler;
    }

    private void SetupQuickActionsSummon()
    {
        var s = _context!.Settings.QuickActions;
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

        // Match the toolbar's screen-size source (EventCoordinator.cs:452): WPF SystemParameters
        // are logical DIPs, so ×ScaleX/×ScaleY yields physical px. Do NOT use WinForms
        // Screen.Bounds — it is ALREADY physical under PerMonitorV2, so ×ScaleX double-scales
        // (would break Right/Bottom summon edges on scaled displays; the default "Left" hides it).
        var controller = new QuickActionsSummonController(
            actualEdge,
            System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY,
            s.SummonThresholdMs,
            s.DismissTimeoutMs,
            calibrationActive: () => _context.CalibrationService.IsSessionActive);

        // Gaze-thread → UI-thread marshal: both events hop through the dispatcher
        // before touching the VM (mirrors ToolbarAutoHideController's visibility adapter).
        var gridVM = _context.QuickActionsGridVM;
        // Keep the VM's effective edge in sync with the resolved edge (the controller watches the
        // resolved edge via ComputeActualSummonEdge; the grid window must anchor there too, or it
        // renders at the raw configured edge which may differ when it collides with the toolbar dock).
        UpdateQuickActionsEffectiveEdge();
        controller.SummonRequested += () =>
            _context.Dispatcher.BeginInvoke(() =>
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
            _context.Dispatcher.BeginInvoke(() =>
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
        _context.GazeEngine.GazeSampled += _quickActionsGazeHandler;

        _summon = controller;

        // React to toolbar edge changes — re-evaluate conflict (not just pass-through)
        _context.Settings.Toolbar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolbarSettings.DockedEdge))
            {
                UpdateQuickActionsEffectiveEdge();
                _summon?.UpdateEdge(gridVM.EffectiveSummonEdge);
            }
        };

        // H1 fix — bidirectional controller↔VM sync. Subscribe to grid VM IsVisible so that
        // toolbar-button shows (which set IsVisible=true without going through the controller)
        // and any other VM-driven show/hide keep the controller honest.
        gridVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(QuickActionsGridViewModel.IsVisible)) return;
            _context.Dispatcher.BeginInvoke(() =>
            {
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
        };
    }

    private string ComputeActualSummonEdge(string configuredEdge)
    {
        if (configuredEdge == _context!.Settings.Toolbar.DockedEdge)
            return configuredEdge switch
            {
                "Left" => "Right", "Right" => "Left",
                "Top" => "Bottom", "Bottom" => "Top",
                _ => "Left"
            };
        return configuredEdge;
    }

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }
        if (_quickActionsGazeHandler != null) _context.GazeEngine.GazeSampled -= _quickActionsGazeHandler;
        if (_quickActionsSettingsHandler != null) _context.Settings.QuickActions.PropertyChanged -= _quickActionsSettingsHandler;
        _context.Messenger.UnregisterAll(this);
        _disposed = true;
    }
}
