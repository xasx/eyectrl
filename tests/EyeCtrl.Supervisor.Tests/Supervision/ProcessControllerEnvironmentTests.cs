namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Diagnostics;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Uses the LITERAL variable name rather than <c>InstanceScope.EnvVar</c>, matching the production
/// code: the Supervisor may not reference Contracts without breaking the layering invariant, and a
/// test that reached across for the constant would hide that constraint. The two are tied together
/// by <c>InstanceScopeEnvVarMatchesTheContract</c> in <c>EyeCtrl.Architecture.Tests</c>, which is
/// the one project that references both assemblies.
/// </summary>
public class ProcessControllerEnvironmentTests
{
    private const string EnvVar = "EYECTRL_INSTANCE_ID";

    [Fact]
    public void StripInstanceScope_RemovesTheInstanceId_SoAChildIsNeverScoped()
    {
        // A scoped, non-test-mode child refuses with exit 78, which is indistinguishable from a
        // crash to the supervisor — so it books strikes and escalates to launching a fallback AT.
        var psi = new ProcessStartInfo("EyeCtrl.App.exe") { UseShellExecute = false };
        psi.Environment[EnvVar] = "run7";

        ProcessController.StripInstanceScope(psi);

        Check.That(psi.Environment.ContainsKey(EnvVar)).IsFalse();
    }

    [Fact]
    public void StripInstanceScope_IsANoOp_WhenNoInstanceIdIsPresent()
    {
        var psi = new ProcessStartInfo("EyeCtrl.App.exe") { UseShellExecute = false };
        var before = psi.Environment.Count;

        ProcessController.StripInstanceScope(psi);

        Check.That(psi.Environment.Count).IsEqualTo(before);
    }

    [Fact]
    public void Launch_StripsTheInheritedInstanceId()
    {
        // The two tests above stay green if someone implements StripInstanceScope and forgets to
        // CALL it, which is the whole failure mode. This one drives Launch through its injectable
        // process factory and inspects the ProcessStartInfo it would have started.
        //
        // The variable must be set process-wide: ProcessStartInfo.Environment is materialised from
        // the current process block on first access, and Launch builds its own psi, so there is no
        // seam to inject it through. That is why this assembly disables test parallelisation.
        var original = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, "run7");
        try
        {
            ProcessStartInfo? captured = null;
            var controller = new ProcessController(
                psi => { captured = psi; return null; },   // Launch tolerates a null handle
                _ => null);

            controller.Launch("EyeCtrl.App.exe", paused: false);

            Check.That(captured).IsNotNull();
            Check.That(captured!.Environment.ContainsKey(EnvVar)).IsFalse();
        }
        finally { Environment.SetEnvironmentVariable(EnvVar, original); }
    }

    [Fact]
    public void Launch_WouldOtherwiseHaveInheritedIt()
    {
        // Proves the previous test is not vacuous. Without a strip, a psi built while the variable
        // is set DOES carry it — so the assertion there is a real observation, not a tautology
        // about a variable that was never present.
        var original = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, "run7");
        try
        {
            var psi = new ProcessStartInfo("EyeCtrl.App.exe") { UseShellExecute = false };

            Check.That(psi.Environment.ContainsKey(EnvVar)).IsTrue();
        }
        finally { Environment.SetEnvironmentVariable(EnvVar, original); }
    }
}
