namespace EyeCtrl.Core.Models;

/// <summary>
/// The ring's user-facing name for an action — shared by the runtime ring and the settings
/// preview. Deliberately apart from the settings-row names (<c>ActionMenuPageSource.FriendlyName</c>
/// in EyeCtrl.App): a row has room for "Scroll Mode" where the ring wants "Scroll", and the two
/// are free to diverge.
///
/// <para>
/// The rules for the ring's navigation affordances ("More", "Back") are not here: a Back slot is
/// not an action, and the plan composes those slots itself. <c>RingPlan</c> now consumes the map
/// only through <c>RingLabelSource.English</c>, the host-supplied seam every surface passes until
/// its own keys arrive.
/// </para>
/// </summary>
public static class RingLabelMap
{
    /// <summary>
    /// Returns the ring name for an action. <c>null</c> names the Back affordance; an unknown
    /// action falls back to its enum name, matching the map this replaced.
    /// </summary>
    /// <param name="action">The action to name, or null for Back.</param>
    public static string For(InteractionAction? action) => action switch
    {
        InteractionAction.LeftClick => "Left click",
        InteractionAction.RightClick => "Right click",
        InteractionAction.DoubleClick => "Double click",
        InteractionAction.MiddleClick => "Middle click",
        InteractionAction.Drag => "Drag",
        InteractionAction.ScrollUp => "Scroll up",
        InteractionAction.ScrollDown => "Scroll down",
        InteractionAction.ScrollLeft => "Scroll left",
        InteractionAction.ScrollRight => "Scroll right",
        InteractionAction.ScrollMode => "Scroll",
        InteractionAction.PinNote => "Pin note",
        InteractionAction.Keyboard => "Keyboard",
        null => "Back",
        _ => action.Value.ToString(),
    };
}
