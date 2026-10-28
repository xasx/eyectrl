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

    // True while any tile or the close button carries non-zero dwell progress — i.e. the gaze is
    // mid-attempt on a target. Written here on the UI thread (the GazeCursorMovedMessage feed) and
    // read on the GAZE thread by the hot-edge summon controller's dismiss hold, hence volatile
    // (B8b F1). A plain bool maintained at the progress callback, never recomputed from the
    // ObservableCollection off-thread: the collection is UI-thread-owned.
    private volatile bool _hasDwellProgress;

    /// <summary>
    /// The host's preset-label function for the tiles. English by default; the App shell sets the
    /// overlays-keyed function at composition (T08), the same way it sets
    /// <see cref="EffectiveSummonEdge"/>.
    /// </summary>
    public Func<string, string?> PresetLabelFor { get; set; } = QuickActionPresets.EnglishLabelFor;

    public int GridColumns => _settings.QuickActions.GridColumns;
    public int RowCount => Tiles.Count == 0 ? 0 : (int)Math.Ceiling((double)Tiles.Count / _settings.QuickActions.GridColumns);
    public string SummonEdge => _settings.QuickActions.SummonEdge;

    /// <summary>
    /// True while a tile or the close button is carrying dwell progress. The hot-edge summon
    /// controller holds its gaze-away dismiss timer while this is true (B8b F1), so a summoned grid
    /// can never vanish mid-attempt. <c>internal</c>: a VM-internal fact consumed by the App's
    /// summon wiring (InternalsVisibleTo App), not part of the surface's public contract.
    /// <para>
    /// Thread-safe by construction: the field is <c>volatile</c> and is only ever assigned on the
    /// UI thread, from <see cref="OnTargetProgress"/> and the two clears. The gaze-thread reader
    /// therefore never touches <see cref="Tiles"/> or the close progress property.
    /// </para>
    /// </summary>
    internal bool HasDwellProgress => _hasDwellProgress;

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
    /// <summary>True while the close button's dwell is held (gaze stepped off mid-dwell): its bar dims.</summary>
    [ObservableProperty] private bool _closeDwellPaused;

    public QuickActionsGridViewModel(
        IQuickActionsStore store,
        EyeCtrlSettings settings,
        IMessenger messenger,
        IStruggleTelemetrySink? sink = null,
        IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.QuickActions, sink, dwellRegistry, settings.Dwell)
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
        // A bar/progress left over from the hidden session must not keep the summon's dismiss
        // hold armed after the next show starts with a fresh detector (see HasDwellProgress).
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
        _hasDwellProgress = false;
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
        CloseDwellProgress = 0;
        CloseDwellPaused = false;
        _hasDwellProgress = false;
        var actions = _store.LoadAll().Where(a => a.Enabled).ToList();
        foreach (var action in actions)
        {
            Tiles.Add(new QuickActionTileViewModel(action, PresetLabelFor));
        }
    }

    /// <summary>
    /// Trims the tile list to at most <paramref name="maxTiles"/>, dropping from the END (the
    /// least-recently-configured actions). B8b F3: the window clamps its height to the work area,
    /// which used to squeeze the UniformGrid's rows until the r50 hit circles overlapped. The
    /// window — the only place that can read the display geometry — computes how many tiles fit
    /// and calls this before laying out; without scrolling, a capped grid is the only way to keep
    /// the R4 dead gap on a gaze-only surface.
    /// </summary>
    /// <param name="maxTiles">The most tiles the available work area can hold. Zero hides them all.</param>
    public void CapVisibleTiles(int maxTiles)
    {
        if (maxTiles < 0) maxTiles = 0;
        if (Tiles.Count <= maxTiles) return;

        while (Tiles.Count > maxTiles)
            Tiles.RemoveAt(Tiles.Count - 1);

        // A removed tile's target would otherwise linger in the detector (dwellable, but with no
        // drawn face); the show path re-registers the survivors after layout.
        DwellDetector.ClearTargets();
        _hasDwellProgress = false;
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

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the close button's dwell is a dismissal, and on the toolbar-driven show path (which does
    // not arm the hot-edge auto-dismiss) it is the ONLY one — gating the dwell would trap a
    // paused-open grid. The EFFECT is gated instead in OnTargetActivated: while paused any tile
    // dwell only dismisses; a quick action (SendText / keystrokes / Process.Start — OS input)
    // never fires.
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == CloseTargetId)
        {
            HideOverlay();
            return;   // close button: dismiss, no selection
        }
        if (LastSampleWasPaused)
        {
            // Paused: the dwell is a DISMISS, never an execution. The grid hides rather than
            // sits inert — inert-but-visible is indistinguishable from a hang for a gaze-only
            // user.
            HideOverlay();
            return;
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
            RecomputeHasDwellProgress();
            return;
        }
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null)
        {
            tile.DwellProgress = progress;
            RecomputeHasDwellProgress();
        }
    }

    // Mirrors the two surfaces' progress into the volatile flag the gaze thread reads. Tiles'
    // values are fresh VMs per show and HideOverlay/Reload reset the close progress, so a stale
    // bar can never pin the flag true across sessions.
    private void RecomputeHasDwellProgress() =>
        _hasDwellProgress = CloseDwellProgress > 0 || Tiles.Any(t => t.DwellProgress > 0);

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (targetId == CloseTargetId)
        {
            CloseDwellPaused = paused;
            return;
        }
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null)
            tile.IsDwellPaused = paused;
    }

    // Test-only hook — same pattern as ToolbarViewModel.SimulateButtonActivated.
    // Exposed so tests can trigger activation without driving the DwellDetector.
    public void SimulateTargetActivated(string targetId) => OnTargetActivated(targetId);
}
