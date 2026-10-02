namespace EyeCtrl.Core.Models;

/// <summary>
/// The single source of truth for which icon an <see cref="InteractionAction"/> shows — shared by
/// the runtime ring and the settings preview, both of which read it through
/// <c>EyeCtrl.Core.ActionMenu.RingPlan</c>.
///
/// <para>
/// There is deliberately no default arm: a new <see cref="InteractionAction"/> must be given an
/// icon here, and the tests fail until it is. The glyph version this replaced ended in
/// <c>_ => action.ToString()</c>, which silently rendered enum names in the ring when a case was
/// dropped; this map throws instead. (It began life as a private switch on the runtime VM, where
/// only that one consumer was checked.)
/// </para>
/// </summary>
public static class ActionIconMap
{
    /// <summary>Returns the icon for an action.</summary>
    /// <param name="action">The action to map.</param>
    /// <exception cref="ArgumentOutOfRangeException">The action has no mapped icon.</exception>
    public static ActionIcon For(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => ActionIcon.LeftClick,
        InteractionAction.RightClick => ActionIcon.RightClick,
        InteractionAction.DoubleClick => ActionIcon.DoubleClick,
        InteractionAction.MiddleClick => ActionIcon.MiddleClick,
        InteractionAction.Drag => ActionIcon.Drag,
        InteractionAction.ScrollUp => ActionIcon.ScrollUp,
        InteractionAction.ScrollDown => ActionIcon.ScrollDown,
        InteractionAction.ScrollLeft => ActionIcon.ScrollLeft,
        InteractionAction.ScrollRight => ActionIcon.ScrollRight,
        InteractionAction.ScrollMode => ActionIcon.ScrollMode,
        InteractionAction.PinNote => ActionIcon.PinNote,
        InteractionAction.Keyboard => ActionIcon.Keyboard,
        _ => throw new ArgumentOutOfRangeException(
            nameof(action), action, "No ActionIcon is mapped for this InteractionAction."),
    };
}
