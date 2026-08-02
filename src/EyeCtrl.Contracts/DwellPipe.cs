namespace EyeCtrl.Contracts;

/// <summary>
/// Transport constants for the external-dwell named-pipe channel. Lives here rather than in
/// <c>EyeCtrl.App</c> so the server (<c>ExternalDwellService</c>) and the SDK client
/// (<c>EyeCtrl.Sdk.Abstractions.DwellClient</c>) — which cannot reference each other — read the
/// name from one place and can never drift apart.
/// </summary>
public static class DwellPipe
{
    /// <summary>
    /// The production pipe name the cooperating dwell app connects to. Pipe names are
    /// machine-global, so tests override the server's name per instance instead of binding this
    /// one — see <c>ExternalDwellService</c>'s <c>pipeName</c> parameter.
    /// </summary>
    public const string Name = "eyectrl_dwell";
}
