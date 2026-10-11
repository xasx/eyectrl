namespace EyeCtrl.Core.Models;

/// <summary>
/// The ring's key for an action — shared by the runtime ring and the settings preview. Deliberately
/// apart from the settings-row keys (<c>ActionMenuPageSource.FriendlyName</c> in EyeCtrl.App): a row
/// has room for "Scroll Mode" where the ring wants "Scroll", and the two are free to diverge.
///
/// <para>
/// This is a key map, not a text map: the English lives only in <c>strings.json</c>. The map is
/// consumed only through <see cref="EyeCtrl.Core.ActionMenu.RingLabelSource"/>, whose two sources
/// resolve these <c>overlays</c> keys — <c>Overlays</c> through the live table, <c>English</c>
/// through the embedded one. An unknown action keeps its enum name as the rendered fallback; that
/// name is not a key, so the resolver returns it unchanged (and logs the missing key, which is the
/// signal that a new action has no text).
/// </para>
///
/// <para>
/// The two navigation affordances split by ownership: Back is the <c>null</c> entry here, while
/// More is the source's own key — the plan composes that slot itself and no action names it.
/// </para>
/// </summary>
public static class RingLabelMap
{
    /// <summary>
    /// Returns the <c>overlays</c> key naming an action's ring label. <c>null</c> names the Back
    /// affordance; an unknown action falls back to its enum name, matching the map this replaced.
    /// </summary>
    /// <param name="action">The action to name, or null for Back.</param>
    public static string For(InteractionAction? action) => action switch
    {
        InteractionAction.LeftClick => "overlays.ring.leftClick",
        InteractionAction.RightClick => "overlays.ring.rightClick",
        InteractionAction.DoubleClick => "overlays.ring.doubleClick",
        InteractionAction.MiddleClick => "overlays.ring.middleClick",
        InteractionAction.Drag => "overlays.ring.drag",
        InteractionAction.ScrollUp => "overlays.ring.scrollUp",
        InteractionAction.ScrollDown => "overlays.ring.scrollDown",
        InteractionAction.ScrollLeft => "overlays.ring.scrollLeft",
        InteractionAction.ScrollRight => "overlays.ring.scrollRight",
        InteractionAction.ScrollMode => "overlays.ring.scrollMode",
        InteractionAction.PinNote => "overlays.ring.pinNote",
        InteractionAction.Keyboard => "overlays.ring.keyboard",
        null => "overlays.ring.back",
        _ => action.Value.ToString(),
    };
}
