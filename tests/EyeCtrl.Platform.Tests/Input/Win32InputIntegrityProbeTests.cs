namespace EyeCtrl.Platform.Tests.Input;

using System.Runtime.InteropServices;
using EyeCtrl.Platform.Input;

/// <summary>
/// C10's integrity-read parsing. R1's rule is only as good as the SID read under it: the last
/// sub-authority of a mandatory-label SID IS the integrity RID, and a parser that reads the first
/// one (or the wrong offset) would compare every elevated window as Low.
/// </summary>
public class Win32InputIntegrityProbeTests
{
    [Fact]
    public void ReadIntegrityLevel_ReturnsTheLastSubAuthority()
    {
        // SID: revision 1, three sub-authorities (0x1000, 0x2000, 0x3000); the mandatory label's
        // SID_AND_ATTRIBUTES points at it. 0x3000 is High.
        var sid = BuildSid(0x1000, 0x2000, 0x3000);
        var label = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(label, sid);

            Check.That(InputIntegrityNativeMethods.ReadIntegrityLevel(label)).IsEqualTo(0x3000);
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            Marshal.FreeHGlobal(sid);
        }
    }

    [Fact]
    public void ReadIntegrityLevel_HandlesASingleSubAuthority()
    {
        var sid = BuildSid(0x2000);
        var label = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(label, sid);

            Check.That(InputIntegrityNativeMethods.ReadIntegrityLevel(label)).IsEqualTo(0x2000);
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            Marshal.FreeHGlobal(sid);
        }
    }

    [Fact]
    public void ReadIntegrityLevel_ReturnsNullWhenThereIsNoSidOrNoSubAuthority()
    {
        var emptyLabel = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(emptyLabel, IntPtr.Zero);
            Check.That(InputIntegrityNativeMethods.ReadIntegrityLevel(emptyLabel)).IsNull();
        }
        finally
        {
            Marshal.FreeHGlobal(emptyLabel);
        }

        var countZeroSid = BuildSid();
        var label = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(label, countZeroSid);
            Check.That(InputIntegrityNativeMethods.ReadIntegrityLevel(label)).IsNull();
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            Marshal.FreeHGlobal(countZeroSid);
        }
    }

    /// <summary>Builds a native SID with the given sub-authorities (winnt.h layout).</summary>
    private static IntPtr BuildSid(params int[] subAuthorities)
    {
        var size = 8 + subAuthorities.Length * 4;
        var sid = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(sid, i, 0);

        Marshal.WriteByte(sid, 0, 1);                       // revision
        Marshal.WriteByte(sid, 1, (byte)subAuthorities.Length);
        for (var i = 0; i < subAuthorities.Length; i++)
            Marshal.WriteInt32(sid, 8 + i * 4, subAuthorities[i]);

        return sid;
    }
}
