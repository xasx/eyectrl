namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// Reads <c>general.startOnBoot</c> from the raw settings file under the writer mutex
/// (<see cref="GuardedSettingsReader"/>). The setup window shows what is on disk, and the App
/// owns that key, so the Supervisor must neither trust its startup snapshot nor call
/// <c>SettingsService.Load()</c>, which takes no mutex and would fail the App's atomic save.
/// </summary>
internal static class StartOnBootReader
{
    /// <summary>The value when the file positively says so (false when absent, the
    /// <c>GeneralSettings</c> default); null when it cannot be told.</summary>
    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="writerMutexName">Writer mutex name; injectable so tests never touch the
    /// production name (box-wide rule).</param>
    public static bool? TryRead(string settingsPath, string? writerMutexName = null) =>
        GuardedSettingsReader.TryRead(
            settingsPath, "general", "startOnBoot", defaultValue: false,
            jsonValue => jsonValue.TryGetValue(out bool startOnBoot) ? startOnBoot : null,
            writerMutexName);
}
