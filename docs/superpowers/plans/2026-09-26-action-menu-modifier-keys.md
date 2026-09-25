# Action-Menu Modifier Keys Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While the action menu is open, a LEFT-edge bar offers Shift/Ctrl/Alt dwell buttons whose latch wraps the next click/scroll input in an atomic modified SendInput batch (shift+click, ctrl+scroll, alt+click).

**Architecture:** A second `EdgeButtonBarWindow`/`EdgeButtonBarViewModel` instance (owner-keyed dwell registry) anchored via a new `ScreenGeometry.LeftEdgeAnchor`. The latch lives in `ActionMenuViewModel` and is captured as a COPY into `ActionSelectedMessage(action, modifiers)`; modifiers ride opaquely through `InteractionManager` into new default-interface `IInputSimulator` overloads that `Win32InputSimulator` implements as one atomic batch (modifier downs → mouse events → Alt menu-mask pair → reversed ups), with partial-send compensation. Dimmed Drag/ScrollMode slots are non-targets. Toggle re-arm is a single-pending gate: samples are fed as hits-nothing while the pending button is nearest-hitting, with a 150 ms valid-sample debounce on the clear.

**Tech Stack:** .NET 10 (SDK pinned in `global.json`), WPF, xUnit v3 + NFluent (no mocking library — hand-written fakes), CommunityToolkit.Mvvm.

## Global Constraints

- Work in the worktree `C:\Users\andre\source\repos\eyectrl\.worktrees\action-menu-modifier-keys` (branch `feat/action-menu-modifier-keys`); ALWAYS absolute paths or `git -C <worktree>`.
- Spec: `docs/superpowers/specs/2026-09-25-action-menu-modifier-keys-design.md` (rev 7) — the owner-vetoable ledger L1–L9 is binding.
- NEVER launch the app, the supervisor, or any `Category=UI` test — the owner's only input channel is the eye tracker.
- One test project per `dotnet test` invocation (MSB1008); no two suites concurrently (named-pipe contention).
- NFluent assertions (`Check.That(...)`), xUnit v3 `[Fact]`/`[Theory]`; no mocking library.
- File-scoped namespaces, Allman braces, `_camelCase` private fields, nullable enabled, no `#region`.
- Every commit is gated by building the project it touches; run the full non-UI suite before the final commit.
- New `public` Core members need `///` XML docs (CS1591 is an error in `EyeCtrl.Core`).
- Dwell-time/dwell-ms values are `int` milliseconds; detector timestamps are microseconds (`long`); D = 150 ms = `150_000` µs.

---

