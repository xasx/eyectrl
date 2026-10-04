namespace EyeCtrl.Supervisor.Supervision;

using EyeCtrl.Core.Settings;

/// <summary>
/// Save-time merge for <c>supervision.enabled</c> — the one key in the Supervisor-owned section
/// that the App may also write (its sanctioned ownership exception, see
/// <c>SettingsWriter</c>). A running supervisor loads its snapshot once at startup, so the App's
/// Settings row can turn supervision on while the setup window sits open; a save must not then
/// write the stale snapshot value silently back to disk. The window compares its toggle against
/// the value captured when it opened: a value the user actually changed in this window is the
/// user's intent and wins; an untouched toggle adopts whatever is on disk now.
/// </summary>
internal static class SupervisionSetupSave
{
    /// <summary>Decides the enable value a setup-window save should persist.</summary>
    /// <param name="current">The toggle's current value.</param>
    /// <param name="atOpen">The value the window captured when it opened.</param>
    /// <param name="onDisk">The enable value currently on disk.</param>
    /// <returns><paramref name="current"/> when the user changed the toggle in this window;
    /// otherwise <paramref name="onDisk"/>.</returns>
    internal static bool ResolveEnabled(bool current, bool atOpen, bool onDisk) =>
        current != atOpen ? current : onDisk;
}

/// <summary>
/// The setup window's view of <c>supervision.enabled</c>. Constructed when the window opens:
/// reads the CURRENT on-disk value (the supervisor's startup snapshot can predate the App's
/// Settings-row write, and the window must display what is true now), and re-reads at save so an
/// untouched toggle adopts whatever the App wrote meanwhile. The service is the window's
/// Supervisor-writer one, so the re-read sees the same file the save will rewrite.
/// </summary>
internal sealed class SupervisionSetupEnabledState
{
    private readonly SettingsService _settingsService;

    /// <param name="settingsService">The Supervisor-writer service the window saves through.</param>
    public SupervisionSetupEnabledState(SettingsService settingsService)
    {
        _settingsService = settingsService;
        EnabledAtOpen = settingsService.Load().Supervision.Enabled;
    }

    /// <summary>The on-disk value when the window opened — what the toggle shows, and the
    /// baseline that tells "the user changed it here" from "the App wrote it since".</summary>
    public bool EnabledAtOpen { get; }

    /// <summary>Decides the enable value a save should persist: the user's in-window change wins;
    /// an untouched toggle adopts what is on disk NOW (re-read at save time).</summary>
    public bool ResolveForSave(bool current) =>
        SupervisionSetupSave.ResolveEnabled(
            current, EnabledAtOpen, _settingsService.Load().Supervision.Enabled);
}
