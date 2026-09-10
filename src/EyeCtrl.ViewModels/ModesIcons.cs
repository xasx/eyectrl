namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

/// <summary>
/// The Modes toolbar button's icon key per locked action (the <see cref="SelectionModeIcons"/>
/// pattern). The button shows the stack glyph when no mode is locked, and the locked action's
/// mouse glyph while dwell-to-act runs (design D30) — the button doubles as the mode's
/// state display, like Pause/Play and the selection-mode icons already do.
/// </summary>
public static class ModesIcons
{
    public const string Off = "Modes";

    public static string ForAction(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "ModeLeftClick",
        InteractionAction.RightClick => "ModeRightClick",
        InteractionAction.DoubleClick => "ModeDoubleClick",
        InteractionAction.MiddleClick => "ModeMiddleClick",
        _ => Off,
    };
}
