namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;   // DpiHelper lives here (UIConstants is in EyeCtrl.ViewModels, this namespace)
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Grid overlay ViewModel for the user-configurable Quick Actions toolbar. Reads enabled
/// <see cref="QuickAction"/>s from <see cref="IQuickActionsStore"/> into <see cref="Tiles"/>
/// when toggled on, exposes a dwell target per tile (the View registers real screen positions
/// via <see cref="UpdateTilePosition"/>), and sends <see cref="QuickActionSelectionMessage"/>
/// when a tile is dwelt on. A dedicated close button target (<see cref="CloseTargetId"/>)
/// dismisses the overlay without firing a selection.
/// </summary>
public partial class QuickActionsGridViewModel : DwellOverlayViewModelBase,
    IRecipient<QuickActionsToggledMessage>
{
    private readonly IQuickActionsStore _store;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;

    public ObservableCollection<QuickActionTileViewModel> Tiles { get; } = new();

    public int GridColumns => _settings.QuickActions.GridColumns;
    public int RowCount => Tiles.Count == 0 ? 0 : (int)Math.Ceiling((double)Tiles.Count / _settings.QuickActions.GridColumns);
    public string SummonEdge => _settings.QuickActions.SummonEdge;

    /// <summary>
    /// The edge the grid should anchor to when shown. Set by EventCoordinator to the RESOLVED
    /// edge (ComputeActualSummonEdge flips it when it collides with the toolbar's dock), so the
    /// grid renders where the user actually summoned it rather than at the raw configured edge.
    /// Falls back to the configured edge until the coordinator sets it.
    /// </summary>
    public string EffectiveSummonEdge { get; set; } = string.Empty;

    /// <summary>Reserved dwell-target id for the grid's close button (not a QuickAction).</summary>
    public const string CloseTargetId = "__qa_close__";
    [ObservableProperty] private double _closeDwellProgress;

    public QuickActionsGridViewModel(
        IQuickActionsStore store,
        EyeCtrlSettings settings,
        IMessenger messenger,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.QuickActions, sink, dwellRegistry)
    {
        _store = store;
        _settings = settings;
        _messenger = messenger;
    }

    /// <summary>
    /// Toggled by the toolbar Quick Actions button. Shows the overlay if hidden (reloading
    /// enabled tiles first), hides it if shown.
    /// </summary>
    public void Receive(QuickActionsToggledMessage message)
    {
        if (IsVisible)
        {
            HideOverlay();
            return;
        }
        if (!_settings.QuickActions.Enabled) return;

        Reload();
        IsVisible = true;
    }

    /// <summary>
    /// Hides the overlay, clears tiles + dwell targets. Public so the EventCoordinator can
    /// dismiss after a selection in addition to the toggle path.
    /// </summary>
    public void HideOverlay()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
        Tiles.Clear();
    }

    /// <summary>
    /// Repopulates tiles from the store, keeping only enabled actions. Dwell targets are NOT
    /// created here: the View computes each tile's real screen center after layout and calls
    /// <see cref="UpdateTilePosition"/>, which creates the target at its true position
    /// (matching TaskSwitcherViewModel — avoids the old (0,0) overlap race during layout).
    /// </summary>
    public void Reload()
    {
        Tiles.Clear();
        DwellDetector.ClearTargets();
        var actions = _store.LoadAll().Where(a => a.Enabled).ToList();
        foreach (var action in actions)
        {
            Tiles.Add(new QuickActionTileViewModel(action));
        }
    }

    /// <summary>
    /// Called by the View after layout. Creates (or repositions — AddTarget replaces by id)
    /// the dwell target at the tile's real screen position. Reuses ToolbarButtonDwellMs
    /// (700 ms default), same choice as TaskSwitcherViewModel.
    /// </summary>
    public void UpdateTilePosition(string tileId, PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            tileId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));
    }

    /// <summary>Called by the View after layout to register the close button as a dwell target.</summary>
    public void UpdateCloseButtonPosition(PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            CloseTargetId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == CloseTargetId)
        {
            HideOverlay();
            return;   // close button: dismiss, no selection
        }
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null)
        {
            _messenger.Send(new QuickActionSelectionMessage(tile.Action));
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
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null)
            tile.DwellProgress = progress;
    }

    // Test-only hook — same pattern as ToolbarViewModel.SimulateButtonActivated.
    // Exposed so tests can trigger activation without driving the DwellDetector.
    public void SimulateTargetActivated(string targetId) => OnTargetActivated(targetId);
}
