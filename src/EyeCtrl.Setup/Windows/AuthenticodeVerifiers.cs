namespace EyeCtrl.Setup.Windows;

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using EyeCtrl.Setup.Abstractions;

/// <summary>Authenticode over WinTrust. The catalog is a signed file; every member is verified
/// against the catalog with <c>WINTRUST_CATALOG_INFO</c>, which recomputes the file hash.</summary>
internal static class WinTrustNative
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>Verifies a signed file's signature and chain. Returns the HRESULT (0 is valid).
    /// <para>Y1: no <c>WTD_SAFER_FLAG</c> here. That flag makes every signed catalog return
    /// <c>TRUST_E_NOSIGNATURE</c>; with 0, the same catalogs verify 0 (measured on 7 system cats).</para>
    /// </summary>
    public static int VerifyFile(string filePath)
    {
        var info = new WinTrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };

        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Zero(infoPointer, Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                Info = infoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = 0,
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            CloseState(ref data, ref action);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(infoPointer);
        }
    }

    /// <summary>Verifies one member file against the catalog's recorded hash. Returns the HRESULT.</summary>
    public static int VerifyCatalogMember(string catalogPath, string memberFilePath, string memberTag)
    {
        var info = new WinTrustCatalogInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
            CatalogVersion = 0,
            CatalogFilePath = catalogPath,
            MemberTag = memberTag,
            MemberFilePath = memberFilePath,
        };

        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());
        try
        {
            Zero(infoPointer, Marshal.SizeOf<WinTrustCatalogInfo>());
            Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceCatalog,
                Info = infoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = 0,
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            CloseState(ref data, ref action);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(infoPointer);
        }
    }

    private static void Zero(IntPtr pointer, int size) =>
        Marshal.Copy(new byte[size], 0, pointer, size);

    private static void CloseState(ref WinTrustData data, ref Guid action)
    {
        data.StateAction = WtdStateActionClose;
        _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustCatalogInfo
    {
        public uint CbStruct;
        public uint CatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberFilePath;
        public IntPtr MemberFileHandle;
        public IntPtr CalculatedFileHash;
        public uint CalculatedFileHashLength;
        public IntPtr CatalogContext;
        public IntPtr CatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr Info;
        public uint StateAction;
        public IntPtr WvtStateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd, [In] ref Guid actionId, [In, Out] ref WinTrustData data);
}

/// <summary>The production file-signature verifier: WinVerifyTrust plus the signer certificate's
/// SHA-1 thumbprint (the value the pinned thumbprint names).</summary>
public sealed class WinTrustFileSignatureVerifier : IFileSignatureVerifier
{
    /// <inheritdoc />
    public FileSignatureCheck Verify(string filePath)
    {
        if (!File.Exists(filePath))
            return new FileSignatureCheck(false, null, "missing");

        try
        {
            string? thumbprint;
            // SYSLIB0057 suggests X509CertificateLoader, but that loads certificate FILES; there
            // is no replacement API that extracts the signer certificate from an Authenticode
            // signed binary. WinVerifyTrust below still performs the real signature and chain check.
#pragma warning disable SYSLIB0057
            using (var certificate = X509Certificate.CreateFromSignedFile(filePath))
            {
                thumbprint = certificate.GetCertHashString();
            }
#pragma warning restore SYSLIB0057

            var result = WinTrustNative.VerifyFile(filePath);
            return new FileSignatureCheck(
                result == 0 && thumbprint is not null,
                thumbprint,
                result == 0 ? null : $"0x{result:X8}");
        }
        catch (Exception ex)
        {
            return new FileSignatureCheck(false, null, ex.GetType().Name);
        }
    }
}

/// <summary>The production member verifier: WinTrust recomputes the hash and looks the tag up.</summary>
public sealed class WinTrustCatalogMemberVerifier : ICatalogMemberVerifier
{
    /// <inheritdoc />
    public int Verify(string catalogPath, string filePath, string memberTag) =>
        WinTrustNative.VerifyCatalogMember(catalogPath, filePath, memberTag);
}

