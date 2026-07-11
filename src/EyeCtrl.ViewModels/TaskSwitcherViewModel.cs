namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
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
        IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
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
        // Repopulate tiles + dwell targets from the live window list.
        Tiles.Clear();
        DwellDetector.ClearTargets();

        var windows = _windowManager.EnumerateOpenWindows();
        // M2 note: reuses ToolbarButtonDwellMs (700ms default). Task-switcher tiles are larger
        // (200x120) than toolbar buttons (~50px), so 700ms feels easier to hit — intentional
        // for a fullscreen picker. If user testing finds it too sensitive, add a dedicated
        // TaskSwitcherTileDwellMs field to DwellSettings (range 200–2000, default ~900).
        var dwellMs = _settings.Dwell.ToolbarButtonDwellMs;

        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            // H3 fix: all tiles start at (0,0) placeholder. IsVisible is NOT set here —
            // the View calls UpdateTilePositions() then ActivateAfterLayout() once layout
            // is done, so the DwellDetector never processes targets at the overlapping
            // (0,0) origin.
            var position = new PhysicalPoint(0, 0);
            var tile = new TaskSwitcherTileViewModel(window.Hwnd, window.Title, window.ProcessId, window.ProcessName, position);
            Tiles.Add(tile);

            DwellDetector.AddTarget(new DwellTarget(
                window.Hwnd.ToString(),
                position,
                DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
                dwellMs));
        }

        // IsVisible is set by ActivateAfterLayout() — NOT here — to avoid the (0,0) race.
    }

    /// <summary>
    /// Called by the View after <c>UpdateTilePositions</c> has set real screen positions
    /// on all dwell targets. Activates the overlay (sets IsVisible) so dwell processing
    /// begins only when targets are correctly positioned, not at the (0,0) placeholder.
    /// </summary>
    public void ActivateAfterLayout()
    {
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

    /// <summary>Called by the View after layout to set a tile's real screen center for dwell hit-testing.</summary>
    public void UpdateTilePosition(IntPtr hwnd, PhysicalPoint screenCenter)
    {
        // Keep the tile's Position property in sync (used by the View / diagnostics).
        var tile = Tiles.FirstOrDefault(t => t.Hwnd == hwnd);
        if (tile != null)
            tile.Position = screenCenter;
        DwellDetector.UpdateTargetCenter(hwnd.ToString(), screenCenter);
    }
}
