namespace EyeCtrl.Core.Settings;

/// <summary>
/// Identifies which process owns a <see cref="SettingsService"/> instance, so <see cref="SettingsService.Save"/>
/// can refuse to persist sections the caller doesn't own. Prevents cross-process lost updates when
/// the App and the Supervisor share one settings file (spec, "Lost updates, not just corruption"):
/// each section has exactly one writer process.
/// </summary>
public enum SettingsWriter
{
    /// <summary>Owns all sections except <see cref="SupervisionSettings"/> (the App process).</summary>
    App,
    /// <summary>Owns only the <see cref="SupervisionSettings"/> section (the Supervisor process).</summary>
    Supervisor
}