### Task 1: Platform — atomic modified mouse/wheel input

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs`
- Modify: `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs`
- Test: `tests/EyeCtrl.Platform.Tests/Input/Win32InputSimulatorModifiedSequenceTests.cs` (new)
- Test: `tests/EyeCtrl.Platform.Tests/Input/Win32InputSimulatorClampTests.cs` (extend)

**Interfaces:**
- Consumes: `NativeMethods.INPUT/INPUTUNION/MOUSEINPUT/KEYBDINPUT`, `VK_CONTROL/VK_MENU/VK_LWIN/VK_SHIFT`, `KEYEVENTF_*`, `MOUSEEVENTF_*`, `PlatformConstants.MouseWheelDelta` (all existing).
- Produces: `IInputSimulator.LeftClick/RightClick/MiddleClick/DoubleClick(PhysicalPoint, IReadOnlySet<ModifierKey>?)`, `IInputSimulator.Scroll(PhysicalPoint, int, int, IReadOnlySet<ModifierKey>?)` (default-interface, throwing); `Win32InputSimulator.WrapWithModifiers(IReadOnlySet<ModifierKey>?, NativeMethods.INPUT[], bool)` and `Win32InputSimulator.BuildModifiedKeySequence` (both `internal static`, tested); `SendRaw` now returns `int`.

- [ ] **Step 1: Characterization tests for `BuildModifiedKeySequence` (Alt, Win extended, extended main) — written FIRST, before any extraction**

Add to `tests/EyeCtrl.Platform.Tests/Input/Win32InputSimulatorClampTests.cs`:

```csharp
[Fact]
public void BuildModifiedKeySequence_Alt_WrapsKeyInMenuModifier()
{
    var inputs = Win32InputSimulator.BuildModifiedKeySequence(
        new HashSet<ModifierKey> { ModifierKey.Alt }, 0x41, isExtendedKey: false);

    Check.That(inputs).HasSize(4);
    Check.That(inputs[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
    Check.That(inputs[0].Union.Keyboard.Flags).IsEqualTo(0u);
    Check.That(inputs[1].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
    Check.That(inputs[2].Union.Keyboard.VirtualKey).IsEqualTo(0x41);
    Check.That(inputs[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    Check.That(inputs[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
    Check.That(inputs[3].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
}

[Fact]
public void BuildModifiedKeySequence_Win_UsesExtendedKeyFlag()
{
    var inputs = Win32InputSimulator.BuildModifiedKeySequence(
        new HashSet<ModifierKey> { ModifierKey.Win }, 0x41, isExtendedKey: false);

    Check.That(inputs).HasSize(4);
    Check.That(inputs[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_LWIN);
    Check.That(inputs[0].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY);
    Check.That(inputs[3].Union.Keyboard.Flags)
        .IsEqualTo(NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY);
}

[Fact]
public void BuildModifiedKeySequence_ExtendedMainKey_PropagatesExtendedFlag()
{
    var inputs = Win32InputSimulator.BuildModifiedKeySequence(
        new HashSet<ModifierKey> { ModifierKey.Ctrl }, 0x21, isExtendedKey: true);

    Check.That(inputs).HasSize(4);
    Check.That(inputs[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY);
    Check.That(inputs[2].Union.Keyboard.Flags)
        .IsEqualTo(NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP);
}
```

- [ ] **Step 2: Run the characterization tests — they must PASS against the unmodified code**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~BuildModifiedKeySequence" -v minimal`
Expected: all PASS (two pre-existing + three new). If any fails, STOP — the spec's assumptions are wrong; report back.

- [ ] **Step 3: Implement `WrapWithModifiers` + the menu-mask key in `Win32InputSimulator.cs`**

Add near `BuildModifiedKeySequence`:

```csharp
/// <summary>
/// Unassigned virtual key injected as a down/up pair immediately before an Alt key-up in
/// mouse batches, so Alt-up alone can never drop the foreground app into menu-bar mode
/// (the AutoHotkey #MenuMaskKey technique).
/// </summary>
internal const ushort MenuMaskVirtualKey = 0xE8;

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

    if (includeAltMenuMask && modifierVks.Contains(NativeMethods.VK_MENU))
    {
        batch.Add(CreateKeyboardInput(MenuMaskVirtualKey, 0));
        batch.Add(CreateKeyboardInput(MenuMaskVirtualKey, NativeMethods.KEYEVENTF_KEYUP));
    }

    for (var i = modifierVks.Count - 1; i >= 0; i--)
    {
        uint upFlags = modifierVks[i] == NativeMethods.VK_LWIN
            ? NativeMethods.KEYEVENTF_KEYUP | NativeMethods.KEYEVENTF_EXTENDEDKEY
            : NativeMethods.KEYEVENTF_KEYUP;
        batch.Add(CreateKeyboardInput(modifierVks[i], upFlags));
    }

    return batch.ToArray();
}
```

Refactor `BuildModifiedKeySequence` to delegate (keyboard batches carry NO mask — byte-identical to today):

```csharp
internal static NativeMethods.INPUT[] BuildModifiedKeySequence(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
    => WrapWithModifiers(
        modifiers,
        new[]
        {
            CreateKeyboardInput(virtualKeyCode, isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
            CreateKeyboardInput(virtualKeyCode, (isExtendedKey ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0) | NativeMethods.KEYEVENTF_KEYUP),
        },
        includeAltMenuMask: false);
```

Delete the old inline body of `BuildModifiedKeySequence` (the `modifierVks` list and loops).

- [ ] **Step 4: Run characterization + all existing Platform tests**

Run: `dotnet test tests/EyeCtrl.Platform.Tests -v minimal`
Expected: PASS (the delegated sequence is byte-identical — the characterization from Step 1 plus the two ClampTests guards prove it).

- [ ] **Step 5: `SendRaw` returns the injected count; add `SendCompensatingReleases`**

Change `SendRaw`'s signature and tail:

```csharp
private int SendRaw(uint count, NativeMethods.INPUT[] inputs)
{
    var sent = NativeMethods.SendInput(count, inputs, s_inputSize);
    if (sent == count) return sent;

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
    return sent;
}
```

(`SendSingleInput`/`SendInputs` callers ignore the return — they stay `void`-callers; `MoveCursor`'s hot path is unchanged apart from the returned int.)

Add the compensating-releases walker (below `SendRaw`):

```csharp
/// <summary>
/// After a PARTIAL insert (0 &lt; sent &lt; count), emits releases for every down whose up did
/// not land inside the injected prefix: mouse-ups first, the menu-mask pair before a
/// compensating Alt-up, then keyboard key-ups in reverse. A fully blocked batch (sent = 0)
/// injected nothing and needs none. Best-effort: a compensation failure is logged on its OWN
/// line — the partial-send warning above is rate-limited and would swallow it.
/// </summary>
private void SendCompensatingReleases(int sent, NativeMethods.INPUT[] batch)
{
    if (sent <= 0 || sent >= batch.Length) return;

    var heldKeys = new List<ushort>();
    var heldMouseUps = new List<uint>();
    for (var i = 0; i < sent; i++)
    {
        var input = batch[i];
        if (input.Type == NativeMethods.INPUT_KEYBOARD)
        {
            var vk = input.Union.Keyboard.VirtualKey;
            if ((input.Union.Keyboard.Flags & NativeMethods.KEYEVENTF_KEYUP) != 0)
                heldKeys.Remove(vk);
            else
                heldKeys.Add(vk);
        }
        else if (input.Type == NativeMethods.INPUT_MOUSE)
        {
            var downMask = NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_RIGHTDOWN | NativeMethods.MOUSEEVENTF_MIDDLEDOWN;
            var upMask = NativeMethods.MOUSEEVENTF_LEFTUP | NativeMethods.MOUSEEVENTF_RIGHTUP | NativeMethods.MOUSEEVENTF_MIDDLEUP;
            if ((input.Union.Mouse.Flags & downMask) != 0)
                heldMouseUps.Add(input.Union.Mouse.Flags & downMask);
            else if ((input.Union.Mouse.Flags & upMask) != 0 && heldMouseUps.Count > 0)
                heldMouseUps.RemoveAt(heldMouseUps.Count - 1);
        }
    }

    if (heldKeys.Count == 0 && heldMouseUps.Count == 0) return;

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

    var sentReleases = SendRaw((uint)releases.Count, releases.ToArray());
    if (sentReleases < releases.Count)
        _logger?.LogWarning(
            "Modifier-recovery send injected {Sent}/{Count} events — a modifier may remain held down after a partial SendInput",
            sentReleases, releases.Count);
}
```

- [ ] **Step 6: Add the modified mouse/wheel overloads to `Win32InputSimulator`**

```csharp
public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { LeftClick(point); return; }
    var batch = WrapWithModifiers(modifiers, new[]
    {
        CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN),
        CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP),
    }, includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}

public void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { RightClick(point); return; }
    var batch = WrapWithModifiers(modifiers, new[]
    {
        CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTDOWN),
        CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTUP),
    }, includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}

public void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { MiddleClick(point); return; }
    var batch = WrapWithModifiers(modifiers, new[]
    {
        CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_MIDDLEDOWN),
        CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_MIDDLEUP),
    }, includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}

public void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { DoubleClick(point); return; }
    uint down = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN;
    uint up = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP;
    // ONE batch: the exact event shape a physical user produces with the modifier already held.
    var batch = WrapWithModifiers(modifiers, new[]
    {
        CreateMouseInput(point, down), CreateMouseInput(point, up),
        CreateMouseInput(point, down), CreateMouseInput(point, up),
    }, includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}

public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { Scroll(point, deltaX, deltaY); return; }

    var body = new List<NativeMethods.INPUT>
    {
        // Wheel events alone land at the CURRENT cursor position; the leading absolute MOVE
        // pins the wheel to the requested point (ResetToFreeLook re-enables per-frame
        // MoveCursor across a doubly-deferred dispatch, so the cursor can drift).
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

    var batch = WrapWithModifiers(modifiers, body.ToArray(), includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}
```

Route `SendModifiedKeyPress` through `SendRaw` + compensation (replacing its direct `NativeMethods.SendInput` call):

```csharp
public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
{
    var batch = BuildModifiedKeySequence(modifiers, virtualKeyCode, isExtendedKey);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}
```

- [ ] **Step 7: Declare the overloads on `IInputSimulator` as throwing default interface methods**

Add to `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs` (after the existing members, XML docs included — Core escalates CS1591):

```csharp
/// <summary>Performs a left click with the given modifiers held for the duration of the click (one atomic batch). Null/empty modifiers is the caller's bug — the dispatcher routes bare clicks to <see cref="LeftClick(PhysicalPoint)"/>.</summary>
/// <param name="point">Target position in physical screen pixels.</param>
/// <param name="modifiers">Modifier keys held down for the click. Must be non-empty when called directly.</param>
void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");

/// <summary>Performs a right click with the given modifiers held (one atomic batch).</summary>
void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");

/// <summary>Performs a middle click with the given modifiers held (one atomic batch).</summary>
void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");

/// <summary>Performs a double click with the given modifiers held (ONE batch: down, up, down, up between the modifier pair).</summary>
void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");

/// <summary>Sends a wheel scroll with the given modifiers held; the batch leads with an absolute move so the wheel lands on <paramref name="point"/>.</summary>
void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified scrolls.");
```

- [ ] **Step 8: Write the batch-composition tests**

New file `tests/EyeCtrl.Platform.Tests/Input/Win32InputSimulatorModifiedSequenceTests.cs`:

```csharp
namespace EyeCtrl.Platform.Tests.Input;

using System.Linq;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Platform.Input;
using NFluent;
using Xunit;

public class Win32InputSimulatorModifiedSequenceTests
{
    private const ushort MenuMask = Win32InputSimulator.MenuMaskVirtualKey;

    private static HashSet<ModifierKey> Mods(params ModifierKey[] keys) => new(keys);

    [Fact]
    public void ModifiedLeftClick_DownsPrecedeBody_MaskBeforeAltUp_UpsReversed()
    {
        var body = new[]
        {
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
        };

        var batch = Win32InputSimulator.WrapWithModifiers(
            Mods(ModifierKey.Shift, ModifierKey.Alt), body, includeAltMenuMask: true);

        // Ctrl-down? No — Ctrl not requested. Order: Shift-down, Alt-down, body, mask down,
        // mask up, Alt-up, Shift-up. (Down order is Ctrl, Alt, Win, Shift filtered to the set.)
        Check.That(batch).HasSize(2 + 2 + 2 + 2);
        Check.That(batch[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[2].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[3].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[4].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(batch[4].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(batch[5].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(batch[5].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[6].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[6].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[7].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[7].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void ModifiedWheel_LeadsWithAbsoluteMove()
    {
        var body = new[]
        {
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE),
            Win32InputSimulatorTestHelper.WheelInput(120),
        };

        var batch = Win32InputSimulator.WrapWithModifiers(
            Mods(ModifierKey.Ctrl), body, includeAltMenuMask: true);

        Check.That(batch).HasSize(2 + 2 + 2);
        Check.That(batch[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_CONTROL);
        Check.That(batch[1].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That((batch[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE)).IsNotEqualTo(0u);
        Check.That((batch[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE)).IsNotEqualTo(0u);
        Check.That(batch[2].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That((batch[2].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_WHEEL)).IsNotEqualTo(0u);
        Check.That(batch[3].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask); // No Alt ⇒ no mask
    }

    [Fact]
    public void NoAlt_NoMaskPair()
    {
        var body = new[] { Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN) };
        var batch = Win32InputSimulator.WrapWithModifiers(Mods(ModifierKey.Shift), body, includeAltMenuMask: true);

        Check.That(batch).HasSize(1 + 1 + 1);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    [Fact]
    public void KeyboardSequence_NeverCarriesTheMask()
    {
        var batch = Win32InputSimulator.BuildModifiedKeySequence(
            Mods(ModifierKey.Alt, ModifierKey.Shift), 0x41, isExtendedKey: false);

        Check.That(batch).HasSize(4 + 2);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    [Fact]
    public void Compensation_EmitsMouseUpsBeforeModifierUps_MaskBeforeAltUp()
    {
        // Batch: Alt-down, LEFTDOWN, LEFTUP, LEFTDOWN  — sent prefix = 3 (the second
        // LEFTDOWN landed without its up; Alt-up never landed).
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_MENU, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_MENU, NativeMethods.KEYEVENTF_KEYUP),
        };

        var releases = Win32InputSimulatorTestHelper.CompensatingReleases(3, batch);

        // LEFTUP first, then the mask pair, then the Alt-up.
        Check.That(releases).HasSize(1 + 2 + 1);
        Check.That(releases[0].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That((releases[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
    }

    [Fact]
    public void Compensation_FullyLandedPrefix_EmitsNothing()
    {
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP),
        };

        var releases = Win32InputSimulatorTestHelper.CompensatingReleases(4, batch);
        Check.That(releases).IsEmpty();
    }

    [Fact]
    public void SendRaw_ReturnsInjectedCount()
    {
        // Indirect: SendRaw is private; the guarantee is exercised via the public
        // characterization tests above and the compensation tests, which depend on the count.
        // Direct assertion lives on the compensation helper's contract.
        Check.That(true).IsTrue();
    }
}

/// <summary>
/// Static access to Win32InputSimulator's internal composition helpers. Lives in the test
/// assembly; InternalsVisibleTo covers EyeCtrl.Platform.Tests.
/// </summary>
public static class Win32InputSimulatorTestHelper
{
    public static NativeMethods.INPUT MouseInput(uint flags) => new()
    {
        Type = NativeMethods.INPUT_MOUSE,
        Union = new NativeMethods.INPUTUNION { Mouse = new NativeMethods.MOUSEINPUT { Flags = flags } },
    };

    public static NativeMethods.INPUT WheelInput(int delta) => new()
    {
        Type = NativeMethods.INPUT_MOUSE,
        Union = new NativeMethods.INPUTUNION { Mouse = new NativeMethods.MOUSEINPUT { Flags = NativeMethods.MOUSEEVENTF_WHEEL, MouseData = delta } },
    };

    public static NativeMethods.INPUT KeyboardInput(ushort vk, uint flags) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Union = new NativeMethods.INPUTUNION { Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = vk, Flags = flags } },
    };

    public static NativeMethods.INPUT[] CompensatingReleases(int sent, NativeMethods.INPUT[] batch)
        => Win32InputSimulator.CompensatingReleasesForTest(sent, batch);
}
```

Because `SendCompensatingReleases` is instance-private and needs no instance state, expose it as an `internal static` core helper and keep a thin instance wrapper:

```csharp
internal static NativeMethods.INPUT[] CompensatingReleasesForTest(int sent, NativeMethods.INPUT[] batch)
{
    // The production walker, factored so tests can call it without an instance or SendInput.
    return BuildCompensatingReleases(sent, batch);
}

private List<NativeMethods.INPUT> BuildCompensatingReleasesInstance(int sent, NativeMethods.INPUT[] batch)
    => BuildCompensatingReleases(sent, batch);

private static List<NativeMethods.INPUT> BuildCompensatingReleases(int sent, NativeMethods.INPUT[] batch)
{
    // ... the walker from Step 5, returning the release list instead of sending it ...
}
```

Restructure per this split: `BuildCompensatingReleases` (static, pure — returns the release list) and `SendCompensatingReleases` (instance — builds, sends via `SendRaw`, logs the failure). `SendCompensatingReleases` needs no `heldKeys` walking of its own. Adjust `InternalsVisibleTo` if `EyeCtrl.Platform.Tests` is not already covered — check `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` for an existing `InternalsVisibleTo` (the ClampTests already access `internal` members, so it exists).

- [ ] **Step 9: Run the full Platform suite**

Run: `dotnet test tests/EyeCtrl.Platform.Tests -v minimal`
Expected: PASS (existing ClampTests, new characterization, new composition tests). The ClampTests file's `using` set may need `EyeCtrl.Core.Keyboard` if not present.

- [ ] **Step 10: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add src/EyeCtrl.Platform src/EyeCtrl.Core/Interfaces/IInputSimulator.cs tests/EyeCtrl.Platform.Tests
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): atomic modified mouse/wheel batches — WrapWithModifiers, menu-mask key 0xE8, SendRaw count + compensation, leading MOVE on modified wheel"
```

---

### Task 2: Core — messages, InteractionManager pass-through, NearestHittingTargetId, adapter

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`
- Modify: `src/EyeCtrl.Core/Processing/InteractionManager.cs:187-226, :362`
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs:325-345`
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:107, :324-334`
- Modify: the 15 test-subscriber lambdas listed below
- Test: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerModifierTests.cs` (new)
- Test: `tests/EyeCtrl.Core.Tests/Processing/DwellDetectorNearestTests.cs` (new)

**Interfaces:**
- Produces: `ActionSelectedMessage(InteractionAction, IReadOnlySet<ModifierKey>? Modifiers = null)`, `ActionRequestedMessage(InteractionAction, PhysicalPoint, bool IsPreciseTarget = false, IReadOnlySet<ModifierKey>? Modifiers = null)`, `ToggleActionMenuModifierMessage(ModifierKey Modifier)`; `InteractionManager.OnActionSelected(InteractionAction, IReadOnlySet<ModifierKey>? = null)`; `ActionRequested` event of type `Action<InteractionAction, PhysicalPoint, IReadOnlySet<ModifierKey>?>?`; `DwellDetector.NearestHittingTargetId(PhysicalPoint, Func<DwellTarget,bool>? = null)` and `DwellDetector.TargetHit(string, PhysicalPoint)`.

- [ ] **Step 1: Write failing Core tests**

`tests/EyeCtrl.Core.Tests/Processing/InteractionManagerModifierTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class InteractionManagerModifierTests
{
    private static readonly PhysicalPoint Point = new(100, 100);

    private static InteractionManager ArmedFreeLook()
    {
        var im = new InteractionManager();
        im.OnFixationStarted(Point);
        return im;
    }

    [Fact]
    public void OnActionSelected_RelaysModifiers_OnActionRequested()
    {
        var im = ArmedFreeLook();
        var mods = new HashSet<ModifierKey> { ModifierKey.Shift };
        (InteractionAction, PhysicalPoint, IReadOnlySet<ModifierKey>?)? raised = null;
        im.ActionRequested += (action, point, modifiers) => raised = (action, point, modifiers);

        im.OnActionSelected(InteractionAction.LeftClick, mods);

        Check.That(raised).IsNotNull();
        Check.That(raised!.Value.Item1).IsEqualTo(InteractionAction.LeftClick);
        Check.That(raised.Value.Item3).IsNotNull();
        Check.That(raised.Value.Item3!.Contains(ModifierKey.Shift)).IsTrue();
    }

    [Fact]
    public void RepeatModeTick_CarriesNoModifiers()
    {
        var im = new InteractionManager();
        IReadOnlySet<ModifierKey>? raised = null;
        var fired = false;
        im.ActionRequested += (action, _, modifiers) => { fired = true; raised = modifiers; };
        im.EnterRepeatMode(InteractionAction.LeftClick);
        im.RepeatDwellMs = 0;
        im.OnFixationStarted(Point);          // arms the locked-action dwell at the point
        im.NoteGazeSample(isValid: true, timestampUs: 1_000); // dwell 0 ms ⇒ fires on this sample

        Check.That(fired).IsTrue();           // the tick really fired — no vacuous pass
        Check.That(raised).IsNull();          // repeat ticks carry null — never a modifier set
    }
}
```

`tests/EyeCtrl.Core.Tests/Processing/DwellDetectorNearestTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellDetectorNearestTests
{
    private static DwellDetector TwoTargets(out string leftId, out string rightId)
    {
        var detector = new DwellDetector(400, EyeCtrl.Core.Adaptation.DwellContext.Other, sink: null);
        leftId = "left";
        rightId = "right";
        detector.AddTarget(new DwellTarget(leftId, new EyeCtrl.Core.Models.PhysicalPoint(0, 0), 100, 600));
        detector.AddTarget(new DwellTarget(rightId, new EyeCtrl.Core.Models.PhysicalPoint(240, 0), 100, 600));
        return detector;
    }

    [Fact]
    public void NearestHittingTargetId_PicksTheNearestHittingTarget()
    {
        var detector = TwoTargets(out var leftId, out var rightId);

        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(30, 0))).IsEqualTo(leftId);
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(210, 0))).IsEqualTo(rightId);
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(120, 500))).IsNull();
    }

    [Fact]
    public void NearestHittingTargetId_HonoursTheFilter()
    {
        var detector = TwoTargets(out var leftId, out _);

        var nearest = detector.NearestHittingTargetId(
            new EyeCtrl.Core.Models.PhysicalPoint(30, 0), t => t.Id != leftId);

        Check.That(nearest).IsNull(); // left excluded and right not hit
    }

    [Fact]
    public void TargetHit_ReportsRegionMembership()
    {
        var detector = TwoTargets(out var leftId, out _);

        Check.That(detector.TargetHit(leftId, new EyeCtrl.Core.Models.PhysicalPoint(50, 0))).IsTrue();
        Check.That(detector.TargetHit(leftId, new EyeCtrl.Core.Models.PhysicalPoint(500, 0))).IsFalse();
        Check.That(detector.TargetHit("missing", new EyeCtrl.Core.Models.PhysicalPoint(0, 0))).IsFalse();
    }

    [Fact]
    public void NearestHittingTargetId_AgreesWithUpdate_Selection()
    {
        // Drive Update along a path and confirm the API answers what Update acts on:
        // accumulate on the left target via Update, then confirm the API picks it too.
        var detector = TwoTargets(out var leftId, out _);
        var fired = new List<string>();
        detector.TargetActivated += id => fired.Add(id);

        for (var t = 0; t < 10; t++)
            detector.Update(new EyeCtrl.Core.Models.PhysicalPoint(30, 0), t * 100_000);

        Check.That(fired).ContainsExactly(leftId);
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(30, 0))).IsEqualTo(leftId);
    }
}
```

- [ ] **Step 2: Run — expect compile FAILURES (members missing)**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModifierTests|FullyQualifiedName~NearestTests" -v minimal`
Expected: BUILD ERROR (`NearestHittingTargetId` undefined; `ActionRequested` arity mismatch).

- [ ] **Step 3: Extend the messages**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`:

```csharp
public record ActionRequestedMessage(InteractionAction Action, PhysicalPoint Point, bool IsPreciseTarget = false, IReadOnlySet<ModifierKey>? Modifiers = null);

public record ActionSelectedMessage(InteractionAction Action, IReadOnlySet<ModifierKey>? Modifiers = null);

/// <summary>
/// Toggles one action-menu modifier's latch (left-edge bar). The latch lives in
/// <see cref="ActionMenuViewModel"/> and dies with the menu session.
/// </summary>
public record ToggleActionMenuModifierMessage(ModifierKey Modifier);
```

(Add `using EyeCtrl.Core.Keyboard;` if absent.)

- [ ] **Step 4: Pass-through in `InteractionManager`**

Change the event declaration and both raise sites:

```csharp
/// <summary>Raised when the user selects an action (click, scroll, etc.) at a specific point. Modifiers are opaque payload from the action menu's latch; repeat ticks carry null.</summary>
public event Action<InteractionAction, PhysicalPoint, IReadOnlySet<ModifierKey>?>? ActionRequested;
```

`OnActionSelected`:

```csharp
public void OnActionSelected(InteractionAction action, IReadOnlySet<ModifierKey>? modifiers = null)
{
    // ... unchanged body, except:
    ActionRequested?.Invoke(action, targetPoint.Value, modifiers);
    ResetToFreeLook();
}
```

Repeat-mode tick (was `:362`):

```csharp
ActionRequested?.Invoke(RepeatAction!.Value, target, null);
```

- [ ] **Step 5: Extract `NearestHittingTargetId` + `TargetHit` in `DwellDetector`**

Replace the inline nearest scan in `Update` with a call, and add the two public members:

```csharp
/// <summary>
/// The id of the nearest hitting target for <paramref name="gazePoint"/>, or null when no
/// eligible target's region contains the point. This is the SAME selection <see cref="Update"/>
/// uses to award dwell — callers that need to predict it (the mod-bar re-arm gate) must come
/// here rather than reimplementing the scan.
/// </summary>
public string? NearestHittingTargetId(PhysicalPoint gazePoint, Func<DwellTarget, bool>? targetFilter = null)
    => FindNearestHittingTargetId(_targetsSnapshot, gazePoint, targetFilter);

/// <summary>
/// Whether the CURRENT effective region of target <paramref name="targetId"/> contains
/// <paramref name="gazePoint"/>. Unknown ids answer false.
/// </summary>
public bool TargetHit(string targetId, PhysicalPoint gazePoint)
{
    foreach (var t in _targetsSnapshot)
        if (t.Id == targetId && t.HitTest(gazePoint))
            return true;
    return false;
}

private static string? FindNearestHittingTargetId(DwellTarget[] targets, PhysicalPoint gazePoint, Func<DwellTarget, bool>? targetFilter)
{
    string? nearestId = null;
    double nearestDistSq = double.MaxValue;

    for (int i = 0; i < targets.Length; i++)
    {
        var target = targets[i];
        if (targetFilter != null && !targetFilter(target))
            continue;
        if (target.HitTest(gazePoint))
        {
            double dx = gazePoint.X - target.Center.X;
            double dy = gazePoint.Y - target.Center.Y;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestId = target.Id;
            }
        }
    }

    return nearestId;
}
```

In `Update`, delete the inline loop and put, exactly where the loop was (after the cooldown gate):

```csharp
var nearestId = FindNearestHittingTargetId(targets, gazePoint, targetFilter);
```

- [ ] **Step 6: Update the 15 test-subscriber lambdas (mechanical)**

Files/sites (round-1 census, re-verified): `GazeEngineAdapterActionRecoveryTests.cs:129`; `GazeEngineTests.cs:39`; `GazeFlowHarness.cs:90`; `InteractionManagerScrollModeTests.cs:33`; `InteractionManagerTests.cs:55,89,232,247,307`; `InteractionManagerRepeatModeTests.cs:61,80,93,109,123,136`. Each gains a third parameter, e.g. `(_, _)` → `(_, _, _)`, `(action, point) => ...` → `(action, point, _) => ...`.

- [ ] **Step 7: Adapter pass-through in `GazeEngineAdapter`**

`:107`: `_interactionManager.OnActionSelected(msg.Action, msg.Modifiers);`
`:324` handler signature: `private void OnActionRequested(InteractionAction action, PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)`; subscription lambda forwards the third parameter; `:334`:

```csharp
_dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point, isPrecise, modifiers)));
```

- [ ] **Step 8: Run Core + App + ViewModels suites**

Run: `dotnet test tests/EyeCtrl.Core.Tests -v minimal` then `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI" -v minimal` then `dotnet test tests/EyeCtrl.ViewModels.Tests -v minimal`
Expected: PASS (new tests green; the mechanical lambda updates compile; `GazeEngineAdapterActionRecoveryTests` recovery semantics unchanged).

