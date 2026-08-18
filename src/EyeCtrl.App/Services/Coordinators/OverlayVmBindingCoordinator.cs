namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Property-change-driven half of <see cref="EventCoordinator"/>'s overlay surface: the overlay VM
/// bindings, TaskSwitcher binding, and glass backdrop lifecycle. Sibling <see cref="OverlayMessageCoordinator"/>;
/// <see cref="HideBackdrop"/> is the cross-side seam (this coordinator owns the <c>_glassBackdrop</c> window).
/// </summary>
internal sealed class OverlayVmBindingCoordinator : IDisposable
{
    private const int ActivationButtonOffsetPixels = 55;
    // Inset (logical px) from the right screen edge for drag/manual-selection edge buttons.
    private const double RightEdgeInsetLogical = 30;

    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private GlassBackdropWindow? _glassBackdrop;
    private bool _disposed;

    public OverlayVmBindingCoordinator(ModalInteractionState modal)
    {
        // Dereferenced by the cursor-visibility guard (OnCursorVMPropertyChanged reads
        // _modal.SettingsOpen), so reject null at the ctor rather than on the first gaze frame.
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
    }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"

        SetupPropertyChangedHandlers();
        SetupBackdrop();
    }

    private void SetupPropertyChangedHandlers()
    {
        if (_context == null) return;

        // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
        _context.CursorVM.PropertyChanged += OnCursorVMPropertyChanged;

        // ActivationButton: VM -> show/hide/dwell
        _context.ActivationVM.PropertyChanged += OnActivationVMPropertyChanged;

        // ManualSelectionOverlay: VM -> show/hide/reposition
        _context.ManualSelectionVM.PropertyChanged += OnManualSelectionVMPropertyChanged;

        // ManualSelectionGazeIndicator: VM property changes -> window updates
        _context.ManualSelectionIndicatorVM.PropertyChanged += OnManualSelectionIndicatorVMPropertyChanged;

        // ManualSelection: VM visibility -> edge bar show/hide
        _context.ManualSelectionVM.PropertyChanged += OnManualSelectionVisibilityChanged;

        // ActionMenu: VM -> show/hide
        _context.ActionMenuVM.PropertyChanged += OnActionMenuVMPropertyChanged;

        // Toolbar: VM state changes -> toolbar state
        _context.ToolbarVM.PropertyChanged += OnToolbarVMPropertyChanged;

        // GlassBackdrop: VM progress -> backdrop window update
        _context.BackdropVM.PropertyChanged += OnBackdropVMPropertyChanged;

        // Task Switcher: the VM receives TaskSwitcherToggledMessage itself (via IRecipient +
        // ObservableRecipient auto-register) and toggles its own IsVisible + tiles. The
        // coordinator only watches IsVisible to show/hide the fullscreen window.
        // (Moved from an anonymous lambda in EventCoordinator.Initialize — extracting a named
        // handler lets Dispose -= it; the lambda was previously never unsubscribed.)
        _context.TaskSwitcherVM.PropertyChanged += OnTaskSwitcherVMPropertyChanged;
    }

    private void SetupBackdrop()
    {
        if (_context == null)
            return;
        _glassBackdrop = _context.OverlayManager.GlassBackdrop;
    }

    private void ShowBackdropIfEnabled()
    {
        if (_glassBackdrop == null || _context == null)
            return;
        var settings = _context.Settings.Backdrop;
        if (!settings.Enabled)
            return;

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
            return;
        var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
        var dipTop = UIConstants.BackdropHideButtonOffset;
        var hideButtonCenter = new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(dipRight),
            DpiHelper.LogicalToPhysicalY(dipTop));

        _glassBackdrop.Show();
        _glassBackdrop.ApplyBackdrop(settings);
        _glassBackdrop.DrawHideButton(hideButtonCenter);
        _context.Messenger.Send(new ShowBackdropMessage(hideButtonCenter));
    }

    /// <summary>Internal seam for the message-driven sibling — owns the <c>_glassBackdrop</c> window.</summary>
    internal void HideBackdrop()
    {
        _glassBackdrop?.Hide();
        _context?.Messenger.Send(new HideBackdropMessage());
    }

    private void OnCursorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var cursorCircle = _context.OverlayManager.CursorCircle;
        switch (e.PropertyName)
        {
            case nameof(CursorCircleViewModel.Position):
                cursorCircle.UpdatePosition(_context.CursorVM.Position);
                break;
            case nameof(CursorCircleViewModel.IsVisible):
                if (_context.CursorVM.IsVisible && !_modal.SettingsOpen) cursorCircle.Show();
                else cursorCircle.Hide();
                break;
        }
    }

    private void OnActivationVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var activationButton = _context.OverlayManager.ActivationButton;
        var actionMenu = _context.OverlayManager.ActionMenu;

        switch (e.PropertyName)
        {
            case nameof(ActivationButtonViewModel.IsVisible):
                if (_context.ActivationVM.IsVisible)
                {
                    actionMenu.Hide();
                    var circleRadius = _context.Settings.Cursor.CircleSize / 2;
                    var buttonOffset = circleRadius + ActivationButtonOffsetPixels;
                    activationButton.Show();
                    activationButton.ShowAt(_context.ActivationVM.Position, buttonOffset);
                }
                else
                {
                    activationButton.HideImmediate();
                }
                break;
            case nameof(ActivationButtonViewModel.DwellProgress):
                activationButton.UpdateDwellProgress(_context.ActivationVM.DwellProgress);
                break;
        }
    }

    private void OnManualSelectionVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var ManualSelectionOverlay = _context.OverlayManager.ManualSelectionOverlay;
        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;
        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;

        if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.Center) && _context.ManualSelectionVM.IsVisible)
        {
            ManualSelectionOverlay.UpdateCenter(_context.ManualSelectionVM.Center, _context.ManualSelectionVM.Radius, _context.ManualSelectionVM.Magnification);
        }
        else if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible))
        {
            if (_context.ManualSelectionVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();
                ShowBackdropIfEnabled();
                ManualSelectionOverlay.Show(_context.ManualSelectionVM.Center, _context.ManualSelectionVM.Radius, _context.ManualSelectionVM.Magnification, _context.ManualSelectionVM.GazePoint, _context.ManualSelectionVM.Recapture);
                ManualSelectionIndicator.Show();
                // Keep the gaze dot above the opaque magnifier: the Show() above is a no-op if the
                // indicator's own VM already showed it before the magnifier, leaving it hidden
                // underneath. Force it back to the top of the top-most band.
                ManualSelectionIndicator.RaiseToTop();
            }
            else
            {
                ManualSelectionOverlay.Hide();
                ManualSelectionIndicator.Hide();
                HideBackdrop();
            }
        }
    }

    private void OnManualSelectionIndicatorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;
        switch (e.PropertyName)
        {
            case nameof(ManualSelectionGazeIndicatorViewModel.IndicatorX):
            case nameof(ManualSelectionGazeIndicatorViewModel.IndicatorY):
                ManualSelectionIndicator.UpdatePosition(_context.ManualSelectionIndicatorVM.IndicatorX, _context.ManualSelectionIndicatorVM.IndicatorY);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.OuterCircleDiameter):
                ManualSelectionIndicator.UpdateOuterDiameter(_context.ManualSelectionIndicatorVM.OuterCircleDiameter);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.IsVisible):
                if (_context.ManualSelectionIndicatorVM.IsVisible) ManualSelectionIndicator.Show();
                else ManualSelectionIndicator.Hide();
                break;
        }
    }

    private void OnManualSelectionVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible))
        {
            if (_context.ManualSelectionVM.IsVisible)
            {
                var anchor = ScreenGeometry.RightEdgeAnchor(RightEdgeInsetLogical, UIConstants.ButtonRadius);
                var dwell = _context.Settings.Dwell.ManualSelectionSideButtonDwellMs;
                // Match the former side-button layout: full-size buttons (UIConstants.ButtonRadius)
                // separated so their edges are ManualSelectionSideButtonGap apart, centred on the anchor.
                var radius = UIConstants.ButtonRadius;
                var offset = DpiHelper.LogicalToPhysicalY(radius + UIConstants.ManualSelectionSideButtonGap / 2);
                var defs = new[]
                {
                    new EdgeButtonDefinition("ManualSelection_close", "\u2715", dwell,
                        m => m.Send(new ManualSelectionCloseMessage()), VerticalOffset: -offset,
                        RadiusLogical: radius),
                    new EdgeButtonDefinition("ManualSelection_recapture", "\u21BB", dwell,
                        m => m.Send(new ManualSelectionRecaptureMessage()), VerticalOffset: offset,
                        RadiusLogical: radius),
                };
                _context.OverlayManager.EdgeButtonBar.ShowButtons(defs, anchor);
            }
            else
            {
                _context.OverlayManager.EdgeButtonBar.Hide();
            }
        }
    }

    private void OnActionMenuVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var actionMenu = _context.OverlayManager.ActionMenu;
        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;

        if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
        {
            if (_context.ActionMenuVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();

                ShowBackdropIfEnabled();
                actionMenu.Show(
                    _context.ActionMenuVM.Center,
                    _context.Settings.ActionMenu.VisibleActions,
                    _context.ActionMenuVM.ShowBack,
                    _context.ActionMenuVM.ShowBack ? _context.Settings.Dwell.ManualSelectionActionMenuDwellMs : _context.Settings.Dwell.ActionMenuDwellMs,
                    _context.ActionMenuVM.EffectiveRingRadius,
                    _context.Settings.Cursor.CircleSize,
                    _context.Settings.Cursor.CircleColor,
                    _context.Settings.Cursor.PositionIndicatorStyle,
                    _context.ActionMenuVM.ManualSelectionCenter,
                    _context.ActionMenuVM.ManualSelectionRadius,
                    _context.ActionMenuVM.GazePoint);
            }
            else
            {
                actionMenu.Hide();
                HideBackdrop();
            }
        }
    }

    private void OnToolbarVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var toolbar = _context.OverlayManager.Toolbar;
        if (e.PropertyName == nameof(ToolbarViewModel.IsPaused))
        {
            toolbar.SetPausedState(_context.ToolbarVM.IsPaused);
        }
    }

    private void OnBackdropVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_glassBackdrop == null) return;
        if (e.PropertyName == nameof(GlassBackdropViewModel.HideButtonProgress))
            _glassBackdrop.UpdateHideButtonProgress(_context!.BackdropVM.HideButtonProgress);
    }

    private void OnTaskSwitcherVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        if (e.PropertyName == nameof(_context.TaskSwitcherVM.IsVisible))
        {
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (_context.TaskSwitcherVM.IsVisible)
                {
                    // The fullscreen switcher covers the toolbar, but dwell has no z-order:
                    // without this, a top-row tile dwell also fires the toolbar button
                    // underneath it (2026-08-18 field report). SetActive(false) also resets
                    // any in-flight toolbar dwell.
                    _context.ToolbarVM.SetActive(false);
                    _context.OverlayManager.TaskSwitcher.ShowFullScreen();
                }
                else
                {
                    _context.OverlayManager.TaskSwitcher.HideOverlay();
                    // Reactivate only if the toolbar is actually on screen: auto-hide may
                    // have hidden it while the switcher was up, and activating a hidden
                    // toolbar creates gaze targets for invisible buttons — the exact hazard
                    // SetActive's contract exists to prevent.
                    if (_context.OverlayManager.Toolbar.IsVisible)
                        _context.ToolbarVM.SetActive(true);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }

        // Mirror SetupPropertyChangedHandlers: unsubscribe the 8 overlay VMs + TaskSwitcherVM.
        _context.CursorVM.PropertyChanged -= OnCursorVMPropertyChanged;
        _context.ActivationVM.PropertyChanged -= OnActivationVMPropertyChanged;
        _context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVMPropertyChanged;
        _context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVisibilityChanged;
        _context.ManualSelectionIndicatorVM.PropertyChanged -= OnManualSelectionIndicatorVMPropertyChanged;
        _context.ActionMenuVM.PropertyChanged -= OnActionMenuVMPropertyChanged;
        _context.ToolbarVM.PropertyChanged -= OnToolbarVMPropertyChanged;
        _context.BackdropVM.PropertyChanged -= OnBackdropVMPropertyChanged;
        _context.TaskSwitcherVM.PropertyChanged -= OnTaskSwitcherVMPropertyChanged;

        _glassBackdrop?.Hide();
        _glassBackdrop = null;

        _disposed = true;
    }
}
