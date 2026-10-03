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

- [ ] **Step 5: Write the batch-composition + compensation tests FIRST (they call internal builders that do not exist yet)**

HARD RULE: tests must NEVER call the send methods (`LeftClick`/`Scroll`/`SendModifiedKeyPress`, ...) on a real `Win32InputSimulator` — that injects real OS input on the owner's gaze-driven machine. Everything is tested through the pure static builders.

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

    // The body builders are instance methods (they route through CreateMouseInput's coordinate
    // mapper), so the tests construct a real simulator over a pass-through mapper — and NEVER
    // call its send methods (no SendInput ever fires in tests).
    // The mapper is NESTED in Win32InputSimulatorTestHelper — qualify it (no `using static`).
    private readonly Win32InputSimulator _sim =
        new(new Win32InputSimulatorTestHelper.PassThroughCoordinateMapper());

    private static HashSet<ModifierKey> Mods(params ModifierKey[] keys) => new(keys);

    [Fact]
    public void ModifiedClickBody_BalancedDownUp_ForSingleClick()
    {
        var body = _sim.BuildModifiedClickBody(
            new EyeCtrl.Core.Models.PhysicalPoint(10, 10), right: false, middle: false, doubleClick: false);

        Check.That(body).HasSize(2);
        Check.That((body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTDOWN)).IsNotEqualTo(0u);
        Check.That((body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
    }

    [Fact]
    public void ModifiedClickBody_DoubleClick_IsOneBatchOfFourMouseEvents()
    {
        var body = _sim.BuildModifiedClickBody(
            new EyeCtrl.Core.Models.PhysicalPoint(10, 10), right: false, middle: false, doubleClick: true);

        Check.That(body).HasSize(4);
        Check.That(body.All(i => (i.Union.Mouse.Flags & (NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_LEFTUP)) != 0)).IsTrue();
    }

    [Fact]
    public void ModifiedWheelBody_LeadsWithAbsoluteMove_ThenClampedWheel()
    {
        var body = _sim.BuildModifiedWheelBody(
            new EyeCtrl.Core.Models.PhysicalPoint(10, 10), deltaX: 0, deltaY: 3);

        Check.That(body).HasSize(2);
        Check.That((body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_MOVE)).IsNotEqualTo(0u);
        Check.That((body[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_ABSOLUTE)).IsNotEqualTo(0u);
        Check.That((body[1].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_WHEEL)).IsNotEqualTo(0u);
        Check.That(body[1].Union.Mouse.MouseData).IsEqualTo(3 * PlatformConstants.MouseWheelDelta);
    }

    [Fact]
    public void Wrap_DownsPrecedeBody_MaskBeforeAltUp_UpsReversed()
    {
        var body = new[]
        {
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
        };

        var batch = Win32InputSimulator.WrapWithModifiers(
            Mods(ModifierKey.Shift, ModifierKey.Alt), body, includeAltMenuMask: true);

        // Down order: Alt-down, Shift-down. Body. Ups REVERSED: Shift-up at [4], mask pair
        // immediately before the Alt-up at [5..6], Alt-up at [7].
        Check.That(batch).HasSize(2 + 2 + 2 + 2);
        Check.That(batch[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[2].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[3].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        Check.That(batch[4].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(batch[4].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[5].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(batch[6].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(batch[7].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(batch[7].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void Wrap_NoAlt_NoMaskPair()
    {
        var body = new[] { Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN) };
        var batch = Win32InputSimulator.WrapWithModifiers(Mods(ModifierKey.Shift), body, includeAltMenuMask: true);

        Check.That(batch).HasSize(1 + 1 + 1);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    [Fact]
    public void Wrap_NullModifiers_ZeroKeyboardInputs_AndKeyboardSequence_NeverCarriesTheMask()
    {
        // The null-mods arm: WrapWithModifiers(null, body, true) must emit the body UNCHANGED.
        var body = new[] { Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN) };
        var wrapped = Win32InputSimulator.WrapWithModifiers(null, body, includeAltMenuMask: true);
        Check.That(wrapped).HasSize(1);
        Check.That(wrapped[0].Type).IsEqualTo(NativeMethods.INPUT_MOUSE);
        var batch = Win32InputSimulator.BuildModifiedKeySequence(
            Mods(ModifierKey.Alt, ModifierKey.Shift), 0x41, isExtendedKey: false);

        Check.That(batch).HasSize(4 + 2);
        Check.That(batch.Any(i => i.Type == NativeMethods.INPUT_KEYBOARD && i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }

    [Fact]
    public void Compensation_EmitsMouseUpBeforeModifierUps_MaskBeforeAltUp()
    {
        // Batch: Alt-down, LEFTDOWN, LEFTUP, LEFTDOWN, Alt-up — sent = 4, so the second
        // LEFTDOWN landed without its up and the Alt-up never landed.
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_MENU, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_MENU, NativeMethods.KEYEVENTF_KEYUP),
        };

        var releases = Win32InputSimulator.BuildCompensatingReleases(4, batch);

        Check.That(releases).HasSize(4); // LEFTUP, mask-down, mask-up, Alt-up
        Check.That((releases[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(MenuMask);
        Check.That(releases[2].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);
        Check.That(releases[3].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That((releases[3].Union.Keyboard.Flags & NativeMethods.KEYEVENTF_KEYUP)).IsNotEqualTo(0u);
    }

    [Fact]
    public void Compensation_FullySentBatch_EmitsNothing_EarlyReturn()
    {
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP),
        };

        Check.That(Win32InputSimulator.BuildCompensatingReleases(4, batch)).IsEmpty();
    }

    [Fact]
    public void Compensation_NoAlt_NoMaskInReleases()
    {
        var batch = new[]
        {
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, 0),
            Win32InputSimulatorTestHelper.MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            Win32InputSimulatorTestHelper.KeyboardInput(NativeMethods.VK_SHIFT, NativeMethods.KEYEVENTF_KEYUP),
        };

        var releases = Win32InputSimulator.BuildCompensatingReleases(2, batch); // LEFTDOWN + Shift-down unmatched

        Check.That(releases).HasSize(2); // LEFTUP first, then Shift-up; no Alt means no mask
        Check.That((releases[0].Union.Mouse.Flags & NativeMethods.MOUSEEVENTF_LEFTUP)).IsNotEqualTo(0u);
        Check.That(releases[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_SHIFT);
        Check.That(releases.Any(i => i.Union.Keyboard.VirtualKey == MenuMask)).IsFalse();
    }
}

/// <summary>Test access to the internal composition helpers (InternalsVisibleTo covers this project).</summary>
internal static class Win32InputSimulatorTestHelper
{
    /// <summary>Identity-ish mapper: the composition tests assert flags and types, never coordinates. IMouseCoordinateMapper lives in EyeCtrl.Platform.Input and returns (int, int).</summary>
    public sealed class PassThroughCoordinateMapper : global::EyeCtrl.Platform.Input.IMouseCoordinateMapper
    {
        public (int normalizedX, int normalizedY) NormalizeScreenCoordinate(
            EyeCtrl.Core.Models.PhysicalPoint point, int screenWidth, int screenHeight)
            => ((int)point.X, (int)point.Y); // PhysicalPoint is (double X, double Y) — no implicit narrowing
    }

    public static NativeMethods.INPUT MouseInput(uint flags) => new()
    {
        Type = NativeMethods.INPUT_MOUSE,
        Union = new NativeMethods.INPUTUNION { Mouse = new NativeMethods.MOUSEINPUT { Flags = flags } },
    };

    public static NativeMethods.INPUT KeyboardInput(ushort vk, uint flags) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Union = new NativeMethods.INPUTUNION { Keyboard = new NativeMethods.KEYBDINPUT { VirtualKey = vk, Flags = flags } },
    };
}
```

- [ ] **Step 6: Run — compile FAIL (builders missing)**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~ModifiedSequenceTests" -v minimal`
Expected: BUILD ERROR (`WrapWithModifiers`, `BuildModifiedClickBody`, `BuildModifiedWheelBody`, `BuildCompensatingReleases`, `MenuMaskVirtualKey` undefined).

- [ ] **Step 7: Implement the composition layer in `Win32InputSimulator.cs`**

(a) `SendRaw` returns the injected count — `SendInput` returns `uint`, so cast:

```csharp
private int SendRaw(uint count, NativeMethods.INPUT[] inputs)
{
    var sent = NativeMethods.SendInput(count, inputs, s_inputSize);
    if (sent == count) return (int)sent;

    // ... existing rate-limited block warning unchanged ...
    return (int)sent;
}
```

(b)/(c) `WrapWithModifiers` and the `BuildModifiedKeySequence` delegation were presented in Step 3 above — do NOT redeclare them here; Task 1 implements them exactly once (Step 3), and Steps 8-9 only USE them.

(d) Pure body builders (they make the PRODUCTION batch shapes testable — the leading MOVE, the four-event double-click, the wheel deltas):

```csharp
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

/// <summary>The wheel-event body of a modified scroll: a leading absolute MOVE (wheel events alone land at the current cursor, and ResetToFreeLook re-enables per-frame MoveCursor across a doubly-deferred dispatch), then the Y and/or X wheel legs with clamped deltas. INSTANCE method — same mapper reason.</summary>
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
```

(e) Compensation — static pure builder + thin instance sender:

```csharp
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
```

(f) The modified overloads (builders + `SendRaw` + compensation) and the `SendModifiedKeyPress` reroute:

```csharp
public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { LeftClick(point); return; }
    var batch = WrapWithModifiers(modifiers, BuildModifiedClickBody(point, right: false, middle: false, doubleClick: false), includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}
// RightClick / MiddleClick: identical shape with right: true / middle: true.
// DoubleClick: BuildModifiedClickBody(point, right: false, middle: false, doubleClick: true).

public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 }) { Scroll(point, deltaX, deltaY); return; }
    var batch = WrapWithModifiers(modifiers, BuildModifiedWheelBody(point, deltaX, deltaY), includeAltMenuMask: true);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}

public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey)
{
    var batch = BuildModifiedKeySequence(modifiers, virtualKeyCode, isExtendedKey);
    SendCompensatingReleases(SendRaw((uint)batch.Length, batch), batch);
}
```

- [ ] **Step 8: Declare the overloads on `IInputSimulator` as throwing default interface methods**

Add to `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs` (after the existing members, XML docs included — Core escalates CS1591):

```csharp
/// <summary>Performs a left click with the given modifiers held for the duration of the click (one atomic batch). The dispatcher routes bare clicks to <see cref="LeftClick(PhysicalPoint)"/>.</summary>
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

    // OnActionSelected only raises in the two menu-visible states — Fixation resolves no
    // target point and returns. The menu is reached via the activation-button dwell.
    private static InteractionManager MenuVisible()
    {
        var im = new InteractionManager();
        im.OnFixationStarted(Point);
        im.OnActivationButtonDwelled(); // SelectionMode.None ⇒ ShowActionMenu, state ActionMenuVisible
        return im;
    }

    [Fact]
    public void OnActionSelected_RelaysModifiers_FromTheMenuState()
    {
        var im = MenuVisible();
        var mods = new HashSet<ModifierKey> { ModifierKey.Shift };
        (InteractionAction Action, IReadOnlySet<ModifierKey>? Modifiers)? raised = null;
        im.ActionRequested += (action, _, modifiers) => raised = (action, modifiers);

        im.OnActionSelected(InteractionAction.LeftClick, mods);

        Check.That(raised).IsNotNull();
        Check.That(raised!.Value.Action).IsEqualTo(InteractionAction.LeftClick);
        Check.That(raised.Value.Modifiers!.Contains(ModifierKey.Shift)).IsTrue();
    }

    [Fact]
    public void OnActionSelected_RelaysModifiers_FromTheManualSelectionMenuState()
    {
        var im = new InteractionManager { SelectionMode = SelectionMode.ManualSelection };
        (InteractionAction Action, IReadOnlySet<ModifierKey>? Modifiers)? raised = null;
        im.ActionRequested += (action, _, modifiers) => raised = (action, modifiers);
        im.OnFixationStarted(Point);
        im.OnActivationButtonDwelled();      // ⇒ StartManualSelection, state ManualSelectionActive
        im.OnManualSelectionDwell(Point);    // ⇒ ManualSelectionActionMenuVisible

        im.OnActionSelected(InteractionAction.LeftClick, new HashSet<ModifierKey> { ModifierKey.Alt });

        Check.That(raised).IsNotNull();
        Check.That(raised!.Value.Modifiers!.Contains(ModifierKey.Alt)).IsTrue();
    }

    [Fact]
    public void RepeatModeTick_CarriesNoModifiers()
    {
        var im = new InteractionManager();
        IReadOnlySet<ModifierKey>? raised = null;
        var fired = false;
        im.ActionRequested += (_, _, modifiers) => { fired = true; raised = modifiers; };
        im.NoteGazeSample(isValid: true, timestampUs: 10_000); // liveness BEFORE entry (the file's precedent)
        im.EnterRepeatMode(InteractionAction.LeftClick);
        im.RepeatDwellMs = 0;
        im.OnFixationStarted(Point);           // arms the locked-action dwell at the point
        im.NoteGazeSample(isValid: true, timestampUs: 11_000); // elapsed 1 ms ≥ 0 ⇒ fires

        Check.That(fired).IsTrue();           // the tick really fired — no vacuous pass
        Check.That(raised).IsNull();          // repeat ticks carry null — never a modifier set
    }

    [Fact]
    public void Drag_WithNullModifiers_TransitionsUnchanged()
    {
        var im = MenuVisible();
        PhysicalPoint? dragStart = null;
        im.DragStarted += p => dragStart = p;

        im.OnActionSelected(InteractionAction.Drag);

        Check.That(dragStart).IsNotNull();    // the drag path is untouched by the new parameter
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Dragging);
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

    [Fact]
    public void NearestHittingTargetId_AgreesWithUpdate_OnAnExactEquidistantTie()
    {
        // Two targets, gaze at the exact midpoint (integer coords ⇒ a real tie). The winner is
        // whatever the detector's own order produces — the API must agree with Update, not pick
        // its own winner.
        var detector = new DwellDetector(400, EyeCtrl.Core.Adaptation.DwellContext.Other, sink: null);
        detector.AddTarget(new DwellTarget("a", new EyeCtrl.Core.Models.PhysicalPoint(0, 0), 100, 600));
        detector.AddTarget(new DwellTarget("b", new EyeCtrl.Core.Models.PhysicalPoint(200, 0), 100, 600));
        var fired = new List<string>();
        detector.TargetActivated += id => fired.Add(id);

        for (var t = 0; t < 10; t++)
            detector.Update(new EyeCtrl.Core.Models.PhysicalPoint(100, 0), t * 100_000);

        var updateWinner = fired.Single();
        Check.That(detector.NearestHittingTargetId(new EyeCtrl.Core.Models.PhysicalPoint(100, 0))).IsEqualTo(updateWinner);
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

- [ ] **Step 4: Pass-through in `InteractionManager`** (add `using EyeCtrl.Core.Keyboard;` to the file for `ModifierKey` — and the same using to `GazeEngineAdapter.cs`)

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
/// eligible target's region contains the point. This is the SAME selection <see cref="Update(PhysicalPoint, long)"/>
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

- [ ] **Step 7: Adapter pass-through in `GazeEngineAdapter`** (add `using EyeCtrl.Core.Keyboard;`; the subscription at `:63` is a method group — its signature gains the third parameter in place)

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

- [ ] **Step 1: Write the failing tests** (INSIDE the existing `KeyboardInteractionDispatcherTests.cs`, using ITS `MakeContext`-based construction — there is no `BuildDispatcher` helper. Supply `TargetHighlightVM` per the file's precedent at `:95–98`, and send every message with `IsPreciseTarget: true` so the handler never reads `Context.GazeEngine.StableCentroid`, which `MakeContext` leaves null):

```csharp
// BuildWithFakeInput is defined ONCE at the top of this test file (it does not exist in the
// source) — the file's only construction pattern is TestFakes.MakeContext(input) with
// TargetHighlightVM supplied (KeyboardInteractionDispatcherTests.cs:95-100):
//
//   private static (KeyboardInteractionDispatcher D, TestFakes.FakeInputSimulator Sim) BuildWithFakeInput()
//   {
//       var input = new TestFakes.FakeInputSimulator();   // nested in TestFakes — qualify it
//       var ctx = TestFakes.MakeContext(input) with
//           { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
//       var dispatcher = new KeyboardInteractionDispatcher(); // parameterless ctor (file precedent :96)
//       dispatcher.Initialize(ctx);                           // CoordinatorBase.Initialize(EventCoordinationContext), CoordinatorBase.cs:19
//       return (dispatcher, input);
//   }
//
// Every message carries IsPreciseTarget: true so HandleActionRequested never reads
// Context.GazeEngine.StableCentroid (MakeContext leaves GazeEngine null!).

[Fact]
public void ModifiedLeftClick_RoutesToTheModifiedOverload()
{
    var (dispatcher, simulator) = BuildWithFakeInput();
    var mods = new HashSet<ModifierKey> { ModifierKey.Shift };

    dispatcher.HandleActionRequested(new ActionRequestedMessage(
        InteractionAction.LeftClick, new PhysicalPoint(10, 10), IsPreciseTarget: true, Modifiers: mods));

    Check.That(simulator.ModifiedClicks).ContainsExactly(
        (InteractionAction.LeftClick, new PhysicalPoint(10, 10), mods));
    Check.That(simulator.BareClickCount).IsEqualTo(0);
}

[Fact]
public void EmptySetMessage_NeverTouchesTheModifiedOverload()
{
    var (dispatcher, simulator) = BuildWithFakeInput();

    // The VM sends null when the latch is empty — a mods != null gating mutant would route
    // through the modified overload. Same observable behaviour, but the null path is the
    // contract: the modified overload must not be reachable with an empty set.
    dispatcher.HandleActionRequested(new ActionRequestedMessage(
        InteractionAction.LeftClick, new PhysicalPoint(10, 10), IsPreciseTarget: true, Modifiers: new HashSet<ModifierKey>()));

    Check.That(simulator.ModifiedClicks).IsEmpty();
    Check.That(simulator.BareClickCount).IsEqualTo(1);
}

[Fact]
public void BareLeftClick_NeverTouchesTheModifiedOverload()
{
    var (dispatcher, simulator) = BuildWithFakeInput();

    dispatcher.HandleActionRequested(new ActionRequestedMessage(
        InteractionAction.LeftClick, new PhysicalPoint(10, 10), IsPreciseTarget: true));

    Check.That(simulator.ModifiedClicks).IsEmpty();
    Check.That(simulator.BareClickCount).IsEqualTo(1);
}

[Fact]
public void AllFourModifiedClickArms_RouteTheirAction()
{
    var (dispatcher, simulator) = BuildWithFakeInput();
    var mods = new HashSet<ModifierKey> { ModifierKey.Ctrl };

    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.RightClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.MiddleClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.DoubleClick, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));

    Check.That(simulator.ModifiedClicks.Select(c => c.Action)).ContainsExactly(
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.MiddleClick, InteractionAction.DoubleClick);
}

[Fact]
public void ModifiedScroll_SendsOneNotch_SignCorrectPerArm()
{
    var (dispatcher, simulator) = BuildWithFakeInput();
    var mods = new HashSet<ModifierKey> { ModifierKey.Ctrl };

    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollUp, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollDown, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollLeft, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));
    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollRight, new PhysicalPoint(0, 0), IsPreciseTarget: true, Modifiers: mods));

    Check.That(simulator.ModifiedScrolls).ContainsExactly(
        ((int Dx, int Dy))(0, 1), (0, -1), (-1, 0), (1, 0));
}

[Fact]
public void UnmodifiedScroll_KeepsThreeNotches()
{
    var (dispatcher, simulator) = BuildWithFakeInput();

    dispatcher.HandleActionRequested(new ActionRequestedMessage(InteractionAction.ScrollUp, new PhysicalPoint(0, 0), IsPreciseTarget: true));

    Check.That(simulator.BareScrolls).ContainsExactly(((int Dx, int Dy))(0, 3));
}
```

- [ ] **Step 2: Extend `FakeInputSimulator` (TestFakes.cs:342 area) — record in the EXISTING bare methods too, and make the modified overloads reject empty sets (a blind nullable pass must fail loudly, spec mechanism 5)**

```csharp
public List<(InteractionAction Action, PhysicalPoint Point, IReadOnlySet<ModifierKey> Modifiers)> ModifiedClicks { get; } = new();
public int BareClickCount { get; private set; }
public List<(int Dx, int Dy)> ModifiedScrolls { get; } = new();
public List<(int Dx, int Dy)> BareScrolls { get; } = new();

// The BARE methods are where the dispatcher's unmodified route lands — record there:
public void LeftClick(PhysicalPoint point) => BareClickCount++;
public void Scroll(PhysicalPoint point, int deltaX, int deltaY) => BareScrolls.Add((deltaX, deltaY));

// The MODIFIED overloads throw on null/empty (the dispatcher must never pass them a nullable)
// and record otherwise:
public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 })
        throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
    ModifiedClicks.Add((InteractionAction.LeftClick, point, modifiers));
}

public void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 })
        throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
    ModifiedClicks.Add((InteractionAction.RightClick, point, modifiers));
}

