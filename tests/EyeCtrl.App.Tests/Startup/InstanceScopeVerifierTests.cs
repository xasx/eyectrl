namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class InstanceScopeVerifierTests
{
    private static InstanceResource Scoped(string name) => new(name, Scoped: true, name + "_run7");
    private static InstanceResource Unscoped(string name) => new(name, Scoped: false, name);

    private static readonly string[] Expected =
    [
        "ProfileDirectory", "AppSingletonMutex", "SettingsWriterMutex", "DwellPipe", "StartupRunValue"
    ];

    [Fact]
    public void Passes_WhenEveryExpectedResourceIsScoped()
    {
        var result = InstanceScopeVerifier.Verify(Expected.Select(Scoped).ToArray(), Expected);

        Check.That(result.IsSatisfied).IsTrue();
        Check.That(result.Failures).IsEmpty();
    }

    [Fact]
    public void Fails_AndNamesTheResource_WhenOneIsUnscoped()
    {
        var reported = Expected.Select(n => n == "DwellPipe" ? Unscoped(n) : Scoped(n)).ToArray();

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.IsSatisfied).IsFalse();
        Check.That(result.Failures).HasSize(1);
        Check.That(result.Failures[0]).Contains("DwellPipe");
    }

    [Fact]
    public void Fails_WhenAnExpectedResourceIsNotReportedAtAll()
    {
        // The failure a missed wiring change produces: the resource exists but nobody collected it.
        var reported = Expected.Where(n => n != "SettingsWriterMutex").Select(Scoped).ToArray();

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.IsSatisfied).IsFalse();
        Check.That(result.Failures[0]).Contains("SettingsWriterMutex");
        Check.That(result.Failures[0]).Contains("not reported");
    }

    [Fact]
    public void Fails_WhenAScopedResourceReportsTheDefaultValue()
    {
        // ExternalDwellService accepts a pipeName and is currently constructed without one. The
        // effective value equalling the default is exactly what that looks like from here.
        var reported = Expected.Select(n =>
            n == "DwellPipe" ? new InstanceResource(n, Scoped: true, "eyectrl_dwell") : Scoped(n)).ToArray();

        var result = InstanceScopeVerifier.Verify(
            reported, Expected, defaults: new Dictionary<string, string> { ["DwellPipe"] = "eyectrl_dwell" });

        Check.That(result.IsSatisfied).IsFalse();
        Check.That(result.Failures[0]).Contains("eyectrl_dwell");
    }

    [Fact]
    public void ReportsEveryFailure_NotJustTheFirst()
    {
        var reported = new[] { Unscoped("ProfileDirectory"), Unscoped("DwellPipe") };

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.Failures.Count).IsStrictlyGreaterThan(1);
    }

    [Fact]
    public void ADefaultForAnUnwatchedResource_IsIgnored_NotTreatedAsAFailure()
    {
        // The defaults map is keyed by resource name and consulted only for resources that are
        // actually reported. A stale entry naming something that no longer exists must not
        // manufacture a failure, or the map becomes a second place to keep in sync.
        var result = InstanceScopeVerifier.Verify(
            Expected.Select(Scoped).ToArray(), Expected,
            defaults: new Dictionary<string, string> { ["SomethingRetired"] = "whatever" });

        Check.That(result.IsSatisfied).IsTrue();
    }

    [Fact]
    public void ExtraReportedResources_AreNotFailures()
    {
        // Verification is driven by the EXPECTED list. Reporting more than that is a caller
        // collecting extra diagnostics, not a scoping defect.
        var reported = Expected.Select(Scoped).Append(Scoped("SomethingExtra")).ToArray();

        var result = InstanceScopeVerifier.Verify(reported, Expected);

        Check.That(result.IsSatisfied).IsTrue();
    }

    [Fact]
    public void AnUnscopedResourceIsReported_EvenWhenItAlsoMatchesItsDefault()
    {
        // Order of the two checks: unscoped is the more direct diagnosis, and reporting the
        // default-value message instead would send the reader looking for a missing constructor
        // argument when the flag itself is false.
        var reported = Expected.Select(n =>
            n == "DwellPipe" ? new InstanceResource(n, Scoped: false, "eyectrl_dwell") : Scoped(n)).ToArray();

        var result = InstanceScopeVerifier.Verify(
            reported, Expected, defaults: new Dictionary<string, string> { ["DwellPipe"] = "eyectrl_dwell" });

        Check.That(result.Failures).HasSize(1);
        Check.That(result.Failures[0]).Contains("unscoped");
    }
}
