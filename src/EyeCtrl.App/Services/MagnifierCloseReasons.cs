namespace EyeCtrl.App.Services;

/// <summary>
/// The values the magnifier's "closed by" INFO line can carry. Constants rather than free strings so
/// the attributing exit sites and the field-log reader cannot drift, and so the log is grep-able as
/// a fixed vocabulary.
/// </summary>
internal static class MagnifierCloseReasons
{
    /// <summary>The magnifier's own ✕ (or any other <c>ManualSelectionCloseMessage</c> that names
    /// no reason).</summary>
    internal const string EdgeClose = "edge-close";

    /// <summary>The magnifier closed itself: no seat ever formed in the session (the stability
    /// onset never seated an anchor), the no-seat close is enabled, and the gaze stayed
    /// continuously outside the rim for at least its threshold after the grace period. An
    /// unwanted open that never seats, released without the second aim at ✕.</summary>
    internal const string NoSeat = "no-seat";

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