public void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 })
        throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
    ModifiedClicks.Add((InteractionAction.MiddleClick, point, modifiers));
}

public void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 })
        throw new InvalidOperationException("dispatcher routed a bare click through the modified overload");
    ModifiedClicks.Add((InteractionAction.DoubleClick, point, modifiers));
}

public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
{
    if (modifiers is not { Count: > 0 })
        throw new InvalidOperationException("dispatcher routed a bare scroll through the modified overload");
    ModifiedScrolls.Add((deltaX, deltaY));
}
```

- [ ] **Step 3: Implement the routing in `HandleActionRequested`** — replace the click/scroll arms of the switch (`:170–181`) — the log line gains `Modifiers={Modifiers}`:

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

- [ ] **Step 3b: The adapter end-to-end test (kills the `:107`/`:334` forwarding mutants)** — the ownership `Harness` (HideActionMenuBarOwnershipTests.cs:149–151) already wires a REAL `GazeEngineAdapter`; add to its file:

```csharp
// The ownership Harness wires a REAL GazeEngineAdapter, but its Messenger is a constructor
// local — EXPOSE it (add `public IMessenger Messenger { get; }` to the private Harness, set
// from the ctor local). The adapter marshals via BeginInvoke, so every step pumps.
[StaFact]
public void ActionSelectedMessage_Modifiers_SurviveToActionRequestedMessage()
{
    using var h = new Harness();
    var received = new List<ActionRequestedMessage>();
    h.Messenger.Register<ActionRequestedMessage>(h, (_, m) => received.Add(m));

    // Reach the menu state: FreeLook OnActionSelected returns without raising.
    h.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
    h.InteractionManager.OnActivationButtonDwelled();
    h.Pump();

    h.Messenger.Send(new ActionSelectedMessage(InteractionAction.LeftClick, new HashSet<ModifierKey> { ModifierKey.Shift }));
    h.Pump();

    Check.That(received.Single().Modifiers!.Contains(ModifierKey.Shift)).IsTrue(); // kills :107/:334
}
```


- [ ] **Step 4: Implement the routing in `HandleActionRequested`**- [ ] **Step 4: Implement the routing in `HandleActionRequested`**

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

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

public class ActionMenuViewModelLatchTests
{
    // Drag is on the DEFAULT secondary layer (PrimaryActions is LeftClick, RightClick,
    // DoubleClick, ScrollMode); ScrollMode is already primary. The settings pin all three
    // onto layer one so the dimming is tested where the owner meets it.
    private static EyeCtrlSettings SettingsWithDragOnLayerOne()
    {
        var settings = new EyeCtrlSettings();
        settings.ActionMenu.PrimaryActions = new List<InteractionAction>
        { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode };
        return settings;
    }

    private static (ActionMenuViewModel Vm, WeakReferenceMessenger Messenger, List<ActionSelectedMessage> Sent) Build()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(SettingsWithDragOnLayerOne(), messenger);
        var sent = new List<ActionSelectedMessage>();
        messenger.Register<ActionSelectedMessage>(vm, (_, m) => sent.Add(m)); // vm keeps the recipient alive
        return (vm, messenger, sent);
    }

    private static ShowActionMenuMessage ShowMessage() => new(
        Center: new PhysicalPoint(500, 500),
        Actions: new List<InteractionAction>
        { InteractionAction.LeftClick, InteractionAction.Drag, InteractionAction.ScrollMode,
          InteractionAction.MiddleClick }, // secondary: the More target exists for the survival test
        CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false);
    // (Match the record's ACTUAL parameter list per InteractionMessages.cs — the construction
    // in ActionMenuViewModelLayerTests is the authoritative pattern.)

    [Fact]
    public void Toggle_LatchesAndUnlatches_RaisingLatchedModifiers()
    {
        var (vm, _, _) = Build();
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
        var (vm, _, _) = Build();

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        Check.That(vm.LatchedModifiers).IsEmpty();
    }

    [Fact]
    public void ShowResetsTheLatch_BeforeTheDimRead_EvenWithoutAnInterveningHide()
    {
        var (vm, _, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl)); // Drag+ScrollMode dimmed

        vm.Receive(ShowMessage()); // NO hide in between: the reset must happen before BuildLayer

        Check.That(vm.LatchedModifiers).IsEmpty();
        Check.That(vm.Items.First(i => i.Action == InteractionAction.Drag).IsDisabled).IsFalse();
        Check.That(vm.Items.First(i => i.Action == InteractionAction.ScrollMode).IsDisabled).IsFalse();
    }

    [Fact]
    public void Crossing_BumpsRingVersion_NonCrossingToggle_DoesNot()
    {
        var (vm, _, _) = Build();
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
    public void DimmedItems_AreNotDwellTargets_KeepSlots_AndNeverFire()
    {
        var (vm, messenger, sent) = Build();
        vm.Receive(ShowMessage());
        var dragBefore = vm.Items.First(i => i.Action == InteractionAction.Drag).Position;
        var scrollBefore = vm.Items.First(i => i.Action == InteractionAction.ScrollMode).Position;

        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl)); // crossing: dim + rebuild

        var drag = vm.Items.First(i => i.Action == InteractionAction.Drag);
        var scrollMode = vm.Items.First(i => i.Action == InteractionAction.ScrollMode);
        Check.That(drag.IsDisabled).IsTrue();
        Check.That(scrollMode.IsDisabled).IsTrue();
        Check.That(drag.Position).IsEqualTo(dragBefore);       // slot geometry unchanged
        Check.That(scrollMode.Position).IsEqualTo(scrollBefore);

        // NOT a dwell target: a 100 ms-cadence full dwell at Drag's slot fires nothing AND
        // raises no progress (progress events only exist for registered targets). An
        // unconditional-AddTarget mutant fails both — the OnTargetActivated backstop alone
        // would still swallow a Drag activation, which is why the neighbour progress is
        // asserted too: the detector is alive and awarding dwell to LeftClick.
        for (var t = 0; t < 6; t++)
            vm.Receive(new GazeCursorMovedMessage(drag.Position, t * 100_000));
        // Mid-dwell progress pin: the unconditional-AddTarget mutant shows 5/6 here (the
        // detector awards dwell); the correct impl shows 0 (no target registered).
        Check.That(drag.DwellProgress).IsEqualTo(0);
        Check.That(sent.Where(m => m.Action == InteractionAction.Drag)).IsEmpty();

        var click = vm.Items.First(i => i.Action == InteractionAction.LeftClick);
        for (var t = 10; t < 20; t++)
            vm.Receive(new GazeCursorMovedMessage(click.Position, t * 100_000));
        // A full dwell on the neighbour fires it: the detector is alive and awarding dwell.
        Check.That(sent.Where(m => m.Action == InteractionAction.LeftClick)).IsNotEmpty();
    }

    [Fact]
    public void Latch_SurvivesMoreAndBackToPrimary()
    {
        var (vm, _, _) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Ctrl));

        vm.ActivateForTest("More");
        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsTrue();

        vm.ActivateForTest("BackToPrimary");
        Check.That(vm.LatchedModifiers.Contains(ModifierKey.Ctrl)).IsTrue();
    }

    [Fact]
    public void Capture_CopiesTheLatch_ToggleThenHideCannotEmptyTheMessage()
    {
        var (vm, messenger, sent) = Build();
        vm.Receive(ShowMessage());
        vm.Receive(new ToggleActionMenuModifierMessage(ModifierKey.Shift));

        vm.ActivateForTest("LeftClick");

        Check.That(sent.Single().Modifiers!.Contains(ModifierKey.Shift)).IsTrue();

        vm.Receive(new HideActionMenuMessage());
        Check.That(sent.Single().Modifiers!.Contains(ModifierKey.Shift)).IsTrue(); // aliased set would be empty now
    }
}
```

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

