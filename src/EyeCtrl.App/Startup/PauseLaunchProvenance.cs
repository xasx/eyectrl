namespace EyeCtrl.App.Startup;

using EyeCtrl.Core.Processing;

/// <summary>
/// The startup pause decision and its provenance, extracted from <c>App.OnStartup</c> so the
/// startup line can be pinned by a headless test. On 2026-09-24 the app ran 1 h 43 min in Paused
/// and nothing in the log said where the state came from; this is the line that answers that.
/// </summary>
public static class PauseLaunchProvenance
{
    /// <summary>The <c>--paused</c> CLI argument (supervisor's paused-retry handoff).</summary>
    public const string PausedArgumentSource = "--paused argument";

    /// <summary>The persisted <c>general.startPaused</c> setting.</summary>
    public const string StartPausedSettingSource = "general.startPaused";

    /// <summary>Neither source — the app starts unpaused.</summary>
    public const string NeitherSource = "neither";

    /// <summary>
    /// Which input starts the app paused. <c>--paused</c> wins when both are set: it is the
    /// per-launch override the supervisor uses, and it must not be masked by a persisted setting.
    /// </summary>
    public static string Source(bool isPausedLaunch, bool startPausedSetting) =>
        isPausedLaunch ? PausedArgumentSource
        : startPausedSetting ? StartPausedSettingSource
        : NeitherSource;

    /// <summary>Whether either input asks for a paused start.</summary>
    public static bool StartsPaused(bool isPausedLaunch, bool startPausedSetting) =>
        isPausedLaunch || startPausedSetting;

    /// <summary>The one startup log line naming the paused state and its source.</summary>
    public static string Describe(bool isPausedLaunch, bool startPausedSetting) =>
        "Startup paused state: "
        + (StartsPaused(isPausedLaunch, startPausedSetting) ? "paused" : "not paused")
        + $" (source: {Source(isPausedLaunch, startPausedSetting)})";

    /// <summary>
    /// Writes the provenance line, then applies the paused start by toggling the interaction
    /// manager. Ordering is the caller's: the toggle must stay AFTER <c>ShowOverlays</c> (the
    /// startup band show bypasses the visibility adapter, so the strip's show+raise lands with
    /// this toggle — spec O4).
    /// </summary>
    public static void Apply(bool isPausedLaunch, bool startPausedSetting,
        InteractionManager interaction, Action<string> writeLog)
    {
        writeLog(Describe(isPausedLaunch, startPausedSetting));
        if (StartsPaused(isPausedLaunch, startPausedSetting))
            interaction.TogglePause();
    }
}