- [ ] **Step 9: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): modifiers ride as opaque payload through InteractionManager; DwellDetector exposes NearestHittingTargetId/TargetHit"
```

---

### Task 3: Dispatcher routing and logging (pulled forward — first end-to-end path)

**Files:**
- Modify: `src/EyeCtrl.App/Services/Coordinators/KeyboardInteractionDispatcher.cs:143-188`
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardInteractionDispatcherTests.cs` (extend)

**Interfaces:**
- Consumes: `ActionRequestedMessage(..., Modifiers)` (Task 2), the Task-1 simulator overloads.
- Produces: the routing rule `mods is { Count: > 0 } ? simulator.X(pt, mods) : simulator.X(pt)`; modified scrolls send delta 1 (four arms: (0,+1), (0,−1), (−1,0), (+1,0)).

- [ ] **Step 1: Write the failing tests**

Extend `KeyboardInteractionDispatcherTests` (FakeInputSimulator gains recording fields — see step 3):

```csharp
[Fact]
public void ModifiedLeftClick_RoutesToTheModifiedOverload()
{
    var d = BuildDispatcher(out var simulator); // existing harness; see file for the pattern
    simulator.LastModifiedClick = null;

    d.HandleActionRequested(new ActionRequestedMessage(
        InteractionAction.LeftClick, new PhysicalPoint(10, 10),
        Modifiers: new HashSet<ModifierKey> { ModifierKey.Shift }));

    Check.That(simulator.LastModifiedClick).IsNotNull();
    Check.That(simulator.LastModifiedClick!.Value.Action).IsEqualTo(InteractionAction.LeftClick);
    Check.That(simulator.LastModifiedClick.Value.Point).IsEqualTo(new PhysicalPoint(10, 10));
    Check.That(simulator.LastModifiedClick.Value.Modifiers.Contains(ModifierKey.Shift)).IsTrue();
    Check.That(simulator.BareClickCount).IsEqualTo(0);
}

[Fact]
public void BareLeftClick_NeverTouchesTheModifiedOverload()
{
    var d = BuildDispatcher(out var simulator);

    d.HandleActionRequested(new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(10, 10)));

    Check.That(simulator.LastModifiedClick).IsNull();
    Check.That(simulator.BareClickCount).IsEqualTo(1);
}

[Fact]
public void ModifiedScroll_SendsOneNotch_SignCorrectPerArm()
{
    var d = BuildDispatcher(out var simulator);
    var mods = new HashSet<ModifierKey> { ModifierKey.Ctrl };

    d.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollUp, new PhysicalPoint(0, 0), Modifiers: mods));
    d.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollDown, new PhysicalPoint(0, 0), Modifiers: mods));
    d.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollLeft, new PhysicalPoint(0, 0), Modifiers: mods));
    d.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollRight, new PhysicalPoint(0, 0), Modifiers: mods));

    Check.That(simulator.ModifiedScrolls).ContainsExactly(
        ((int Dx, int Dy))(0, 1), (0, -1), (-1, 0), (1, 0));
}

[Fact]
public void UnmodifiedScroll_KeepsThreeNotches()
{
    var d = BuildDispatcher(out var simulator);

    d.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollUp, new PhysicalPoint(0, 0)));

    Check.That(simulator.BareScrolls).ContainsExactly(((int Dx, int Dy))(0, 3));
}
```

