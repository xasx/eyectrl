namespace EyeCtrl.App.Services.Coordinators;

using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
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
internal sealed class SettingsUiCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly KeyboardCoordinator _keyboard;
    private SettingsWindow? _settingsWindow;
    private ModelRegistryService? _modelRegistry;
    private ModelManagementService? _modelManagementService;
    private AppRestartCoordinator? _restartCoordinator;
    // Owned HERE — moved from EventCoordinator. Read in OnSettingsChanged to gate the toolbar
    // re-init (only re-initialize the toolbar when its layout-affecting settings changed).
    private ToolbarSnapshot? _toolbarSnapshot;
    private bool _disposed;

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

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"

        var messenger = context.Messenger;

        // Settings window
        messenger.Register<SettingsUiCoordinator, SettingsRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => ShowSettingsWindow()));

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
    }

    private void OnTrackStatusSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;
        if (e.PropertyName == nameof(TrackStatusSettings.Enabled))
            _context.Dispatcher.BeginInvoke(UpdateTrackStatusVisibility);
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
        if (_context == null) return;
        var trackStatusWindow = _context.OverlayManager.TrackStatus;
        if (_context.Settings.TrackStatus.Enabled)
            trackStatusWindow.StartTracking();
        else
            trackStatusWindow.StopTracking();
    }

    private void ShowSettingsWindow()
    {
        if (_context == null || _settingsWindow != null) return;

        _modal.SettingsOpen = true;
        _toolbar.SetAutoHideSuspended(true);
        _quickActions.SetSummonSuspended(true);
        _context.OverlayManager.CursorCircle.Hide();

        try
        {
            // Create SettingsViewModels
            var settingsVM = new SettingsViewModel(
                _context.Settings,
                _context.SettingsService,
                _context.Messenger);

            var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
                _context.QuickActionsStore, _context.InstalledAppProvider);
            quickActionsEditor.Load();

            var settingsWindowVM = new SettingsWindowViewModel(
                settingsVM,
                quickActionsEditor,
                _context.Messenger,
                _context.Settings.Dwell);

            _settingsWindow = new SettingsWindow(
                settingsWindowVM,
                _context.Settings,
                _context.SettingsService,
                _modelRegistry ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _modelManagementService ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _context.Messenger,
                _context.CalibrationService,
                _restartCoordinator ?? throw new InvalidOperationException(
                    "Restart coordinator not set; SetRestartCoordinator must be called at startup."));

            _settingsWindow.SettingsChanged += OnSettingsChanged;
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show();
        }
        catch
        {
            _modal.SettingsOpen = false;
            // Window construction threw before Show() — OnSettingsWindowClosed will never fire, so
            // undo the suspend we set alongside _modal.SettingsOpen = true above.
            _toolbar.SetAutoHideSuspended(false);
            _quickActions.SetSummonSuspended(false);
            if (_context.CursorVM.IsVisible)
                _context.OverlayManager.CursorCircle.Show();
            throw;
        }
    }

    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (_context == null) return;

        _context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        _context.InteractionManager.SelectionMode = s.SelectionMode;
        _context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        _context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));

        var toolbarSnapshot = ToolbarSnapshot.Capture(s);
        if (_toolbarSnapshot != toolbarSnapshot)
        {
            _context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
            _context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);

            // Re-evaluate enablement (IsDocked may have flipped to false → disable + force-show),
            // refresh the cached edge, and reposition the pause indicator along the new edge.
            bool nowEnabled = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse && s.Toolbar.IsDocked;
            _toolbar.UpdateEnabled(nowEnabled);
            _toolbar.UpdateEdge(s.Toolbar.DockedEdge);
            _toolbar.PositionIndicator(s.Toolbar.DockedEdge);
            _toolbar.RefreshToolbarRect();

            _toolbarSnapshot = toolbarSnapshot;
        }

        // The keyboard snapshot compare + reload lives on KeyboardCoordinator (cluster 3), exposed
        // as the ReloadIfChanged seam.
        _keyboard.ReloadIfChanged(s, _context.InputSimulator, _context.SettingsService);

        Log.Information("Settings saved");
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_context == null) return;

        _settingsWindow = null;
        _modal.SettingsOpen = false;
        _toolbar.SetAutoHideSuspended(false);
        _quickActions.SetSummonSuspended(false);
        if (_context.CursorVM.IsVisible)
            _context.OverlayManager.CursorCircle.Show();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
        }

        if (_context != null)
        {
            _context.Settings.TrackStatus.PropertyChanged -= OnTrackStatusSettingsPropertyChanged;
            _context.Messenger.UnregisterAll(this);
        }
    }
}