Declare the interface — the class's `IRecipient<...>` list gains `IRecipient<ToggleActionMenuModifierMessage>` (registration is `RegisterAll` via `MessengerViewModelBase`/`ObservableRecipient`; without the interface the method below is never called in production):

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

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

public class EdgeButtonBarViewModelRearmTests
{
    // The DETECTOR MODEL (round-1 review, F9): the first inside sample only SETS
    // EnteredTimestamp (DwellDetector.cs:363–367); a LATER sample with elapsed ≥ dwell fires
    // (:368–385). Every timeline below uses two inside samples to toggle. D = the debounce,
    // 150 ms = 150_000 µs — the MaxCreditedSampleGapUs precedent.
    private const long D = 150_000;
    private static readonly PhysicalPoint Anchor = new(500, 500);

    private static EdgeButtonDefinition Def(string id, List<string> toggles, double verticalOffset = 0,
        int dwellMs = 1, bool rearm = true, Action? afterToggle = null) => new(
        id, Symbol: string.Empty, DwellMs: dwellMs,
        Activate: _ => { toggles.Add(id); afterToggle?.Invoke(); },
        VerticalOffset: verticalOffset, RadiusLogical: 50, Label: id, RearmOnGazeExit: rearm);

    private static void Look(EdgeButtonBarViewModel vm, PhysicalPoint p, long t, bool inGap = false, bool paused = false)
        => vm.Receive(new GazeCursorMovedMessage(new GazeFrame(p, t, IsInGap: inGap)) { IsPaused = paused });
    // GazeFrame is (Position, TimestampMicroseconds, IsInGap) — IsPaused is an init property
    // ON THE MESSAGE (GazeMessages.cs:48), not a GazeFrame member.

