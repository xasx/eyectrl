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
internal sealed class OverlayVmBindingCoordinator : CoordinatorBase
{
    private const int ActivationButtonOffsetPixels = 55;
    // Inset (logical px) from the right screen edge for drag/manual-selection edge buttons.
    private const double RightEdgeInsetLogical = 30;

    private readonly ModalInteractionState _modal;
    private IGlassBackdropOverlay? _glassBackdrop;
    public OverlayVmBindingCoordinator(ModalInteractionState modal)
    {
        // Dereferenced by the cursor-visibility guard (OnCursorVMPropertyChanged reads
        // _modal.SettingsOpen), so reject null at the ctor rather than on the first gaze frame.
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
    }

    protected override void OnInitialize()
    {
        SetupPropertyChangedHandlers();
        SetupBackdrop();
    }

    private void SetupPropertyChangedHandlers()
    {
        if (Context is null) return;

        // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
        Context.CursorVM.PropertyChanged += OnCursorVMPropertyChanged;

        // ActivationButton: VM -> show/hide/dwell
        Context.ActivationVM.PropertyChanged += OnActivationVMPropertyChanged;

        // ManualSelectionOverlay: VM -> show/hide/reposition
        Context.ManualSelectionVM.PropertyChanged += OnManualSelectionVMPropertyChanged;

        // ManualSelectionGazeIndicator: VM property changes -> window updates
        Context.ManualSelectionIndicatorVM.PropertyChanged += OnManualSelectionIndicatorVMPropertyChanged;

        // ManualSelection: VM visibility -> edge bar show/hide
        Context.ManualSelectionVM.PropertyChanged += OnManualSelectionVisibilityChanged;

        // ActionMenu: VM -> show/hide
        Context.ActionMenuVM.PropertyChanged += OnActionMenuVMPropertyChanged;

        // Toolbar: VM state changes -> toolbar state
        Context.ToolbarVM.PropertyChanged += OnToolbarVMPropertyChanged;

        // GlassBackdrop: VM progress -> backdrop window update
        Context.BackdropVM.PropertyChanged += OnBackdropVMPropertyChanged;

        // Task Switcher: the VM receives TaskSwitcherToggledMessage itself (via IRecipient +
        // ObservableRecipient auto-register) and toggles its own IsVisible + tiles. The
        // coordinator only watches IsVisible to show/hide the fullscreen window.
        // (Moved from an anonymous lambda in EventCoordinator.Initialize — extracting a named
        // handler lets Dispose -= it; the lambda was previously never unsubscribed.)
        Context.TaskSwitcherVM.PropertyChanged += OnTaskSwitcherVMPropertyChanged;

        // Modes menu: same pattern as Task Switcher — the VM toggles its own IsVisible on the
        // toolbar's ModesMenuToggledMessage; this side shows/hides the panel. While it is up the
        // toolbar's own dwell is suspended (the 2026-08-18 dwell-through field report).
        Context.ModesMenuVM.PropertyChanged += OnModesMenuVMPropertyChanged;

        // Repeat cursor: shown only while the locked mode runs; position/progress drive the
        // contracting ring. The window moves itself per update (gaze-thread → dispatcher).
        Context.RepeatCursorVM.PropertyChanged += OnRepeatCursorVMPropertyChanged;
    }

    private void SetupBackdrop()
    {
        if (Context is null)
            return;
        _glassBackdrop = Context.OverlayManager.GlassBackdrop;
    }

    private void ShowBackdropIfEnabled()
    {
        if (_glassBackdrop == null || Context is null)
            return;
        var settings = Context.Settings.Backdrop;
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
        Context.Messenger.Send(new ShowBackdropMessage(hideButtonCenter));
    }

    /// <summary>Internal seam for the message-driven sibling — owns the <c>_glassBackdrop</c> window.</summary>
    internal void HideBackdrop()
    {
        _glassBackdrop?.Hide();
        Context?.Messenger.Send(new HideBackdropMessage());
    }

