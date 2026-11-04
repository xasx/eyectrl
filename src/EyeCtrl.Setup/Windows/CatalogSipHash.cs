namespace EyeCtrl.Setup.Windows;

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Computes the catalog member tag for a file: the uppercase hex of its SIP SHA-256 (the
/// Authenticode file hash; for PE files this excludes the checksum/security directory, for data
/// files it is the file hash). <c>New-FileCatalog -CatalogVersion 2</c> records exactly this value
/// as the CTL subject identifier, and <c>WINTRUST_CATALOG_INFO.pcwszMemberTag</c> looks the member
/// up by it — never by a path.
/// </summary>
public static class CatalogSipHash
{
    private const int ErrorInsufficientBuffer = 122;
    private const int InitialBufferSize = 64;

    /// <summary>Returns the uppercase hex SIP SHA-256 of <paramref name="filePath"/>.</summary>
    /// <exception cref="Win32Exception">The catalog hash API refused the file.</exception>
    public static string Compute(string filePath)
    {
        if (!CryptCATAdminAcquireContext2(
                out var context, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            using var handle = File.OpenHandle(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var size = InitialBufferSize;
            var buffer = new byte[size];
            if (!CryptCATAdminCalcHashFromFileHandle2(
                    context, handle.DangerousGetHandle(), ref size, buffer, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer)
                    throw new Win32Exception(error);

                buffer = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(
                        context, handle.DangerousGetHandle(), ref size, buffer, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            return Convert.ToHexString(buffer.AsSpan(0, checked((int)size)));
        }
        finally
        {
            _ = CryptCATAdminReleaseContext(context, 0);
        }
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm,
        IntPtr pStrongCertificate, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr hCatAdmin, IntPtr hFile, ref int pcbHash, byte[] pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);
}
