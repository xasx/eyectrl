namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActionMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>
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

    public PhysicalPoint? ManualSelectionCenter { get; private set; }
    public int? ManualSelectionRadius { get; private set; }
    public PhysicalPoint GazePoint { get; private set; } = new(0, 0);

    public ObservableCollection<ActionMenuItemViewModel> Items { get; } = new();

    public ActionMenuViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActionMenu, sink)
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
        // here instead would silently ignore the message and break all of them.
        var primaryList = _settings.ActionMenu.PrimaryActions ?? new List<InteractionAction>();
        var primary = message.Actions.Where(a => primaryList.Contains(a)).ToList();
        var secondary = message.Actions.Where(a => !primaryList.Contains(a)).ToList();

        var allItems = new List<(string Id, InteractionAction? Action, ActionIcon Icon, string Label, ActionMenuItemKind Kind)>();

        if (_onSecondaryLayer)
        {
            foreach (var action in secondary)
                allItems.Add((action.ToString(), action, GetActionIcon(action), FriendlyName(action), ActionMenuItemKind.Action));
            allItems.Add(("BackToPrimary", null, ActionIcon.Back, "Back", ActionMenuItemKind.BackToPrimary));
        }
        else
        {
            foreach (var action in primary)
                allItems.Add((action.ToString(), action, GetActionIcon(action), FriendlyName(action), ActionMenuItemKind.Action));

            // No More target when it would open an empty layer.
            if (secondary.Count > 0)
                allItems.Add(("More", null, ActionIcon.More, "More", ActionMenuItemKind.OpenMore));

            // The zoom Back only exists on layer one. From layer two it takes two dwells — back
            // to primary, then Back — because one nav slot cannot mean two things.
            if (message.ShowBack)
                allItems.Add(("Back", null, ActionIcon.Back, "Back", ActionMenuItemKind.ExitMenu));
        }

        if (allItems.Count == 0) return;

        EffectiveRingRadius = message.CircleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;
        var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
        var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ActionMenuButtonRadius);

        var angleStep = MathConstants.FullCircleDegrees / allItems.Count;
        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, action, icon, label, kind) = allItems[i];
            var angleRad = (angleStep * i - MathConstants.RightAngleDegrees) * MathConstants.DegreesToRadians;
            var bx = message.Center.X + physicalRingRadius * Math.Cos(angleRad);
            var by = message.Center.Y + physicalRingRadius * Math.Sin(angleRad);
            var position = new PhysicalPoint(bx, by);

            var itemVm = new ActionMenuItemViewModel(id, action, icon, label, position, kind);
            Items.Add(itemVm);
            _itemLookup[id] = itemVm;

            DwellDetector.AddTarget(new DwellTarget(id, position, physicalButtonRadius, message.DwellTimeMs));
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
        DwellDetector.ClearTargets();
        Items.Clear();
        _itemLookup.Clear();
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

    /// <summary>
    /// The user-facing name for an action. Duplicated from <c>ActionMenuPageSource.FriendlyName</c>
    /// rather than shared: that one lives in EyeCtrl.App and names a settings row, this one names
    /// a ring target, and they are free to diverge — a settings row has room for "Scroll Mode"
    /// where the ring wants "Scroll".
    /// </summary>
    private static string FriendlyName(InteractionAction? action) => action switch
    {
        InteractionAction.LeftClick => "Left click",
        InteractionAction.RightClick => "Right click",
        InteractionAction.DoubleClick => "Double click",
        InteractionAction.Drag => "Drag",
        InteractionAction.ScrollUp => "Scroll up",
        InteractionAction.ScrollDown => "Scroll down",
        InteractionAction.ScrollLeft => "Scroll left",
        InteractionAction.ScrollRight => "Scroll right",
        InteractionAction.ScrollMode => "Scroll",
        InteractionAction.PinNote => "Pin note",
        null => "Back",
        _ => action.Value.ToString(),
    };

    /// <summary>
    /// The single source of truth for which icon an action shows. There is deliberately no
    /// default arm: a new <see cref="InteractionAction"/> must be given an icon here, and
    /// <c>ActionMenuViewModelSymbolTests.EveryAction_MapsToADistinctIcon</c> fails until it is.
    /// The glyph version this replaced ended in <c>_ => action.ToString()</c>, which silently
    /// rendered enum names in the ring when a case was dropped.
    /// </summary>
    private static ActionIcon GetActionIcon(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => ActionIcon.LeftClick,
        InteractionAction.RightClick => ActionIcon.RightClick,
        InteractionAction.DoubleClick => ActionIcon.DoubleClick,
        InteractionAction.Drag => ActionIcon.Drag,
        InteractionAction.ScrollUp => ActionIcon.ScrollUp,
        InteractionAction.ScrollDown => ActionIcon.ScrollDown,
        InteractionAction.ScrollLeft => ActionIcon.ScrollLeft,
        InteractionAction.ScrollRight => ActionIcon.ScrollRight,
        InteractionAction.ScrollMode => ActionIcon.ScrollMode,
        InteractionAction.PinNote => ActionIcon.PinNote,
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "No ActionIcon is mapped for this InteractionAction."),
    };
}
