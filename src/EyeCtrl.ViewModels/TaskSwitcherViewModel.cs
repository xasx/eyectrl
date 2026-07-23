namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
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
/// dwell targets over each tile, and sends <see cref="TaskSwitcherSelectionMessage"/>
/// when a tile is dwelt on. WPF-free (ViewModels targets plain net10.0): the View
/// (TaskSwitcherWindow) resolves icons via IAppIconProvider using each tile's ProcessId.
/// </summary>
public partial class TaskSwitcherViewModel : DwellOverlayViewModelBase,
    IRecipient<TaskSwitcherToggledMessage>
{
    private readonly IWindowManager _windowManager;
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;  // H2 fix: declared + assigned

    public ObservableCollection<TaskSwitcherTileViewModel> Tiles { get; } = new();

    public TaskSwitcherViewModel(
        IWindowManager windowManager,
        EyeCtrlSettings settings,
        IMessenger messenger,
        IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.TaskSwitcher, sink)
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
        // the View computes each tile's real screen center after layout and calls
        // UpdateTilePosition(), which creates the target at its true position. Creating all
        // targets up front at a shared (0,0) placeholder would let gaze accumulate dwell on
        // the overlapping origin targets during the layout gap (the old (0,0) race).
        Tiles.Clear();
        DwellDetector.ClearTargets();

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
    }

    protected override void OnTargetActivated(string targetId)
    {
        // targetId is the hwnd string
        if (IntPtr.TryParse(targetId, out var hwnd))
        {
            _messenger.Send(new TaskSwitcherSelectionMessage(hwnd));
        }
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
            tile.DwellProgress = progress;
    }

    /// <summary>
    /// Called by the View once a tile's container is laid out, with the tile's real screen
    /// center. Creates (or repositions — <see cref="DwellDetector.AddTarget"/> replaces by id)
    /// the dwell target here so the detector only ever hit-tests correctly-positioned targets.
    /// Reuses <c>ToolbarButtonDwellMs</c> (700 ms default): tiles are large (200x120) so it
    /// feels easy to hit, which is intentional for a fullscreen picker. If user testing finds
    /// it too sensitive, add a dedicated TaskSwitcherTileDwellMs field to DwellSettings.
    /// </summary>
    public void UpdateTilePosition(IntPtr hwnd, PhysicalPoint screenCenter)
    {
        var tile = Tiles.FirstOrDefault(t => t.Hwnd == hwnd);
        if (tile != null)
            tile.Position = screenCenter;

        DwellDetector.AddTarget(new DwellTarget(
            hwnd.ToString(),
            screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));
    }
}