    private static void Hold(EdgeButtonBarViewModel vm, PhysicalPoint p, long startUs, int samples)
    {
        for (var t = 0; t < samples; t++)
            Look(vm, p, startUs + t * 100_000);
    }

    [Fact]
    public void HeldGaze_TogglesExactlyOnce()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 1_000_000);              // sample 1: enter
        Look(vm, Anchor, 1_100_000);              // sample 2: elapsed ≥ dwell ⇒ toggle; pending set
        Hold(vm, Anchor, 1_200_000, 20);          // held: the gate feeds hits-nothing

        Check.That(toggles).ContainsExactly("mod");
    }

    [Fact]
    public void TwoFlickers_KillTheNeverResetClock()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 0);                      // enter
        Look(vm, Anchor, 100_000);                // toggle #1 (100 ms ≥ 1 ms dwell); pending
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 200_000);  // flicker #1: one outside sample
        Hold(vm, Anchor, 300_000, 6);             // back inside (600 ms held — a never-reset clock that
                                                  // only counts outside samples would NOT clear here)
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 900_000);  // flicker #2: one outside sample
        Hold(vm, Anchor, 1_000_000, 8);           // held again, ≥ dwell + margin

        Check.That(toggles).ContainsExactly("mod"); // a never-reset clock mutant's exit span
                                                    // 200k→900k (700 ms ≥ D) clears the pending
                                                    // and the hold re-toggles here
    }

    [Fact]
    public void SustainedExit_ReArms_ASecondEntryTogglesAgain()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 0);                      // enter
        Look(vm, Anchor, 100_000);                // toggle #1 (100 ms ≥ 1 ms dwell); pending
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 200_000);  // OUTSIDE: clock starts
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 500_000);  // outside: 400 ms ≥ D ⇒ cleared
        Hold(vm, Anchor, 1_000_000, 8);           // deliberate re-entry

        Check.That(toggles).ContainsExactly("mod", "mod");
    }

    [Fact]
    public void GapSamples_RestartTheExitClock()
    {
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Look(vm, Anchor, 0);
        Look(vm, Anchor, 100_000);                // toggle (100 ms ≥ 1 ms dwell); pending
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 700_000);  // OUTSIDE: clock starts
        for (var t = 0; t < 4; t++)
            Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 800_000 + t * 100_000, inGap: true); // blink
        Look(vm, new PhysicalPoint(Anchor.X + 500, Anchor.Y), 1_400_000); // reopen OUTSIDE: the in-gap
                                                  // samples nulled the clock, so this restarts at 1.4M
                                                  // with span 0 — without the restarts, 700k→1.4M ≥ D
                                                  // would clear the pending here
        Hold(vm, Anchor, 1_500_000, 8);           // back inside and held

        Check.That(toggles).ContainsExactly("mod"); // the gap restart kept the pending alive: no re-toggle
    }

    [Fact]
    public void Lens_NeighbourCompletes_SupersedeHeals_TheFirstButtonNotDead()
    {
        var registry = new DwellDetectorRegistry();
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink: null, registry);
        // Vertical stack: shift at −spacing, ctrl at 0 — the offsets are PHYSICAL px, so the
        // spacing comes from DpiHelper (scale-independent test).
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        vm.ShowButtons(new[]
        {
            Def("shift", toggles, verticalOffset: -spacing, dwellMs: 600),
            Def("ctrl", toggles, verticalOffset: 0, dwellMs: 600),
        }, Anchor);
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3); // scale-independent lens: 3× regions overlap at any DPI

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 0, 8);   // shift toggles; pending(shift)

        // The lens: ctrl-nearest but inside shift's 3× region — gaze 0.7 of the way to ctrl.
        var lensPoint = new PhysicalPoint(Anchor.X, Anchor.Y - spacing + (long)(spacing * 0.7));
        Hold(vm, lensPoint, 2_000_000, 8);          // ctrl accumulates a full dwell ⇒ ctrl toggles

        Check.That(toggles).ContainsExactly("shift", "ctrl"); // gate did not dead-zone ctrl

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 4_000_000, 8); // return to shift

        Check.That(toggles.Count(s => s == "shift")).IsEqualTo(2); // supersede healed it: no dead button
    }

    [Fact]
    public void LensReturn_KillsTheClockViaNearestHittingMutant()
    {
        var registry = new DwellDetectorRegistry();
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink: null, registry);
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        vm.ShowButtons(new[] { Def("shift", toggles, verticalOffset: -spacing, dwellMs: 600) }, Anchor);
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3);

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 0, 8);       // toggle; pending
        // A second NON-rearm definition gives the lens a ctrl-nearest target whose toggle
        // would be visible if the excursion dwelled it:
        vm.AddButton(Def("probe", toggles, verticalOffset: 0, dwellMs: 600, rearm: false));
        // In the lens the exit clock (region membership) does NOT run — gaze is inside shift's
        // region. A nearest-hitting mutant runs it and clears the pending after D.
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - (long)(spacing * 0.3)), 2_000_000);
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - (long)(spacing * 0.3)), 2_100_000);
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - (long)(spacing * 0.3)), 2_200_000); // excursion ≥ D:
        // the nearest-hitting mutant runs the clock through the lens and clears the pending here

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 3_000_000, 8); // return + full dwell

        Check.That(toggles).ContainsExactly("shift"); // no re-toggle without a genuine exit
    }

    [Fact]
    public void PartialCredit_GatedCrossing_ClearsNeighbourDwell_NotDead()
    {
        var registry = new DwellDetectorRegistry();
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(), sink: null, registry);
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        vm.ShowButtons(new[]
        {
            Def("shift", toggles, verticalOffset: -spacing, dwellMs: 600),
            Def("ctrl", toggles, verticalOffset: 0, dwellMs: 600, rearm: false),
        }, Anchor);
        foreach (var registration in registry.Registrations)
            registration.ApplyGeometry(baseExtent => baseExtent * 3);

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 0, 8);       // shift toggles about T+600; pending
        // Ctrl accumulates a PARTIAL dwell, then the gated crossing happens INSIDE the
        // detector's 150 ms re-anchor window — so the skip mutant's kept EnteredTimestamp
        // fires on the first sample back (elapsed over dwell), while the hits-nothing feed
        // cleared it and the correct impl re-dwells from zero.
        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y), 1_000_000, 6);         // ctrl 1.0M..1.5M: elapsed 500 ms
                                                                               // at the last ungated sample
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 1_550_000);  // gated crossing (shift-nearest)
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y - spacing), 1_600_000);
        Look(vm, new PhysicalPoint(Anchor.X, Anchor.Y), 1_620_000);            // back on ctrl: last ungated
                                                                               // sample was 1.5M, silence 120 ms
                                                                               // < 150 ms, so NO re-anchor; the
                                                                               // skip mutant's kept timestamp gives
                                                                               // elapsed 620k ≥ dwell ⇒ fires HERE
        Check.That(toggles).ContainsExactly("shift");                          // correct impl: cleared, no fire

        Hold(vm, new PhysicalPoint(Anchor.X, Anchor.Y), 2_000_000, 8);         // correct impl: fresh dwell
        Check.That(toggles.Count(s => s == "ctrl")).IsEqualTo(1);              // the button is not dead
    }

    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    [Fact]
    public void GatedSamples_PublishNoDwellOutcome_RealActivations_PublishTheCue()
    {
        var sink = new RecordingSink();
        var messenger = new WeakReferenceMessenger();
        var cues = new List<DwellActivationMessage>();
        messenger.Register<DwellActivationMessage>(messenger, (_, m) => cues.Add(m));
        var toggles = new List<string>();
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger, sink, null);
        vm.ShowButtons(new[] { Def("mod", toggles) }, Anchor);

        Hold(vm, Anchor, 0, 8);                            // real activation: outcome + cue
        Check.That(sink.Outcomes.Count(o => o.Kind == OutcomeKind.Activated)).IsEqualTo(1);
        Check.That(cues).HasSize(1);

        var before = sink.Outcomes.Count;
        Hold(vm, Anchor, 3_000_000, 8);                    // gated run: hits-nothing, no outcomes, no cue
        Check.That(sink.Outcomes.Count).IsEqualTo(before);
        Check.That(cues).HasSize(1);
        // Partial-dwell abandon suppression: build a REAL partial dwell on a second button,
        // then gate the first — the filtered crossing clears the neighbour without publishing
        // an Abandoned outcome (DwellDetector.cs:393–394).
        var toggles2 = new List<string>();
        var vm2 = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger, sink, null);
        vm2.ShowButtons(new[]
        {
            Def("shift2", toggles2, verticalOffset: -DpiHelper.LogicalToPhysicalY(120), dwellMs: 600),
            Def("ctrl2", toggles2, verticalOffset: 0, dwellMs: 600, rearm: false),
        }, new PhysicalPoint(Anchor.X, Anchor.Y + 500));
        Hold(vm2, new PhysicalPoint(Anchor.X, Anchor.Y + 500), 10_000_000, 4); // ctrl2 partial (400 ms)
        Hold(vm2, new PhysicalPoint(Anchor.X, Anchor.Y + 500 - DpiHelper.LogicalToPhysicalY(120)), 11_000_000, 2); // shift2 toggle; pending
        Hold(vm2, new PhysicalPoint(Anchor.X, Anchor.Y + 500), 12_000_000, 2); // gated crossing
        var abandoned = sink.Outcomes.Count(o => o.Kind == OutcomeKind.Abandoned);
        Check.That(abandoned).IsEqualTo(0);               // filtered targets publish no abandon
    }

    [Fact]
    public void ReplaceButton_MissingId_NoOp_AndCooldownSurvives()
    {
        var toggles = new List<string>();
        var vmRef = new List<EdgeButtonBarViewModel>();
        // 'a's Activate mirrors production's synchronous chain: toggle, then ReplaceButton runs
        // INSIDE the detector event (this is where a ResetCooldown mutant would do its damage).
        var aDef = Def("a", toggles, dwellMs: 600, afterToggle: () =>
            vmRef.Single().ReplaceButton("a", Def("a", toggles, dwellMs: 600, rearm: false)));
        // 'b' sits on its OWN slot: same-center buttons made the round-1 version flake on the
        // ConcurrentDictionary tie-break.
        var spacing = DpiHelper.LogicalToPhysicalY(2 * UIConstants.ButtonRadius + UIConstants.ManualSelectionSideButtonGap);
        var bCenter = new PhysicalPoint(Anchor.X, Anchor.Y - spacing);
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vmRef.Add(vm);
        vm.ShowButtons(new[] { aDef, Def("b", toggles, dwellMs: 600, verticalOffset: -spacing) }, Anchor);

        Check.ThatCode(() => vm.ReplaceButton("missing", Def("missing", toggles))).DoesNotThrow();

        Hold(vm, Anchor, 0, 8);                                // 'a' toggles about T+600; pending; ReplaceButton ran
        // Cooldown survival: 'b' cannot accumulate until the cooldown elapses (T+400 ms), so it
        // completes about T+1600 — a ShowButtons mutant (ResetCooldown) completes about T+1300.
        Hold(vm, bCenter, 700_000, 7);                         // T+700..T+1300
        Check.That(toggles).ContainsExactly("a");              // the mutant would have b here
        Hold(vm, bCenter, 1_400_000, 4);                       // correct impl: b about T+1600
        Check.That(toggles).ContainsExactly("a", "b");
    }
}
```

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
    // The PRIMARY latched cue is the rim in the STROKE geometry (outline-level, peripheral-
    // vision rule) — pin that the latched stroke differs from the unlatched glyph:
    Check.That(ActionIcons.For(ActionIcon.ShiftLatched).Stroke!.ToString().Contains(LatchedRim)).IsTrue();
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
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

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
    Check.That(defs[0].VerticalOffset).IsStrictlyLessThan(0);
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

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "(Category!=UI&FullyQualifiedName~ScreenGeometry)|FullyQualifiedName~ActionMenuModifierButtons" -v minimal`
Expected: PASS. (The coordinator wiring that consumes `ActionMenuModBar` lands in Task 7, after the interface member and second window exist.)

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "(Category!=UI&FullyQualifiedName~ScreenGeometry)|FullyQualifiedName~ActionMenuModifierButtons" -v minimal`
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
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayMessageCoordinator.cs:113-190`
- Modify: `src/EyeCtrl.App/Services/Coordinators/OverlayVmBindingCoordinator.cs:282-316`
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
/// The test fakes gain an explicit optional slot for it (round-2 F9).
/// </summary>
IEdgeButtonBar ActionMenuModBar => throw new NotSupportedException("This overlay manager does not implement the action-menu modifier bar.");
```

And in the `OverlayViewModels` record (IOverlayManager.cs:114), after `EdgeButtonBarViewModel EdgeButtonBar,`:

```csharp
EdgeButtonBarViewModel ActionMenuModBar,
```

Fix the construction sites (`App.xaml.cs:198`, `AppBootstrapper.cs:627`) and the `ResolveViewModels` tuple (`App.xaml.cs:572–595`), PLUS the deconstruction at `App.xaml.cs:185`, by adding the member in position.

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

`ResolveViewModels` (`:627+`): `ActionMenuModBar: sp.GetRequiredKeyedService<EdgeButtonBarViewModel>("actionMenuModBar"),` — `GetRequiredService<T>` has NO key overload; keyed resolution is `GetRequiredKeyedService`. The unkeyed `GetRequiredService<EdgeButtonBarViewModel>()` at `:637` still resolves the right bar (unkeyed resolution ignores keyed registrations).

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

Hide handler — PLACEMENT (round-1 F19): the right-bar guarded `Hide()` currently shares the outer try with the TargetHighlight hide; give EACH bar its own try and leave the TargetHighlight hide in the outer one, ordering right bar then left bar (unconditional — nothing else ever owns the left bar). Minimal correct form: right-bar hide in its own try first, left-bar hide in its own try second, TargetHighlight last:

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

`OverlayVmBindingCoordinator` — the `LatchedModifiers` listener is HOISTED into its own handler (spec mechanism 9: the try/catch must enclose the handler prologue, and an `else if` inside `OnActionMenuVMPropertyChanged` cannot — the prologue at `:286-288` dereferences `ActionMenu`, `CursorCircle` and `ActivationButton` before any branch, so a throw there escapes into the mod bar's `DwellDetector.Update`). The hoisted handler has NO prologue (it touches only `OverlayManager.ActionMenuModBar` and `ActionMenuVM`) and is fully try-wrapped:

```csharp
// OnInitialize — subscribed AFTER OnActionMenuVMPropertyChanged so the ring rebuild (main
// handler) precedes the bar-icon swap on a crossing:
Context.ActionMenuVM.PropertyChanged += OnActionMenuLatchedModifiersChanged;

