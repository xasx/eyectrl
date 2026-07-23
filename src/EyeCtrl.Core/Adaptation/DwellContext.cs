namespace EyeCtrl.Core.Adaptation;

/// <summary>The interaction surface a dwell outcome originated from, used for per-context
/// struggle scoring.</summary>
public enum DwellContext
{
    /// <summary>Un-instrumented or aggregate-only surface; the default.</summary>
    Other = 0,
    /// <summary>On-screen keyboard keys (nearest-key dwell).</summary>
    Keyboard,
    /// <summary>Keyboard word-prediction suggestion buttons.</summary>
    KeyboardPrediction,
    /// <summary>Radial action-menu buttons.</summary>
    ActionMenu,
    /// <summary>Manual (zoom-to-point) selection and its buttons.</summary>
    ManualSelection,
    /// <summary>Utility toolbar buttons.</summary>
    Toolbar,
    /// <summary>Unified right-edge button bar.</summary>
    EdgeButtonBar,
    /// <summary>Quick-actions grid tiles.</summary>
    QuickActions,
    /// <summary>Task-switcher tiles.</summary>
    TaskSwitcher,
    /// <summary>Emoji category tabs and grid.</summary>
    Emoji,
    /// <summary>Pinned sticky-note controls.</summary>
    Notes,
    /// <summary>Gaze-scroll stop/control buttons.</summary>
    Scroll,
    /// <summary>Gaze-trail drag-and-drop release buttons.</summary>
    DragTrail,
    /// <summary>The post-fixation activation button.</summary>
    ActivationButton,
    /// <summary>Glass-backdrop hide button.</summary>
    Backdrop,
    /// <summary>External hardware-button dwell activation.</summary>
    ExternalDwell,
    /// <summary>Eye-care break banner buttons.</summary>
    EyeCare,
}
