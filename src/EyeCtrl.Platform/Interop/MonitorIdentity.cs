namespace EyeCtrl.Platform.Interop;

using System.Runtime.InteropServices;

/// <summary>
/// Resolves a monitor's stable hardware identity from its GDI display name.
/// <para>
/// The GDI name (<c>\\.\DISPLAY1</c>) is an enumeration index: Windows reassigns it whenever monitors
/// are attached, detached, or re-enumerated, so persisting it as a monitor's identity means anything
/// keyed to it — a gaze calibration, say — is discarded after a dock change, a driver update, or
/// sometimes just a reboot. The EDID PnP ID behind the display does not move, so that is what this
/// returns.
/// </para>
/// <para>
/// Limitation: DisplayConfig exposes the EDID manufacturer and product code but not the serial number,
/// so the id names a monitor <em>model</em>. Two identical monitors are indistinguishable.
/// </para>
/// </summary>
public static class MonitorIdentity
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;

    /// <summary>
    /// Returns the stable hardware id (e.g. <c>DELA1CF</c>) of the monitor currently presented under
    /// <paramref name="gdiDeviceName"/>, or an empty string when it cannot be determined.
    /// </summary>
    /// <param name="gdiDeviceName">
    /// GDI display name to look up, as returned by <c>Screen.DeviceName</c> (e.g. <c>\\.\DISPLAY1</c>).
    /// </param>
    /// <returns>
    /// The EDID PnP id, or empty on any failure — an unreadable id is not an error, it just means callers
    /// fall back to matching on the GDI name.
    /// </returns>
    public static string GetHardwareId(string gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName)) return string.Empty;

        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != ErrorSuccess)
                return string.Empty;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ErrorSuccess)
                return string.Empty;

            for (int i = 0; i < pathCount; i++)
            {
                // A path pairs a source (the desktop surface that carries the GDI name) with a target
                // (the physical connector/monitor that carries the EDID), so resolve the source first
                // to find the path that belongs to the caller's display.
                var source = new DisplayConfigSourceDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoType.GetSourceName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                        AdapterId = paths[i].SourceInfo.AdapterId,
                        Id = paths[i].SourceInfo.Id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref source) != ErrorSuccess) continue;
                if (!string.Equals(source.ViewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = new DisplayConfigTargetDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoType.GetTargetName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        AdapterId = paths[i].TargetInfo.AdapterId,
                        Id = paths[i].TargetInfo.Id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref target) != ErrorSuccess) return string.Empty;

                return FormatHardwareId(target.EdidManufactureId, target.EdidProductCodeId);
            }

            return string.Empty;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Builds the persisted id from the two EDID fields: three-letter manufacturer code followed by the
    /// four-hex-digit product code (e.g. <c>DELA1CF</c>). Returns empty when the manufacturer code does
    /// not decode, which is the signal that no usable EDID identity exists.
    /// </summary>
    /// <param name="edidManufactureId">EDID manufacturer id as reported by DisplayConfig.</param>
    /// <param name="edidProductCodeId">EDID product code as reported by DisplayConfig.</param>
    /// <returns>The PnP id, or an empty string.</returns>
    internal static string FormatHardwareId(ushort edidManufactureId, ushort edidProductCodeId)
    {
        var manufacturer = DecodeEdidManufacturer(edidManufactureId);
        // The product code alone is not distinctive enough to key on (many vendors reuse small values),
        // so without a manufacturer there is no identity worth persisting.
        if (manufacturer.Length == 0) return string.Empty;
        return $"{manufacturer}{edidProductCodeId:X4}";
    }

    /// <summary>
    /// Decodes the EDID manufacturer id into its three-letter PnP vendor code (e.g. <c>DEL</c>).
    /// </summary>
    /// <param name="edidManufactureId">EDID manufacturer id as reported by DisplayConfig.</param>
    /// <returns>The three-letter code, or an empty string when the value is not a valid encoding.</returns>
    internal static string DecodeEdidManufacturer(ushort edidManufactureId)
    {
        // EDID bytes 8-9 hold three 5-bit letters (1 = 'A' … 26 = 'Z') packed big-endian into 16 bits,
        // with the top bit reserved; DisplayConfig hands them over as a little-endian ushort, so swap
        // the bytes before unpacking. A value that does not decode to three letters (all-zero EDID,
        // a virtual display) yields empty rather than mojibake.
        int packed = ((edidManufactureId & 0xFF) << 8) | (edidManufactureId >> 8);

        Span<char> letters = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            int value = (packed >> (10 - (i * 5))) & 0x1F;
            if (value is < 1 or > 26) return string.Empty;
            letters[i] = (char)('A' + value - 1);
        }
        return new string(letters);
    }

    // ── Win32 interop ──
    // [DllImport], not [LibraryImport]: the device-name packets carry inline fixed-length string buffers
    // (ByValTStr), which source-generated marshalling does not support.

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    // 64 bytes: infoType + id + adapterId, then a 48-byte target/source/desktop-image mode union that is
    // never read here. Size reserves the union without declaring it.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    private enum DisplayConfigDeviceInfoType : uint
    {
        GetSourceName = 1,
        GetTargetName = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public DisplayConfigDeviceInfoType Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }
}
