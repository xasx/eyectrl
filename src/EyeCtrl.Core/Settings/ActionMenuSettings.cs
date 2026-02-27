namespace EyeCtrl.Core.Settings;
using EyeCtrl.Core.Models;
public class ActionMenuSettings
{
    public List<InteractionAction> VisibleActions { get; set; } = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.DragStart,
        InteractionAction.ScrollUp, InteractionAction.ScrollDown
    };
}
