namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActionMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>,
    IRecipient<ToggleActionMenuLabelsMessage>
{

    private readonly Dictionary<string, ActionMenuItemViewModel> _itemLookup = new();
    private readonly EyeCtrlSettings _settings;
    private bool _onSecondaryLayer;
    private ShowActionMenuMessage? _shownWith;

    [ObservableProperty] private PhysicalPoint _center = new(0, 0);
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private double _effectiveRingRadius;

    /// <summary>
    /// Incremented every time the ring's items are rebuilt. The only signal the window gets that
    /// a layer changed — see <c>BuildLayer</c> for why Items cannot serve.
    /// </summary>
    [ObservableProperty] private int _ringVersion;

    /// <summary>
    /// Whether the ring shows each target's name. Off on every open; the Help target on the
    /// right-edge bar turns it on for the rest of the menu session.
    /// </summary>
    [ObservableProperty] private bool _showLabels;

    public PhysicalPoint? ManualSelectionCenter { get; private set; }
    public int? ManualSelectionRadius { get; private set; }
    public PhysicalPoint GazePoint { get; private set; } = new(0, 0);

    public ObservableCollection<ActionMenuItemViewModel> Items { get; } = new();

    public ActionMenuViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActionMenu, sink, dwellRegistry)
    {
        _settings = settings;
    }

    public void Receive(ShowActionMenuMessage message)
    {
        Center = message.Center;
        ManualSelectionCenter = message.ManualSelectionCenter;
        ManualSelectionRadius = message.ManualSelectionRadius;
        GazePoint = message.GazePoint;
        ShowBack = message.ShowBack;

        // A fresh open always lands on layer one. Reopening onto whichever layer was last used
        // would put a different target under the slot the user is already looking at.
        _onSecondaryLayer = false;
        _shownWith = message;
        ShowLabels = false;

        BuildLayer();
        IsVisible = true;
    }

    /// <summary>
    /// Rebuilds the ring for the current layer. Called on open and on every layer change — the
    /// gaze engine is never involved, so a layer change costs no round trip and cannot race the
    /// engine's own state machine.
    /// </summary>
    private void BuildLayer()
    {
        var message = _shownWith;
        if (message is null) return;

        Items.Clear();
        DwellDetector.ClearTargets();
        _itemLookup.Clear();

        // Partition what the MESSAGE asked for, not what settings hold. Production is
        // equivalent — GazeEngineAdapter passes settings.ActionMenu.VisibleActions — but every
        // existing test drives the ring by sending a hand-picked action list. Reading settings
        // here instead would silently ignore the message and break all of them. The plan keeps
        // that same rule: visible-list order filtered by primary membership, deliberately not
        // PrimaryActions order.
        var plan = RingPlan.Create(
            message.Actions, _settings.ActionMenu.PrimaryActions, message.CircleSize);
        var layer = plan.BuildLayer(
            _onSecondaryLayer ? RingLayer.Secondary : RingLayer.Primary, message.ShowBack);
        if (layer.Slots.Count == 0) return;

        EffectiveRingRadius = plan.EffectiveRingRadius;
        var physicalRingRadius = DpiHelper.LogicalToPhysicalX(plan.EffectiveRingRadius);
        var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ActionMenuButtonRadius);

        foreach (var slot in layer.Slots)
        {
            var angleRad = slot.AngleDegrees * MathConstants.DegreesToRadians;
            var bx = message.Center.X + physicalRingRadius * Math.Cos(angleRad);
            var by = message.Center.Y + physicalRingRadius * Math.Sin(angleRad);
            var position = new PhysicalPoint(bx, by);

            var itemVm = new ActionMenuItemViewModel(slot.Id, slot.Action, slot.Icon, slot.Label, position, slot.Kind);
            Items.Add(itemVm);
            _itemLookup[slot.Id] = itemVm;

            DwellDetector.AddTarget(new DwellTarget(slot.Id, position, physicalButtonRadius, message.DwellTimeMs));
        }

        // The window redraws only when something it subscribes to changes. It listens to
        // ActionMenuViewModel.IsVisible and to each item's DwellProgress, and nothing else —
        // Items is a plain ObservableCollection whose PropertyChanged never fires. Without this
        // counter a layer switch would repoint the DwellDetector while the canvas still showed
        // layer one: the slot drawn as "More" would activate Back, and the four action slots
        // would fire the SECOND layer's actions.
        //
        // Bumped LAST, after Items and the DwellDetector are both filled. The subscriber is
        // OverlayVmBindingCoordinator, PropertyChanged is raised synchronously, and its handler
        // enumerates Items to draw the ring — so bumping any earlier hands it an empty collection
        // and renders layer two as a bare circle. ALayerChange_PublishesRingVersionAfterTheItems
        // pins the ordering.
        RingVersion++;
    }

    public void Receive(HideActionMenuMessage message)
    {
        IsVisible = false;
        ManualSelectionCenter = null;
        ManualSelectionRadius = null;
        GazePoint = new(0, 0);
        _onSecondaryLayer = false;
        _shownWith = null;
        ShowLabels = false;
        DwellDetector.ClearTargets();
        Items.Clear();
        _itemLookup.Clear();
    }

    public void Receive(ToggleActionMenuLabelsMessage message) => ShowLabels = !ShowLabels;

    protected override void OnTargetActivated(string targetId)
    {
        if (!_itemLookup.TryGetValue(targetId, out var item)) return;

        switch (item.Kind)
        {
            case ActionMenuItemKind.OpenMore:
                _onSecondaryLayer = true;
                BuildLayer();
                break;

            case ActionMenuItemKind.BackToPrimary:
                _onSecondaryLayer = false;
                BuildLayer();
                break;

            case ActionMenuItemKind.ExitMenu:
                Messenger.Send(new BackSelectedMessage());
                break;

            case ActionMenuItemKind.Action when item.Action.HasValue:
                Messenger.Send(new ActionSelectedMessage(item.Action.Value));
                break;
        }
    }

    /// <summary>
    /// Drives one activation without a DwellDetector, for tests. Layer changes are the only
    /// behaviour in this class that a caller cannot reach through a message, because they happen
    /// entirely inside a dwell activation.
    /// </summary>
    internal void ActivateForTest(string targetId) => OnTargetActivated(targetId);

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (_itemLookup.TryGetValue(targetId, out var item))
            item.DwellProgress = progress;
    }
}
