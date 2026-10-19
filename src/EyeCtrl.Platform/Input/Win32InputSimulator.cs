namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sends mouse/keyboard input via Win32 SendInput.
/// <para>
/// SINGLE-PRIMARY-MONITOR ASSUMPTION: absolute mouse coordinates are normalized against
/// SM_CXSCREEN/SM_CYSCREEN, which describe the PRIMARY monitor only. On multi-monitor
/// setups, gaze-driven cursor positioning is therefore only correct on the primary
/// display (matching the eye tracker's single-screen calibration). Extending to
/// multiple monitors would require MOUSEEVENTF_VIRTUALDESK + virtual-screen metrics.
/// </para>
/// </summary>
public class Win32InputSimulator : IInputSimulator
{
    private static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
    private static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

    private readonly IMouseCoordinateMapper _coordinateMapper;
    private readonly ILogger<Win32InputSimulator>? _logger;

    public Win32InputSimulator(IMouseCoordinateMapper coordinateMapper,
        ILogger<Win32InputSimulator>? logger = null)
    {
        _coordinateMapper = coordinateMapper;
        _logger = logger;
    }

    public void MoveCursor(PhysicalPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        SendSingleInput(input);
    }

    public void LeftClick(PhysicalPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendInputs(down, up);
    }

    public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not { Count: > 0 }) { LeftClick(point); return; }
        var batch = WrapWithModifiers(modifiers, BuildModifiedClickBody(point, right: false, middle: false, doubleClick: false), includeAltMenuMask: true);
        SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
    }

    public void RightClick(PhysicalPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTUP);
        SendInputs(down, up);
    }

    public void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not { Count: > 0 }) { RightClick(point); return; }
        var batch = WrapWithModifiers(modifiers, BuildModifiedClickBody(point, right: true, middle: false, doubleClick: false), includeAltMenuMask: true);
        SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
    }

    public void MiddleClick(PhysicalPoint point)
    {
        var down = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_MIDDLEDOWN);
        var up = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_MIDDLEUP);
        SendInputs(down, up);
    }

    public void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not { Count: > 0 }) { MiddleClick(point); return; }
        var batch = WrapWithModifiers(modifiers, BuildModifiedClickBody(point, right: false, middle: true, doubleClick: false), includeAltMenuMask: true);
        SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
    }

    public void DoubleClick(PhysicalPoint point)
    {
        LeftClick(point);
        LeftClick(point);
    }

    public void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not { Count: > 0 }) { DoubleClick(point); return; }
        var batch = WrapWithModifiers(modifiers, BuildModifiedClickBody(point, right: false, middle: false, doubleClick: true), includeAltMenuMask: true);
        SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
    }

    public void MouseDown(PhysicalPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        SendSingleInput(input);
    }

    public void MouseUp(PhysicalPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendSingleInput(input);
    }

    public void Scroll(PhysicalPoint point, int deltaX, int deltaY)
    {
        deltaX = ClampScrollDelta(deltaX);
        deltaY = ClampScrollDelta(deltaY);

        if (deltaY != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_WHEEL);
            input.Union.Mouse.MouseData = deltaY * PlatformConstants.MouseWheelDelta;
            SendSingleInput(input);
        }
        if (deltaX != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_HWHEEL);
            input.Union.Mouse.MouseData = deltaX * PlatformConstants.MouseWheelDelta;
            SendSingleInput(input);
        }
    }

    public void ScrollAt(PhysicalPoint point, int deltaX, int deltaY)
    {
        // One SendInput batch: the leading absolute move puts the wheel's hit-test on the point,
        // so it lands there even though the OS cursor was left elsewhere (FreeLook / another
        // gaze-mouse). No modifier compensation is needed — the batch is mouse-only.
        var body = BuildModifiedWheelBody(point, deltaX, deltaY);
        SendRaw((uint)body.Length, body);
    }

    public void ScrollAtAndRestore(PhysicalPoint at, PhysicalPoint restore, int deltaX, int deltaY)
    {
        // One batch: the wheel is hit-tested at `at`, and the cursor is put back where it was in
        // the same SendInput call (the net position is unchanged; no other input can land between).
        var body = BuildWheelAtAndRestoreBody(at, restore, deltaX, deltaY);
        SendRaw((uint)body.Length, body);
    }

    public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not { Count: > 0 }) { Scroll(point, deltaX, deltaY); return; }
        var batch = WrapWithModifiers(modifiers, BuildModifiedWheelBody(point, deltaX, deltaY), includeAltMenuMask: true);
        SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
    }

    public void SendKeyPress(ushort virtualKeyCode)
    {
        SendKeyDown(virtualKeyCode);
        SendKeyUp(virtualKeyCode);
    }

    public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey)
    {
        uint flags = isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
        var down = CreateKeyboardInput(virtualKeyCode, flags);
        var up = CreateKeyboardInput(virtualKeyCode, flags | NativeMethods.KEYEVENTF_KEYUP);
        SendInputs(down, up);
    }

    public void SendKeyDown(ushort virtualKeyCode)
    {
        var input = CreateKeyboardInput(virtualKeyCode, 0);
        SendSingleInput(input);
    }

    public void SendKeyUp(ushort virtualKeyCode)
    {
        var input = CreateKeyboardInput(virtualKeyCode, NativeMethods.KEYEVENTF_KEYUP);
        SendSingleInput(input);
    }

    public void SendText(string text)
    {
        foreach (char c in text)
        {
            var down = new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Union = new NativeMethods.INPUTUNION
                {
                    Keyboard = new NativeMethods.KEYBDINPUT
                    {
                        VirtualKey = 0,
                        ScanCode = c,
                        Flags = NativeMethods.KEYEVENTF_UNICODE
                    }
                }
            };
            var up = down;
            up.Union.Keyboard.Flags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
            SendInputs(down, up);
        }
    }

    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode)
    {
        SendModifiedKeyPress(modifiers, virtualKeyCode, isExtendedKey: false);
    }

    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
    {
        var batch = BuildModifiedKeySequence(modifiers, virtualKeyCode, isExtendedKey);
        SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
    }

    /// <summary>
    /// Convert a character to its virtual key code and required modifiers
    /// using the current keyboard layout. The required modifiers indicate
    /// which keys must be held to produce this character (e.g., '!' requires Shift,
    /// AltGr characters require Ctrl+Alt on some layouts).
    /// Returns (0, empty) if the character cannot be mapped.
    /// </summary>
    public static (ushort VirtualKeyCode, IReadOnlyList<ModifierKey> RequiredModifiers) CharToVirtualKey(char c)
    {
        short result = NativeMethods.VkKeyScanW((ushort)c);
        if (result == -1) return (0, Array.Empty<ModifierKey>());

        ushort vk = (ushort)(result & 0xFF);
        byte modifierBits = (byte)((result >> 8) & 0xFF);
        var required = new List<ModifierKey>(3);

        if ((modifierBits & 0x01) != 0)
            required.Add(ModifierKey.Shift);
        if ((modifierBits & 0x02) != 0)
            required.Add(ModifierKey.Ctrl);
        if ((modifierBits & 0x04) != 0)
            required.Add(ModifierKey.Alt);

        return (vk, required);
    }

    private NativeMethods.INPUT CreateMouseInput(PhysicalPoint point, uint flags)
    {
        // Get screen dimensions
        var screenWidth = ScreenWidth;
        var screenHeight = ScreenHeight;

        // Use coordinate mapper to normalize coordinates
        var (normalizedX, normalizedY) = _coordinateMapper.NormalizeScreenCoordinate(point, screenWidth, screenHeight);

        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            Union = new NativeMethods.INPUTUNION
            {
                Mouse = new NativeMethods.MOUSEINPUT { Dx = normalizedX, Dy = normalizedY, Flags = flags }
            }
        };
    }

    private static NativeMethods.INPUT CreateKeyboardInput(ushort vk, uint flags)
    {
        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Union = new NativeMethods.INPUTUNION
            {
                Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = vk, Flags = flags }
            }
        };
    }

    internal static int ClampScrollDelta(int value)
    {
        // Clamp delta values to prevent overflow when multiplied by 120.
        // Max safe value: int.MaxValue / 120 = 17,895,697.
        const int maxSafeDelta = 17_000_000;
        return Math.Clamp(value, -maxSafeDelta, maxSafeDelta);
    }

    /// <summary>
    /// Unassigned virtual key injected as a down/up pair immediately before an Alt key-up in
    /// mouse batches, so Alt-up alone can never drop the foreground app into menu-bar mode
    /// (the AutoHotkey #MenuMaskKey technique).
    /// </summary>
    internal const ushort MenuMaskVirtualKey = 0xE8;

    internal static NativeMethods.INPUT[] BuildModifiedKeySequence(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
        => WrapWithModifiers(
            modifiers,
            new[]
            {
                CreateKeyboardInput(virtualKeyCode, isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
                CreateKeyboardInput(virtualKeyCode, (isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0) | NativeMethods.KEYEVENTF_KEYUP),
            },
            includeAltMenuMask: false);

    /// <summary>
    /// Wraps <paramref name="body"/> in one atomic INPUT batch: modifier key-downs in
    /// <see cref="BuildModifiedKeySequence"/>'s order (Ctrl, Alt, Win, Shift), the body, then
    /// modifier key-ups in reverse. When <paramref name="includeAltMenuMask"/> is set and Alt is
    /// in the set, an unassigned menu-mask key down/up pair is injected immediately before the
    /// Alt key-up (mouse batches only — a keyboard batch contains its own keystroke, so Alt-up
    /// cannot activate a menu there).
    /// </summary>
    internal static NativeMethods.INPUT[] WrapWithModifiers(
        IReadOnlySet<ModifierKey>? modifiers, NativeMethods.INPUT[] body, bool includeAltMenuMask)
    {
        var modifierVks = new List<ushort>();
        if (modifiers is not null)
        {
            if (modifiers.Contains(ModifierKey.Ctrl)) modifierVks.Add(NativeMethods.VK_CONTROL);
            if (modifiers.Contains(ModifierKey.Alt)) modifierVks.Add(NativeMethods.VK_MENU);
            if (modifiers.Contains(ModifierKey.Win)) modifierVks.Add(NativeMethods.VK_LWIN);
            if (modifiers.Contains(ModifierKey.Shift)) modifierVks.Add(NativeMethods.VK_SHIFT);
        }

        var batch = new List<NativeMethods.INPUT>(
            modifierVks.Count * 2 + body.Length + (modifierVks.Contains(NativeMethods.VK_MENU) && includeAltMenuMask ? 2 : 0));

        foreach (var vk in modifierVks)
        {
            uint downFlags = vk == NativeMethods.VK_LWIN ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;
            batch.Add(CreateKeyboardInput(vk, downFlags));
        }

        batch.AddRange(body);

        for (var i = modifierVks.Count - 1; i >= 0; i--)
        {
            var vk = modifierVks[i];
            // The mask pair rides IMMEDIATELY BEFORE the Alt key-up (spec: mask before the Alt-up,
            // wherever in the reversed order that lands).
            if (includeAltMenuMask && vk == NativeMethods.VK_MENU)
            {
                batch.Add(CreateKeyboardInput(MenuMaskVirtualKey, 0));
                batch.Add(CreateKeyboardInput(MenuMaskVirtualKey, NativeMethods.KEYEVENTF_KEYUP));
            }
            uint upFlags = vk == NativeMethods.VK_LWIN
                ? NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY
                : NativeMethods.KEYEVENTF_KEYUP;
            batch.Add(CreateKeyboardInput(vk, upFlags));
        }

        return batch.ToArray();
    }

    /// <summary>The mouse-event body of a modified click: one down/up pair, or two pairs for a double click (ONE batch — the exact shape a physical user produces). INSTANCE method: it goes through <see cref="CreateMouseInput"/>, which needs the coordinate mapper.</summary>
    internal NativeMethods.INPUT[] BuildModifiedClickBody(PhysicalPoint point, bool right, bool middle, bool doubleClick)
    {
        uint down = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE
            | (right ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : middle ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_LEFTDOWN);
        uint up = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE
            | (right ? NativeMethods.MOUSEEVENTF_RIGHTUP : middle ? NativeMethods.MOUSEEVENTF_MIDDLEUP : NativeMethods.MOUSEEVENTF_LEFTUP);

        if (!doubleClick) return new[] { CreateMouseInput(point, down), CreateMouseInput(point, up) };
        return new[] { CreateMouseInput(point, down), CreateMouseInput(point, up), CreateMouseInput(point, down), CreateMouseInput(point, up) };
    }

    /// <summary>The wheel-event body of a POINTED scroll — shared by the modified scroll and <see cref="ScrollAt"/>: a leading absolute MOVE (wheel events alone land at the current cursor, and ResetToFreeLook re-enables per-frame MoveCursor across a doubly-deferred dispatch), then the Y and/or X wheel legs with clamped deltas. INSTANCE method — same mapper reason.</summary>
    internal NativeMethods.INPUT[] BuildModifiedWheelBody(PhysicalPoint point, int deltaX, int deltaY)
    {
        var body = new List<NativeMethods.INPUT>
        {
            CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE),
        };
        deltaX = ClampScrollDelta(deltaX);
        deltaY = ClampScrollDelta(deltaY);
        if (deltaY != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_WHEEL);
            input.Union.Mouse.MouseData = deltaY * PlatformConstants.MouseWheelDelta;
            body.Add(input);
        }
        if (deltaX != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_HWHEEL);
            input.Union.Mouse.MouseData = deltaX * PlatformConstants.MouseWheelDelta;
            body.Add(input);
        }
        return body.ToArray();
    }

    /// <summary>
    /// Scroll mode's pointed hop: the pointed wheel body (absolute MOVE, then the Y and/or X wheel
    /// legs) with a trailing absolute MOVE back to <paramref name="restore"/>, so one atomic batch
    /// lands the wheel at <paramref name="at"/> without leaving the cursor there. INSTANCE method —
    /// same mapper reason.
    /// </summary>
    internal NativeMethods.INPUT[] BuildWheelAtAndRestoreBody(
        PhysicalPoint at, PhysicalPoint restore, int deltaX, int deltaY)
    {
        var body = BuildModifiedWheelBody(at, deltaX, deltaY).ToList();
        body.Add(CreateMouseInput(restore, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE));
        return body.ToArray();
    }

    /// <summary>
    /// After a PARTIAL insert (0 &lt; sent &lt; count), builds releases for every down whose up did
    /// not land inside the injected prefix: mouse-ups first, the menu-mask pair before a
    /// compensating Alt-up, then keyboard key-ups in reverse. A fully blocked batch (sent = 0)
    /// injected nothing and needs none.
    /// </summary>
    internal static NativeMethods.INPUT[] BuildCompensatingReleases(int sent, NativeMethods.INPUT[] batch)
    {
        if (sent <= 0 || sent >= batch.Length) return Array.Empty<NativeMethods.INPUT>();

        var heldKeys = new List<ushort>();
        var heldMouseUps = new List<uint>();
        for (var i = 0; i < sent; i++)
        {
            var input = batch[i];
            if (input.Type == NativeMethods.INPUT_KEYBOARD)
            {
                var vk = input.Union.Keyboard.VirtualKey;
                if ((input.Union.Keyboard.Flags & NativeMethods.KEYEVENTF_KEYUP) != 0) heldKeys.Remove(vk);
                else heldKeys.Add(vk);
            }
            else if (input.Type == NativeMethods.INPUT_MOUSE)
            {
                uint downMask = NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_RIGHTDOWN | NativeMethods.MOUSEEVENTF_MIDDLEDOWN;
                uint upMask = NativeMethods.MOUSEEVENTF_LEFTUP | NativeMethods.MOUSEEVENTF_RIGHTUP | NativeMethods.MOUSEEVENTF_MIDDLEUP;
                if ((input.Union.Mouse.Flags & downMask) != 0) heldMouseUps.Add(input.Union.Mouse.Flags & downMask);
                else if ((input.Union.Mouse.Flags & upMask) != 0 && heldMouseUps.Count > 0) heldMouseUps.RemoveAt(heldMouseUps.Count - 1);
            }
        }

        if (heldKeys.Count == 0 && heldMouseUps.Count == 0) return Array.Empty<NativeMethods.INPUT>();

        var releases = new List<NativeMethods.INPUT>();
        foreach (var downFlags in heldMouseUps)
        {
            uint upFlags = downFlags == NativeMethods.MOUSEEVENTF_LEFTDOWN ? NativeMethods.MOUSEEVENTF_LEFTUP
                : downFlags == NativeMethods.MOUSEEVENTF_RIGHTDOWN ? NativeMethods.MOUSEEVENTF_RIGHTUP
                : NativeMethods.MOUSEEVENTF_MIDDLEUP;
            releases.Add(new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                Union = new NativeMethods.INPUTUNION { Mouse = new NativeMethods.MOUSEINPUT { Flags = upFlags } },
            });
        }

        if (heldKeys.Contains(NativeMethods.VK_MENU))
        {
            releases.Add(CreateKeyboardInput(MenuMaskVirtualKey, 0));
            releases.Add(CreateKeyboardInput(MenuMaskVirtualKey, NativeMethods.KEYEVENTF_KEYUP));
        }

        for (var i = heldKeys.Count - 1; i >= 0; i--)
            releases.Add(CreateKeyboardInput(heldKeys[i], NativeMethods.KEYEVENTF_KEYUP));

        return releases.ToArray();
    }

    /// <summary>Instance path: build the releases, send them, and log a failure on its OWN line — the partial-send warning above is rate-limited and would swallow it.</summary>
    private void SendCompensatingReleases(int sent, NativeMethods.INPUT[] batch)
    {
        var releases = BuildCompensatingReleases(sent, batch);
        if (releases.Length == 0) return;
        var sentReleases = SendRaw((uint)releases.Length, releases);
        if (sentReleases < releases.Length)
            _logger?.LogWarning(
                "Modifier-recovery send injected {Sent}/{Count} events — a modifier may remain held down after a partial SendInput",
                sentReleases, releases.Length);
    }

    private static readonly int s_inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    // MoveCursor runs per gaze frame (30-60 Hz); a [ThreadStatic] one-element buffer
    // makes the single-input path allocation-free. ThreadStatic (not a shared static)
    // because clicks can be sent from the UI thread while gaze moves on the poll thread.
    [ThreadStatic]
    private static NativeMethods.INPUT[]? t_singleInput;

    // Rate limit for blocked-injection logging: a UIPI block (elevated foreground window) is
    // persistent, so an unbounded log would fire per frame. UI-thread and gaze-thread callers
    // share this instance field; the worst race is one extra line.
    private long _lastBlockedLogTicks;

    private void SendSingleInput(NativeMethods.INPUT input)
    {
        var buffer = t_singleInput ??= new NativeMethods.INPUT[1];
        buffer[0] = input;
        SendRaw(1, buffer);
    }

    private void SendInputs(params NativeMethods.INPUT[] inputs) =>
        SendRaw((uint)inputs.Length, inputs);

    /// <summary>
    /// The single place <c>SendInput</c> is called, exposed to tests so GA2/GB2 can record the
    /// batch shape without injecting real input. Production leaves it null and calls
    /// <see cref="NativeMethods.SendInput"/> directly.
    /// </summary>
    internal Func<uint, NativeMethods.INPUT[], int, int>? SendInputOverride { get; set; }

    private int SendRaw(uint count, NativeMethods.INPUT[] inputs)
    {
        var sent = SendInputOverride is { } send
            ? send(count, inputs, s_inputSize)
            : (int)NativeMethods.SendInput(count, inputs, s_inputSize);
        if (sent == count) return (int)sent;

        // SendInput does not throw on a block: it returns 0 when the foreground process has
        // higher integrity (elevated windows, the secure desktop). Every event is then silently
        // dropped, and for a gaze-only user "nothing happens" is indistinguishable from
        // "paused" without this log. Rate-limited above.
        var now = Environment.TickCount64;
        if (now - _lastBlockedLogTicks > 30_000)
        {
            _lastBlockedLogTicks = now;
            _logger?.LogWarning(
                "SendInput injected {Sent}/{Count} events (Win32 error {ErrorCode}) — input into elevated or secure-desktop windows is blocked by Windows",
                sent, count, Marshal.GetLastWin32Error());
        }
        return (int)sent;
    }
}
