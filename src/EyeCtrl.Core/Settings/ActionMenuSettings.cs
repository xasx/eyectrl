namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;
using System.Collections.Generic;

public partial class ActionMenuSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private List<InteractionAction> _visibleActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.DragStart,
        InteractionAction.ScrollUp, InteractionAction.ScrollDown,
        InteractionAction.PinNote
    };

    public IReadOnlyList<string> Validate()
    {
        // ActionMenuSettings has no numeric ranges to validate
        return new List<string>();
    }

    public void EnsureDefaults()
    {
        if (VisibleActions is null)
            VisibleActions = new List<InteractionAction>();

        var defaults = new[]
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.DragStart,
            InteractionAction.ScrollUp, InteractionAction.ScrollDown,
            InteractionAction.PinNote
        };

        foreach (var action in defaults)
        {
            if (!VisibleActions.Contains(action))
                VisibleActions.Add(action);
        }
    }
}
