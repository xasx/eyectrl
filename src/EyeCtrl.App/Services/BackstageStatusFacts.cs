namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Input;

/// <summary>
/// The App-side status answer for the Backstage strip: the localized snapshot plus whether the
/// Supervisor badge may be shown at all. <see cref="ShowSupervisor"/> is false only when the
/// <see cref="SupervisionToggleService"/> was never composed — the l10n ruling is to HIDE the badge
/// in that state rather than add a fourth "unknown yet" string. In production the service is always
/// composed (<see cref="SupervisionComposition"/> constructs it unconditionally), so the false
/// branch is defensive.
/// </summary>
internal sealed record BackstageStatusView(BackstageStatusSnapshot Snapshot, bool ShowSupervisor);

/// <summary>
/// THE composition-root resolution for the Backstage status strip (Ticket 03). Kept as a pure
/// function over plain facts so a test can pin exactly where each badge fact comes from:
/// <c>installed ← gateways?.Installed ?? false</c> and
/// <c>hasUIAccess ← integrity.ReadSelf().HasUIAccess</c>.
///
/// <para>
/// This exists because a Debug test host once hid resolver wiring: a constant
/// <c>installed: true</c> would make a dev build read "Installed · Standard" and offer an install
/// path that does not exist, with every other test still green. The coordinator calls this method
/// and nothing else to build the strip.
/// </para>
/// </summary>
internal static class BackstageStatusFacts
{
    /// <summary>
    /// Resolves the strip facts from the fresh per-visit <see cref="ISetupGateways"/> instance,
    /// the process token probe and the live supervision service.
    /// </summary>
    /// <param name="gateways">The fresh per-visit setup gateways, or null when the factory has not
    /// been wired yet (a dev/test composition).</param>
    /// <param name="integrity">The process token probe; null reads as no UIAccess.</param>
    /// <param name="supervision">The live supervision service; null hides the Supervisor badge.</param>
    /// <param name="assemblyVersion">The running assembly's version, e.g. <c>1.4.2.0</c>.</param>
    internal static BackstageStatusView Capture(
        ISetupGateways? gateways,
        IInputIntegrityProbe? integrity,
        SupervisionToggleService? supervision,
        string? assemblyVersion)
    {
        var installed = gateways?.Installed ?? false;
        var hasUIAccess = integrity?.ReadSelf().HasUIAccess ?? false;

        var snapshot = BackstageStatusSnapshot.Build(
            assemblyVersion,
            installedVersion: assemblyVersion,
            installed,
            hasUIAccess,
            BackstageStatusSnapshot.PresenceOf(supervision));

        return new BackstageStatusView(snapshot, ShowSupervisor: supervision is not null);
    }
}
