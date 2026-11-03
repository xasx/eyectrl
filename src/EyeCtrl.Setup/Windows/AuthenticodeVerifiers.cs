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
    private const uint WtdSaferFlag = 0x100;
    private const int TrustENosignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>Verifies a signed file's signature and chain. Returns the HRESULT (0 is valid).</summary>
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
            Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                Info = infoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdSaferFlag,
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
            CatalogFilePath = catalogPath,
            MemberTag = memberTag,
            MemberFilePath = memberFilePath,
        };

        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());
        try
        {
            Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceCatalog,
                Info = infoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdSaferFlag,
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

    /// <summary>True when <paramref name="filePath"/> is unsigned or not a member at all.</summary>
    public static bool IsMissingFromCatalog(int result) =>
        result is TrustENosignature or TrustESubjectFormUnknown;

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

/// <summary>
/// Verifies a <c>New-FileCatalog</c> (v2, SHA-256) catalog over every other file in the staging
/// folder. A file that is not a member and a file whose hash differs both fail WinTrust's catalog
/// check; both are reported as a hash mismatch because WinTrust does not distinguish them. Either
/// way the staging step refuses.
/// </summary>
public sealed class AuthenticodeCatalogVerifier : ICatalogVerifier
{
    private readonly IFileSignatureVerifier _signature;

    /// <summary>Creates the verifier.</summary>
    public AuthenticodeCatalogVerifier(IFileSignatureVerifier signature) => _signature = signature;

    /// <inheritdoc />
    public CatalogVerificationResult Verify(string catalogPath, string directory, string pinnedThumbprint)
    {
        var signature = _signature.Verify(catalogPath);
        var thumbprintMatches = string.Equals(
            signature.SignerThumbprint, pinnedThumbprint, StringComparison.OrdinalIgnoreCase);

        var mismatches = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(
                        Path.GetFullPath(file), Path.GetFullPath(catalogPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(directory, file).Replace('/', '\\');
                var result = WinTrustNative.VerifyCatalogMember(catalogPath, file, relative);
                // New-FileCatalog records the member tag as the path it was given. A flat publish
                // folder normally yields the bare file name; try that when the relative path does
                // not match, so the check does not depend on which spelling the catalog used.
                if (result != 0)
                    result = WinTrustNative.VerifyCatalogMember(
                        catalogPath, file, Path.GetFileName(file));
                if (result != 0)
                    mismatches.Add(relative);
            }
        }
        catch (Exception ex)
        {
            return new CatalogVerificationResult(
                signature.Valid, signature.SignerThumbprint, thumbprintMatches,
                mismatches, [], ex.GetType().Name);
        }

        return new CatalogVerificationResult(
            signature.Valid, signature.SignerThumbprint, thumbprintMatches, mismatches, [],
            signature.Error);
    }
}
