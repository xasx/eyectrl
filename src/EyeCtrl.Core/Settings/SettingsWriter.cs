namespace EyeCtrl.Core.Settings;

/// <summary>
/// Identifies which process owns a <see cref="SettingsService"/> instance, so <see cref="SettingsService.Save"/>
/// can refuse to persist sections the caller doesn't own. Prevents cross-process lost updates when
/// the App and the Supervisor share one settings file (spec, "Lost updates, not just corruption"):
/// each section has exactly one writer process.
/// </summary>
/// <remarks>
/// <b>The single exception:</b> the App may write ONE key in the Supervisor-owned section,
/// <c>supervision.enabled</c>, and only through
/// <see cref="SettingsService.SaveSupervisionEnabled(bool)"/> on a
/// <see cref="SettingsWriter.Supervisor"/> instance. The App's Settings row needs to turn
/// supervision on and off, and the App-writer <see cref="SettingsService.Save"/> cannot persist the
/// section at all (it overlays the on-disk copy); that method edits the one raw on-disk key without
/// rewriting anything else. Every other key in <see cref="SupervisionSettings"/> stays
/// Supervisor-only.
/// </remarks>
public enum SettingsWriter
{
    /// <summary>Owns all sections except <see cref="SupervisionSettings"/> (the App process).</summary>
    App,
    /// <summary>Owns only the <see cref="SupervisionSettings"/> section (the Supervisor process).</summary>
    Supervisor
}
