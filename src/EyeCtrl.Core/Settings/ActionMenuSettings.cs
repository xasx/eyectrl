namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ActionMenuSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private List<InteractionAction> _visibleActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.DragStart,
        InteractionAction.ScrollUp, InteractionAction.ScrollDown
    };
    [ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;

    public IReadOnlyList<string> Validate()
    {
        // ActionMenuSettings has no numeric ranges to validate
        // Position is an enum so type-safe by default
        return new List<string>();
    }
}