// OnDispose (next to the existing unsubscribe at :450):
Context.ActionMenuVM.PropertyChanged -= OnActionMenuLatchedModifiersChanged;

/// <summary>
/// Swaps the toggled mod button's icon variant. A separate handler rather than a branch of
/// <see cref="OnActionMenuVMPropertyChanged"/>: that handler's prologue dereferences three
/// overlays before any branch, and this swap runs INSIDE the mod bar's synchronous toggle
/// chain — a throw must be caught here, never escape into the gaze-sample dispatch.
/// </summary>
private void OnActionMenuLatchedModifiersChanged(object? sender, PropertyChangedEventArgs e)
{
    if (Context is null
        || e.PropertyName != nameof(ActionMenuViewModel.LatchedModifiers)
        || !Context.ActionMenuVM.IsVisible) return;

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
[StaFact]
public void ShowActionMenu_ShowsTheModBar_EvenWithoutAResolvedControl()
{
    var h = BuildContext(); // ownership-test harness + the new slots; the harness MUST    // supply a real ActionMenuVM (MakeContext nulls it, TestFakes.cs:34 - the show handler    // reads ActionMenuVM.LatchedModifiers inside its try, so a null NREs and the mod bar    // never shows)
    h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));
    h.PumpDispatcher();     // the harness's dispatcher pump — see HideActionMenuBarOwnershipTests

    Check.That(h.ModBarCalls).Contains("ShowButtons");
}

