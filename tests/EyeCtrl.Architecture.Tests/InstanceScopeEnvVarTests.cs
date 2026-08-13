namespace EyeCtrl.Architecture.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ties the Supervisor's hard-coded environment-variable name to the contract it must match.
/// <para>
/// <c>EyeCtrl.Supervisor</c> may not reference <c>EyeCtrl.Contracts</c> — <see cref="LayeringTests"/>
/// pins its dependency graph to Core + Platform + Controls, in both the csproj map and an IL usage
/// test — so it spells <c>EYECTRL_INSTANCE_ID</c> literally. This assembly is the only one that
/// references both, which makes it the only place the two can be compared.
/// </para>
/// <para>
/// If they drift, the supervisor stops stripping the variable it thinks it is stripping, and a
/// relaunched child inherits a scope: it would lack <c>--test-mode</c>, refuse with exit 78, look
/// exactly like a crash, book strikes, and escalate toward launching a fallback assistive
/// technology on the user's desktop.
/// </para>
/// </summary>
public class InstanceScopeEnvVarTests
{
    [Fact]
    public void SupervisorsHardCodedEnvVarNameMatchesTheContract()
    {
        Check.That(ProcessController.InstanceScopeEnvVarName).IsEqualTo(InstanceScope.EnvVar);
    }
}