    private void OnCursorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        var cursorCircle = Context.OverlayManager.CursorCircle;
        switch (e.PropertyName)
        {
            case nameof(CursorCircleViewModel.Position):
                cursorCircle.UpdatePosition(Context.CursorVM.Position);
                break;
            case nameof(CursorCircleViewModel.IsVisible):
                if (Context.CursorVM.IsVisible && !_modal.SettingsOpen) cursorCircle.Show();
                else cursorCircle.Hide();
                break;
        }
    }

    private void OnActivationVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        var activationButton = Context.OverlayManager.ActivationButton;
        var actionMenu = Context.OverlayManager.ActionMenu;

        switch (e.PropertyName)
        {
            case nameof(ActivationButtonViewModel.IsVisible):
                if (Context.ActivationVM.IsVisible)
                {
                    actionMenu.Hide();
                    var circleRadius = Context.Settings.Cursor.CircleSize / 2;
                    var buttonOffset = circleRadius + ActivationButtonOffsetPixels;
                    activationButton.Show();
                    activationButton.ShowAt(Context.ActivationVM.Position, buttonOffset);
                }
                else
                {
                    activationButton.HideImmediate();
                }
                break;
            case nameof(ActivationButtonViewModel.DwellProgress):
                activationButton.UpdateDwellProgress(Context.ActivationVM.DwellProgress);
                break;
        }
    }

    private void OnManualSelectionVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        var ManualSelectionOverlay = Context.OverlayManager.ManualSelectionOverlay;
        var cursorCircle = Context.OverlayManager.CursorCircle;
        var activationButton = Context.OverlayManager.ActivationButton;
        var ManualSelectionIndicator = Context.OverlayManager.ManualSelectionIndicator;

        if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.Center) && Context.ManualSelectionVM.IsVisible)
        {
            ManualSelectionOverlay.UpdateCenter(Context.ManualSelectionVM.Center, Context.ManualSelectionVM.Radius, Context.ManualSelectionVM.Magnification);
        }
        else if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.RecaptureVersion) && Context.ManualSelectionVM.IsVisible)
        {
            // Same call as a Center change, but keyed off the version counter instead of Center's
            // own equality check: a recapture must refresh the screenshot even when Center/Radius/
            // Magnification are all unchanged from the last capture (the normal case — the view
            // stays put; only the content underneath is refreshed).
            ManualSelectionOverlay.UpdateCenter(Context.ManualSelectionVM.Center, Context.ManualSelectionVM.Radius, Context.ManualSelectionVM.Magnification);
        }
        else if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible))
        {
            if (Context.ManualSelectionVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();
                ShowBackdropIfEnabled();
                // The magnifier is the one view that currently renders no textual sign that ESC
                // works — the ring it can dwell into already carries labelled Close/Help buttons.
                // Not mirrored at the action-menu backdrop call site: OnManualSelectionDwell hides
                // this VM (IsVisible -> false, running the else-branch's HideBackdrop below) before
                // the ring's own ShowBackdropIfEnabled call re-shows the window, so the hint
                // correctly turns off rather than needing a second toggle there.
                _glassBackdrop?.ShowEscapeHint();
                ManualSelectionOverlay.Show(Context.ManualSelectionVM.Center, Context.ManualSelectionVM.Radius, Context.ManualSelectionVM.Magnification, Context.ManualSelectionVM.GazePoint, Context.ManualSelectionVM.Recapture);
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
        if (Context is null) return;

        var ManualSelectionIndicator = Context.OverlayManager.ManualSelectionIndicator;
        switch (e.PropertyName)
        {
            case nameof(ManualSelectionGazeIndicatorViewModel.IndicatorX):
            case nameof(ManualSelectionGazeIndicatorViewModel.IndicatorY):
                ManualSelectionIndicator.UpdatePosition(Context.ManualSelectionIndicatorVM.IndicatorX, Context.ManualSelectionIndicatorVM.IndicatorY);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.OuterCircleDiameter):
                ManualSelectionIndicator.UpdateOuterDiameter(Context.ManualSelectionIndicatorVM.OuterCircleDiameter);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.AnchorX):
            case nameof(ManualSelectionGazeIndicatorViewModel.AnchorY):
            case nameof(ManualSelectionGazeIndicatorViewModel.IsAnchorVisible):
                ManualSelectionIndicator.UpdateAnchor(
                    Context.ManualSelectionIndicatorVM.AnchorX,
                    Context.ManualSelectionIndicatorVM.AnchorY,
                    Context.ManualSelectionIndicatorVM.IsAnchorVisible);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.IsVisible):
                if (Context.ManualSelectionIndicatorVM.IsVisible) ManualSelectionIndicator.Show();
                else ManualSelectionIndicator.Hide();
                break;
        }
    }

    private void OnManualSelectionVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible))
        {
            if (Context.ManualSelectionVM.IsVisible)
            {
                var anchor = ScreenGeometry.RightEdgeAnchor(RightEdgeInsetLogical, UIConstants.ButtonRadius);
                var dwell = Context.Settings.Dwell.ManualSelectionSideButtonDwellMs;
                Context.OverlayManager.EdgeButtonBar.ShowButtons(
                    ManualSelectionEdgeButtons.Definitions(dwell), anchor);
            }
            else
            {
                Context.OverlayManager.EdgeButtonBar.Hide();
            }
        }
    }

    private void OnActionMenuVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        var actionMenu = Context.OverlayManager.ActionMenu;
        var cursorCircle = Context.OverlayManager.CursorCircle;
        var activationButton = Context.OverlayManager.ActivationButton;

        if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
        {
            if (Context.ActionMenuVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();

                ShowBackdropIfEnabled();
                ShowActionMenu();
            }
            else
            {
                actionMenu.Hide();
                HideBackdrop();
            }
        }
        else if ((e.PropertyName == nameof(ActionMenuViewModel.RingVersion)
                  || e.PropertyName == nameof(ActionMenuViewModel.ShowLabels))
                 && Context.ActionMenuVM.IsVisible)
        {
            // A layer switch, or the Help target turning names on. Same call either way, so the
            // ring, the preview capture and the indicator all rebuild together and the canvas can
            // never disagree with the DwellDetector. ShowActionMenu reads ShowLabels itself, so
            // there is nothing to keep in sync between the two triggers.
            ShowActionMenu();
        }
    }

    /// <summary>
    /// Redraws the action menu from the ViewModel's current items. One method rather than two
    /// call sites, because the open path and the layer-change path must build the ring from
    /// identical arguments — a divergence there is a canvas that disagrees with the DwellDetector,
    /// which means every slot fires the wrong action.
    /// </summary>
    private void ShowActionMenu()
    {
        if (Context is null) return;

        Context.OverlayManager.ActionMenu.Show(
            Context.ActionMenuVM.Center,
            Context.Settings.ActionMenu.VisibleActions,
            Context.ActionMenuVM.ShowBack,
            Context.ActionMenuVM.ShowBack ? Context.Settings.Dwell.ManualSelectionActionMenuDwellMs : Context.Settings.Dwell.ActionMenuDwellMs,
            Context.ActionMenuVM.EffectiveRingRadius,
            Context.Settings.Cursor.CircleSize,
            Context.Settings.Cursor.CircleColor,
            Context.Settings.Cursor.PositionIndicatorStyle,
            Context.ActionMenuVM.ManualSelectionCenter,
            Context.ActionMenuVM.ManualSelectionRadius,
            Context.ActionMenuVM.GazePoint,
            Context.ActionMenuVM.ShowLabels);
    }

    private void OnToolbarVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        var toolbar = Context.OverlayManager.Toolbar;
        if (e.PropertyName == nameof(ToolbarViewModel.IsPaused))
        {
            toolbar.SetPausedState(Context.ToolbarVM.IsPaused);
        }
    }

    private void OnBackdropVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_glassBackdrop == null) return;
        if (e.PropertyName == nameof(GlassBackdropViewModel.HideButtonProgress))
            _glassBackdrop.UpdateHideButtonProgress(Context!.BackdropVM.HideButtonProgress);
    }

    private void OnRepeatCursorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        Context.Dispatcher.BeginInvoke(() =>
        {
            var vm = Context.RepeatCursorVM;
            if (e.PropertyName == nameof(vm.IsVisible))
            {
                if (vm.IsVisible) Context.OverlayManager.RepeatCursor.ShowCursor();
                else Context.OverlayManager.RepeatCursor.HideCursor();
            }
            else if (e.PropertyName is nameof(vm.Position) or nameof(vm.DwellProgress))
            {
                if (vm.IsVisible) Context.OverlayManager.RepeatCursor.Update(vm);
            }
        });
    }

    private void OnModesMenuVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        if (e.PropertyName == nameof(Context.ModesMenuVM.IsVisible))
        {
            Context.Dispatcher.BeginInvoke(() =>
            {
                if (Context.ModesMenuVM.IsVisible)
                {
                    Context.ToolbarVM.SetActive(false);
                    Context.OverlayManager.ModesMenu.ShowAtTopCentre();
                }
                else
                {
                    Context.OverlayManager.ModesMenu.HideMenu();
                    // Reactivate only if the toolbar is actually on screen — same contract and
                    // same property as the task-switcher restore (window IsOnScreen, not VM
                    // IsVisible, which stays true during a slide-out).
                    if (Context.OverlayManager.Toolbar.IsOnScreen)
                        Context.ToolbarVM.SetActive(true);
                }
            });
        }
    }

    private void OnTaskSwitcherVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null) return;

        if (e.PropertyName == nameof(Context.TaskSwitcherVM.IsVisible))
        {
            Context.Dispatcher.BeginInvoke(() =>
            {
                if (Context.TaskSwitcherVM.IsVisible)
                {
                    // The fullscreen switcher covers the toolbar, but dwell has no z-order:
                    // without this, a top-row tile dwell also fires the toolbar button
                    // underneath it (2026-08-18 field report). SetActive(false) also resets
                    // any in-flight toolbar dwell.
                    Context.ToolbarVM.SetActive(false);
                    Context.OverlayManager.TaskSwitcher.ShowFullScreen();
                }
                else
                {
                    Context.OverlayManager.TaskSwitcher.HideOverlay();
                    // Reactivate only if the toolbar is actually on screen: auto-hide may
                    // have hidden it while the switcher was up, and activating a hidden
                    // toolbar creates gaze targets for invisible buttons — the exact hazard
                    // SetActive's contract exists to prevent. IsOnScreen, not IsVisible:
                    // once Hide() is deferred to a slide's completion, IsVisible is still true
                    // during a slide-out, while the band is leaving. It is deliberately TRUE
                    // during a slide-IN, where the band is visible and staying and dwell is
                    // armed by design — and re-arming here is the only repair for that case,
                    // since the slide-in's completion no longer touches dwell state.
                    if (Context.OverlayManager.Toolbar.IsOnScreen)
                        Context.ToolbarVM.SetActive(true);
                }
            });
        }
    }

    protected override void OnDispose()
    {
        if (Context is null) return;
        Context.CursorVM.PropertyChanged -= OnCursorVMPropertyChanged;
        Context.ActivationVM.PropertyChanged -= OnActivationVMPropertyChanged;
        Context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVMPropertyChanged;
        Context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVisibilityChanged;
        Context.ManualSelectionIndicatorVM.PropertyChanged -= OnManualSelectionIndicatorVMPropertyChanged;
        Context.ActionMenuVM.PropertyChanged -= OnActionMenuVMPropertyChanged;
        Context.ToolbarVM.PropertyChanged -= OnToolbarVMPropertyChanged;
        Context.BackdropVM.PropertyChanged -= OnBackdropVMPropertyChanged;
        Context.TaskSwitcherVM.PropertyChanged -= OnTaskSwitcherVMPropertyChanged;
        Context.ModesMenuVM.PropertyChanged -= OnModesMenuVMPropertyChanged;
        Context.RepeatCursorVM.PropertyChanged -= OnRepeatCursorVMPropertyChanged;

        _glassBackdrop?.Hide();
        _glassBackdrop = null;
    }
}