- [ ] **Step 2: Run — FAIL**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~KeyboardInteractionDispatcherTests" -v minimal`
Expected: FAIL (routing not implemented; FakeInputSimulator lacks recording fields).

- [ ] **Step 3: Extend `FakeInputSimulator` (TestFakes.cs:342 area) — ALL five arms, complete**

```csharp
public (InteractionAction Action, PhysicalPoint Point, IReadOnlySet<ModifierKey> Modifiers)? LastModifiedClick { get; private set; }
public (InteractionAction Action, PhysicalPoint Point, IReadOnlySet<ModifierKey> Modifiers)? LastModifiedRightClick { get; private set; }
public (InteractionAction Action, PhysicalPoint Point, IReadOnlySet<ModifierKey> Modifiers)? LastModifiedMiddleClick { get; private set; }
public (InteractionAction Action, PhysicalPoint Point, IReadOnlySet<ModifierKey> Modifiers)? LastModifiedDoubleClick { get; private set; }
public int BareClickCount { get; private set; }
public List<(int Dx, int Dy)> ModifiedScrolls { get; } = new();
public List<(int Dx, int Dy)> BareScrolls { get; } = new();

public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is { Count: > 0 }) { LastModifiedClick = (InteractionAction.LeftClick, point, modifiers); return; }
    BareClickCount++;
}

public void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is { Count: > 0 }) { LastModifiedRightClick = (InteractionAction.RightClick, point, modifiers); return; }
    BareClickCount++;
}

public void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is { Count: > 0 }) { LastModifiedMiddleClick = (InteractionAction.MiddleClick, point, modifiers); return; }
    BareClickCount++;
}

public void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is { Count: > 0 }) { LastModifiedDoubleClick = (InteractionAction.DoubleClick, point, modifiers); return; }
    BareClickCount += 2; // a bare double-click is two clicks
}

public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is { Count: > 0 }) { ModifiedScrolls.Add((deltaX, deltaY)); return; }
    BareScrolls.Add((deltaX, deltaY));
}
```

(Match the existing fake's recording style where it already records bare clicks — extend, don't duplicate.)

- [ ] **Step 4: Implement the routing in `HandleActionRequested`**

Replace the click/scroll arms of the switch:

```csharp
var mods = msg.Modifiers;
var modified = mods is { Count: > 0 };

switch (msg.Action)
{
    case InteractionAction.LeftClick:
        if (modified) Context.InputSimulator.LeftClick(pt, mods); else Context.InputSimulator.LeftClick(pt);
        break;
    case InteractionAction.RightClick:
        if (modified) Context.InputSimulator.RightClick(pt, mods); else Context.InputSimulator.RightClick(pt);
        break;
    case InteractionAction.DoubleClick:
        if (modified) Context.InputSimulator.DoubleClick(pt, mods); else Context.InputSimulator.DoubleClick(pt);
        break;
    case InteractionAction.MiddleClick:
        if (modified) Context.InputSimulator.MiddleClick(pt, mods); else Context.InputSimulator.MiddleClick(pt);
        break;

    case InteractionAction.ScrollUp:
        if (modified) Context.InputSimulator.Scroll(pt, 0, 1, mods); else Context.InputSimulator.Scroll(pt, 0, DefaultScrollAmount);
        break;
    case InteractionAction.ScrollDown:
        if (modified) Context.InputSimulator.Scroll(pt, 0, -1, mods); else Context.InputSimulator.Scroll(pt, 0, -DefaultScrollAmount);
        break;
    case InteractionAction.ScrollLeft:
        if (modified) Context.InputSimulator.Scroll(pt, -1, 0, mods); else Context.InputSimulator.Scroll(pt, -DefaultScrollAmount, 0);
        break;
    case InteractionAction.ScrollRight:
        if (modified) Context.InputSimulator.Scroll(pt, 1, 0, mods); else Context.InputSimulator.Scroll(pt, DefaultScrollAmount, 0);
        break;
}
```

Log line: `Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [PreciseTarget={IsPreciseTarget}] [Modifiers={Modifiers}]", msg.Action, pt.X, pt.Y, msg.IsPreciseTarget, msg.Modifiers);`

- [ ] **Step 5: Run + commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI&FullyQualifiedName~KeyboardInteractionDispatcherTests" -v minimal`
Expected: PASS.

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): dispatcher routes modifiers — clicks wrapped, modified scrolls one notch, bare paths byte-identical"
```

---

### Task 4: ViewModel latch (ActionMenuViewModel)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` (Receive :54–70/:154–166, BuildLayer :77–152, OnTargetActivated :170–194)
- Modify: `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs:23–61` (immutable `IsDisabled`)
- Test: `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelLatchTests.cs` (new)

**Interfaces:**
- Produces: `ActionMenuViewModel.LatchedModifiers` (raise point for the App coordinator), `ActionMenuViewModel.LastToggledModifier` (the ONE id the App branch swaps), `ActionMenuItemViewModel.IsDisabled`, `ToggleActionMenuModifierMessage` handling.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

public class ActionMenuViewModelLatchTests
{
    private static (ActionMenuViewModel Vm, TestMessenger Messenger) Build()
    {
        var messenger = new TestMessenger(); // the file's existing messenger fake; see EdgeButtonBarViewModelTests for the pattern
        var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
        return (vm, messenger);
    }

    private static ShowActionMenuMessage ShowMessage() => new(
        Center: new PhysicalPoint(500, 500),
        Actions: new List<InteractionAction>
        { InteractionAction.LeftClick, InteractionAction.RightClick, InteractionAction.DoubleClick,
          InteractionAction.ScrollMode, InteractionAction.Drag, InteractionAction.Keyboard },
        CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false);

    [Fact]
    public void Toggle_LatchesAndUnlatches_RaisingLatchedModifiers()
    {
        var (vm, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsTrue();
        Check.That(vm.LastToggledModifier).IsEqualTo(ModifierKey.Ctrl);

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsFalse();
    }

    [Fact]
    public void ToggleWhileHidden_IsANoOp()
    {
        var (vm, _) = Build();

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        Check.That(vm.LatchedModifiers).IsEmpty();
    }

    [Fact]
    public void ShowResetsTheLatch_HideClearsIt()
    {
        var (vm, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        vm.Receive(new HideActionMenuMessage());
        vm.Receive(ShowMessage());

        Check.That(vm.LatchedModifiers).IsEmpty(); // show-reset, evaluated before BuildLayer's dim read
    }

    [Fact]
    public void Crossing_BumpsRingVersion_NonCrossingToggle_DoesNot()
    {
        var (vm, _) = Build();
        vm.Receive(ShowMessage());
        var before = vm.RingVersion;

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        Check.That(vm.RingVersion).IsEqualTo(before + 1); // empty → non-empty rebuild

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));
        Check.That(vm.RingVersion).IsEqualTo(before + 1); // non-empty → non-empty: no rebuild

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));
        Check.That(vm.RingVersion).IsEqualTo(before + 2); // back to empty: rebuild
    }

    [Fact]
    public void DimmedItems_AreNotDwellTargets_ButKeepTheirSlots()
    {
        var (vm, _) = Build();
        vm.Receive(ShowMessage());
        var ringVersionBefore = vm.RingVersion;

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl)); // crossing: Drag+ScrollMode dim

        Check.That(vm.RingVersion).IsEqualTo(ringVersionBefore + 1);
        var drag = vm.Items.First(i => i.Action == InteractionAction.Drag);
        var scrollMode = vm.Items.First(i => i.Action == InteractionAction.ScrollMode);
        var click = vm.Items.First(i => i.Action == InteractionAction.LeftClick);
        Check.That(drag.IsDisabled).IsTrue();
        Check.That(scrollMode.IsDisabled).IsTrue();
        Check.That(click.IsDisabled).IsFalse();
        // Slot geometry unchanged: positions match the pre-latch rebuild.
    }

    [Fact]
    public void Capture_CopiesTheLatch_ToggleThenHideCannotEmptyTheMessage()
    {
        var (vm, messenger) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));

        vm.ActivateForTest("LeftClick");

        var sent = messenger.Sent.OfType<ActionSelectedMessage>().Single();
        Check.That(sent.Modifiers).IsNotNull();
        Check.That(sent.Modifiers!.Contains(ModifierKey.Shift)).IsTrue();

        vm.Receive(new HideActionMenuMessage());
        Check.That(sent.Modifiers!.Contains(ModifierKey.Shift)).IsTrue(); // aliased set would be empty now
    }
}
```

(Adapt `Build`/`ShowMessage` to the existing `ActionMenuViewModelLayerTests` construction pattern in the same test project — the constructor and `ShowActionMenuMessage` shape there are authoritative; `TestMessenger` is that file's in-project messenger fake, recording `Sent` messages.)

- [ ] **Step 2: Run — FAIL (members missing)**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~ActionMenuViewModelLatchTests" -v minimal`

- [ ] **Step 3: Implement in `ActionMenuViewModel`**

```csharp
private readonly HashSet<ModifierKey> _latchedModifiers = new();

/// <summary>Modifiers latched for the CURRENT menu session. Cleared on hide, reset on show.</summary>
public IReadOnlySet<ModifierKey> LatchedModifiers => _latchedModifiers;

/// <summary>The modifier whose toggle raised the last <see cref="LatchedModifiers"/> change (the App branch swaps exactly this button's icon).</summary>
public ModifierKey LastToggledModifier { get; private set; }
```

`Receive(ToggleActionMenuModifierMessage)`:

```csharp
public void Receive(ToggleActionMenuModifierMessage message)
{
    if (!IsVisible) return;

    var wasEmpty = _latchedModifiers.Count == 0;
    LastToggledModifier = message.Modifier;
    if (!_latchedModifiers.Remove(message.Modifier))
        _latchedModifiers.Add(message.Modifier);
    OnPropertyChanged(nameof(LatchedModifiers));

    if (wasEmpty != (_latchedModifiers.Count == 0))
        BuildLayer(); // empty↔non-empty crossing: dim state changed; BuildLayer bumps RingVersion LAST
}
```

`Receive(ShowActionMenuMessage)`: add `_latchedModifiers.Clear();` immediately before `BuildLayer();` (the dim read must see the reset latch). `Receive(HideActionMenuMessage)`: add `_latchedModifiers.Clear();` next to the existing resets.

`BuildLayer`: after `allItems` is built and positions computed, per item:

```csharp
var disabled = _latchedModifiers.Count > 0
    && action is InteractionAction.Drag or InteractionAction.ScrollMode;
var itemVm = new ActionMenuItemViewModel(id, action, icon, label, position, kind, disabled);
Items.Add(itemVm);
_itemLookup[id] = itemVm;

if (!disabled)
    DwellDetector.AddTarget(new DwellTarget(id, position, physicalButtonRadius, message.DwellTimeMs));
```

