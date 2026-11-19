namespace EyeCtrl.Setup;

using EyeCtrl.Core.Services;

/// <summary>
/// The ONE place the Setup process reads the profile resolver.
/// <para>
/// A Release Setup is the installed product's maintenance tool wherever its exe sits (review 01
/// F1 / fixup 01 D1), so every profile path, name and log folder in this assembly must derive
/// from <see cref="Location"/> — the SETUP rule. The App rule (<see cref="ProfileLocation.Current"/>)
/// belongs to the App and the Supervisor; the only legitimate use inside Setup is the App's
/// pre-hand-off session probe (<see cref="SetupSessionGuard.AppProbeMutexName"/>), because that
/// name must be the App's own.
/// </para>
/// <para>
/// A source ratchet (<c>SetupProfileWiringTests</c>) enforces that split, and
/// <see cref="SetupComposition.ProcessProfile"/> exposes this instance so a reference test can
/// prove WHICH resolver feeds Setup: in a Debug test host the two rules produce equal values, so
/// only object identity can tell them apart.
/// </para>
/// </summary>
internal static class SetupProcessProfile
{
    /// <summary>The SETUP rule's resolution for this process (see
    /// <see cref="ProfileLocation.CurrentSetup"/>).</summary>
    internal static ProfileLocation Location => ProfileLocation.CurrentSetup;

    /// <summary>The session mutex name, from that one resolution.</summary>
    internal static string SessionMutexName => Location.SetupSessionMutexName;
}