[StaFact]
public void HideActionMenu_HidesTheModBar_Unconditionally_EvenWhenTheRightBarIsOwnedByADrag()
{
    var h = BuildContext();
    // Reach Dragging the way the ownership tests do: fixation, then activation dwell (menu
    // shown), then select Drag. OnActionSelected(Drag) from FreeLook would be a no-op.
    h.InteractionManager.OnFixationStarted(new PhysicalPoint(500, 500));
    h.InteractionManager.OnActivationButtonDwelled();
    h.PumpDispatcher();
    h.InteractionManager.OnActionSelected(InteractionAction.Drag); // DragStarted: drag owns the right bar
    h.PumpDispatcher();
    h.Messenger.Send(new HideActionMenuMessage());
    h.PumpDispatcher();

    Check.That(h.ModBarCalls).Contains("Hide");
    Check.That(h.RightBarCalls).DoesNotContain("Hide"); // ownership guard held
}

[StaFact]
public void ThrowingModBar_DoesNotPreventTheRightBarHide()
{
    var h = BuildContext(withModBar: false); // mod-bar slot absent ⇒ the left-bar hide throws;
    // TargetHighlightVM.IsVisible = true (the TargetHighlight hide is the casualty under test)
    h.TargetHighlightVM.IsVisible = true;
    h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));
    h.PumpDispatcher();
    h.Messenger.Send(new HideActionMenuMessage());
    h.PumpDispatcher();

    Check.That(h.RightBarCalls).Contains("Hide"); // the right-bar hide ran before the throw
    Check.That(h.TargetHighlightVM.IsVisible).IsFalse(); // the TargetHighlight hide also ran
}

