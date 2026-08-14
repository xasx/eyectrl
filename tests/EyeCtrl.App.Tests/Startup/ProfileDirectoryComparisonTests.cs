namespace EyeCtrl.App.Tests.Startup;

using System.IO;

/// <summary>
/// Phase 2 decides whether the profile directory is scoped by asking "is it the live profile?".
/// That question is asked about two <b>strings</b>, and <c>AppPaths.Resolve</c> returns an explicit
/// <c>EYECTRL_APPDATA_DIR</c> override verbatim — so the comparison has to normalise, or it fails
/// OPEN on the one check whose entire job is to fail closed.
/// <para>
/// Regression for a 2026-08-14 review finding: with an ordinal compare, pointing the override at a
/// case- or separator-variant of the live profile made phase 2 certify a "scoped" instance that was
/// writing the live <c>settings.json</c>, <c>calibration.json</c> and logs — and record that lie in
/// <c>instance.json</c>.
/// </para>
/// </summary>
public class ProfileDirectoryComparisonTests
{
    private static readonly string Live =
        Path.Combine(@"C:\Users\someone\AppData\Roaming", "EyeCtrl");

    [Fact]
    public void IdenticalPaths_AreTheSame()
    {
        Check.That(App.SamePath(Live, Live)).IsTrue();
    }

    [Fact]
    public void CaseVariant_IsTheSamePath()
    {
        // Windows paths are case-insensitive. This is the exact input that defeated the ordinal
        // compare and let a "scoped" instance run on the live profile.
        Check.That(App.SamePath(Live.ToLowerInvariant(), Live)).IsTrue();
    }

    [Fact]
    public void ForwardSlashVariant_IsTheSamePath()
    {
        Check.That(App.SamePath(Live.Replace('\\', '/'), Live)).IsTrue();
    }

    [Fact]
    public void TrailingSeparator_IsTheSamePath()
    {
        Check.That(App.SamePath(Live + @"\", Live)).IsTrue();
    }

    [Fact]
    public void RelativeSegments_ResolveToTheSamePath()
    {
        Check.That(App.SamePath(Path.Combine(Live, "..", "EyeCtrl"), Live)).IsTrue();
    }

    [Fact]
    public void AGenuinelyScopedProfile_IsNotTheSamePath()
    {
        // The nested instances\<id> directory must still read as different, or nothing could ever
        // verify as scoped.
        Check.That(App.SamePath(Path.Combine(Live, "instances", "run7"), Live)).IsFalse();
    }

    [Fact]
    public void AnUnrelatedDirectory_IsNotTheSamePath()
    {
        Check.That(App.SamePath(@"C:\scratch\elsewhere", Live)).IsFalse();
    }

    [Fact]
    public void AMalformedPath_DoesNotThrow()
    {
        // An override is arbitrary user input and reaches this before anything validates it. A
        // throw here would escape into OnStartup's generic catch and exit 0 instead of 78.
        Check.ThatCode(() => App.SamePath("C:\\bad\0path", Live)).DoesNotThrow();
    }
}
