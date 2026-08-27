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

    [ObservableProperty] private PhysicalPoint _center = new(0, 0);
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private double _effectiveRingRadius;

    public PhysicalPoint? ManualSelectionCenter { get; private set; }
    public int? ManualSelectionRadius { get; private set; }
    public PhysicalPoint GazePoint { get; private set; } = new(0, 0);

    public ObservableCollection<ActionMenuItemViewModel> Items { get; } = new();

    public ActionMenuViewModel(EyeCtrlSettings settings, IMessenger messenger, IStruggleTelemetrySink? sink = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.ActionMenu, sink)
    {
    }

    public void Receive(ShowActionMenuMessage message)
    {
        Center = message.Center;
        ManualSelectionCenter = message.ManualSelectionCenter;
        ManualSelectionRadius = message.ManualSelectionRadius;
        GazePoint = message.GazePoint;
        ShowBack = message.ShowBack;
        Items.Clear();
        DwellDetector.ClearTargets();
        _itemLookup.Clear();

        var allItems = new List<(string Id, InteractionAction? Action, ActionIcon Icon, bool IsBack)>();
        foreach (var action in message.Actions)
            allItems.Add((action.ToString(), action, GetActionIcon(action), false));
        if (message.ShowBack)
            allItems.Add(("Back", null, ActionIcon.Back, true));

        EffectiveRingRadius = message.CircleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;
        var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
        var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(UIConstants.ActionMenuButtonRadius);

        var angleStep = MathConstants.FullCircleDegrees / allItems.Count;
        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, action, icon, isBack) = allItems[i];
            var angleRad = (angleStep * i - MathConstants.RightAngleDegrees) * MathConstants.DegreesToRadians;
            var bx = message.Center.X + physicalRingRadius * Math.Cos(angleRad);
            var by = message.Center.Y + physicalRingRadius * Math.Sin(angleRad);
            var position = new PhysicalPoint(bx, by);

            var itemVm = new ActionMenuItemViewModel(id, action, icon, position, isBack);
            Items.Add(itemVm);
            _itemLookup[id] = itemVm;

            DwellDetector.AddTarget(new DwellTarget(id, position, physicalButtonRadius, message.DwellTimeMs));
        }

        IsVisible = true;
    }

    public void Receive(HideActionMenuMessage message)
    {
        IsVisible = false;
        ManualSelectionCenter = null;
        ManualSelectionRadius = null;
        GazePoint = new(0, 0);
        DwellDetector.ClearTargets();
        Items.Clear();
        _itemLookup.Clear();
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (!_itemLookup.TryGetValue(targetId, out var item)) return;

        if (item.IsBackButton)
            Messenger.Send(new BackSelectedMessage());
        else if (item.Action.HasValue)
            Messenger.Send(new ActionSelectedMessage(item.Action.Value));
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        if (_itemLookup.TryGetValue(targetId, out var item))
            item.DwellProgress = progress;
    }

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
