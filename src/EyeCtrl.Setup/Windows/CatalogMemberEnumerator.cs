namespace EyeCtrl.Setup.Windows;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Reads a signed <c>New-FileCatalog</c> catalog's SHA-256 members: the member tag (uppercase SIP
/// SHA-256) and its <c>FilePath</c> attribute. Page-hash members (SHA-1, 40 hex chars) are skipped;
/// only the 64-char SHA-256 member is the file itself.
/// </summary>
internal static class CatalogMemberEnumerator
{
    private const uint OpenExisting = 0x00000004;

    /// <summary>Maps relative file path → uppercase SIP SHA-256 tag.</summary>
    /// <exception cref="Win32Exception">The catalog could not be opened or enumerated.</exception>
    public static IReadOnlyDictionary<string, string> Read(string catalogPath)
    {
        var catalog = CryptCATOpen(catalogPath, OpenExisting, IntPtr.Zero, 0, 0);
        if (catalog == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var previous = IntPtr.Zero;
            while (true)
            {
                var current = CryptCATEnumerateMember(catalog, previous);
                if (current == IntPtr.Zero)
                    break;

                var member = Marshal.PtrToStructure<CryptCatMember>(current);
                previous = current;

                if (member.ReferenceTag is not { Length: 64 })
                    continue; // page hash (SHA-1) or an unknown member class

                var attributePointer = CryptCATGetAttrInfo(catalog, current, "FilePath");
                if (attributePointer == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var attribute = Marshal.PtrToStructure<CryptCatAttribute>(attributePointer);
                if (attribute.Value == IntPtr.Zero || attribute.ValueLength == 0)
                    throw new InvalidDataException("A catalog member has no FilePath value.");

                var bytes = new byte[attribute.ValueLength];
                Marshal.Copy(attribute.Value, bytes, 0, bytes.Length);
                var path = ParseFilePath(bytes).Replace('/', '\\');
                members[path] = member.ReferenceTag;
            }
        }
        finally
        {
            _ = CryptCATClose(catalog);
        }

        return members;
    }

    private static string ParseFilePath(byte[] value) =>
        Encoding.Unicode.GetString(value).TrimEnd('\0');

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CryptCatMember
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ReferenceTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileName;
        public Guid SubjectType;
        public uint MemberFlags;
        public IntPtr IndirectData;
        public uint CertVersion;
        public uint Reserved;
        public IntPtr HReserved;
        public CryptAttrBlob EncodedIndirectData;
        public CryptAttrBlob EncodedMemberInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAttrBlob
    {
        public uint Length;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CryptCatAttribute
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ReferenceTag;
        public uint TypeAndAction;
        public uint ValueLength;
        public IntPtr Value;
        public uint Reserved;
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CryptCATOpen(
        string fileName, uint openFlags, IntPtr provider, uint publicVersion, uint encodingType);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATEnumerateMember(IntPtr catalog, IntPtr previous);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CryptCATGetAttrInfo(
        IntPtr catalog, IntPtr member, string referenceTag);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATClose(IntPtr catalog);
}
