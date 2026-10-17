namespace EyeCtrl.App.Services;

/// <summary>
/// The values the magnifier's "closed by" INFO line can carry. Constants rather than free strings so
/// the four attributing exit sites and the field-log reader cannot drift, and so the log is
/// grep-able as a fixed vocabulary.
/// </summary>
internal static class MagnifierCloseReasons
{
    /// <summary>The magnifier's own ✕ (or any other <c>ManualSelectionCloseMessage</c>).</summary>
    internal const string EdgeClose = "edge-close";

    /// <summary>The backdrop's ✕ while manual selection is the active state.</summary>
    internal const string BackdropClose = "backdrop-close";

    /// <summary>The global ESC hatch.</summary>
    internal const string Esc = "esc";

    /// <summary>The dwell completed and the action menu took over (the view was hidden, not closed by a user exit).</summary>
    internal const string Commit = "commit";

    /// <summary>Pause/exit: the interaction state is Paused when the hide lands.</summary>
    internal const string Pause = "pause";

    /// <summary>Anything else (e.g. an unattributed state-machine teardown).</summary>
    internal const string Other = "other";
}
