namespace EyeCtrl.Setup.Tests;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;
using EyeCtrl.Setup.Windows;

/// <summary>
/// Y1–Y5: the real catalog path against real files. It reads Microsoft-signed system catalogs
/// (read-only) and the committed New-FileCatalog v2 fixture; nothing is signed or written outside
/// a temp folder.
/// </summary>
public class CatalogVerifierTests
{
    private const string ExpectedTag =
        "753B2705E34C3DA25CF44DC6F547B23839D7FA1B0A81555912E477A7C1F82432";

    private const string SecondTag =
        "61D9FCC515269A316004E4233FF47374226912B1692CF887EBC349E90A512E4B";

    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-v2");

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

    private static string FixtureCatalog => Path.Combine(FixtureDirectory, "EyeCtrl.cat");

    private static IFileSignatureVerifier PinnedFake() => new FakeSignatureVerifier();

    [Fact]
    public void TheFixtureCatalog_ExposesItsMemberAndFilePath()
    {
        var members = CatalogMemberEnumerator.Read(
            Path.Combine(FixtureDirectory, "sample.cat"));

        Check.That(members).ContainsKey("sample.txt");
        Check.That(members).ContainsKey("second.txt");
        Check.That(members["sample.txt"]).IsEqualTo(ExpectedTag);
        Check.That(members["second.txt"]).IsEqualTo(SecondTag);
        Check.That(members).HasSize(2);
    }

