namespace EyeCtrl.App.Tests;

/// <summary>
/// Every test class that sets or reads <c>EYECTRL_SUPERVISOR_EXE</c> (<c>SupervisorLauncher.ExeEnvVar</c>)
/// belongs here — directly, or through <c>SupervisorLauncher.ResolveExePath</c>/<c>IsExecutablePresent</c>,
/// <c>SupervisionComposition.CreateForProduction</c> or the request channel's production client
/// check. The variable is process-wide and xUnit runs different classes in parallel, so one class's
/// write or <c>Dispose</c> reset can land between another's two reads. A named collection runs its
/// members one after the other without stopping the rest of the suite (unlike
/// <c>DisableParallelization</c>, which in xUnit v3 runs alongside nothing at all).
/// </summary>
/// <remarks>
/// <c>HeartbeatPublisherTests</c> also reads the variable (its squatter test runs the real
/// server check), but it already belongs to the <c>"HeartbeatPipe"</c> collection and a class can
/// be in one only. Its outcome cannot flip: the squatter is the test host, which no value of the
/// variable names.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SupervisorExeEnvironmentCollection
{
    public const string Name = "EYECTRL_SUPERVISOR_EXE environment";
}
