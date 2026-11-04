namespace EyeCtrl.Setup;

using EyeCtrl.Setup.Abstractions;
using Microsoft.Win32;

/// <summary>
/// Plan §10's gate: <c>ConsentPromptBehaviorAdmin</c> must be 0 (elevate without prompting) before
/// Setup changes anything. Any other value — including a missing or unreadable one — refuses,
/// because a consent prompt on the secure desktop cannot be reached by gaze and the engine must
/// never wait on one.
/// </summary>
public sealed class ElevationPolicyGate : IElevationPolicy
{
    private readonly IRegistryStore _registry;

    /// <summary>Creates the gate over the registry seam.</summary>
    public ElevationPolicyGate(IRegistryStore registry) => _registry = registry;

    /// <inheritdoc />
    public ElevationPolicyResult Read()
    {
        int? value;
        try
        {
            value = _registry.ReadDword(
                RegistryHive.LocalMachine, SetupConstants.PolicyKeyPath,
                SetupConstants.ConsentPromptBehaviorAdmin);
        }
        catch (Exception)
        {
            // Fail closed: an unreadable policy is not permission.
            value = null;
        }

        return new ElevationPolicyResult(value == 0, value);
    }
}