    [Fact]
    public void TheCatalogFile_IsNotWalkedAsAMember()
    {
        var directory = CloneFixture();
        try
        {
            var result = new AuthenticodeCatalogVerifier(PinnedFake(), new FakeCatalogMemberVerifier())
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.UncataloguedFiles).Not.Contains("EyeCtrl.cat");
            Check.That(result.MissingFiles).IsEmpty();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AFileUnderAnotherMembersName_IsAMismatch()
    {
        var directory = CloneFixture();
        try
        {
            // Both contents are in the catalog, but under the wrong names. WinTrust's tag lookup
            // would accept each file's own hash; only the FilePath binding catches the swap.
            File.Copy(
                Path.Combine(directory, "second.txt"),
                Path.Combine(directory, "sample.txt"),
                overwrite: true);

            var result = new AuthenticodeCatalogVerifier(PinnedFake(), new FakeCatalogMemberVerifier())
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.HashMismatches).Contains("sample.txt");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ANonZeroMemberVerifier_MarksEveryFile()
    {
        var directory = CloneFixture();
        try
        {
            var memberVerifier = new FakeCatalogMemberVerifier
            {
                Result = unchecked((int)0x800B0100),
            };

            var result = new AuthenticodeCatalogVerifier(PinnedFake(), memberVerifier)
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.HashMismatches).Contains("sample.txt");
            Check.That(result.HashMismatches).Contains("second.txt");
            Check.That(memberVerifier.Calls).HasSize(2);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AnExtraFile_IsUncatalogued()
    {
        var directory = CloneFixture();
        try
        {
            File.WriteAllText(Path.Combine(directory, "zz-planted.dll"), "not in any catalog");

            var result = new AuthenticodeCatalogVerifier(PinnedFake(), new FakeCatalogMemberVerifier())
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.UncataloguedFiles).Contains("zz-planted.dll");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AMissingCataloguedFile_IsReported()
    {
        var directory = CloneFixture();
        try
        {
            File.Delete(Path.Combine(directory, "sample.txt"));

            var result = new AuthenticodeCatalogVerifier(PinnedFake(), new FakeCatalogMemberVerifier())
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.MissingFiles).Contains("sample.txt");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void ATamperedFile_IsAMismatch()
    {
        var directory = CloneFixture();
        try
        {
            File.AppendAllText(Path.Combine(directory, "sample.txt"), "tampered");

            var result = new AuthenticodeCatalogVerifier(PinnedFake(), new FakeCatalogMemberVerifier())
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.HashMismatches).Contains("sample.txt");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AnUnhashableFile_IsAMismatch_AndDoesNotStopTheLoop()
    {
        var directory = CloneFixture();
        try
        {
            // A system PE plus one byte: CatalogSipHash throws Win32 193 for it.
            var pe = Directory.EnumerateFiles(@"C:\Windows\System32", "*.dll")
                .FirstOrDefault(file =>
                {
                    try { _ = CatalogSipHash.Compute(file); return true; }
                    catch { return false; }
                });
            if (pe is null)
            {
                Assert.Skip("No hashable System32 DLL available on this machine.");
                return;
            }

            var bad = Path.Combine(directory, "0-bad.dll");
            File.Copy(pe, bad);
            File.AppendAllText(bad, "x");
            File.WriteAllText(Path.Combine(directory, "zz-planted.dll"), "not in any catalog");

            var result = new AuthenticodeCatalogVerifier(PinnedFake(), new FakeCatalogMemberVerifier())
                .Verify(Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.HashMismatches).Contains("0-bad.dll");
            Check.That(result.UncataloguedFiles).Contains("zz-planted.dll");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void AWrongThumbprint_IsReported()
    {
        var directory = CloneFixture();
        try
        {
            var verifier = new AuthenticodeCatalogVerifier(
                new FakeSignatureVerifier
                {
                    Result = new FileSignatureCheck(true, "DEADBEEF", null),
                },
                new FakeCatalogMemberVerifier());

            var result = verifier.Verify(
                Path.Combine(directory, "EyeCtrl.cat"), directory, SetupConstants.PinnedThumbprint);

            Check.That(result.ThumbprintMatches).IsFalse();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void TheRealCatalogSignature_IsValid_WithAThumbprint()
    {
        var catalog = FindSystemCatalog();
        if (catalog is null)
        {
            Assert.Skip("No signed System32 catalog available on this machine.");
            return;
        }

        var check = new WinTrustFileSignatureVerifier().Verify(catalog);

        Check.That(check.Valid).IsTrue();
        Check.That(check.SignerThumbprint).IsNotNull();
    }

    [Fact]
    public void TheCatalogInfo_HasTheVersionFieldAndAZeroedBuffer()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "AuthenticodeVerifiers.cs"));

        Check.That(source).Contains("public uint CatalogVersion;");
        Check.That(source).Contains("CatalogVersion = 0,");
        Check.That(source).Contains("Zero(infoPointer, Marshal.SizeOf<WinTrustCatalogInfo>());");
    }

    [Fact]
    public void ARealSignedCatalogMember_AcceptsTheSipTag_AndRejectsPathsAndFlatHashes()
    {
        var found = FindCataloguedPe();
        if (found is null)
        {
            Assert.Skip("No catalog-signed System32 PE available on this machine.");
            return;
        }

        var (file, tag, catalog) = found.Value;
        var flat = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        Check.That(tag == flat).IsFalse(); // a PE's Authenticode hash differs from its flat hash

        Check.That(WinTrustNative.VerifyCatalogMember(catalog, file, tag)).IsEqualTo(0);
        Check.That(WinTrustNative.VerifyCatalogMember(catalog, file, Path.GetFileName(file)))
            .IsNotEqualTo(0);
        Check.That(WinTrustNative.VerifyCatalogMember(catalog, file, flat)).IsNotEqualTo(0);
    }

    private static string CloneFixture()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "eyectrl-setup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.Copy(
            Path.Combine(FixtureDirectory, "sample.txt"),
            Path.Combine(directory, "sample.txt"));
        File.Copy(
            Path.Combine(FixtureDirectory, "second.txt"),
            Path.Combine(directory, "second.txt"));
        File.Copy(
            Path.Combine(FixtureDirectory, "sample.cat"),
            Path.Combine(directory, "EyeCtrl.cat"));
        return directory;
    }

    private static string? FindSystemCatalog()
    {
        var root = @"C:\Windows\System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}";
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cat").FirstOrDefault()
            : null;
    }

    private static (string File, string Tag, string Catalog)? FindCataloguedPe()
    {
        if (!Directory.Exists(@"C:\Windows\System32"))
            return null;

        foreach (var file in Directory.EnumerateFiles(@"C:\Windows\System32", "*.dll").Take(400))
        {
            string tag;
            try { tag = CatalogSipHash.Compute(file); }
            catch { continue; }

            var catalog = FindCatalog(tag);
            if (catalog is not null)
                return (file, tag, catalog);
        }

        return null;
    }

    private static string? FindCatalog(string tagHex)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
            return null;
        try
        {
            var hash = Convert.FromHexString(tagHex);
            var previous = IntPtr.Zero;
            var info = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, ref previous);
            if (info == IntPtr.Zero)
                return null;
            try
            {
                var catalogInfo = new CatalogInfo { CbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
                return CryptCATCatalogInfoFromContext(info, ref catalogInfo, 0)
                    ? catalogInfo.CatalogFile
                    : null;
            }
            finally
            {
                _ = CryptCATAdminReleaseCatalogContext(admin, info, 0);
            }
        }
        finally
        {
            _ = CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string CatalogFile;
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr admin, IntPtr subsystem, string hashAlgorithm, IntPtr strongCertificate, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr admin, byte[] hash, uint size, uint flags, ref IntPtr previous);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATCatalogInfoFromContext(
        IntPtr info, ref CatalogInfo catalogInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr info, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
