namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Runtime.InteropServices;
using Serilog;

/// <summary>
/// The sub-pixel probe's display facts for the results file (test design, What is recorded). The
/// refresh rate is read through <c>EnumDisplaySettings</c> on the current display mode; a headless
/// runner has no display mode, so every read fails soft to <c>null</c> rather than throwing.
/// </summary>
internal static class ProbeDisplayInfo
{
    private const int EnumCurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    /// <summary>The current display's refresh rate in Hz, or <c>null</c> when it is not readable.</summary>
    internal static double? TryGetRefreshRateHz()
    {
        try
        {
            var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode)) return null;
            if (mode.dmDisplayFrequency is 0 or 1) return null;   // 1 = "hardware default"
            return mode.dmDisplayFrequency;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the display refresh rate");
            return null;
        }
    }

    /// <summary>
    /// The <c>DEVMODEW</c> fields up to <c>dmDisplayFrequency</c>. The tail after that field is
    /// declared so <c>dmSize</c> matches the native structure exactly — the API writes up to
    /// <c>dmSize</c> bytes and rejects a short structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string? dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        // The positional union: print fields and display fields overlay each other.
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string? dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;

        // The DEVMODEW tail: ICM, reserved and panning fields. Never read; they exist so the
        // structure's size matches what the API expects to fill.
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
