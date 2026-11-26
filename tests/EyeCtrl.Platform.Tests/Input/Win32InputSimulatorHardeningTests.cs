namespace EyeCtrl.Platform.Tests.Input;

using System.Linq;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

/// <summary>
/// Issue #172 input-injection hardening pins:
/// <list type="number">
/// <item><see cref="Win32InputSimulator.CreateKeyboardInput"/> populates <c>ScanCode</c> via
/// <c>MapVirtualKey(vk, MAPVK_VK_TO_VSC)</c> while keeping <c>KEYEVENTF_SCANCODE</c> off so the
/// virtual key stays authoritative locally and scan-code consumers (RDP/Hyper-V/VM clients,
/// raw-input apps, games) do not drop injected modifiers or keys.</item>
/// <item><see cref="Win32InputSimulator.BuildCompensatingReleases"/> guards compensating
/// <c>Win</c> releases with the <c>0xE8</c> mask pair (so a partial <c>SendInput</c> cut right
/// after <c>Win↓</c> cannot emit a bare <c>Win↓, Win↑</c> that opens Start) and preserves
/// <c>KEYEVENTF_EXTENDEDKEY</c> on held extended keys.</item>
/// </list>
/// </summary>
public class Win32InputSimulatorHardeningTests
{
    private const ushort MenuMask = Win32InputSimulator.MenuMaskVirtualKey;

    private static (Win32InputSimulator Sim, List<(uint Count, NativeMethods.INPUT[] Inputs)> Sends)
        NewRecordingSimulator()
    {
        var sim = new Win32InputSimulator(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());
        var sends = new List<(uint Count, NativeMethods.INPUT[] Inputs)>();
        sim.SendInputOverride = (count, inputs, _) =>
        {
            sends.Add((count, inputs.ToArray()));
            return (int)count;
        };
        return (sim, sends);
    }

    [Theory]
    [InlineData((ushort)0x41, false)]             // 'A'
    [InlineData(NativeMethods.VK_SHIFT, false)]
    [InlineData(NativeMethods.VK_CONTROL, false)]
    [InlineData(NativeMethods.VK_MENU, false)]
    [InlineData(NativeMethods.VK_LWIN, true)]
    [InlineData(VirtualKeys.Left, true)]
    [InlineData(VirtualKeys.Return, false)]
    public void CreateKeyboardInput_PopulatesScanCode_AndNeverSetsKeyeventfScancode(ushort vk, bool extended)
    {
        uint downFlags = extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u;
        uint upFlags = downFlags | NativeMethods.KEYEVENTF_KEYUP;
        ushort expectedScan = (ushort)NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);

        var down = Win32InputSimulator.CreateKeyboardInput(vk, downFlags);
        var up = Win32InputSimulator.CreateKeyboardInput(vk, upFlags);

        Check.That(expectedScan).IsStrictlyGreaterThan((ushort)0);
        Check.That(down.Union.Keyboard.VirtualKey).IsEqualTo(vk);
        Check.That(down.Union.Keyboard.ScanCode).IsEqualTo(expectedScan);
        Check.That(down.Union.Keyboard.Flags).IsEqualTo(downFlags);
        Check.That(down.Union.Keyboard.Flags & NativeMethods.KEYEVENTF_SCANCODE).IsEqualTo(0u);