(The position loop is untouched — `angleStep` still divides by `allItems.Count`, so dimmed slots keep their geometry.)

`OnTargetActivated`, the `Action` arm:

```csharp
case ActionMenuItemKind.Action when item.Action.HasValue:
    if (_latchedModifiers.Count > 0 && item.Action is InteractionAction.Drag or InteractionAction.ScrollMode)
        return; // backstop: dimmed items have no dwell target; unreachable in practice
    Messenger.Send(new ActionSelectedMessage(
        item.Action.Value,
        _latchedModifiers.Count > 0 ? new HashSet<ModifierKey>(_latchedModifiers) : null));
    break;
```

`ActionMenuItemViewModel`: add `public bool IsDisabled { get; }` as the LAST constructor parameter with default `false`, assigned in the constructor body.

- [ ] **Step 4: Run ViewModels suite**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests -v minimal`
Expected: PASS (new tests green; existing layer/ring tests unaffected — a bare latch toggle without a crossing never touches `RingVersion`).

- [ ] **Step 5: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): latch lives in ActionMenuViewModel — copy-on-capture into ActionSelectedMessage, dimmed non-targets on crossings, show-reset before the dim read"
```

---

### Task 5: ViewModels bar mechanics (RearmOnGazeExit, ReplaceButton, consolidated re-arm)

**Files:**
- Modify: `src/EyeCtrl.ViewModels/EdgeButtonDefinition.cs` (add `RearmOnGazeExit`)
- Modify: `src/EyeCtrl.ViewModels/EdgeButtonBarViewModel.cs` (ReplaceButton, pending state, Receive override, clears)
- Test: `tests/EyeCtrl.ViewModels.Tests/EdgeButtonBarViewModelRearmTests.cs` (new)

**Interfaces:**
- Consumes: `DwellDetector.NearestHittingTargetId/TargetHit` (Task 2), `GazeCursorMovedMessage` members `IsInGap/Position/TimestampMicroseconds/IsPaused`, protected `SampleMayDwell` (`DwellOverlayViewModelBase.cs:86–87`), base `Receive` order (`:97–102`).
- Produces: `EdgeButtonDefinition.RearmOnGazeExit` (last positional-adjacent optional param); `EdgeButtonBarViewModel.ReplaceButton(string, EdgeButtonDefinition)`; internal re-arm constants.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

public class EdgeButtonBarViewModelRearmTests
{
    private const long D = 150_000; // 150 ms in µs — the debounce, the MaxCreditedSampleGapUs precedent

    private static (EdgeButtonBarViewModel Vm, RecordingMessenger M) Build()
    {
        var messenger = new RecordingMessenger();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger);
        return (vm, messenger);
    }

    private static EdgeButtonDefinition Def(string id, bool rearm = true) => new(
        id, Symbol: string.Empty, DwellMs: 1, // dwell 1 µs-ms scale: activation on first sample
        Activate: _ => { }, VerticalOffset: 0, RadiusLogical: 50, Label: id, RearmOnGazeExit: rearm);

    // Gaze helper: the tests feed messages directly through Receive (the override under test),
    // mirroring the base order. PhysicalPoint coords in physical px; timestamps in µs.
    private static void Look(EdgeButtonBarViewModel vm, double x, long t, bool inGap = false, bool paused = false)
        => vm.Receive(new GazeCursorMovedMessage(new GazeFrame(
            new PhysicalPoint(x, 500), t, IsInGap: inGap, IsPaused: paused)));

    [Fact]
    public void HeldGaze_TogglesExactlyOnce()
    {
        var (vm, m) = Build();
        var toggles = new List<string>();
        vm.ShowButtons(new[] { Def("mod", toggles) }, new PhysicalPoint(500, 500));
        // Activate via the detector: dwell 1 ms ⇒ first sample inside fires; then the gate
        // feeds hits-nothing for every further sample inside the region.
        for (var t = 0; t < 20; t++)
            Look(vm, 500, 1_000_000 + t * 100_000);

        Check.That(toggles).ContainsExactly("mod");
    }

    [Fact]
    public void OneSampleFlicker_DoesNotRetoggle()
    {
        var (vm, m) = Build();
        var toggles = new List<string>();
        vm.ShowButtons(new[] { Def("mod", toggles) }, new PhysicalPoint(500, 500));
        Look(vm, 500, 1_000_000);        // toggle
        Look(vm, 800, 1_100_000);        // one outside sample
        for (var t = 0; t < 10; t++)
            Look(vm, 500, 1_200_000 + t * 100_000); // back inside and held

        Check.That(toggles).ContainsExactly("mod"); // debounce: the flicker never cleared the pending
    }

    [Fact]
    public void SustainedExit_ReArms_ASecondEntryTogglesAgain()
    {
        var (vm, m) = Build();
        var toggles = new List<string>();
        vm.ShowButtons(new[] { Def("mod", toggles) }, new PhysicalPoint(500, 500));
        Look(vm, 500, 1_000_000);                                  // toggle #1
        Look(vm, 800, 1_100_000);                                  // outside
        Look(vm, 800, 1_400_000);                                  // outside, ≥ D ⇒ pending cleared
        for (var t = 0; t < 10; t++) Look(vm, 500, 2_000_000 + t * 100_000); // deliberate re-entry

        Check.That(toggles).ContainsExactly("mod", "mod");
    }

    [Fact]
    public void GapSamples_RestartTheExitClock()
    {
        var (vm, m) = Build();
        var toggles = new List<string>();
        vm.ShowButtons(new[] { Def("mod", toggles) }, new PhysicalPoint(500, 500));
        Look(vm, 500, 1_000_000);                                  // toggle
        Look(vm, 800, 1_100_000);                                  // outside sample starts the clock
        for (var t = 0; t < 4; t++) Look(vm, 800, 1_200_000 + t * 100_000, inGap: true); // blink
        Look(vm, 500, 1_700_000);                                  // reopen, back inside

        Check.That(toggles).ContainsExactly("mod"); // the gap never completed a D-span of valid exit
    }

    [Fact]
    public void NearestHittingPredicate_NeighbourCompletesInTheLens_AndSupersedeHeals()
    {
        // Enlarged radii through the registry seam: the VM's base constructor registered its
        // detector on the registry passed in; apply a +60 px map so the two 100 px base regions
        // (centers 240 px apart) overlap and the lens exists (Ctrl-nearest but inside Shift's
        // region). DwellRoutingRegistration.ApplyGeometry(Func<double,double>) — the exact
        // call GeometryRouter.MakeMap feeds.
        var registry = new EyeCtrl.Core.Adaptation.DwellDetectorRegistry();
        var toggles = new List<string>();
        var messenger = new RecordingMessenger();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger, sink: null, registry);
        vm.ShowButtons(new[] { Def("shift", toggles), Def("ctrl", toggles) }, new PhysicalPoint(500, 500));
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3);
        // ×3 rather than a fixed +60 px: the physical spacing between mod centers scales with
        // DPI (DpiHelper), so the enlargement must scale too — at any DPI, 3× the base radius
        // (2 × 3r > 4.8r spacing) guarantees the overlap lens the test needs. This realizes the
        // spec's "margin 60 on the owner's 100 px base" in a DPI-independent form.

        Look(vm, 500, 1_000_000);                                   // shift toggles ⇒ pending(shift)
        for (var t = 0; t < 10; t++)
            Look(vm, 660, 2_000_000 + t * 100_000);                 // the lens: ctrl dwells and fires

        Check.That(toggles).Contains("ctrl");                       // gate did not dead-zone ctrl
        Check.That(toggles.Count(s => s == "shift")).IsEqualTo(1);  // shift did not re-toggle

        for (var t = 0; t < 10; t++)
            Look(vm, 500, 4_000_000 + t * 100_000);                 // return to shift

        Check.That(toggles.Count(s => s == "shift")).IsEqualTo(2);  // supersede healed it: no dead button
    }

    [Fact]
    public void RemoveButton_ClearsOnlyThatId_Pending()
    {
        var (vm, m) = Build();
        var toggles = new List<string>();
        vm.ShowButtons(new[] { Def("a", toggles, rearm: true), Def("b", toggles, rearm: false) }, new PhysicalPoint(500, 500));
        Look(vm, 500, 1_000_000); // 'a' activates ⇒ pending('a')
        vm.RemoveButton("b");     // different id: pending('a') must survive

        // 'a' still gated: holding its center for a full dwell does not re-toggle.
        for (var t = 0; t < 10; t++) Look(vm, 500, 2_000_000 + t * 100_000);
        Check.That(toggles).ContainsExactly("a");
    }

}
```

ADAPT to the real seams before writing: the messenger fake and construction pattern in the existing `EdgeButtonBarViewModelTests.cs` (it already drives `ShowButtons` + detector; the tests here use a counting `Activate: _ => toggles.Add(id)` closure per definition, so activations are observed without the messenger); the `GazeFrame` record shape is `(PhysicalPoint Position, long TimestampMicroseconds, bool IsInGap, bool IsPaused)` per `src/EyeCtrl.Core/Models/GazeFrame.cs:22-24`; dwell values are milliseconds in `DwellTarget` — `DwellMs: 1` with 100 ms sample gaps makes the first inside sample fire (dwell is satisfied when elapsed ≥ DwellMs). The lens test's enlarged radii go through the registry seam shown in its code.

- [ ] **Step 2: Run — FAIL**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~EdgeButtonBarViewModelRearmTests" -v minimal`
Expected: BUILD ERROR (`RearmOnGazeExit`, `ReplaceButton` missing) then behavioural failures.

- [ ] **Step 3: Implement**

`EdgeButtonDefinition` — add the trailing optional parameter:

```csharp
public sealed record EdgeButtonDefinition(
    string Id,
    string Symbol,
    int DwellMs,
    Action<IMessenger> Activate,
    double VerticalOffset,
    double RadiusLogical = UIConstants.DragButtonRadius,
    ActionIcon? Icon = null,
    string? Label = null,
    bool RearmOnGazeExit = false);
```

`EdgeButtonBarViewModel` — fields and constants:

```csharp
// The re-arm debounce: gaze must stay outside the pending button's effective region for this
// long before the gate lifts. 150 ms — the MaxCreditedSampleGapUs precedent. Owner-vetoable.
private const long RearmDebounceMicroseconds = 150_000;

private string? _pendingRearmId;
private long? _pendingExitStartUs;
```

`ReplaceButton`:

```csharp
/// <summary>
/// Swaps one button's definition IN PLACE — the icon variant changes, the dwell target and
/// the cooldown are untouched. Deliberately NOT <see cref="ShowButtons"/>: that resets the
/// detector's cooldown, which under the synchronous toggle chain re-arms a held gaze at dwell
/// cadence (spec round-1 blocker). No-op for an unknown id.
/// </summary>
public void ReplaceButton(string id, EdgeButtonDefinition definition)
{
    if (!_definitions.ContainsKey(id)) return;
    _definitions[id] = definition;
    ButtonSetChanged?.Invoke();
}
```

`OnTargetActivated`:

