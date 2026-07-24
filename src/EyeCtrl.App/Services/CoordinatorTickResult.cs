namespace EyeCtrl.App.Services;

/// <summary>The branch <see cref="AdaptationCoordinator.Tick"/> took this tick (for tests + diagnostics).</summary>
public enum CoordinatorTickResult
{
    /// <summary>Adaptation is disabled; the tick did no monitor/controller work (design §9.1).</summary>
    Disabled,
    /// <summary>Adaptation transitioned enabled→disabled this tick; momentary state was cleared once.</summary>
    ClearedOnDisable,
    /// <summary>First active tick after enable / app-start; a fresh evidence session was started.</summary>
    ResetOnEnable,
    /// <summary>The gaze provider went connected→disconnected; the session was reset (1b-③c F3) and the tick skipped.</summary>
    ResetOnDisconnect,
    /// <summary>Interaction resumed from Pause; the session was reset and the tick skipped.</summary>
    ResetOnResume,
    /// <summary>The gaze provider is disconnected; the tick was skipped.</summary>
    Disconnected,
    /// <summary>Interaction is paused; the tick was skipped.</summary>
    Paused,
    /// <summary>No gaze sample has been seen yet; the tick was skipped.</summary>
    AwaitingGaze,
    /// <summary>Struggle was assessed and the controller was driven.</summary>
    Assessed,
}
