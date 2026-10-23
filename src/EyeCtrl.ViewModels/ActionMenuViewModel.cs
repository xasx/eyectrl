namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActionMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>,
    IRecipient<ToggleActionMenuLabelsMessage>,
    IRecipient<ToggleActionMenuModifierMessage>
{

    private readonly Dictionary<string, ActionMenuItemViewModel> _itemLookup = new();
    private readonly HashSet<ModifierKey> _latchedModifiers = new();
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

    /// <summary>Modifiers latched for the CURRENT menu session. Cleared on hide, reset on show.</summary>
    public IReadOnlySet<ModifierKey> LatchedModifiers => _latchedModifiers;

    /// <summary>The modifier whose toggle raised the last <see cref="LatchedModifiers"/> change (the App branch swaps exactly this button's icon).</summary>
    public ModifierKey LastToggledModifier { get; private set; }

    public ObservableCollection<ActionMenuItemViewModel> Items { get; } = new();

    public ActionMenuViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActionMenu, sink, dwellRegistry, settings.Dwell)
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

        // The latch dies with the previous session. This must run BEFORE BuildLayer: the dim
        // read inside it decides dwell-target membership, and a stale latch would dim Drag and
        // ScrollMode on an open whose modifiers the user already lost.
        _latchedModifiers.Clear();

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
            message.Actions, _settings.ActionMenu.PrimaryActions, message.CircleSize,
            RingLabelSource.Overlays); // the live ring's host passes its own keyed source
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

            // A latched modifier dims Drag and ScrollMode: neither action has a meaning the
            // dispatcher can perform once a modifier rides along. Dimmed items keep their plan
            // slot (the plan spaced the ring over every slot) but gain no dwell target, so
            // dwelling their position does nothing at all.
            var disabled = _latchedModifiers.Count > 0
                && slot.Action is InteractionAction.Drag or InteractionAction.ScrollMode;
            var itemVm = new ActionMenuItemViewModel(
                slot.Id, slot.Action, slot.Icon, slot.Label, position, slot.Kind, disabled);
            Items.Add(itemVm);
            _itemLookup[slot.Id] = itemVm;

            if (!disabled)
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
        _latchedModifiers.Clear();
        DwellDetector.ClearTargets();
        Items.Clear();
        _itemLookup.Clear();
    }

    public void Receive(ToggleActionMenuLabelsMessage message) => ShowLabels = !ShowLabels;

    /// <summary>
    /// Toggles one modifier's latch for the current menu session (the left-edge bar). Toggling
    /// across the empty/non-empty boundary rebuilds the ring, because that crossing is what
    /// changes Drag/ScrollMode between dwell-target and dimmed non-target — a toggle inside a
    /// non-empty latch changes only which modifiers ride along at capture.
    /// </summary>
    public void Receive(ToggleActionMenuModifierMessage message)
    {
        if (!IsVisible) return;

        var wasEmpty = _latchedModifiers.Count == 0;
        LastToggledModifier = message.Modifier;
        if (!_latchedModifiers.Remove(message.Modifier))
            _latchedModifiers.Add(message.Modifier);
        OnPropertyChanged(nameof(LatchedModifiers));

        if (wasEmpty != (_latchedModifiers.Count == 0))
            BuildLayer(); // empty↔non-empty crossing: dim state changed; BuildLayer bumps RingVersion LAST
    }

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
                if (_latchedModifiers.Count > 0 && item.Action is InteractionAction.Drag or InteractionAction.ScrollMode)
                    return; // backstop: dimmed items have no dwell target; unreachable in practice
                // Copy-on-capture: the message must carry its own set, or the hide that follows
                // the activation would empty an aliased latch before the dispatcher reads it.
                Messenger.Send(new ActionSelectedMessage(
                    item.Action.Value,
                    _latchedModifiers.Count > 0 ? new HashSet<ModifierKey>(_latchedModifiers) : null));
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

    protected override void OnTargetPauseChanged(string targetId, bool paused)
    {
        if (_itemLookup.TryGetValue(targetId, out var item))
            item.IsDwellPaused = paused;
    }
}
