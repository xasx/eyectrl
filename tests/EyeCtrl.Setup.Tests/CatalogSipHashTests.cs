namespace EyeCtrl.Setup.Tests;

using System.Security.Cryptography;
using EyeCtrl.Setup.Windows;
using NFluent;

/// <summary>
/// X1: a catalog member is looked up by the uppercase hex of its SIP SHA-256, never by a path.
/// The fixture is a real <c>New-FileCatalog -CatalogVersion 2</c> output (unsigned, so no
/// certificate or signing is involved); <c>certutil -dump</c> shows the CTL subject identifier
/// <c>753B2705…F82432</c> for <c>sample.txt</c>, which is exactly this tag.
/// </summary>
public class CatalogSipHashTests
{
    private const string ExpectedTag =
        "753B2705E34C3DA25CF44DC6F547B23839D7FA1B0A81555912E477A7C1F82432";

    [Fact]
    public void TheRealV2CatalogFixture_HasTheExpectedSipSha256Tag()
    {
        var sample = FixturePath("sample.txt");

        Check.That(File.Exists(sample)).IsTrue();
        Check.That(File.Exists(FixturePath("sample.cat"))).IsTrue();
        Check.That(CatalogSipHash.Compute(sample)).IsEqualTo(ExpectedTag);
        Check.That(AuthenticodeCatalogVerifier.MemberTag(sample)).IsEqualTo(ExpectedTag);
    }

    [Fact]
    public void ForANonPeFile_TheSipHashIsTheFilesSha256()
    {
        var sample = FixturePath("sample.txt");
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sample)));

        Check.That(CatalogSipHash.Compute(sample)).IsEqualTo(sha256);
    }

    [Fact]
    public void MutatingTheFile_ChangesTheTag()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "eyectrl-setup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var copy = Path.Combine(directory, "sample.txt");
            File.Copy(FixturePath("sample.txt"), copy);
            var before = CatalogSipHash.Compute(copy);
            File.AppendAllText(copy, "x");

            Check.That(CatalogSipHash.Compute(copy)).IsNotEqualTo(before);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheVerifier_LooksMembersUpByHash_NotByPath()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "AuthenticodeVerifiers.cs"));

        Check.That(source).Contains("var tag = MemberTag(file);");
        Check.That(source).Contains("VerifyCatalogMember(catalogPath, file, tag)");
        Check.That(source).DoesNotContain("VerifyCatalogMember(catalogPath, file, relative)");
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-v2", name);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EyeCtrl.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find EyeCtrl.slnx above " + AppContext.BaseDirectory);
    }
}
