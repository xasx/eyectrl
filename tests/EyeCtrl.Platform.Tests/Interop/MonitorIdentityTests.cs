namespace EyeCtrl.Platform.Tests.Interop;

using EyeCtrl.Platform.Interop;

/// <summary>
/// Covers the pure EDID-decoding half of <see cref="MonitorIdentity"/>. The DisplayConfig query itself
/// needs real display hardware, so it is not exercised here; what is pinned is the bit-unpacking that
/// turns an EDID manufacturer id into a PnP vendor code, since getting the byte order wrong would
/// silently produce a stable-but-wrong identity and mismatch every saved calibration.
/// </summary>
public class MonitorIdentityTests
{
    // Byte order confirmed against real hardware on 2026-08-02: DisplayConfig reported edidMfr=0xAF06
    // for the primary display, which decodes to "AUO" — and Windows' own device path for that display
    // reads \\?\DISPLAY#AUOC5AC#..., i.e. the OS agrees letter for letter.
    [Theory]
    [InlineData(0xAC10, "DEL")]  // Dell
    [InlineData(0x2D4C, "SAM")]  // Samsung
    [InlineData(0xAF06, "AUO")]  // AU Optronics
    [InlineData(0xE430, "LGD")]  // LG Display
    public void DecodeEdidManufacturer_UnpacksKnownVendorCodes(int edidManufactureId, string expected)
    {
        Check.That(MonitorIdentity.DecodeEdidManufacturer((ushort)edidManufactureId)).IsEqualTo(expected);
    }

    [Theory]
    [InlineData(0x0000)]  // all-zero EDID (letter value 0 is out of the 1..26 range)
    [InlineData(0xFFFF)]  // unwritten EDID (letter value 31 is out of range)
    public void DecodeEdidManufacturer_ReturnsEmpty_ForValuesThatAreNotThreeLetters(int edidManufactureId)
    {
        // Empty is the signal that no usable identity exists, which falls the caller back to the
        // GDI display name rather than persisting mojibake as a monitor's identity.
        Check.That(MonitorIdentity.DecodeEdidManufacturer((ushort)edidManufactureId)).IsEqualTo(string.Empty);
    }

    [Fact]
    public void FormatHardwareId_CombinesVendorCodeAndProductCode()
    {
        Check.That(MonitorIdentity.FormatHardwareId(0xAF06, 0xC5AC)).IsEqualTo("AUOC5AC");
    }

    [Fact]
    public void FormatHardwareId_PadsProductCodeToFourHexDigits()
    {
        Check.That(MonitorIdentity.FormatHardwareId(0xAC10, 0x00A1)).IsEqualTo("DEL00A1");
    }

    [Fact]
    public void FormatHardwareId_ReturnsEmpty_WhenManufacturerDoesNotDecode()
    {
        // A product code on its own is not distinctive enough to key a calibration to.
        Check.That(MonitorIdentity.FormatHardwareId(0x0000, 0xC5AC)).IsEqualTo(string.Empty);
    }

    [Fact]
    public void GetHardwareId_ReturnsEmpty_ForAnEmptyDeviceName()
    {
        Check.That(MonitorIdentity.GetHardwareId("")).IsEqualTo(string.Empty);
    }

    [Fact]
    public void GetHardwareId_ReturnsEmpty_ForAnUnknownDeviceName()
    {
        // No path matches, so the lookup must degrade to "no id" rather than throwing — callers treat
        // an empty id as "match on the device name instead".
        Check.That(MonitorIdentity.GetHardwareId("\\\\.\\DISPLAY_NOT_PRESENT")).IsEqualTo(string.Empty);
    }
}
