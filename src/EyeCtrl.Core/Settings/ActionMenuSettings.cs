namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ActionMenuSettings : ObservableObject
{
    [ObservableProperty] private List<InteractionAction> _visibleActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.DragStart,
        InteractionAction.ScrollUp, InteractionAction.ScrollDown
    };
}
