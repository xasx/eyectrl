namespace EyeCtrl.Supervisor.Supervision;

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