[StaFact]
public void ThrowingModBar_DoesNotPreventTheRightBarShow()
{
    var h = BuildContext(withModBar: false); // PartialOverlayManager WITHOUT the slot ⇒ default throws
    h.Messenger.Send(new ShowActionMenuMessage(new PhysicalPoint(500, 500), new List<InteractionAction> { InteractionAction.LeftClick }, CircleSize: 250, DwellTimeMs: 600, GazePoint: new PhysicalPoint(500, 500), ShowBack: false));
    h.PumpDispatcher();

    Check.That(h.RightBarCalls).Contains("ShowButtons"); // own try block isolated the fault
}
```

- [ ] **Step 7: The masked-mutation + held-gaze App test** (`ActionMenuModBarMaskedMutationTests.cs`) — built on the precedent harness (`EdgeButtonBarZOrderTests.cs:146–164` / `ManualSelectionAnchorWiringTests.cs:50–65`: every VM on ONE messenger + a real `Dispatcher`). Requirements checklist from the spec (round-4 F4 + round-5 F6): real VMs for ActionMenu + BOTH bars; `Settings` real; `MakeContext`'s `null!` VMs supplied (coordinator subscribes eleven VMs — `:43–80`); the mod-bar fake's `EdgeButtonBarViewModel` on the SHARED messenger; `PartialOverlayManager` slots for mod bar + `ActionMenu` + `GlassBackdrop`; a real or pumped `Dispatcher`.

```csharp
[StaFact]
public void HeldGaze_TogglesExactlyOnce_ThroughTheRealCoordinator()
{
    var h = BuildFullHarness(); // real coordinator + real VMs; mod-bar VM on the shared messenger
    h.ShowActionMenu();                                // ShowActionMenuMessage + dispatcher pump
    for (var t = 0; t <= 6; t++)
        h.LookAt(ShiftCenter, t * 100_000);            // 100 ms cadence, dwell 600 ms: Shift fires
        // at 600k (elapsed 600 ms at the 600k sample)
    for (var t = 7; t <= 30; t++)
        h.LookAt(ShiftCenter, t * 100_000);            // held: the gate feeds hits-nothing

    Check.That(h.ShiftToggleCount).IsEqualTo(1);
}

