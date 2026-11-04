namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

public class InboxScannerTests
{
    private static SetupHarness Harness() => new();

    private static void SeedInbox(
        SetupHarness harness, string name, string version, string? created = null)
    {
        var json = "{\"version\":\"" + version + "\",\"sha\":\"abc\",\"created\":\""
            + (created ?? "2026-11-03T10:00:00Z") + "\"}";
        harness.Files.AddFile(
            Path.Combine(harness.Paths.InboxRoot, name, "package.json"), json);
    }

    [Fact]
    public void DotPrefixedFolders_AreIgnored()
    {
        var harness = Harness();
        SeedInbox(harness, ".partial", "9.0.0");
        SeedInbox(harness, "1.0.0", "1.0.0");
        SeedInbox(harness, "2.0.0", "2.0.0");

        var packages = harness.Inbox.List();

        Check.That(packages).HasSize(2);
        Check.That(packages.Select(p => p.Name)).Contains("1.0.0");
        Check.That(packages.Select(p => p.Name)).Contains("2.0.0");
    }

    [Fact]
    public void Newest_UsesCreatedFirst()
    {
        var harness = Harness();
        SeedInbox(harness, "2.0.0", "2.0.0", "2026-01-01T00:00:00Z");
        SeedInbox(harness, "1.5.0", "1.5.0", "2026-02-01T00:00:00Z");

        Check.That(harness.Inbox.Newest()?.Name).IsEqualTo("1.5.0");
    }

    [Fact]
    public void Newest_ThenVersion_WhenCreatedTies()
    {
        var harness = Harness();
        SeedInbox(harness, "1.0.0", "1.0.0", "2026-02-01T00:00:00Z");
        SeedInbox(harness, "2.0.0", "2.0.0", "2026-02-01T00:00:00Z");

        Check.That(harness.Inbox.Newest()?.Name).IsEqualTo("2.0.0");
    }

    [Fact]
    public void Newest_ThenName_WhenVersionTies()
    {
        var harness = Harness();
        SeedInbox(harness, "alpha", "1.0.0", "2026-02-01T00:00:00Z");
        SeedInbox(harness, "beta", "1.0.0", "2026-02-01T00:00:00Z");

        Check.That(harness.Inbox.Newest()?.Name).IsEqualTo("beta");
    }

    [Fact]
    public void MissingCreated_SortsLast()
    {
        var harness = Harness();
        harness.Files.AddFile(
            Path.Combine(harness.Paths.InboxRoot, "no-created", "package.json"),
            "{\"version\":\"9.0.0\"}");
        SeedInbox(harness, "dated", "1.0.0", "2026-02-01T00:00:00Z");

        Check.That(harness.Inbox.Newest()?.Name).IsEqualTo("dated");
    }

    [Fact]
    public void AMissingInbox_IsEmpty()
    {
        Check.That(Harness().Inbox.List()).IsEmpty();
        Check.That(Harness().Inbox.Newest()).IsNull();
    }

    [Theory]
    [InlineData("2.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "2.0.0", false)]
    [InlineData("1.0.0+abc123", "1.0.0", false)]
    [InlineData("1.0.1+abc123", "1.0.0", true)]
    [InlineData("2.0.0-beta", "1.9.0", true)]
    [InlineData("not-a-version", "1.0.0", false)]
    [InlineData("2.0.0", null, false)]
    public void IsNewerThan_ComparesTheSemanticVersion(string? package, string? installed, bool newer)
    {
        Check.That(InboxScanner.IsNewerThan(package, installed)).IsEqualTo(newer);
    }
}
