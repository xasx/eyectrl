namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Fullscreen Task Switcher overlay ViewModel. Enumerates open windows via
/// <see cref="IWindowManager"/> into <see cref="Tiles"/> when toggled on, exposes
/// dwell targets over each tile plus a dedicated close target (<see cref="CloseTargetId"/>,
/// a pure dismissal that never selects a window), and sends
/// <see cref="TaskSwitcherSelectionMessage"/> when a tile is dwelt on. WPF-free
/// (ViewModels targets plain net10.0): the View (TaskSwitcherWindow) resolves icons via
/// IAppIconProvider using each tile's ProcessId.
/// </summary>
public partial class TaskSwitcherViewModel : DwellOverlayViewModelBase,
    IRecipient<TaskSwitcherToggledMessage>
{
    private readonly IWindowManager _windowManager;
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;  // H2 fix: declared + assigned

    public ObservableCollection<TaskSwitcherTileViewModel> Tiles { get; } = new();

    /// <summary>Reserved dwell-target id for the switcher's close button (not a window tile).</summary>
    public const string CloseTargetId = "__taskswitcher_close__";
    [ObservableProperty] private double _closeDwellProgress;
    /// <summary>True while the close button's dwell is held (gaze stepped off mid-dwell): its bar dims.</summary>
    [ObservableProperty] private bool _closeDwellPaused;

    public TaskSwitcherViewModel(
        IWindowManager windowManager,
        EyeCtrlSettings settings,
        IMessenger messenger,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.TaskSwitcher, sink, dwellRegistry, settings.Dwell)
    {
        _windowManager = windowManager;
        _messenger = messenger;
        _settings = settings;  // H2 fix: assigned
    }

    /// <summary>
    /// Toggled by the toolbar Task Switcher button. Shows the overlay if hidden,
    /// hides it if shown.
    /// </summary>
    public void Receive(TaskSwitcherToggledMessage message)
    {
        if (IsVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void ShowOverlay()
    {
        // Repopulate tiles from the live window list. Dwell targets are NOT created here:
        // the View computes each tile's real on-screen rectangle (its visible face) after
        // layout and calls UpdateTilePosition(), which creates the target at its true
        // position. Creating all targets up front at a shared (0,0) placeholder would let
        // gaze accumulate dwell on the overlapping origin targets during the layout gap
        // (the old (0,0) race).
        Tiles.Clear();
        DwellDetector.ClearTargets();
        CloseDwellProgress = 0;
        CloseDwellPaused = false;

        foreach (var window in _windowManager.EnumerateOpenWindows())
        {
            Tiles.Add(new TaskSwitcherTileViewModel(
                window.Hwnd, window.Title, window.ProcessId, window.ProcessName,
                new PhysicalPoint(0, 0)));
        }

        // Flip IsVisible so the EventCoordinator shows the fullscreen window. This is the
        // show trigger — the coordinator watches IsVisible. Dwell processing is harmless
        // until targets exist (UpdateTilePosition creates them once layout completes).
        IsVisible = true;
    }

    /// <summary>
    /// Hides the overlay, clears tiles + dwell targets. Public so the EventCoordinator
    /// can dismiss the overlay after a selection (in addition to the toggle path).
    /// </summary>
    public void HideOverlay()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
        Tiles.Clear();
        // A progress bar left over from the hidden session must not greet the next show (the
        // VM is a DI singleton — the next show reuses this instance, AppBootstrapper pattern).
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
    }

    /// <summary>
    /// Called by the View after layout to register the close disc as a dwell target.
    /// Mirrors <see cref="QuickActionsGridViewModel.UpdateCloseButtonPosition"/> — same
    /// physical-pixel convention and the same <see cref="DwellSettings.ToolbarButtonDwellMs"/>.
    /// </summary>
    public void UpdateCloseButtonPosition(PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));
    }

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the close target and the tile dwells must stay dwellable while paused so a paused
    // fullscreen switcher stays escapable; the effect stays gated via LastSampleWasPaused.
    // While paused a tile dwell dismisses (never switches — see OnTargetActivated) and the
    // close target always just dismisses.
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        // The close target is a pure dismissal — paused or not it NEVER sends the selection
        // (OS input); it only hides the overlay.
        if (targetId == CloseTargetId)
        {
            HideOverlay();
            return;
        }
        // targetId is the hwnd string. While paused the dwell is a DISMISS, never a switch —
        // suppressing the selection send (OS input) but keeping HideOverlay is what keeps the
        // overlay escapable while still obeying "no input leaves EyeCtrl while paused".
        if (!LastSampleWasPaused && IntPtr.TryParse(targetId, out var hwnd))
        {
            _messenger.Send(new TaskSwitcherSelectionMessage(hwnd));
        }
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (targetId == CloseTargetId)
        {
            CloseDwellProgress = progress;
            return;
        }
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
            tile.DwellProgress = progress;
    }

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (targetId == CloseTargetId)
        {
            CloseDwellPaused = paused;
            return;
        }
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
            tile.IsDwellPaused = paused;
    }

    /// <summary>
    /// Called by the View once a tile's container is laid out, with the tile's real on-screen
    /// rectangle (already clamped to the visible face for clipped rows). Creates (or
    /// repositions — <see cref="DwellDetector.AddTarget"/> replaces by id) a RECTANGULAR dwell
    /// target here so the detector hit-tests the drawn tile face: the tiles are 184x104 DIP
    /// faces (200x120 cells minus the template border's 8 DIP margin), and the old r50 circle
    /// covered only ~41% of every visible face, leaving ~59% dead — gaze accumulated nothing
    /// there, with no cue explaining why (the same drawn-vs-hit mismatch QuickActions fixed as
    /// a field finding, and the class behind the 2026-08-17 'tiles gave no feedback' report).
    /// Reuses <c>ToolbarButtonDwellMs</c> (700 ms default): tiles are large so it feels easy to
    /// hit, which is intentional for a fullscreen picker. If user testing finds it too
    /// sensitive, add a dedicated TaskSwitcherTileDwellMs field to DwellSettings.
    /// </summary>
    public void UpdateTilePosition(IntPtr hwnd, PhysicalRect screenRect)
    {
        var tile = Tiles.FirstOrDefault(t => t.Hwnd == hwnd);
        if (tile != null)
            tile.Position = screenRect.Center;

        DwellDetector.AddTarget(new DwellTarget(
            hwnd.ToString(),
            screenRect,
            _settings.Dwell.ToolbarButtonDwellMs));
    }
}