        Check.That(up.Union.Keyboard.VirtualKey).IsEqualTo(vk);
        Check.That(up.Union.Keyboard.ScanCode).IsEqualTo(expectedScan);
        Check.That(up.Union.Keyboard.Flags).IsEqualTo(upFlags);
        Check.That(up.Union.Keyboard.Flags & NativeMethods.KEYEVENTF_SCANCODE).IsEqualTo(0u);
    }

    [Fact]
    public void PublicSendPaths_CarryMappedScanCodes_WithoutScancodeFlag()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.SendKeyPress(0x41);
        sim.SendKeyPress(VirtualKeys.Left, isExtendedKey: true);
        sim.SendModifiedKeyPress(new HashSet<ModifierKey> { ModifierKey.Ctrl, ModifierKey.Shift }, 0x43);
        sim.LeftClick(new PhysicalPoint(100, 100), new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Alt });
        sim.Scroll(new PhysicalPoint(100, 100), deltaX: 0, deltaY: 1, new HashSet<ModifierKey> { ModifierKey.Ctrl });

        var keyboardEvents = sends
            .SelectMany(s => s.Inputs)
            .Where(i => i.Type == NativeMethods.INPUT_KEYBOARD)
            .ToList();

        Check.That(keyboardEvents).Not.IsEmpty();
        foreach (var ev in keyboardEvents)
        {
            var vk = ev.Union.Keyboard.VirtualKey;
            var expectedScan = (ushort)NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
            Check.That(ev.Union.Keyboard.ScanCode).IsEqualTo(expectedScan);
            Check.That(ev.Union.Keyboard.Flags & NativeMethods.KEYEVENTF_SCANCODE).IsEqualTo(0u);
            if (vk != MenuMask)
                Check.That(ev.Union.Keyboard.ScanCode).IsStrictlyGreaterThan((ushort)0);
        }
    }

    [Fact]
    public void Compensation_LoneHeldLWin_EmitsMaskPairBeforeExtendedWinUp()
    {
        // Win+E cut at sent = 1 (only Win-down landed): without the 0xE8 mask pair, the
        // compensating Win-up completes a bare Win-down/Win-up tap and opens the Start menu.
        var batch = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey> { ModifierKey.Win }, 0x45, isExtendedKey: false);

        var releases = Win32InputSimulator.BuildCompensatingReleases(1, batch);

        Check.That(releases).HasSize(3);
        Check.That(releases[0].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[2].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_LWIN);
        Check.That(releases[2].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Check.That(releases[2].Union.Keyboard.ScanCode)
            .IsEqualTo((ushort)NativeMethods.MapVirtualKey(NativeMethods.VK_LWIN, NativeMethods.MAPVK_VK_TO_VSC));
    }

    [Fact]
    public void Compensation_LoneHeldRWin_EmitsMaskPairBeforeExtendedWinUp()
    {
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_RWIN, NativeMethods.KEYEVENTF_EXTENDEDKEY),
            Win32InputSimulatorTestHelper.KeyboardInput(0x45, 0),
            Win32InputSimulatorTestHelper.KeyboardInput(0x45, NativeMethods.KEYEVENTF_KEYUP),
            Win32InputSimulatorTestHelper.KeyboardInput(
                NativeMethods.VK_RWIN, NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY),
        };

        var releases = Win32InputSimulator.BuildCompensatingReleases(1, batch);

        Check.That(releases).HasSize(3);
        Check.That(releases[0].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[2].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_RWIN);
        Check.That(releases[2].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }

    [Fact]
    public void Compensation_AltAndWinHeldTogether_EmitsSingleMaskPair_ThenReversedUpsWithExtendedWin()
    {
        var batch = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey> { ModifierKey.Ctrl, ModifierKey.Alt, ModifierKey.Win },
            0x45,
            isExtendedKey: false);

        // Cut at sent = 3: Ctrl-down, Alt-down, Win-down all landed; 'E' never did.
        var releases = Win32InputSimulator.BuildCompensatingReleases(3, batch);

        Check.That(releases).HasSize(5); // mask-down, mask-up, Win-up (ext), Alt-up, Ctrl-up
        Check.That(releases[0].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[2].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_LWIN);
        Check.That(releases[2].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Check.That(releases[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(releases[3].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[4].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_CONTROL);
        Check.That(releases[4].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void Compensation_CutMidMaskPair_ReleasesHeldMaskKeyWithoutDuplicateMaskDown()
    {
        // Alt-down, LEFTDOWN, LEFTUP, mask-down, mask-up, Alt-up cut at sent = 4:
        // mask-down already landed, so the reverse release loop closes mask-up then Alt-up
        // without injecting a redundant mask-down while 0xE8 is already held.
        var body = new[]
        {
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
        };
        var batch = Win32InputSimulator.WrapWithModifiers(
            new HashSet<ModifierKey> { ModifierKey.Alt }, body, includeAltMenuMask: true);

        var releases = Win32InputSimulator.BuildCompensatingReleases(4, batch);

        Check.That(releases).HasSize(2);
        Check.That(releases[0].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[0].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void Compensation_ExtendedMainKeyHeld_PreservesExtendedBitOnRelease()
    {
        var batch = Win32InputSimulator.BuildModifiedKeySequence(
            new HashSet<ModifierKey> { ModifierKey.Ctrl }, VirtualKeys.Left, isExtendedKey: true);

        // Cut at sent = 2: Ctrl-down and Left-down (extended) landed; ups did not.
        var releases = Win32InputSimulator.BuildCompensatingReleases(2, batch);

        Check.That(releases).HasSize(2);
        Check.That(releases[0].Union.Keyboard.VirtualKey).IsEqualTo(VirtualKeys.Left);
        Check.That(releases[0].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_CONTROL);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void SendModifiedKeyPress_PartialCutAfterWinDown_SendsMaskedExtendedWinCompensation()
    {
        var sim = new Win32InputSimulator(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());
        var sends = new List<(uint Count, NativeMethods.INPUT[] Inputs)>();
        var calls = 0;
        sim.SendInputOverride = (count, inputs, _) =>
        {
            sends.Add((count, inputs.ToArray()));
            return calls++ == 0 ? 1 : (int)count;
        };

        sim.SendModifiedKeyPress(new HashSet<ModifierKey> { ModifierKey.Win }, 0x45);

        Check.That(sends).HasSize(2);
        Check.That(sends[0].Count).IsEqualTo(4u);
        Check.That(sends[1].Count).IsEqualTo(3u);
        var releases = sends[1].Inputs;
        Check.That(releases[0].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[2].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_LWIN);
        Check.That(releases[2].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }
}
