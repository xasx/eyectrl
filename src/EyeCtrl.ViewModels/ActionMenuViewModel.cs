namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ActionMenuViewModel : ObservableObject,
    IRecipient<ShowActionMenuMessage>,
    IRecipient<HideActionMenuMessage>,
    IRecipient<GazeCursorMovedMessage>
{
    private const double ButtonRadius = 50;
    private const double ButtonOuterGap = 20;

    private readonly IMessenger _messenger;
    private readonly DwellDetector _dwellDetector;
    private readonly Dictionary<string, ActionMenuItemViewModel> _itemLookup = new();

    [ObservableProperty] private ScreenPoint _center = new(0, 0);
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private double _effectiveRingRadius;

    public ScreenPoint? ZoomCenter { get; private set; }
    public int? ZoomRadius { get; private set; }
    public ScreenPoint GazePoint { get; private set; } = new(0, 0);

    public ObservableCollection<ActionMenuItemViewModel> Items { get; } = new();

    public ActionMenuViewModel(DwellSettings dwellSettings, IMessenger messenger)
    {
        _messenger = messenger;
        _dwellDetector = new DwellDetector(dwellSettings.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
        messenger.RegisterAll(this);
    }

    public void Receive(ShowActionMenuMessage message)
    {
        Center = message.Center;
        ZoomCenter = message.ZoomCenter;
        ZoomRadius = message.ZoomRadius;
        GazePoint = message.GazePoint;
        ShowBack = message.ShowBack;
        Items.Clear();
        _dwellDetector.ClearTargets();
        _itemLookup.Clear();

        var allItems = new List<(string Id, InteractionAction? Action, string Symbol, bool IsBack)>();
        foreach (var action in message.Actions)
            allItems.Add((action.ToString(), action, GetActionSymbol(action), false));
        if (message.ShowBack)
            allItems.Add(("Back", null, "\u21A9", true));

        EffectiveRingRadius = message.CircleSize / 2 + ButtonRadius + ButtonOuterGap;
        var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
        var physicalButtonRadius = DpiHelper.LogicalToPhysicalX(ButtonRadius);

        var angleStep = 360.0 / allItems.Count;
        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, action, symbol, isBack) = allItems[i];
            var angleRad = (angleStep * i - 90) * Math.PI / 180;
            var bx = message.Center.X + physicalRingRadius * Math.Cos(angleRad);
            var by = message.Center.Y + physicalRingRadius * Math.Sin(angleRad);
            var position = new ScreenPoint(bx, by);

            var itemVm = new ActionMenuItemViewModel(id, action, symbol, position, isBack);
            Items.Add(itemVm);
            _itemLookup[id] = itemVm;

            _dwellDetector.AddTarget(new DwellTarget(id, position, physicalButtonRadius, message.DwellTimeMs));
        }

        IsVisible = true;
    }

    public void Receive(HideActionMenuMessage message)
    {
        IsVisible = false;
        ZoomCenter = null;
        ZoomRadius = null;
        GazePoint = new(0, 0);
        _dwellDetector.ClearTargets();
        Items.Clear();
        _itemLookup.Clear();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (IsVisible)
            _dwellDetector.Update(message.Position, message.TimestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (!_itemLookup.TryGetValue(targetId, out var item)) return;

        if (item.IsBackButton)
            _messenger.Send(new BackSelectedMessage());
        else if (item.Action.HasValue)
            _messenger.Send(new ActionSelectedMessage(item.Action.Value));
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (_itemLookup.TryGetValue(targetId, out var item))
            item.DwellProgress = progress;
    }

    private static string GetActionSymbol(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "\u261D",
        InteractionAction.RightClick => "\u2630",
        InteractionAction.DoubleClick => "\u270C",
        InteractionAction.DragStart => "\u270A",
        InteractionAction.DragEnd => "\u270B",
        InteractionAction.ScrollUp => "\u25B2",
        InteractionAction.ScrollDown => "\u25BC",
        InteractionAction.ScrollLeft => "\u25C0",
        InteractionAction.ScrollRight => "\u25B6",
        _ => action.ToString()
    };
}