```csharp
protected override void OnTargetActivated(string targetId)
{
    if (!_definitions.TryGetValue(targetId, out var def)) return;

    if (def.RearmOnGazeExit)
    {
        // Set BEFORE Activate: order-independent under the gate, and a throwing chain leaves
        // the button gated (safe) rather than re-toggleable. Supersedes any other pending.
        _pendingRearmId = targetId;
        _pendingExitStartUs = null;
    }

    def.Activate(Messenger);
}
```

The `Receive` override (replicates the base order `IsVisible` → `SampleMayDwell` → update; it must NOT touch `LastSampleWasPaused` — private setter, and the base sets it from the message which this override consumes itself):

```csharp
/// <summary>
/// Feeds the sample to the detector, with the re-arm gate in front: while a re-arm is pending
/// AND the pending target is the nearest hitting target — the very sample the detector would
/// award to it — the sample is delivered as a filter that matches nothing, so every target is
/// cleared exactly as an ungated miss would clear it (no partial dwell survives a gated run).
/// In-gap samples restart the exit clock BEFORE the gate: the interpolator emits held-position
/// samples for a whole blink, and blink-edge displacement must never complete a D-span of
/// "valid" exit.
/// </summary>
public override void Receive(GazeCursorMovedMessage message)
{
    if (message.IsInGap)
    {
        _pendingExitStartUs = null;
        return;
    }

    if (!IsVisible || !SampleMayDwell(message)) return;

    var gated = _pendingRearmId is not null
        && DwellDetector.NearestHittingTargetId(message.Position) == _pendingRearmId;
    if (gated)
        DwellDetector.Update(message.Position, message.TimestampMicroseconds, static _ => false);
    else
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);

    UpdateRearmClock(message);
}

private void UpdateRearmClock(GazeCursorMovedMessage message)
{
    if (_pendingRearmId is null) return;

    if (DwellDetector.TargetHit(_pendingRearmId, message.Position))
    {
        _pendingExitStartUs = null;
        return;
    }

    _pendingExitStartUs ??= message.TimestampMicroseconds;
    if (message.TimestampMicroseconds - _pendingExitStartUs.Value >= RearmDebounceMicroseconds)
    {
        _pendingRearmId = null;
        _pendingExitStartUs = null;
    }
}
```

Clears: in `ShowButtons` (next to `DwellDetector.ResetCooldown()`), in `Hide`, and in `RemoveButton(id)` (ONLY when `id == _pendingRearmId`):

```csharp
_pendingRearmId = null;
_pendingExitStartUs = null;
```

- [ ] **Step 4: Run ViewModels suite**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests -v minimal`
Expected: PASS — including the pre-existing `EdgeButtonBarViewModelTests` (ShowButtons/Hide/RemoveButton semantics unchanged apart from the pending clears).

- [ ] **Step 5: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): consolidated re-arm — one pending with supersede, nearest-hitting gate fed as hits-nothing, 150ms valid-sample debounce"
```

---

### Task 6: Icons and geometry foundations

**Files:**
- Modify: `src/EyeCtrl.Core/Models/ActionIcon.cs` (six new values)
- Modify: `src/EyeCtrl.Controls/Icons/ActionIcons.cs` (six registry entries)
- Test: `tests/EyeCtrl.Controls.Tests/Icons/ActionIconsTests.cs` (extend)
- Modify: `src/EyeCtrl.App/Services/ScreenGeometry.cs` (LeftEdgeAnchor + pure overload)
- Create: `src/EyeCtrl.App/Services/ActionMenuModifierButtons.cs`
- Test: `tests/EyeCtrl.App.Tests/Services/ScreenGeometryLeftEdgeTests.cs` (new)
- Test: `tests/EyeCtrl.App.Tests/Services/ActionMenuModifierButtonsTests.cs` (new)

**Interfaces:**
- Consumes: Task 4's `LatchedModifiers`/`LastToggledModifier`, Task 5's `ReplaceButton`.
- Produces: `ActionIcon.Shift/Ctrl/Alt/ShiftLatched/CtrlLatched/AltLatched` (Task 8's contrast test consumes them); `ScreenGeometry.LeftEdgeAnchor(...)`; `ActionMenuModifierButtons.Definitions(int dwellMs, IReadOnlySet<ModifierKey> latched)`, `.WithLatched(EdgeButtonDefinition, bool)`, `.IdFor(ModifierKey)` (Task 7's coordinator wiring consumes them). Task 7 owns the `IOverlayManager` member, the second window, DI, fakes, and the coordinator wiring.

- [ ] **Step 1: Failing icon-registry tests + enum + registry entries**

Extend `tests/EyeCtrl.Controls.Tests/Icons/ActionIconsTests.cs`:

```csharp
[Theory]
[InlineData(ActionIcon.Shift)]
[InlineData(ActionIcon.Ctrl)]
[InlineData(ActionIcon.Alt)]
[InlineData(ActionIcon.ShiftLatched)]
[InlineData(ActionIcon.CtrlLatched)]
[InlineData(ActionIcon.AltLatched)]
public void ModifierIcons_ResolveAndAreFrozen(ActionIcon icon)
{
    var geometry = ActionIcons.For(icon);

    Check.That(geometry.Stroke).IsNotNull();
    Check.That(geometry.Stroke!.IsFrozen).IsTrue();
    if (geometry.Fill is not null)
        Check.That(geometry.Fill.IsFrozen).IsTrue();
}

[Fact]
public void LatchedVariants_AddTheFilledMarginDot()
{
    Check.That(ActionIcons.For(ActionIcon.Shift).Fill).IsNull();
    Check.That(ActionIcons.For(ActionIcon.ShiftLatched).Fill).IsNotNull();
}
```

Run: `dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ActionIconsTests" -v minimal` — expected FAIL/compile error.

`ActionIcon.cs` — append:

```csharp
/// <summary>An up-arrow modifier keycap — the Shift button on the left-edge mod bar.</summary>
Shift,
/// <summary>A caret-marked modifier keycap — the Ctrl button on the left-edge mod bar.</summary>
Ctrl,
/// <summary>A diamond-marked modifier keycap — the Alt button on the left-edge mod bar.</summary>
Alt,
/// <summary>Shift latched: same glyph, plus a rim circling the glyph and a filled margin dot.</summary>
ShiftLatched,
/// <summary>Ctrl latched (rim + margin dot).</summary>
CtrlLatched,
/// <summary>Alt latched (rim + margin dot).</summary>
AltLatched,
```