/// <summary>
/// Verifies a <c>New-FileCatalog</c> (v2, SHA-256) catalog over every other file in the staging
/// folder:
/// <list type="number">
/// <item>the catalog's signature and the pinned signer thumbprint;</item>
/// <item>every present file's hash against the catalog (per-file, so an unhashable file cannot
/// switch the rest off);</item>
/// <item>the full member set: a catalogued file missing from staging, or a file renamed to another
/// member's name, is refused;</item>
/// <item>no file outside the catalog.</item>
/// </list>
/// </summary>
public sealed class AuthenticodeCatalogVerifier : ICatalogVerifier
{
    private readonly IFileSignatureVerifier _signature;
    private readonly ICatalogMemberVerifier _memberVerifier;
    private readonly bool _requirePackageExecutables;

    /// <summary>Creates the verifier.</summary>
    public AuthenticodeCatalogVerifier(
        IFileSignatureVerifier signature, ICatalogMemberVerifier? memberVerifier = null,
        bool requirePackageExecutables = true)
    {
        _signature = signature;
        _memberVerifier = memberVerifier ?? new WinTrustCatalogMemberVerifier();
        _requirePackageExecutables = requirePackageExecutables;
    }

    /// <summary>The catalog member tag for a file: uppercase hex SIP SHA-256 (X1), never a path.</summary>
    internal static string MemberTag(string filePath) => CatalogSipHash.Compute(filePath);

    /// <inheritdoc />
    public CatalogVerificationResult Verify(string catalogPath, string directory, string pinnedThumbprint)
    {
        var signature = _signature.Verify(catalogPath);
        var thumbprintMatches = string.Equals(
            signature.SignerThumbprint, pinnedThumbprint, StringComparison.OrdinalIgnoreCase);

        // Y3: the member set is the other half of the check. An unreadable catalog fails closed.
        IReadOnlyDictionary<string, string> members;
        try
        {
            members = CatalogMemberEnumerator.Read(catalogPath);
        }
        catch (Exception ex)
        {
            return new CatalogVerificationResult(
                signature.Valid, signature.SignerThumbprint, thumbprintMatches,
                [], [], [], ex.GetType().Name);
        }

        // N3: an empty member set is an error, not a vacuously clean catalog.
        if (members.Count == 0)
        {
            return new CatalogVerificationResult(
                signature.Valid, signature.SignerThumbprint, thumbprintMatches,
                [], [], [], "emptyCatalog");
        }

        var mismatches = new List<string>();
        var missing = new List<string>();
        var uncatalogued = new List<string>();
        var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(
                        Path.GetFullPath(file), Path.GetFullPath(catalogPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue; // only EyeCtrl.cat is outside the catalog
                }

                var relative = Path.GetRelativePath(directory, file).Replace('/', '\\');
                actualPaths.Add(relative);

                // Y2: one file that cannot be hashed is a mismatch, not an abort.
                string tag;
                try
                {
                    tag = MemberTag(file);
                }
                catch (Exception)
                {
                    mismatches.Add(relative);
                    continue;
                }

                if (!members.TryGetValue(relative, out var memberTag))
                {
                    uncatalogued.Add(relative);
                    continue;
                }

                if (!string.Equals(tag, memberTag, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(relative);

                // WinTrust re-computes the member hash itself; a file copied under another
                // member's name fails here even though its own hash is in the catalog.
                if (_memberVerifier.Verify(catalogPath, file, tag) != 0)
                    mismatches.Add(relative);
            }

            foreach (var memberPath in members.Keys)
            {
                if (!actualPaths.Contains(memberPath))
                    missing.Add(memberPath);
            }

            // N3: a package without its three executables is not a package.
            if (_requirePackageExecutables)
            {
                foreach (var required in new[]
                         {
                             SetupConstants.AppExeName, SetupConstants.SupervisorExeName,
                             SetupConstants.SetupExeName,
                         })
                {
                    if (!members.ContainsKey(required))
                        missing.Add(required);
                }
            }
        }
        catch (Exception ex)
        {
            return new CatalogVerificationResult(
                signature.Valid, signature.SignerThumbprint, thumbprintMatches,
                mismatches, missing, uncatalogued, ex.GetType().Name);
        }

        return new CatalogVerificationResult(
            signature.Valid, signature.SignerThumbprint, thumbprintMatches,
            mismatches.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            uncatalogued.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            signature.Error);
    }
}
