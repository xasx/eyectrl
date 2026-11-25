namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Localization;

/// <summary>
/// The four states the Backstage status strip's Supervisor badge can report — the live presence
/// answer, not the on-disk setting alone. <see cref="Off"/> means supervision is deliberately off;
/// <see cref="NotAnswering"/> means it is on but nothing is answering; <see cref="Unavailable"/>
/// means the Supervisor exe is missing. In production that last state has exactly one cause:
/// <see cref="SupervisionComposition"/> always composes a non-null service, so the badge's
/// "Supervisor not installed" text is true whenever it is shown. The null-service branch is
/// defensive/test-only and deliberately reuses the same label.
/// </summary>
public enum SupervisorPresence
{
    Connected,
    Off,
    NotAnswering,
    Unavailable,
}

/// <summary>
/// The read-only status strip the Backstage menu shows beneath its title (Ticket 02). A pure value
/// record plus a builder: every input is a plain fact (versions, installed, UIAccess, presence), and
/// the builder turns them into the localized texts the View binds. No WPF, no timers, no IO — the
/// window polls <see cref="SupervisionToggleService"/> every 2 s and rebuilds this.
/// </summary>
/// <param name="VersionText">e.g. <c>Version 1.4.2.0</c>.</param>
/// <param name="InstallText"><c>Installed · UIAccess active</c>, <c>Installed · Standard</c> or
/// <c>Dev build</c>.</param>
/// <param name="SupervisorText">The Supervisor badge's localized state.</param>
public sealed record BackstageStatusSnapshot(
    string VersionText,
    string InstallText,
    string SupervisorText)
{
    /// <summary>
    /// Reads the <see cref="SupervisionToggleService"/>'s presence. Order matters: a missing
    /// Supervisor exe is <see cref="SupervisorPresence.Unavailable"/> even when the setting says
    /// enabled, because "off" would claim a deliberate choice the owner never made; a deliberately
    /// off supervision is <see cref="SupervisorPresence.Off"/> regardless of a stale process;
    /// otherwise the live delivery-freshness answer decides connected vs. not answering. The null
    /// argument is the defensive/test-only branch — production always composes a service — and it
    /// maps to the same unavailable state the exe probe produces.
    /// </summary>
    public static SupervisorPresence PresenceOf(SupervisionToggleService? supervision) =>
        supervision is null
            ? SupervisorPresence.Unavailable
            : PresenceOf(supervision.IsAvailable, supervision.Enabled, supervision.IsSupervisorConnected);

    /// <summary>
    /// The pure presence mapping, separated from the service so every branch is testable without
    /// composing a live <see cref="SupervisionToggleService"/>. See
    /// <see cref="PresenceOf(SupervisionToggleService?)"/> for the ordering rationale.
    /// </summary>
    public static SupervisorPresence PresenceOf(bool available, bool enabled, bool connected)
    {
        if (!available)
            return SupervisorPresence.Unavailable;
        if (!enabled)
            return SupervisorPresence.Off;
        return connected
            ? SupervisorPresence.Connected
            : SupervisorPresence.NotAnswering;
    }

    /// <summary>
    /// Builds the three localized labels. An installed build names the INSTALLED package's version
    /// when it is known (that is what the owner is running), falling back to the assembly's own
    /// version; a dev build always names the assembly version. Every state the ticket names has a
    /// label: Installed+UIAccess, Installed+Standard, Dev build, and Connected / Off / Not
    /// answering / Unavailable.
    /// </summary>
    /// <param name="assemblyVersion">The running assembly's version, e.g. <c>1.4.2.0</c>.</param>
    /// <param name="installedVersion">The installed package's version, when the caller can read it;
    /// ignored for a dev build and used only when non-blank.</param>
    /// <param name="installed">Whether this process runs from the install root.</param>
    /// <param name="hasUIAccess">Whether EyeCtrl's own token carries UIAccess.</param>
    /// <param name="supervisor">The Supervisor badge's state.</param>
    public static BackstageStatusSnapshot Build(
        string? assemblyVersion,
        string? installedVersion,
        bool installed,
        bool hasUIAccess,
        SupervisorPresence supervisor)
    {
        var version = installed && !string.IsNullOrWhiteSpace(installedVersion)
            ? installedVersion!
            : string.IsNullOrWhiteSpace(assemblyVersion) ? "?" : assemblyVersion!;

        var versionText = StringTable.Current.T("overlays.backstage.status.version", version);

        var installText = !installed
            ? StringTable.Current.T("overlays.backstage.status.devBuild")
            : hasUIAccess
                ? StringTable.Current.T("overlays.backstage.status.installed.uiAccess")
                : StringTable.Current.T("overlays.backstage.status.installed.standard");

        var supervisorText = supervisor switch
        {
            SupervisorPresence.Connected => StringTable.Current.T("overlays.backstage.status.supervisor.connected"),
            SupervisorPresence.Off => StringTable.Current.T("overlays.backstage.status.supervisor.off"),
            SupervisorPresence.NotAnswering => StringTable.Current.T("overlays.backstage.status.supervisor.notAnswering"),
            _ => StringTable.Current.T("overlays.backstage.status.supervisor.unavailable"),
        };

        return new BackstageStatusSnapshot(versionText, installText, supervisorText);
    }
}