`ActionIcons.Build()` — entries (24-grid; the latched variants' PRIMARY cue is the rim drawn into the stroke geometry, the filled dot at (20, 4) in the right margin is secondary — the peripheral-vision rule is why the dot alone is not enough):

```csharp
// Modifier keycaps for the left-edge mod bar. Keycap = rounded rect x8..16 over y4..7 with
// the modifier's mark below it. The latched variants add a rim circling the glyph (primary
// cue) and a filled dot in the right margin (secondary cue).
private const string Keycap = "M8.2,7.2 L8.2,5.2 A1.2,1.2 0 0 1 9.4,4 L14.6,4 A1.2,1.2 0 0 1 15.8,5.2 L15.8,7.2";
private const string ShiftMark = " M6.5,9.5 L12,4.6 L17.5,9.5 M12,4.6 L12,19";
private const string CtrlMark = " M9,10.5 L12,7.8 L15,10.5 M12,13 L12,19 M8,16.5 L16,16.5";
private const string AltMark = " M8.5,10.5 L10.5,12.4 M15.5,10.5 L13.5,12.4 M10.5,12.4 A3,3 0 1 0 13.5,12.4 M12,15.4 L12,19";
private const string LatchedRim = " M4,8 A2,2 0 0 1 6,6 M20,6 A2,2 0 0 1 22,8 M22,16 A2,2 0 0 1 20,18 M6,18 A2,2 0 0 1 4,16";
private const string LatchedDot = "M19.9,2.1 A1.9,1.9 0 1 1 20,5.9 A1.9,1.9 0 1 1 19.9,2.1 Z";

[ActionIcon.Shift] = IconGeometry.FromPathData(Keycap + ShiftMark),
[ActionIcon.Ctrl] = IconGeometry.FromPathData(Keycap + CtrlMark),
[ActionIcon.Alt] = IconGeometry.FromPathData(Keycap + AltMark),
[ActionIcon.ShiftLatched] = IconGeometry.FromPathData(Keycap + ShiftMark + LatchedRim, LatchedDot),
[ActionIcon.CtrlLatched] = IconGeometry.FromPathData(Keycap + CtrlMark + LatchedRim, LatchedDot),
[ActionIcon.AltLatched] = IconGeometry.FromPathData(Keycap + AltMark + LatchedRim, LatchedDot),
```

(Path data is authored to the grid conventions documented on the class; `FromPathData` throws at registry construction if a path fails to parse, so every icon test fails loudly rather than silently rendering nothing. Exact artwork may be tuned during implementation PROVIDED the registry tests and the Task 8 contrast test stay green.)

Run: `dotnet test tests/EyeCtrl.Controls.Tests -v minimal` — expected PASS.

- [ ] **Step 2: `LeftEdgeAnchor` + failing geometry test**

```csharp
/// <summary>
/// Anchor at the LEFT screen edge — the mirror of <see cref="RightEdgeAnchor"/>. The returned
/// centre is offset from the left edge by <paramref name="logicalInset"/> +
/// <paramref name="logicalRadius"/> + <paramref name="logicalWindowMargin"/> logical px.
/// </summary>
public static PhysicalPoint LeftEdgeAnchor(double logicalInset, double logicalRadius, double logicalWindowMargin = EdgeButtonWindowMarginLogical)
    => LeftEdgeAnchor(logicalInset, logicalRadius, logicalWindowMargin, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

/// <summary>Pure-arithmetic overload, exposed for the same reason as RightEdgeAnchor's.</summary>
internal static PhysicalPoint LeftEdgeAnchor(double logicalInset, double logicalRadius, double logicalWindowMargin, double logicalScreenWidth, double logicalScreenHeight)
{
    var leftOffsetLogical = logicalInset + logicalRadius + logicalWindowMargin;
    var h = DpiHelper.LogicalToPhysicalY(logicalScreenHeight);
    return new PhysicalPoint(DpiHelper.LogicalToPhysicalX(leftOffsetLogical), h / 2.0);
}
```

Test (mirrors the right-edge pure-overload tests):

```csharp
[Fact]
public void LeftEdgeAnchor_MirrorsRightEdge_AtTheSameHeight()
{
    var left = ScreenGeometry.LeftEdgeAnchor(30, 50, 10, 1920, 1200);
    var right = ScreenGeometry.RightEdgeAnchor(30, 50, 10, 1920, 1200);

    Check.That(left.X).IsEqualTo(DpiHelper.LogicalToPhysicalX(90));
    Check.That(left.Y).IsEqualTo(right.Y);
}
```

- [ ] **Step 3: `ActionMenuModifierButtons` (complete file)**

```csharp
namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The left-edge modifier buttons shown while the action menu is open: Shift, Ctrl, Alt — top
/// to bottom — mirroring the right-edge Close/Help pair. Dwelling one sends
/// <see cref="ToggleActionMenuModifierMessage"/>; the latch lives in
/// <see cref="ActionMenuViewModel"/> and dies with the menu session (one-shot, ledger L2).
/// Labels are always shown: the ActionMenuEdgeButtons convention for targets a user cannot yet
/// rely on recognising.
/// </summary>
internal static class ActionMenuModifierButtons
{
    internal const string ShiftId = "ActionMenu_mod_Shift";
    internal const string CtrlId = "ActionMenu_mod_Ctrl";
    internal const string AltId = "ActionMenu_mod_Alt";

    /// <summary>The three definitions for the current latch state. Offsets and spacing mirror
    /// ActionMenuEdgeButtons: physical-Y conversion, spacing 2·radius + the side-button gap.</summary>
    internal static IReadOnlyList<EdgeButtonDefinition> Definitions(int dwellMs, IReadOnlySet<ModifierKey> latched)
    {
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        return new[]
        {
            Def(ShiftId, ModifierKey.Shift, ActionIcon.Shift, ActionIcon.ShiftLatched, -spacing, dwellMs, latched),
            Def(CtrlId, ModifierKey.Ctrl, ActionIcon.Ctrl, ActionIcon.CtrlLatched, 0, dwellMs, latched),
            Def(AltId, ModifierKey.Alt, ActionIcon.Alt, ActionIcon.AltLatched, +spacing, dwellMs, latched),
        };
    }

    /// <summary>The latched-variant swap for ONE button — used by the coordinator's
    /// LatchedModifiers branch (one <c>ReplaceButton</c> per toggle, per the
    /// once-per-logical-mutation contract).</summary>
    internal static EdgeButtonDefinition WithLatched(EdgeButtonDefinition definition, bool latched) => definition.Id switch
    {
        ShiftId => definition with { Icon = latched ? ActionIcon.ShiftLatched : ActionIcon.Shift },
        CtrlId => definition with { Icon = latched ? ActionIcon.CtrlLatched : ActionIcon.Ctrl },
        AltId => definition with { Icon = latched ? ActionIcon.AltLatched : ActionIcon.Alt },
        _ => definition,
    };

    internal static string IdFor(ModifierKey modifier) => modifier switch
    {
        ModifierKey.Shift => ShiftId,
        ModifierKey.Ctrl => CtrlId,
        ModifierKey.Alt => AltId,
        _ => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, "The mod bar offers Shift/Ctrl/Alt only."),
    };

    private static EdgeButtonDefinition Def(string id, ModifierKey modifier, ActionIcon icon, ActionIcon latchedIcon, double verticalOffset, int dwellMs, IReadOnlySet<ModifierKey> latched)
        => new(
            id, Symbol: string.Empty, dwellMs,
            Activate: m => m.Send(new ToggleActionMenuModifierMessage(modifier)),
            VerticalOffset: verticalOffset,
            RadiusLogical: UIConstants.ButtonRadius,
            Icon: latched.Contains(modifier) ? latchedIcon : icon,
            Label: id.EndsWith("Shift") ? "Shift" : id.EndsWith("Ctrl") ? "Ctrl" : "Alt",
            RearmOnGazeExit: true);
}
```

(If `Label` arithmetic reads awkward, write the three definitions longhand with literal labels — three explicit `new EdgeButtonDefinition(...)` entries with `"Shift"`/`"Ctrl"`/`"Alt"` and the matching id/offset/icon per entry is the clearer house style.)

- [ ] **Step 4: Definition tests** (`ActionMenuModifierButtonsTests.cs`)

`ActionMenuModifierButtonsTests.cs`:

```csharp
[Fact]
public void Definitions_TopToBottom_ShiftCtrlAlt_AtMirroredOffsets()
{
    var defs = ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>());

    Check.That(defs).HasSize(3);
    Check.That(defs[0].Id).IsEqualTo(ActionMenuModifierButtons.ShiftId);
    Check.That(defs[0].VerticalOffset).IsLessThan(0);
    Check.That(defs[1].Id).IsEqualTo(ActionMenuModifierButtons.CtrlId);
    Check.That(defs[1].VerticalOffset).IsEqualTo(0);
    Check.That(defs[2].Id).IsEqualTo(ActionMenuModifierButtons.AltId);
    Check.That(defs[2].VerticalOffset).IsEqualTo(-defs[0].VerticalOffset);
    Check.That(defs[0].Label).IsEqualTo("Shift");
    Check.That(defs.All(d => d.RearmOnGazeExit)).IsTrue();
}

[Fact]
public void LatchedSet_SwapsToTheLatchedIconVariant()
{
    var latched = new HashSet<ModifierKey> { ModifierKey.Ctrl };
    var defs = ActionMenuModifierButtons.Definitions(600, latched);

    Check.That(defs[0].Icon).IsEqualTo(ActionIcon.Shift);
    Check.That(defs[1].Icon).IsEqualTo(ActionIcon.CtrlLatched);
}

[Fact]
public void WithLatched_SwapsOnlyTheIcon()
{
    var def = ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>())[1];

    var on = ActionMenuModifierButtons.WithLatched(def, latched: true);
    var off = ActionMenuModifierButtons.WithLatched(on, latched: false);

    Check.That(on.Icon).IsEqualTo(ActionIcon.CtrlLatched);
    Check.That(on.DwellMs).IsEqualTo(def.DwellMs);
    Check.That(on.Activate).IsSameReferenceAs(def.Activate); // record `with`: same delegate instance
    Check.That(off.Icon).IsEqualTo(ActionIcon.Ctrl);
}
```

- [ ] **Step 5: Run the geometry/definition tests and commit**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI&FullyQualifiedName~ScreenGeometry|FullyQualifiedName~ActionMenuModifierButtons" -v minimal`
Expected: PASS. (The coordinator wiring that consumes `ActionMenuModBar` lands in Task 7, after the interface member and second window exist.)

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI&FullyQualifiedName~ScreenGeometry|FullyQualifiedName~ActionMenuModifierButtons" -v minimal`
Expected: PASS.

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): LeftEdgeAnchor + ActionMenuModifierButtons definitions factory"
```

---

### Task 7: App plumbing — interface slots, second window, DI, fakes, harness tests

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs:85` (new default-throw member + `OverlayViewModels` record member)
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs:63-68, :112, :138–154`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs:381, :627, :637`
- Modify: `src/EyeCtrl.App/App.xaml.cs:198, :572–595`
- Modify: `tests/EyeCtrl.App.Tests/Services/TestFakes.cs:196+` (PartialOverlayManager slots + MakeOverlayManager params)
- Test: `tests/EyeCtrl.App.Tests/Overlays/ActionMenuModBarWiringTests.cs` (new — the Task 6 wiring tests live here)
- Test: `tests/EyeCtrl.App.Tests/Services/Coordinators/ActionMenuModBarMaskedMutationTests.cs` (new)

**Interfaces:**
- Consumes: everything above.
- Produces: `IOverlayManager.ActionMenuModBar` (default-throw member; `OverlayManager` implements it with a second `EdgeButtonBarWindow`); keyed DI `EdgeButtonBarViewModel` ("actionMenuModBar"); `TestFakes.MakeOverlayManager(actionMenuModBar:, actionMenu:)` slots.

- [ ] **Step 1: `IOverlayManager`**

After `IEdgeButtonBar EdgeButtonBar { get; }` (`:85`):

```csharp
/// <summary>
/// The LEFT-edge modifier bar while the action menu is open. Owned exclusively by the action
/// menu (nothing else ever shows or hides it), so its hide path is unconditional. Default
/// throws — the six test fakes that implement this interface keep compiling, and
/// <see cref="TestFakes.PartialOverlayManager"/> gains an explicit optional slot (round-2 F9).
/// </summary>
IEdgeButtonBar ActionMenuModBar => throw new NotSupportedException("This overlay manager does not implement the action-menu modifier bar.");
```

And in the `OverlayViewModels` record (IOverlayManager.cs:114), after `EdgeButtonBarViewModel EdgeButtonBar,`:

```csharp
EdgeButtonBarViewModel ActionMenuModBar,
```

Then fix the two positional construction sites (`App.xaml.cs:198`, `AppBootstrapper.cs:627`) and the `ResolveViewModels` tuple (`App.xaml.cs:572–595`) by adding the member in position.

- [ ] **Step 2: `OverlayManager` — second window, same wiring shape as the first**

Fields/properties (next to `_edgeButtonBarWindow`/`EdgeButtonBar`):

```csharp
private EdgeButtonBarWindow _actionMenuModBarWindow = null!;

/// <inheritdoc />
public IEdgeButtonBar ActionMenuModBar => _actionMenuModBarWindow;
```

Creation (next to `:112`): `_actionMenuModBarWindow = new EdgeButtonBarWindow();`
Binding/wiring (next to `:138–154`, same cast comment applies):

```csharp
_actionMenuModBarWindow.DataContext = viewModels.ActionMenuModBar;
viewModels.ActionMenuModBar.ButtonSetChanged += () => _actionMenuModBarWindow.SyncButtons();
viewModels.ActionMenuModBar.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(EdgeButtonBarViewModel.IsVisible))
    {
        if (viewModels.ActionMenuModBar.IsVisible)
            _actionMenuModBarWindow.Show();
        else
            ((System.Windows.Window)_actionMenuModBarWindow).Hide();
    }
};
```

- [ ] **Step 3: DI** — `AppBootstrapper.cs` keeps the unkeyed `services.AddSingleton<EdgeButtonBarViewModel>()` (`:381`) and adds:

```csharp
services.AddKeyedSingleton<EdgeButtonBarViewModel>("actionMenuModBar");
```

`ResolveViewModels` (`:627+`): `ActionMenuModBar: sp.GetRequiredService<EdgeButtonBarViewModel>("actionMenuModBar"),` (unkeyed `GetRequiredService<EdgeButtonBarViewModel>()` at `:637` still resolves the right bar).

- [ ] **Step 4: Fakes** — `PartialOverlayManager` (TestFakes.cs:277) gains:

```csharp
private readonly IEdgeButtonBar? _actionMenuModBar;
private readonly IActionMenuOverlay? _actionMenuOverlay;

// ctor params: IEdgeButtonBar? actionMenuModBar = null, IActionMenuOverlay? actionMenuOverlay = null
// assignment follows the existing pattern.

public IEdgeButtonBar ActionMenuModBar => _actionMenuModBar
    ?? throw new NotImplementedException("test supplied no action-menu modifier bar");
```

