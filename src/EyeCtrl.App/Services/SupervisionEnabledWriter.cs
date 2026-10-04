namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Settings;

/// <summary>
/// The App's single write into the Supervisor-owned section: persists <c>supervision.enabled</c>
/// through <see cref="SettingsService.SaveSupervisionEnabled(bool)"/> on a Supervisor-writer
/// instance. Wraps the writer-identity choice so no caller can accidentally use the App writer,
/// whose <see cref="SettingsService.Save"/> discards the change over a valid on-disk section —
/// see the ownership exception on <see cref="SettingsWriter"/>.
/// </summary>
public sealed class SupervisionEnabledWriter
{
    private readonly string _settingsPath;

    /// <param name="settingsPath">Full path to the shared settings JSON (the App's own settings file).</param>
    public SupervisionEnabledWriter(string settingsPath) => _settingsPath = settingsPath;

    /// <summary>Persists only the enable flag; every other key stays exactly as it is on disk.</summary>
    /// <param name="enabled">Whether supervised process lifecycle is on.</param>
    public void Persist(bool enabled) =>
        new SettingsService(_settingsPath, SettingsWriter.Supervisor).SaveSupervisionEnabled(enabled);
}
