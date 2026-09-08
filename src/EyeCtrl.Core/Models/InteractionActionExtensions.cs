namespace EyeCtrl.Core.Models;

/// <summary>Classification helpers over <see cref="InteractionAction"/>.</summary>
public static class InteractionActionExtensions
{
    /// <summary>
    /// Whether the action is a plain click that repeat mode can lock and re-fire. Scroll has its
    /// own continuous mode, drag follows gaze already, and the remaining members are not things a
    /// user re-fires rhythmically.
    /// </summary>
    public static bool IsRepeatableClick(this InteractionAction action) =>
        action is InteractionAction.LeftClick
            or InteractionAction.RightClick
            or InteractionAction.DoubleClick
            or InteractionAction.MiddleClick;
}