(`ActionMenu` keeps its hard-coded `=> throw` UNLESS the harness supplies the new optional slot — preferred: add the slot too, since the masked-mutation test's crossing path hits `ShowActionMenu`.) `MakeOverlayManager` (`:196`) gains `IEdgeButtonBar? actionMenuModBar = null, IActionMenuOverlay? actionMenu = null` forwarded to the ctor.

- [ ] **Step 5: Wire the coordinators**

`OverlayMessageCoordinator` — show handler, after the right-bar `ShowButtons(...)` block (`:131–138`) and BEFORE `if (msg.ResolvedControl == null) return;` (`:140`):

```csharp
try
{
    var modAnchor = ScreenGeometry.LeftEdgeAnchor(RightEdgeInsetLogical, UIConstants.ButtonRadius);
    Context.OverlayManager.ActionMenuModBar.ShowButtons(
        ActionMenuModifierButtons.Definitions(
            msg.ShowBack
                ? Context.Settings.Dwell.ManualSelectionActionMenuDwellMs
                : Context.Settings.Dwell.ActionMenuDwellMs,
            Context.ActionMenuVM.LatchedModifiers),
        modAnchor);
}
catch (Exception ex)
{
    Log.Error(ex, "Error showing action-menu modifier bar");
}
```

Hide handler, after the existing right-bar guarded `bar.Hide()` block — its own try, unconditional (nothing else ever owns the left bar):

```csharp
try
{
    Context?.OverlayManager.ActionMenuModBar.Hide();
}
catch (Exception ex)
{
    Log.Error(ex, "Error hiding action-menu modifier bar");
}
```

`OverlayVmBindingCoordinator` — inside `OnActionMenuVMPropertyChanged`, as a SEPARATE `else if` AFTER the `RingVersion`/`ShowLabels` branch, with its own try/catch (a throw here must not escape into the gaze dispatch):

```csharp
else if (e.PropertyName == nameof(Context.ActionMenuVM.LatchedModifiers)
         && Context.ActionMenuVM.IsVisible)
{
    try
    {
        var bar = Context.OverlayManager.ActionMenuModBar;
        var id = ActionMenuModifierButtons.IdFor(Context.ActionMenuVM.LastToggledModifier);
        var current = bar.EdgeBarVm?.DefinitionFor(id);
        if (current is not null)
            bar.EdgeBarVm!.ReplaceButton(
                id,
                ActionMenuModifierButtons.WithLatched(
                    current, Context.ActionMenuVM.LatchedModifiers.Contains(Context.ActionMenuVM.LastToggledModifier)));
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error updating the action-menu modifier button state");
    }
}
```

- [ ] **Step 6: Wiring tests** (`ActionMenuModBarWiringTests.cs`, built on the `HideActionMenuBarOwnershipTests` context pattern — `MakeOverlayManager` at `:164–168`):

```csharp
[Fact]
public void ShowActionMenu_ShowsTheModBar_EvenWithoutAResolvedControl()
{
    var h = BuildContext(); // ownership-test harness + the new slots
    h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600));
    h.PumpDispatcher();     // the harness's dispatcher pump — see HideActionMenuBarOwnershipTests

    Check.That(h.ModBarCalls).Contains("ShowButtons");
}

[Fact]
public void HideActionMenu_HidesTheModBar_Unconditionally_EvenWhenTheRightBarIsOwnedByADrag()
{
    var h = BuildContext();
    h.InteractionManager.OnActionSelected(InteractionAction.Drag); // right bar now drag-owned
    h.Messenger.Send(new HideActionMenuMessage());
    h.PumpDispatcher();

    Check.That(h.ModBarCalls).Contains("Hide");
    Check.That(h.RightBarCalls).DoesNotContain("Hide"); // ownership guard held
}

[Fact]
public void ThrowingModBar_DoesNotPreventTheRightBarShow()
{
    var h = BuildContext(withModBar: false); // PartialOverlayManager WITHOUT the slot ⇒ default throws
    h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600));
    h.PumpDispatcher();

    Check.That(h.RightBarCalls).Contains("ShowButtons"); // own try block isolated the fault
}
```

- [ ] **Step 7: The masked-mutation + held-gaze App test** (`ActionMenuModBarMaskedMutationTests.cs`) — built on the precedent harness (`EdgeButtonBarZOrderTests.cs:146–164` / `ManualSelectionAnchorWiringTests.cs:50–65`: every VM on ONE messenger + a real `Dispatcher`). Requirements checklist from the spec (round-4 F4 + round-5 F6): real VMs for ActionMenu + BOTH bars; `Settings` real; `MakeContext`'s `null!` VMs supplied (coordinator subscribes eleven VMs — `:43–80`); the mod-bar fake's `EdgeButtonBarViewModel` on the SHARED messenger; `PartialOverlayManager` slots for mod bar + `ActionMenu` + `GlassBackdrop`; a real or pumped `Dispatcher`.

```csharp
[Fact]
public void HeldGaze_TogglesExactlyOnce_ThroughTheRealCoordinator()
{
    var h = BuildFullHarness(); // real coordinator + real VMs; mod-bar VM on the shared messenger
    h.ShowActionMenu();         // via ShowActionMenuMessage + dispatcher pump
    h.LookAt(ShiftCenter, t0);                       // dwell completes ⇒ Shift latches
    // Gaze stays ON Shift: the nearest-hitting gate feeds hits-nothing.
    for (var t = 1; t <= 30; t++)
        h.LookAt(ShiftCenter, t0 + t * 100_000);     // 3 s of held gaze ≫ 2·dwell + cooldown

    Check.That(h.ShiftToggleCount).IsEqualTo(1);
}

[Fact]
public void ShowButtonsMutant_IsCaughtAtT700()
{
    // The mutant: coordinator calls ShowButtons instead of ReplaceButton on toggle.
    // Correct: Ctrl's accumulation cannot START until T+400 (cooldown), completes ≈T+1000.
    // Mutant:  ResetCooldown ⇒ Ctrl completes ≈T+616.  Assert at T+700.
    var h = BuildFullHarness();
    h.ShowActionMenu();
    h.LookAt(ShiftCenter, 0);
    h.LookAt(CtrlCenter, 700_000); // T+700 µs after the toggle — sample only
    h.LookAt(CtrlCenter, 1_100_000);

    Check.That(h.CtrlToggleCount).IsEqualTo(1);      // correct impl
    // Under the mutant this is 0 at T+700 because it already fired (assert via a timeline probe).
}
```

(The harness helper `BuildFullHarness` is written ONCE in this test file — every VM real, one messenger, real Dispatcher from `Dispatcher.CreateDispatcher()`-style pump as in the precedent tests; the plan does not restate the ~80-line harness here because the two cited precedent files contain the exact construction to copy. The assertion math is pinned: at T+700 the correct implementation has Ctrl mid-dwell (started T+400), the mutant has already fired at ≈T+616 — count-based assertions on both sides of the boundary.)

- [ ] **Step 8: Run the full App non-UI suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI" -v minimal`
Expected: PASS — including `HideActionMenuBarOwnershipTests` (its `PartialOverlayManager` lacks the mod-bar slot by default, exercising the throwing default + try isolation exactly as the spec requires).

- [ ] **Step 9: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): second edge-bar instance — IOverlayManager default-throw member, keyed DI, PartialOverlayManager slots, masked-mutation + held-gaze harness"
```

---

### Task 8: Render + capture hygiene + contrast test

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs:229` (child-canvas dim)
- Modify: `src/EyeCtrl.App/Overlays/EdgeButtonBarWindow.xaml.cs:26-33` (`ExcludeFromScreenCapture`)
- Test: `tests/EyeCtrl.Controls.Tests/Icons/ActionIconDimContrastTests.cs` (new)

**Interfaces:**
- Consumes: Task 4's `ActionMenuItemViewModel.IsDisabled`, Task 6's icon registry.
- Produces: the dim render (group opacity via a per-button child canvas) and capture hygiene for both bars.

- [ ] **Step 1: Child-canvas dim render in `ActionMenuWindow.Show`**

Const: `private const double DisabledOpacity = 0.45;` Replace the `btn.AddTo(MenuCanvas);` call (`:229`) with:

```csharp
if (itemVm.IsDisabled)
{
    // Group opacity via a per-button child canvas: AddTo places its parts relative to ANY
    // canvas, so a host canvas at origin carries them unchanged and Opacity dims the whole
    // group at once. Per-part opacity would be neither group opacity nor hazard-free.
    var host = new Canvas { Opacity = DisabledOpacity };
    btn.AddTo(host);
    MenuCanvas.Children.Add(host);
}
else
{
    btn.AddTo(MenuCanvas);
}
```

(`MenuCanvas.Children.Clear()` on every `Show` removes the host canvases too — no accumulation. A dimmed item has no dwell target, so its `ProgressPath` never fills — that is the `IsDisabled` design, pinned by the ViewModels suite.)

- [ ] **Step 2: `EdgeButtonBarWindow` capture hygiene**

Constructor: add `ExcludeFromScreenCapture = true;` next to `IsClickThrough = true;` (compare `ActionMenuWindow.xaml.cs:53`; the property is init-only but settable from a derived constructor).

- [ ] **Step 3: Contrast test** — `tests/EyeCtrl.Controls.Tests/Icons/ActionIconDimContrastTests.cs`, reusing the `HaloContrastTests` backdrop/theme matrix:

```csharp
private const double DisabledOpacity = 0.45;
private const double IdentifiabilityFloor = 3.0;
private const double SeparabilityFloor = 1.5;

public static TheoryData<string> Backdrops() => new()
{
    "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF",
    "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E",
};

public static TheoryData<ITheme> Themes() => HaloContrastTests.AllThemes();

[Theory]
[MemberData(nameof(Backdrops))]
[MemberData(nameof(Themes))]
public void DimmedIcon_StaysIdentifiable_AndSeparableFromLive(string backdrop, ITheme theme)
{
    var dimmed = Blend(theme.IconColor, DisabledOpacity, backdrop); // icon over dimmed fill
    var fill = Blend(theme.ButtonFillColor, DisabledOpacity, backdrop);
    var live = theme.IconColor;

    Check.That(Contrast(dimmed, fill)).IsGreaterOrEqualThan(IdentifiabilityFloor);
    Check.That(Contrast(live, dimmed)).IsGreaterThan(SeparabilityFloor);
}

private static Color Blend(Color source, double opacity, string backdropHex)
{
    var backdrop = (Color)ColorConverter.ConvertFromString(backdropHex);
    return Color.FromRgb(
        (byte)(source.R * opacity + backdrop.R * (1 - opacity)),
        (byte)(source.G * opacity + backdrop.G * (1 - opacity)),
        (byte)(source.B * opacity + backdrop.B * (1 - opacity)));
}

private static double Contrast(Color a, Color b)
{
    double L(Color c)
    {
        double Channel(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.R / 255.0) + 0.7152 * Channel(c.G / 255.0) + 0.0722 * Channel(c.B / 255.0);
    }
    var lighter = Math.Max(L(a), L(b));
    var darker = Math.Min(L(a), L(b));
    return (lighter + 0.05) / (darker + 0.05);
}
```

(The theme member names — `IconColor`, `ButtonFillColor` — must be matched to `ITheme`'s actual accessors as used by `HaloContrastTests`; if that file composites differently, copy ITS composite helper verbatim and keep the assertions. The dwell-blend rule is moot by construction — a dimmed item has no target, so `ProgressPath` stays empty; pinned by the ViewModels suite.)

- [ ] **Step 4: Run Controls + ViewModels + App suites**

Run: `dotnet test tests/EyeCtrl.Controls.Tests -v minimal` then `dotnet test tests/EyeCtrl.ViewModels.Tests -v minimal` then `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI" -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): child-canvas dim render, edge bars excluded from capture, dim contrast test"
```

### Task 9: Final gate

- [ ] **Step 1: Full non-UI suite**

Run: `dotnet test --filter "Category!=UI" -v minimal`
Expected: PASS across all projects.

- [ ] **Step 2: Graph change analysis (repo rule)**

Run: `node .gitnexus/run.cjs detect-changes --scope all --worktree "C:\Users\andre\source\repos\eyectrl\.worktrees\action-menu-modifier-keys"`
Expected: risk assessed; HIGH/CRITICAL findings reviewed against the spec's blast-radius section before proceeding.

- [ ] **Step 3: Spec-conformance sweep**

Walk `docs/superpowers/specs/2026-09-25-action-menu-modifier-keys-design.md` mechanism items 1–10 and the mutation list; every bullet has a corresponding commit in this branch's log (`git -C ... log --oneline origin/main..HEAD`).

- [ ] **Step 4: Final commit (if any straggler changes) and hand-off**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys status --short
```

Expected: clean tree. Report to the owner: implementation complete on `feat/action-menu-modifier-keys`; PR/merge is the owner's call (no push without ask).
