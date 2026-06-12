namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

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

    /// <summary>
    /// Ensures all current default actions are present in the visible list.
    /// Called after settings deserialization to add newly-introduced actions
    /// that existing settings.json files may not include.
    /// </summary>
    public void EnsureDefaults()
    {
        var defaults = new HashSet<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.DragStart,
            InteractionAction.ScrollUp, InteractionAction.ScrollDown,
            InteractionAction.PinNote
        };
        foreach (var action in defaults)
        {
            if (!_visibleActions.Contains(action))
                _visibleActions.Add(action);
        }
    }
}