// Centers are DERIVED, never hardcoded physical px (CI runs 96 DPI, the box 200 %):
// ShiftCenter/CtrlCenter come from the anchor + DpiHelper-scaled offsets exactly as
// ActionMenuModifierButtons computes them (Shift at minus-spacing, Ctrl at 0).
[StaFact]
public void ShowButtonsMutant_IsCaught_AtT700ZeroFires_ThenCompletes()
{
    // Seven inside samples toggle Shift (100 ms cadence, fires at 600k):
    // correct impl - the cooldown gate blocks Ctrl accumulation START until T+400 ms, so
    //          Ctrl completes at about T+1000;
    // ShowButtons mutant - ResetCooldown means Ctrl accumulates from first contact and fires
    //          at about T+616.
    var h = BuildFullHarness();
    h.ShowActionMenu();
    for (var t = 0; t <= 6; t++)
        h.LookAt(ShiftCenter, t * 100_000);            // 100 ms cadence: Shift toggles at 600k = T
    // T+700 and T+1100 are TOGGLE-RELATIVE (T = 600k): correct fires at T+1000 = 1.6M;
    // the mutant fires at T+616 = 1.216M. Ctrl sampled densely from T+16k.
    for (var t = 616; t <= 1_300; t += 21)             // T+16k .. T+700k
        h.LookAt(CtrlCenter, t * 1_000);

    Check.That(h.CtrlToggleCount).IsEqualTo(0);        // the mutant fires by T+616 (1.216M)

    for (var t = 1_301; t <= 1_700; t += 25)           // T+701k .. T+1100k
        h.LookAt(CtrlCenter, t * 1_000);

    Check.That(h.CtrlToggleCount).IsEqualTo(1);        // correct impl fires at T+1000 (1.6M)
}
```

(The harness helper `BuildFullHarness` is written ONCE in this test file, copying the two
precedent files' construction; `ShiftCenter`/`CtrlCenter` are DERIVED from
`ScreenGeometry.LeftEdgeAnchor(...)` plus the DpiHelper-scaled definition offsets - Shift at
minus-spacing, Ctrl at 0 - never hardcoded physical px, because CI runs 96 DPI while the dev
box runs 200 %.)

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
- Test: `tests/EyeCtrl.App.Tests/ActionIconDimContrastTests.cs` (new)

**Interfaces:**
- Consumes: Task 4's `ActionMenuItemViewModel.IsDisabled`, Task 6's icon registry.
- Produces: the dim render (group opacity via a per-button child canvas) and capture hygiene for both bars.

- [ ] **Step 1: Child-canvas dim render in `ActionMenuWindow.Show`**

Const: `private const double DisabledOpacity = 0.6;` (the value the contrast test validates) — replace the `btn.AddTo(MenuCanvas);` call (`:229`) with:

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

[StaFact]
public void EdgeButtonBar_IsExcludedFromScreenCapture()
{
    // Follow-up 3's verification: the preview must never bake the edge bars in.
    Check.That(new EdgeButtonBarWindow().ExcludeFromScreenCapture).IsTrue();
}

- [ ] **Step 3: Contrast test** — `tests/EyeCtrl.App.Tests/ActionIconDimContrastTests.cs` (the `HaloContrastTests` matrix lives in THIS project — Controls.Tests does not reference it):

```csharp
private const double DisabledOpacity = 0.6;   // 0.45 measured 2.30:1 dimmed-icon vs dimmed-fill on
                                              // Midnight/#FFFFFF - below any usable floor; 0.6 chosen
                                              // and the floors validated against the computed matrix
                                              // (adjust ONLY with a measured note).
private const double IdentifiabilityFloor = 2.0;
private const double SeparabilityFloor = 1.5;

// ONE MemberData holding the CARTESIAN PRODUCT - two [MemberData] attributes on a
// two-parameter theory are two separate one-column row sources, not a product.
public static TheoryData<string, ITheme> BackdropThemePairs()
{
    var data = new TheoryData<string, ITheme>();
    foreach (var backdrop in HaloContrastTests.Backdrops)   // the repo's nine backdrops
        foreach (var theme in ThemeRegistry.All)             // ThemeRegistry.cs:15 — xUnit v3
                                                             // TheoryData<T> enumerates rows, not T
            data.Add(backdrop, theme);
    // Own-fill silhouette case (round-5 F7): one row per theme whose backdrop equals the
    // theme's own fill hex — the hardest silhouette case.
    foreach (var theme in ThemeRegistry.All)
        data.Add(theme.SurfaceMedium.ToString(), theme);
    return data;
}

[Theory]
[MemberData(nameof(BackdropThemePairs))]
public void DimmedIcon_StaysIdentifiable_AndSeparableFromLive(string backdrop, ITheme theme)
{
    var icon = theme.TextPrimary;      // DwellArcButton's icon brush (NOT theme.IconColor - no such member)
    var fill = theme.SurfaceMedium;    // the button fill (DwellArcButton.cs:165)

    var dimmed = Blend(icon, DisabledOpacity, backdrop);
    var dimmedFill = Blend(fill, DisabledOpacity, backdrop);

    Check.That(Contrast(dimmed, dimmedFill)).IsGreaterOrEqualThan(IdentifiabilityFloor);
    Check.That(Contrast(Contrast(icon, backdrop) > Contrast(fill, backdrop) ? dimmed : dimmedFill, icon)).IsStrictlyGreaterThan(SeparabilityFloor);
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

(The dwell-blend rule is moot by construction - a dimmed item has no target, so `ProgressPath` stays empty; pinned by the ViewModels no-target test. `IsStrictlyGreaterThan`/`IsStrictlyLessThan` are the NFluent 3.1 double comparators - plain `IsGreaterThan`/`IsLessThan` exist only for `TimeSpan`.)- [ ] **Step 4: Run Controls + ViewModels + App suites**

Run: `dotnet test tests/EyeCtrl.Controls.Tests -v minimal` then `dotnet test tests/EyeCtrl.ViewModels.Tests -v minimal` then `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI" -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys add -A
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys commit -m "feat(mods): child-canvas dim render, edge bars excluded from capture, dim contrast test"
```

### Task 9: Final gate

- [ ] **Step 1: Full non-UI suite, SEQUENTIALLY per project (Global Constraint: never two suites concurrently)**

Run each in turn:
`dotnet test tests/EyeCtrl.Core.Tests -v minimal`
`dotnet test tests/EyeCtrl.Platform.Tests -v minimal`
`dotnet test tests/EyeCtrl.ViewModels.Tests -v minimal`
`dotnet test tests/EyeCtrl.Controls.Tests -v minimal`
`dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI" -v minimal`
`dotnet test tests/EyeCtrl.Supervisor.Tests -v minimal` (if present)
Expected: PASS. `AppLaunchTests` are gated behind `EYECTRL_UI_TESTS=1` and SKIP cleanly without it — compare any unexpected failures against `main`'s baseline on this box before investigating.

- [ ] **Step 2: Graph change analysis (repo rule)**

Run: `node "C:\Users\andre\source\repos\eyectrl\.gitnexus\run.cjs" detect-changes --scope compare --base-ref "main" --worktree "C:\Users\andre\source\repos\eyectrl\.worktrees\action-menu-modifier-keys"` (run.cjs is untracked and lives only in the MAIN checkout — absolute path)
(Compare mode against main — working-tree scope is vacuous after per-task commits.)
ALSO run the repo-mandated impact checks BEFORE the tasks that edit these symbols: `impact({target: "DwellDetector.Update"})`, `impact({target: "SendRaw"})`, `impact({target: "OnActionSelected"})` — record the risk levels in the task notes.
Expected: risk assessed; HIGH/CRITICAL findings reviewed against the spec's blast-radius section before proceeding.

- [ ] **Step 3: Spec-conformance sweep**

Walk `docs/superpowers/specs/2026-09-25-action-menu-modifier-keys-design.md` mechanism items 1–10 and the mutation list; every bullet has a corresponding commit in this branch's log (`git -C ... log --oneline origin/main..HEAD`).

- [ ] **Step 4: Final commit (if any straggler changes) and hand-off**

```bash
git -C /c/Users/andre/source/repos/eyectrl/.worktrees/action-menu-modifier-keys status --short
```

Expected: clean tree. Report to the owner: implementation complete on `feat/action-menu-modifier-keys`; PR/merge is the owner's call (no push without ask).
