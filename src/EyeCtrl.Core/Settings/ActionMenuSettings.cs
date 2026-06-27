namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;
using System.Collections.Generic;

/// <summary>Settings controlling which actions are visible in the gaze action menu ring.</summary>
public partial class ActionMenuSettings : ObservableObject, IValidatable
{
    /// <summary>List of interaction actions displayed as buttons in the action menu ring.</summary>
    [ObservableProperty] private List<InteractionAction> _visibleActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.DragStart,
        InteractionAction.ScrollUp, InteractionAction.ScrollDown,
        InteractionAction.ScrollMode,
        InteractionAction.PinNote
    };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        // ActionMenuSettings has no numeric ranges to validate
        return new List<string>();
    }

    /// <summary>Ensures the default set of actions is present in <see cref="VisibleActions"/>.</summary>
    public void EnsureDefaults()
    {
        if (VisibleActions is null)
            VisibleActions = new List<InteractionAction>();

        var defaults = new[]
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.DragStart,
            InteractionAction.ScrollUp, InteractionAction.ScrollDown,
            InteractionAction.ScrollMode,
            InteractionAction.PinNote
        };

        foreach (var action in defaults)
        {
            if (!VisibleActions.Contains(action))
                VisibleActions.Add(action);
        }
    }
}
