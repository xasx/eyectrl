namespace EyeCtrl.Core.Tests.Services;

using System.IO;
using EyeCtrl.Core.Services;

public class AppPathsTests
{
    private const string Roaming = @"C:\Users\someone\AppData\Roaming";

    [Fact]
    public void Resolve_WithNoIdAndNoOverride_IsTheLiveProfile()
    {
        var result = AppPaths.Resolve(overrideDir: null, Roaming, instanceId: null);

        Check.That(result).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void Resolve_WithAnId_NestsUnderInstances()
    {
        var result = AppPaths.Resolve(overrideDir: null, Roaming, instanceId: "run7");

        Check.That(result).IsEqualTo(Path.Combine(Roaming, "EyeCtrl", "instances", "run7"));
    }

    [Fact]
    public void Resolve_ScopedPathIsNestedInsideTheLiveProfile_NotBesideIt()
    {
        // The E2E harness deletes its own profile directory. It must be provably BELOW the live one
        // so a recursive delete of the wrong level is impossible to write by accident.
        var live = AppPaths.Resolve(null, Roaming, null);
        var scoped = AppPaths.Resolve(null, Roaming, "run7");

        Check.That(scoped.StartsWith(live, StringComparison.Ordinal)).IsTrue();
        Check.That(scoped).IsNotEqualTo(live);
    }

    [Fact]
    public void Resolve_OverrideWins_OverBothDefaultAndId()
    {
        var result = AppPaths.Resolve(@"C:\scratch\here", Roaming, instanceId: "run7");

        Check.That(result).IsEqualTo(@"C:\scratch\here");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankOverrideIsTreatedAsAbsent(string blank)
    {
        var result = AppPaths.Resolve(blank, Roaming, instanceId: null);

        Check.That(result).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void Resolve_TrimsAPaddedOverride()
    {
        var result = AppPaths.Resolve("  C:\\scratch  ", Roaming, null);

        Check.That(result).IsEqualTo(@"C:\scratch");
    }

    [Fact]
    public void Resolve_RejectsAMalformedId()
    {
        ArgumentException? ex = null;
        try { AppPaths.Resolve(null, Roaming, "bad\\id"); }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
    }

    [Fact]
    public void OverrideEnvVar_IsTheDocumentedName()
    {
        Check.That(AppPaths.OverrideEnvVar).IsEqualTo("EYECTRL_APPDATA_DIR");
    }
}
