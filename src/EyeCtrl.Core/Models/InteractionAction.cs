namespace EyeCtrl.Core.Models;

/// <summary>Mouse and scroll actions triggered by gaze-based interaction.</summary>
public enum InteractionAction
{
    /// <summary>Single left-click at the current gaze point.</summary>
    LeftClick,
    /// <summary>Single right-click at the current gaze point.</summary>
    RightClick,
    /// <summary>Double-click at the current gaze point.</summary>
    DoubleClick,
    /// <summary>Press and hold the left mouse button to begin a drag operation.</summary>
    DragStart,
    /// <summary>Release the left mouse button to end a drag operation.</summary>
    DragEnd,
    /// <summary>Scroll upward (toward top of content).</summary>
    ScrollUp,
    /// <summary>Scroll downward (toward bottom of content).</summary>
    ScrollDown,
    /// <summary>Scroll leftward.</summary>
    ScrollLeft,
    /// <summary>Scroll rightward.</summary>
    ScrollRight,
    /// <summary>Pin a draggable note at the gaze point.</summary>
    PinNote
}
