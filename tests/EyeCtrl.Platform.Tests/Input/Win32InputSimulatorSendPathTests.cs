namespace EyeCtrl.Platform.Tests.Input;

using System.Linq;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using NFluent;
using Xunit;

/// <summary>
/// The PUBLIC send paths of <see cref="Win32InputSimulator"/> — MoveCursor, the clicks, SendText,
/// SendKeyPress, SendModifiedKeyPress and Scroll — recorded at the internal
/// <see cref="Win32InputSimulator.SendInputOverride"/> seam, so no SendInput ever fires in these
/// tests. The sibling families (Clamp/ModifiedSequence/ReleaseLeftButton/ScrollAt*) cover only the
/// internal builders and two scroll paths; this file pins the batches the gaze-only user's sole
/// input channel actually emits: a swapped overload flag (a modified left-click turning into a
/// right-click), a lost KEYEVENTF_UNICODE (every typed char becoming a garbage scancode
/// keystroke), a stray button bit on MoveCursor's per-frame path, or a reordered compensation
/// that leaves a modifier stuck down are all wrong input that nothing else observes.
/// </summary>
public class Win32InputSimulatorSendPathTests
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

    /// <summary>
    /// MoveCursor runs per gaze frame (30-60 Hz); its event must be a bare absolute move. EXACT
    /// flag equality: a stray LEFTDOWN/WHEEL bit would click or scroll wherever the user is
    /// merely looking.
    /// </summary>
    [Fact]
    public void MoveCursor_SendsOneAbsoluteMoveEvent_WithNoButtonOrWheelBits()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.MoveCursor(new PhysicalPoint(120, 80));

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(1u);
        var input = sends[0].Inputs[0];
        Check.That(input.Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(input.Union.Mouse.Flags)
            .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        Check.That(input.Union.Mouse.Dx).IsEqualTo(120);
        Check.That(input.Union.Mouse.Dy).IsEqualTo(80);
        Check.That(input.Union.Mouse.MouseData).IsEqualTo(0);
    }

    /// <summary>
    /// Each plain click is ONE down/up pair carrying exactly the move+absolute+button flags, at
    /// the requested point. Exact flag equality kills the swapped-overload mutant (a left click
    /// sending RIGHTDOWN/RIGHTUP) and any dropped MOUSEEVENTF_MOVE/ABSOLUTE.
    /// </summary>
    [Fact]
    public void PlainClicks_SendOneDownUpPair_WithTheRightButtonBitsAtThePoint()
    {
        var (sim, sends) = NewRecordingSimulator();
        var cases = new (uint DownFlag, uint UpFlag, int X, int Y)[]
        {
            (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP, 10, 20),
            (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP, 30, 40),
            (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 50, 60),
        };

        sim.LeftClick(new PhysicalPoint(10, 20));
        sim.RightClick(new PhysicalPoint(30, 40));
        sim.MiddleClick(new PhysicalPoint(50, 60));

        Check.That(sends).HasSize(3);
        for (var i = 0; i < cases.Length; i++)
        {
            var (downFlag, upFlag, x, y) = cases[i];
            Check.WithCustomMessage($"click #{i} must be one SendInput call.").That(sends[i].Count).IsEqualTo(2u);
            var pair = sends[i].Inputs;
            Check.That(pair).HasSize(2);
            Check.That(pair[0].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
            Check.That(pair[0].Union.Mouse.Flags)
                .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | downFlag);
            Check.That(pair[1].Union.Mouse.Flags)
                .IsEqualTo(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | upFlag);
            Check.That(pair[0].Union.Mouse.Dx).IsEqualTo(x);
            Check.That(pair[0].Union.Mouse.Dy).IsEqualTo(y);
        }
    }

    /// <summary>
    /// A modified click is ONE atomic batch — modifier-downs (Ctrl, Alt, Win, Shift order), the
    /// click body at the point, then reversed ups with the menu-mask pair immediately before the
    /// Alt-up — and a fully-delivered send is followed by NO compensating release.
    /// </summary>
    [Fact]
    public void ModifiedLeftClick_IsOneBatch_ModifierDownsBodyReversedUpsMaskBeforeAltUp_NoSecondSend()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.LeftClick(new PhysicalPoint(400, 300),
            new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Alt });

        Check.That(sends).HasSize(1); // fully delivered -> no compensating send
        Check.That(sends[0].Count).IsEqualTo(8u);
        var batch = sends[0].Inputs;
        Check.That(batch).HasSize(8);
        // Downs: Alt before Shift (WrapWithModifiers' Ctrl/Alt/Win/Shift order).
        Check.That(batch[0].Type).IsEqualTo(NativeMethods.INPUT_KEYBOARD);
        Check.That(batch[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(batch[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[1].Union.Keyboard.Flags).IsEqualTo(0u);
        // Body: LEFTDOWN then LEFTUP, both at the point.
        Check.That(batch[2].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[2].Union.Mouse.Flags).IsEqualTo(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        Check.That(batch[2].Union.Mouse.Dx).IsEqualTo(400);
        Check.That(batch[2].Union.Mouse.Dy).IsEqualTo(300);
        Check.That(batch[3].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[3].Union.Mouse.Flags).IsEqualTo(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        Check.That(batch[3].Union.Mouse.Dx).IsEqualTo(400);
        Check.That(batch[3].Union.Mouse.Dy).IsEqualTo(300);
        // Reversed ups: Shift-up first, then the mask pair immediately before the Alt-up.
        Check.That(batch[4].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[4].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[5].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(batch[5].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(batch[6].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(batch[6].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[7].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[7].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    /// <summary>
    /// A PARTIAL insert (the OS injects only the prefix) must be followed by exactly one
    /// compensating send that releases what is actually held: the mouse-up first — with no move
    /// flags, so the cursor stays where the user's gaze left it — then the mask pair before the
    /// Alt-up, so a compensation never leaves a modifier stuck down.
    /// </summary>
    [Fact]
    public void PartialInsert_IsFollowedByOneCompensatingSend_MouseUpFirstMaskBeforeAltUp()
    {
        var sim = new Win32InputSimulator(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());
        var sends = new List<(uint Count, NativeMethods.INPUT[] Inputs)>();
        var calls = 0;
        sim.SendInputOverride = (count, inputs, _) =>
        {
            sends.Add((count, inputs.ToArray()));
            // The FIRST send (the 6-event Alt click) reports only 2 events injected; every later
            // send (the compensation) reports full delivery so it does not cascade further.
            return calls++ == 0 ? 2 : (int)count;
        };

        sim.LeftClick(new PhysicalPoint(10, 20), new HashSet<ModifierKey> { ModifierKey.Alt });

        Check.That(sends).HasSize(2);
        Check.That(sends[0].Count).IsEqualTo(6u); // Alt-down, LEFTDOWN, LEFTUP, mask pair, Alt-up
        Check.That(sends[1].Count).IsEqualTo(4u); // LEFTUP, mask pair, Alt-up
        var releases = sends[1].Inputs;
        Check.That(releases).HasSize(4);
        Check.That(releases[0].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(releases[0].Union.Mouse.Flags).IsEqualTo(NativeMethods.MOUSEEVENTF_LEFTUP);
        Check.That(releases[1].Type).IsEqualTo(NativeMethods.INPUT_KEYBOARD);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[1].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(releases[2].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(releases[3].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    /// <summary>
    /// SendText rides KEYEVENTF_UNICODE per character: VirtualKey must stay 0 (a real vk would
    /// re-map the char through the active layout) and ScanCode carries the char. Losing the
    /// UNICODE flag turns every typed character into a garbage scancode keystroke.
    /// </summary>
    [Fact]
    public void SendText_SendsOneUnicodeDownUpPerChar_WithVirtualKeyZeroAndScanCodeChar()
    {
        var (sim, sends) = NewRecordingSimulator();
        var chars = new[] { 'a', '\u00E9' }; // one ASCII and one non-ASCII char

        sim.SendText("a\u00E9");

        Check.That(sends).HasSize(2);
        for (var i = 0; i < chars.Length; i++)
        {
            var pair = sends[i].Inputs;
            Check.WithCustomMessage($"char #{i} must be one SendInput call.").That(sends[i].Count).IsEqualTo(2u);
            Check.That(pair).HasSize(2);
            Check.That(pair[0].Type).IsEqualTo(NativeMethods.INPUT_KEYBOARD);
            Check.That(pair[0].Union.Keyboard.VirtualKey).IsEqualTo(0);
            Check.That((char)pair[0].Union.Keyboard.ScanCode).IsEqualTo(chars[i]);
            Check.That(pair[0].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_UNICODE);
            Check.That(pair[1].Union.Keyboard.VirtualKey).IsEqualTo(0);
            Check.That((char)pair[1].Union.Keyboard.ScanCode).IsEqualTo(chars[i]);
            Check.That(pair[1].Union.Keyboard.Flags)
                .IsEqualTo(NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
        }
    }

    /// <summary>
    /// An extended-key press carries KEYEVENTF_EXTENDEDKEY on BOTH legs (down and up), and a
    /// non-extended press carries no stray extended bit.
    /// </summary>
    [Fact]
    public void SendKeyPress_ExtendedKey_CarriesTheExtendedBitOnBothLegs()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.SendKeyPress(0x25, isExtendedKey: true); // VK_LEFT — extended
        sim.SendKeyPress(0x41, isExtendedKey: false); // 'A' — not extended

        Check.That(sends).HasSize(2);
        var extended = sends[0].Inputs;
        Check.That(sends[0].Count).IsEqualTo(2u);
        Check.That(extended).HasSize(2);
        Check.That(extended[0].Type).IsEqualTo(NativeMethods.INPUT_KEYBOARD);
        Check.That(extended[0].Union.Keyboard.VirtualKey).IsEqualTo(0x25);
        Check.That(extended[0].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Check.That(extended[1].Union.Keyboard.VirtualKey).IsEqualTo(0x25);
        Check.That(extended[1].Union.Keyboard.Flags)
            .IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP);

        var plain = sends[1].Inputs;
        Check.That(plain[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(plain[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    /// <summary>
    /// A keyboard modifier batch contains its own keystroke, so the menu-mask pair must NEVER be
    /// injected into it (that mask exists only because a bare Alt-up can open the menu bar).
    /// </summary>
    [Fact]
    public void SendModifiedKeyPress_KeyboardBatchNeverCarriesTheMenuMask()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.SendModifiedKeyPress(new HashSet<ModifierKey> { ModifierKey.Alt }, 0x41);

        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(4u);
        var batch = sends[0].Inputs;
        Check.That(batch).HasSize(4);
        Check.That(batch[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(batch[1].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
        Check.That(batch[1].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(batch[2].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
        Check.That(batch[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[3].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    /// <summary>
    /// Plain Scroll sends one event per non-zero leg (no leading move — that is ScrollAt's job),
    /// skips both legs when both deltas are zero, and clamps an overflowing delta so the ×120
    /// scaling cannot wrap into the negatives.
    /// </summary>
    [Fact]
    public void Scroll_SkipsZeroLegs_SendsOneEventPerNonZeroLeg_AndClamps()
    {
        var (sim, sends) = NewRecordingSimulator();

        sim.Scroll(new PhysicalPoint(5, 6), deltaX: 0, deltaY: 0);
        Check.That(sends).IsEmpty(); // both legs zero -> no send at all

        sim.Scroll(new PhysicalPoint(5, 6), deltaX: 0, deltaY: 3);
        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(1u);
        Check.That(sends[0].Inputs[0].Union.Mouse.Flags).IsEqualTo(NativeMethods.MOUSEEVENTF_WHEEL);
        Check.That(sends[0].Inputs[0].Union.Mouse.MouseData).IsEqualTo(3 * PlatformConstants.MouseWheelDelta);

        sends.Clear();
        sim.Scroll(new PhysicalPoint(5, 6), deltaX: int.MaxValue, deltaY: 0);
        Check.That(sends).HasSize(1);
        Check.That(sends[0].Count).IsEqualTo(1u);
        Check.That(sends[0].Inputs[0].Union.Mouse.Flags).IsEqualTo(NativeMethods.MOUSEEVENTF_HWHEEL);
        // int.MaxValue × 120 would overflow int; the clamp keeps the scaled delta in range.
        Check.That(sends[0].Inputs[0].Union.Mouse.MouseData)
            .IsEqualTo(17_000_000 * PlatformConstants.MouseWheelDelta);
    }
}
