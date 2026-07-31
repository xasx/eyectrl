# ITheme — Theme System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the three validated palettes (Midnight, Ember, Contrast) behind an `ITheme` indirection, make the persisted theme setting actually select one at startup, and delete the dead theme flags that currently pretend to do this.

**Architecture:** `Tokens` stops holding literals and becomes a static *facade* whose members forward to `Tokens.Current`, an `ITheme`. Each theme is a small class declaring 22 colours plus one thickness; `ThemeBase` derives the 23 frozen brushes from those colours so no theme repeats brush plumbing. Because every member stays a *static* member of `Tokens`, all 131 C# `Tokens.` call sites and every `{x:Static theme:Tokens.X}` in `OverlayStyles.xaml` are untouched. At startup the resolved theme is written into `Application.Resources` so `{StaticResource}` consumers see it regardless of when WPF parsed the dictionary.

**Tech Stack:** C# / .NET 10 / WPF, xUnit v3 + NFluent (`Check.That`), `Xunit.StaFact` for dictionary-loading tests, Python 3 for the palette validator.

**Source spec:** `docs/design/overlay-unification-spec.md` — this plan implements §10.6 ("What a theme system requires structurally"). §4.1 is Midnight, §10.2 is Ember, §10.7 is Contrast, §4.4 is the thickness token.

---

## Scope boundaries — read before starting

- **Theme applies at startup, not live.** The recorded decision (`src/EyeCtrl.Controls/Theme/Tokens.cs:15-18`) is that a theme change *recreates windows* rather than re-resolving live, which is why `{StaticResource}` throughout needs no rework. **That decision still holds and is not reopened here.** This plan takes the coarsest correct form of "recreate": the theme is resolved once in `App.OnStartup`, and changing it takes effect on the next launch. Recreating the overlay singletons in place is *not* in scope — `IOverlayManager` hands out 15 long-lived window instances (`src/EyeCtrl.App/Services/IOverlayManager.cs:18-77`) that `EventCoordinator` holds references to, and swapping them at runtime is a separate change that could not be verified without launching the app.
- **The setting must not be a fourth dead knob.** Task 11 labels the picker `Theme (restart to apply)`. That is the whole mitigation; do not add a restart button, and do not silently leave the knob inert. **The supervisor does not offer a shortcut here — see below.**

### Why "restart to apply" rather than a restart button

The obvious objection is that `EyeCtrl.Supervisor` already respawns the app, so the picker could just exit and let the watchdog bring it back. It cannot, and the reason is worth writing down because the next implementer will ask the same thing.

**A clean exit is precisely the case the supervisor refuses to restart.** `App.OnExit` stamps an authenticated `shuttingDown:true` heartbeat before tearing down services, so the supervisor can "distinguish a graceful exit (no restart) from a crash" within its 5s deadline (`src/EyeCtrl.App/App.xaml.cs:304-307`). That drives `Supervising → CleanExitPending → IdleWatch` (`src/EyeCtrl.Supervisor/Supervision/SupervisionStateMachine.cs:90-104`), pinned by `CleanExit_AuthenticatedWithinDeadline_GoesIdleWatch_NoRelaunch` in `tests/EyeCtrl.Supervisor.Tests/Supervision/SupervisionStateMachineTests.cs`. Only an *unannounced* exit relaunches (`SupervisionStateMachine.cs:106-107`).

That leaves three options, two of which the supervisor makes worse rather than better:

1. **Exit uncleanly on purpose.** The supervisor does relaunch — but `GoRecovering` books a strike (`ConsecutiveFailures++`, `_crash.RecordStrike`), applies a backoff delay, and accumulated strikes escalate to `FallenBack`, which launches a fallback AT (Windows Eye Control, `src/EyeCtrl.Supervisor/Fallback/FallbackAtLauncher.cs:84`) and shows a recovery dialog. Burning crash budget to change a colour scheme is a bad trade: a couple of theme experiments plus one genuine crash could push the user into the fallback path. **Rejected.**
2. **Self-relaunch, then exit cleanly.** Architecturally supported: the heartbeat pipe runs in *adoption* mode with exe-path-only binding specifically "so a manually-launched app can connect" (`src/EyeCtrl.Supervisor/Program.cs:461` remarks), and `AdoptRequest` in `IdleWatch` returns straight to `Supervising` (`SupervisionStateMachine.cs:55-58`). But `SingleInstanceGuard` makes the new instance exit immediately while the old one holds the mutex (`App.xaml.cs:81-87`), and that is only released partway through `OnExit` (`App.xaml.cs:311`) — so the relaunch has to be ordered against teardown, and getting it wrong is a race.
3. **Label the picker and apply at startup.** What this plan does.

The decider is the failure mode, not the elegance. Under option 2, if `Process.Start` throws, the supervisor is already in `IdleWatch` and **will not recover** — a clean exit told it to stop watching. The user is left with no app and no watchdog. And `Supervision.Enabled` may be false (`App.xaml.cs:120`), in which case options 1 and 2 have no safety net whatsoever. For a user whose sole input channel is the application being restarted, that is a stranding risk, and it cuts against the same "never strand the user" goal the supervisor's own fallback path cites (`src/EyeCtrl.Supervisor/Program.cs`, `OnFallenBack`).

A "Restart now" affordance is a legitimate follow-up and is planned separately in `docs/superpowers/plans/2026-07-30-restart-now.md`, covering **both** configurations. Where a supervisor answers, it performs the relaunch off a `restartRequested` flag on the final heartbeat — preferred, because it is the only path that recovers a relaunch which then fails. Where none answers, the app restarts itself: option 2's mutex objection is dissolved by a handshake in which the child signals readiness *before* queueing for the mutex, so the parent learns its replacement started while it still holds the mutex and can still change its mind. Either way the app refuses to exit until a successor is proven. It is **not** part of this slice, and it does not change anything here: the picker's label stays correct until that work lands.
- **No settings-migration code.** Single-user project. Legacy JSON keys (`mode`, `overlayOpacity`, `highContrast`, `circleHighContrast`) become unknown members, and both `JsonSerializerOptions` in play leave `UnmappedMemberHandling` at its default (`src/EyeCtrl.Core/Settings/SettingsService.cs:27-32`, `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs:9-13`), so they are ignored on load. Nothing throws; the values are simply dropped.
- **No new palette values.** Every hex in this plan already exists in `tools/palette_validate.py` and has passed all ten gates. If a task tempts you to invent a colour, stop — it has to go through the validator and the spec first.
- **No app launch.** The user controls this PC solely by eye tracking; launching the app or simulating input seizes their only input channel. Every task here is verified by `dotnet build`, `dotnet test --filter "Category!=UI"`, and `python tools/palette_validate.py`. **No task in this plan requires a launch.** If you believe one does, stop and ask for an explicit go-ahead first.

---

## Preconditions — check these before Task 1

The overlay-unification work (PR #72) is **merged into `main`**, so the spec, the validator and the whole token layer are on `main` and this plan branches from there. No dependency on any other branch.

```bash
git merge-base --is-ancestor origin/main HEAD && echo "based on main"
test -f docs/design/overlay-unification-spec.md && echo spec ok
test -f src/EyeCtrl.Controls/Theme/Tokens.cs && echo tokens ok
python tools/palette_validate.py         # -> ALL GATES PASS - 3 theme(s), exit 0
```

If `Tokens.cs` is missing you are on a branch that predates the merge, and every file reference below will be unresolvable.

**Line numbers in this plan are pinned to `4451cdb`** — the merge commit on `main`, whose tree is byte-identical to the `f02cd2a` the plan was originally written against (verified by `git diff main docs/overlay-unification`, empty). If `git log --oneline -1 origin/main` shows something newer, re-verify each citation before editing.

**Baseline to beat:**

```bash
dotnet build EyeCtrl.slnx --nologo                          # 0 errors
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo   # 1710 passing
python tools/palette_validate.py                            # exit 0
```

`Category=UI` needs an interactive desktop and is **not** part of the gate. Note that `dotnet test` output is localised to German: per-test failures read `Fehler <FQN>`, not `Failed`. Grepping for `Failed` will report a green run that is not green.

---

## What already exists (do not re-derive)

| Fact | Evidence |
|---|---|
| 22 `Color` + 23 `SolidColorBrush` statics, all frozen, + 1 `double` | `src/EyeCtrl.Controls/Theme/Tokens.cs:40-115` |
| Plain statics are deliberate — `Controls.Tests`/`Core.Tests` run with no WPF `Application` | `src/EyeCtrl.Controls/Theme/Tokens.cs:11-13` |
| `BrushAccentDim10` is derived (Accent @ 10%), has no matching `Color` field | `src/EyeCtrl.Controls/Theme/Tokens.cs:97-103` |
| XAML re-exposes every brush via `{x:Static}`, zero literals | `src/EyeCtrl.Controls/Theme/OverlayStyles.xaml:12-51` |
| `BorderThicknessStandard` is the one theme-varying non-colour token (1/1/2) | `docs/design/overlay-unification-spec.md:379-397`; `tools/palette_validate.py:47,76,114` |
| Three fully gated theme token sets live in the validator | `tools/palette_validate.py:23-117` |
| `ThemeSettings` already appears in `EnumerateSubSettings` | `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:66` |

**Consumer counts, reproduce with:**

```bash
grep -rn "Tokens\." --include=*.cs src/ | grep -v "/obj/\|/bin/" | wc -l   # 131
grep -rno "{StaticResource Brush[A-Za-z0-9]*}" --include=*.xaml src/ | wc -l  # 106
```

None of these 237 call sites change in this plan. That is the point of the facade.

---

## Findings that shape this plan

These were measured against the merged tree (`4451cdb` on `main`), not assumed. Each one is a task below.

**1. There is already dead theme UI, and it is worse than three flags.**
`ThemeSettings.Mode` / `.OverlayOpacity` / `.HighContrast` (`src/EyeCtrl.Core/Settings/ThemeSettings.cs:9-13`) have **no consumer outside their own class and the settings UI that writes them**. `Mode` and `OverlayOpacity` and `HighContrast` are surfaced as live controls at `src/EyeCtrl.ViewModels/SettingsViewModel.cs:175-177`. Separately, `CursorSettings.CircleHighContrast` (`src/EyeCtrl.Core/Settings/CursorSettings.cs:37`) is surfaced **twice** — `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:447-450` and `src/EyeCtrl.ViewModels/SettingsViewModel.cs:71` — and no renderer reads it. Five gaze-reachable controls, zero effect. Task 7 deletes all five and replaces them with one that works.

**2. 30 static token captures snapshot values at type-init time.** This is the sharpest hazard in the slice, and a facade alone does *not* fix it:

```
src/EyeCtrl.App/Overlays/EmojiGridManager.cs:23-29      (6 fields)
src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:22-37   (11: 6 field initialisers + 5 assigned in a static constructor)
src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:44-51      (8 fields)
src/EyeCtrl.Controls/DwellComboBox.cs:31-32             (2 fields)
src/EyeCtrl.Controls/DwellSlider.cs:21                  (1 field)
src/EyeCtrl.Controls/DwellToggle.cs:21-22               (2 fields)
```

Each reads `Tokens.X` exactly once, whenever the CLR first touches that type. Five of them are not field initialisers at all: `ForegroundFuzzyMatch`, `ForegroundNextWord`, `ForegroundPhrase`, `ForegroundSentenceCompletion` and `ForegroundLMSuggestion` are declared at `KeyboardWindow.xaml.cs:33-37` and assigned from `Tokens.Brush*` inside the static constructor (`:39-52`, assignments at `:47-51`) — the same type-init snapshot, in a form a `= Tokens.` grep cannot see. Under Contrast they would silently render Midnight if any of those types is touched before the theme is activated. Task 8 converts all 30 to forwarding properties (and deletes the constructor that held five) and adds a guard that catches both forms, so a 31st cannot appear quietly.

**3. Ember trips an existing guard.** `ToggleContrastTests.ToggleOff_IsNotRed` asserts `c.R <= c.B` (`tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs:46`). Ember's `ToggleOff` is `#342C21` → R=0x34=52, B=0x21=33, so **52 <= 33 is false and the test fails**. Ember's toggle-off is a warm near-neutral, not a red. Measured Lab chroma: Midnight 13.7, Ember 8.6, Contrast 0.0 — all comfortably neutral. Task 5 restates the rule as a chroma floor, which is what "off is not an error colour" actually means.

**4. The colour-picker preset list *is* portable; only its first entry is not.** All four current presets clear 7:1 against all three halos — measured minimum **9.13** (Amber vs Midnight's halo):

| Preset | vs Midnight `#0A0A14` | vs Ember `#0E0A06` | vs Contrast `#000000` |
|---|---|---|---|
| `#A5F3FC` Aqua | 15.78 | 15.80 | 16.83 |
| `#7EE787` Green | 12.82 | 12.84 | 13.67 |
| `#E8A33C` Amber | 9.13 | 9.14 | 9.74 |
| `#D6DEF5` Light | 14.65 | 14.68 | 15.63 |

So §10.6's "the preset list is not automatically portable" resolves in the reassuring direction: it *is*. What breaks is `FirstPreset_IsTheAccent` (`tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs:59`), because the accent differs per theme. Conveniently, Midnight's four presets are **exactly** `{Accent, AccentSuccess, AccentWarning, TextPrimary}`, so deriving the list from the theme reproduces today's Midnight list hex-for-hex (the first swatch's tooltip label changes from "Aqua" to "Accent"; the swatches themselves render label-less) and gives the other two coherent lists. Measured minimum halo edge for the derived list across all three themes: **9.13**.

**5. The `RawColourLiteralTests` exemption list must move with the literals, starting at Task 1.** `Exempt` (`tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs:40-46`) lists `Tokens.cs` and `DwellColorPicker.cs`. Two guards read it: `NoUIFile_ContainsARawColourOutsideThePalette` fails when a non-exempt UI file holds a literal, and `EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour` fails when an exemption protects nothing. Both trip during this plan, and **the first trips at Task 1**, the moment `MidnightTheme.cs` lands holding `Color.FromArgb(0xFA, …)` with no exemption. This was observed on a trial run, not predicted:

```
Fehler NoUIFile_ContainsARawColourOutsideThePalette
  src/EyeCtrl.Controls/Theme/MidnightTheme.cs: Color.FromArgb(0xFA,, Color.FromArgb(0xB3,
Fehler EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour
  src/EyeCtrl.Controls/Theme/Tokens.cs: no raw colour left — drop the exemption
```

So the list is edited **in the same task as the file whose literals moved**, never deferred — otherwise the suite is red from Task 1 to Task 6 while each task's own gate demands 0 failures:

| After | `Exempt` holds | Because |
|---|---|---|
| Task 1 | `Tokens.cs`, `DwellColorPicker.cs`, **+ `MidnightTheme.cs`** | Midnight gains literals; `Tokens.cs` still has its own |
| Task 2 | **− `Tokens.cs`** | the facade has no literals left |
| Task 3 | **+ `EmberTheme.cs`, + `ContrastTheme.cs`** | both gain literals |
| Task 6 | **− `DwellColorPicker.cs`** | Step 3 derives its presets |

The end state is exactly the three theme files — literals live in palettes and nowhere else.

**Known hole in the guard, out of scope here.** `RawColour` (`RawColourLiteralTests.cs:54-62`) is built with `RegexOptions.IgnorePatternWhitespace`, so its `Color\.From A? rgb` alternative collapses to `Color\.FromA?rgb` — and the regex is not case-insensitive. It therefore matches `Color.FromArgb(...)` but **never** `Color.FromRgb(...)` with a capital R. Verified:

```
MATCH  Color.FromArgb(0xFA, 0x1A, 0x1A, 0x2E)   -> Color.FromArgb(0xFA,
MISSED Color.FromRgb(0x3A, 0x3A, 0x5E)
MISSED Color.FromRgb(0, 120, 212)
```

One real literal already hides behind this: `src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml.cs:28` sets `Color.FromRgb(0, 120, 212)` — Windows accent blue, not a token, not exempt, and silently passing.

This does **not** affect the exemption sequence above: all three theme files carry at least one `Color.FromArgb` (each theme's `ModalBackdrop` has an alpha byte — `#B3000000`, `#B3000000`, `#E6000000`), so every exemption stays live and `EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour` is satisfied throughout.

Fixing the regex is **not** in this slice. Tightening it immediately surfaces that blue, and deciding which token replaces it is a palette question — it must go through `tools/palette_validate.py` and the spec, which this plan explicitly forbids doing inline ("No new palette values"). Track it separately.

**6. Contrast legitimately uses `#00FF00`, which one guard forbids.** `Presets_DoNotOfferTheOutgoingLime` (`ColorPickerPresetTests.cs:49-53`) bans `#00FF00` from the preset list; Contrast's `AccentSuccess` and `ToggleOn` are `#00FF00` (`tools/palette_validate.py:104,111`), validated by all ten gates. The guard's real intent was "the *dwell accent* must not be the old Lime". Task 6 restates it against the accent, where it belongs.

**7. Constraint checks that come back clean.** `ThemeSettings` is already enumerated at `EyeCtrlSettings.cs:66`, so `EyeCtrlSettingsEnumerationTests` (which pins 19 entries, `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs:12`) needs **no change** — reusing `ThemeSettings` rather than adding a sub-setting is what buys that. No test in `tests/EyeCtrl.ViewModels.Tests/` references the General tab or any of the five dead controls.

---

## File Structure

**New files:**

| Path | Responsibility |
|---|---|
| `src/EyeCtrl.Controls/Theme/ITheme.cs` | The contract: 22 colours, 23 brushes, 1 thickness. The one non-colour member is there from the start (§4.4). |
| `src/EyeCtrl.Controls/Theme/ThemeBase.cs` | Derives all 23 frozen brushes from the abstract colours, so no theme repeats brush plumbing. |
| `src/EyeCtrl.Controls/Theme/MidnightTheme.cs` | §4.1 values — the only place they appear in C#. |
| `src/EyeCtrl.Controls/Theme/EmberTheme.cs` | §10.2 values. |
| `src/EyeCtrl.Controls/Theme/ContrastTheme.cs` | §10.7 values, including `BorderThicknessStandard = 2`. |
| `src/EyeCtrl.Controls/Theme/ThemeRegistry.cs` | Name ↔ theme, ordered for UI, unknown-name fallback. |
| `src/EyeCtrl.Controls/Theme/ThemeResourceApplier.cs` | Writes a theme into a `ResourceDictionary`. Order-independent by construction. |
| `src/EyeCtrl.App/Services/ThemeStartup.cs` | The one call site that activates a theme. Extracted so it is testable without launching. |
| `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs` | Cross-language guard: every C# theme equals its `palette_validate.py` entry. |
| `tests/EyeCtrl.Controls.Tests/Theme/ThemeRegistryTests.cs` | Name resolution and fallback. |
| `tests/EyeCtrl.Controls.Tests/Theme/ThemeResourceApplierTests.cs` | Applier covers every key the dictionary declares. |
| `tests/EyeCtrl.App.Tests/StaticTokenCaptureTests.cs` | Guard: no file may snapshot a token into a static field or static constructor. |
| `tests/EyeCtrl.App.Tests/ThemeStartupTests.cs` | Startup resolves the persisted name and applies it. |

**Modified files:**

| Path | Change |
|---|---|
| `src/EyeCtrl.Controls/Theme/Tokens.cs` | Literals out, forwarding properties in. Public surface unchanged. |
| `src/EyeCtrl.Controls/DwellColorPicker.cs:35-39` | `DefaultPresets` derived from the current theme. |
| `src/EyeCtrl.Controls/DwellComboBox.cs:31-32`, `DwellSlider.cs:21`, `DwellToggle.cs:21-22` | 5 static captures → properties. |
| `src/EyeCtrl.Core/Settings/ThemeSettings.cs` | Three dead flags → one `Name`. |
| `src/EyeCtrl.Core/Settings/CursorSettings.cs:36-37` | Delete `CircleHighContrast`. |
| `src/EyeCtrl.App/App.xaml.cs:101-104` | Two-line theme activation. |
| `src/EyeCtrl.App/Overlays/EmojiGridManager.cs:23-29`, `KeyboardWindow.xaml.cs:22-37`, `Views/SettingsWindow.xaml.cs:44-51` | 25 static captures → properties (incl. KeyboardWindow's static constructor, deleted). |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:447-450,461,901-940` | Delete the dead toggle; add the theme picker. |
| `src/EyeCtrl.ViewModels/SettingsViewModel.cs:71,175-177` | Delete four dead items; add one live one. |
| `tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs` | Reflection moves field→property; hexes move to the parity test. |
| `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs:55-56` | Reflection moves field→property. |
| `tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs` | Per theme; red-rule → chroma floor. |
| `tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs` | Per theme; derived presets. |
| `tests/EyeCtrl.App.Tests/HaloContrastTests.cs` | Per theme. |
| `tests/EyeCtrl.App.Tests/SettingsDefaultsMatchTokensTests.cs` | Pin to Midnight explicitly, not to ambient `Current`. |
| `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs:40-46` | Exemption list tracks the literals — edited in Tasks 1, 2, 3 and 6 (finding 5). |
| `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs:31,49` | `Theme.Mode` → `Theme.Name`. |
| `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceValidationTests.cs:187-191,208-209` | Same. |
| `docs/design/overlay-unification-spec.md:1069-1096` | Record §10.6 as resolved. |

---

## Task 1: `ITheme`, `ThemeBase`, and `MidnightTheme`

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/ITheme.cs`
- Create: `src/EyeCtrl.Controls/Theme/ThemeBase.cs`
- Create: `src/EyeCtrl.Controls/Theme/MidnightTheme.cs`
- Modify: `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs:40-46` (exempt the new palette file — finding 5)
- Test: `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs` (create)

`Tokens.cs` is **not** touched in this task — the new types sit alongside it and nothing consumes them yet. That keeps the build green throughout and makes Task 2 a pure swap.

- [x] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Reflection;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Structural guarantees every theme must satisfy, independent of its values.
///
/// The brush/colour pairing test is load-bearing: ThemeBase derives brushes from
/// abstract colour properties, so a theme whose override read an instance field
/// instead of returning a constant would produce a brush built from default(Color).
/// This test makes that failure loud instead of silent.
/// </summary>
public class ThemeShapeTests
{
    public static TheoryData<ITheme> AllThemes() => new() { MidnightTheme.Instance };

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryColour_HasAMatchingFrozenBrush(ITheme theme)
    {
        var colours = typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType == typeof(Color))
            .ToList();

        Check.WithCustomMessage("ITheme declares no Color members — wrong type reflected")
            .That(colours).Not.IsEmpty();

        foreach (var colour in colours)
        {
            var brushProperty = typeof(ITheme).GetProperty("Brush" + colour.Name);
            Check.WithCustomMessage($"ITheme.Brush{colour.Name} is missing")
                .That(brushProperty).IsNotNull();

            var brush = (SolidColorBrush)brushProperty!.GetValue(theme)!;
            Check.WithCustomMessage(
                    $"{theme.Name}.Brush{colour.Name} is {brush.Color} but "
                    + $"{theme.Name}.{colour.Name} is {colour.GetValue(theme)}. "
                    + "ThemeBase derives brushes from the colour properties; an "
                    + "override that reads a field instead of returning a constant "
                    + "breaks that.")
                .That(brush.Color).IsEqualTo((Color)colour.GetValue(theme)!);

            Check.WithCustomMessage($"{theme.Name}.Brush{colour.Name} must be frozen")
                .That(brush.IsFrozen).IsTrue();
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void DerivedAccentDim10_IsAccentAtTenPercent(ITheme theme)
    {
        Check.That(theme.BrushAccentDim10.Color).IsEqualTo(theme.Accent);
        Check.That(theme.BrushAccentDim10.Opacity).IsEqualTo(0.10);
        Check.That(theme.BrushAccentDim10.IsFrozen).IsTrue();
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void BorderThickness_IsAtLeastOnePixel(ITheme theme)
    {
        // Mirrors the validator's non-colour gate (tools/palette_validate.py:319-322).
        Check.That(theme.BorderThicknessStandard).IsGreaterOrEqualThan(1.0);
    }

    [Fact]
    public void Midnight_UsesTheOnePixelBorder()
    {
        Check.That(MidnightTheme.Instance.BorderThicknessStandard).IsEqualTo(1.0);
    }

    [Fact]
    public void BrushesAreCachedNotRebuiltPerAccess()
    {
        // 106 XAML lookups and 131 C# call sites read these on redraw at gaze rate.
        // A new SolidColorBrush per access would allocate in the render path.
        Check.That(MidnightTheme.Instance.BrushAccent)
             .IsSameReferenceAs(MidnightTheme.Instance.BrushAccent);
    }
}
```

- [x] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ThemeShapeTests" --nologo
```

Expected: **build failure**, `CS0246: The type or namespace name 'ITheme' could not be found`.

- [x] **Step 3: Create the interface**

Create `src/EyeCtrl.Controls/Theme/ITheme.cs`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// One complete palette. See docs/design/overlay-unification-spec.md §4.1
/// (Midnight), §10.2 (Ember) and §10.7 (Contrast); tools/palette_validate.py
/// runs ten gates against all three and must exit 0 after any edit.
///
/// Carries more than colour on purpose: <see cref="BorderThicknessStandard"/> is
/// theme-varying (§4.4), so the interface is non-colour-capable from the start
/// rather than being widened later.
///
/// Implementations must be immutable and their brushes frozen — overlays redraw
/// at gaze rate and share them across windows and threads.
/// </summary>
public interface ITheme
{
    /// <summary>Stable identifier, persisted in settings and shown in the picker.</summary>
    string Name { get; }

    // ── Surfaces ────────────────────────────────────────────────────────────
    /// <summary>Base overlay surface. Alpha where it composites over the desktop (§4.1.2).</summary>
    Color SurfaceDark { get; }
    /// <summary>Second layer: cards, button discs.</summary>
    Color SurfaceMedium { get; }
    /// <summary>Elevated surfaces — fully opaque (§4.1.1 rule 2).</summary>
    Color SurfaceLight { get; }
    /// <summary>Normal keyboard key.</summary>
    Color SurfaceKey { get; }
    /// <summary>Modifier key at rest.</summary>
    Color SurfaceKeyModifier { get; }
    /// <summary>Layout-switch key.</summary>
    Color SurfaceKeySwitch { get; }
    /// <summary>Held modifier — the 5th, brightest key-surface step (gate 8 ramp).</summary>
    Color SurfaceKeyActive { get; }
    /// <summary>Word-prediction bar.</summary>
    Color SurfacePrediction { get; }

    // ── Text and borders ────────────────────────────────────────────────────
    /// <summary>Container borders. Carries the layer boundary (gate 1).</summary>
    Color BorderSubtle { get; }
    /// <summary>Focus ring. Equals <see cref="Accent"/> in every theme (§4.1.2).</summary>
    Color BorderFocus { get; }
    /// <summary>Body text.</summary>
    Color TextPrimary { get; }
    /// <summary>Secondary text.</summary>
    Color TextSecondary { get; }
    /// <summary>De-emphasised text.</summary>
    Color TextDim { get; }
    /// <summary>Note Move-state marker — distinct from the accent and from body text (gate 10).</summary>
    Color MarkerMove { get; }

    // ── Accent and semantics ────────────────────────────────────────────────
    /// <summary>Reserved: means gaze dwell in progress and nothing else (§4.1.2).</summary>
    Color Accent { get; }
    /// <summary>Outline drawn under/around marks on transparent overlays (§4.1.3).</summary>
    Color StrokeHalo { get; }
    /// <summary>Success. Shared between Midnight and Ember; Contrast differs (§10.7).</summary>
    Color AccentSuccess { get; }
    /// <summary>Warning.</summary>
    Color AccentWarning { get; }
    /// <summary>Danger.</summary>
    Color AccentDanger { get; }
    /// <summary>Toggle in the on state.</summary>
    Color ToggleOn { get; }
    /// <summary>Toggle in the off state.</summary>
    Color ToggleOff { get; }
    /// <summary>Scrim behind modal dialogs.</summary>
    Color ModalBackdrop { get; }

    // ── Brushes ─────────────────────────────────────────────────────────────
    /// <summary>Frozen brush over <see cref="SurfaceDark"/>.</summary>
    SolidColorBrush BrushSurfaceDark { get; }
    /// <summary>Frozen brush over <see cref="SurfaceMedium"/>.</summary>
    SolidColorBrush BrushSurfaceMedium { get; }
    /// <summary>Frozen brush over <see cref="SurfaceLight"/>.</summary>
    SolidColorBrush BrushSurfaceLight { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKey"/>.</summary>
    SolidColorBrush BrushSurfaceKey { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKeyModifier"/>.</summary>
    SolidColorBrush BrushSurfaceKeyModifier { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKeySwitch"/>.</summary>
    SolidColorBrush BrushSurfaceKeySwitch { get; }
    /// <summary>Frozen brush over <see cref="SurfaceKeyActive"/>.</summary>
    SolidColorBrush BrushSurfaceKeyActive { get; }
    /// <summary>Frozen brush over <see cref="SurfacePrediction"/>.</summary>
    SolidColorBrush BrushSurfacePrediction { get; }
    /// <summary>Frozen brush over <see cref="BorderSubtle"/>.</summary>
    SolidColorBrush BrushBorderSubtle { get; }
    /// <summary>Frozen brush over <see cref="BorderFocus"/>.</summary>
    SolidColorBrush BrushBorderFocus { get; }
    /// <summary>Frozen brush over <see cref="TextPrimary"/>.</summary>
    SolidColorBrush BrushTextPrimary { get; }
    /// <summary>Frozen brush over <see cref="TextSecondary"/>.</summary>
    SolidColorBrush BrushTextSecondary { get; }
    /// <summary>Frozen brush over <see cref="TextDim"/>.</summary>
    SolidColorBrush BrushTextDim { get; }
    /// <summary>Frozen brush over <see cref="MarkerMove"/>.</summary>
    SolidColorBrush BrushMarkerMove { get; }
    /// <summary>Frozen brush over <see cref="Accent"/>.</summary>
    SolidColorBrush BrushAccent { get; }
    /// <summary>Frozen brush over <see cref="StrokeHalo"/>.</summary>
    SolidColorBrush BrushStrokeHalo { get; }
    /// <summary>Frozen brush over <see cref="AccentSuccess"/>.</summary>
    SolidColorBrush BrushAccentSuccess { get; }
    /// <summary>Frozen brush over <see cref="AccentWarning"/>.</summary>
    SolidColorBrush BrushAccentWarning { get; }
    /// <summary>Frozen brush over <see cref="AccentDanger"/>.</summary>
    SolidColorBrush BrushAccentDanger { get; }
    /// <summary>Frozen brush over <see cref="ToggleOn"/>.</summary>
    SolidColorBrush BrushToggleOn { get; }
    /// <summary>Frozen brush over <see cref="ToggleOff"/>.</summary>
    SolidColorBrush BrushToggleOff { get; }
    /// <summary>Frozen brush over <see cref="ModalBackdrop"/>.</summary>
    SolidColorBrush BrushModalBackdrop { get; }

    /// <summary>
    /// <see cref="Accent"/> at 10% opacity — the translucent dwell-progress fill on
    /// <c>DwellFeedbackOverlay</c>. Derived, not a palette colour: there is no matching
    /// <c>Color</c> member, because XAML has no syntax for "this brush at N% alpha".
    /// </summary>
    SolidColorBrush BrushAccentDim10 { get; }

    /// <summary>
    /// The one non-colour theme-varying token (§4.4). Midnight and Ember use 1;
    /// Contrast uses 2, because high-contrast structure is carried by the border
    /// rather than by fill steps.
    /// </summary>
    double BorderThicknessStandard { get; }
}
```

- [x] **Step 4: Create the base class**

Create `src/EyeCtrl.Controls/Theme/ThemeBase.cs`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// Derives every brush in <see cref="ITheme"/> from that theme's colours, so a
/// concrete theme declares 22 colours and one thickness and nothing else.
///
/// Brushes are built on first access rather than in the constructor: a constructor
/// would have to call the abstract colour getters before the derived type is fully
/// constructed. The lazy form has no such ordering hazard, and a race between two
/// threads is harmless — both produce equal frozen brushes over the same colour,
/// and nothing in the codebase compares brushes by reference.
/// </summary>
public abstract class ThemeBase : ITheme
{
    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();     // shared across overlays that redraw at gaze rate
        return b;
    }

    private static SolidColorBrush Dimmed(Color c, double opacity)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Color SurfaceDark { get; }
    /// <inheritdoc />
    public abstract Color SurfaceMedium { get; }
    /// <inheritdoc />
    public abstract Color SurfaceLight { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKey { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKeyModifier { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKeySwitch { get; }
    /// <inheritdoc />
    public abstract Color SurfaceKeyActive { get; }
    /// <inheritdoc />
    public abstract Color SurfacePrediction { get; }
    /// <inheritdoc />
    public abstract Color BorderSubtle { get; }
    /// <inheritdoc />
    public abstract Color BorderFocus { get; }
    /// <inheritdoc />
    public abstract Color TextPrimary { get; }
    /// <inheritdoc />
    public abstract Color TextSecondary { get; }
    /// <inheritdoc />
    public abstract Color TextDim { get; }
    /// <inheritdoc />
    public abstract Color MarkerMove { get; }
    /// <inheritdoc />
    public abstract Color Accent { get; }
    /// <inheritdoc />
    public abstract Color StrokeHalo { get; }
    /// <inheritdoc />
    public abstract Color AccentSuccess { get; }
    /// <inheritdoc />
    public abstract Color AccentWarning { get; }
    /// <inheritdoc />
    public abstract Color AccentDanger { get; }
    /// <inheritdoc />
    public abstract Color ToggleOn { get; }
    /// <inheritdoc />
    public abstract Color ToggleOff { get; }
    /// <inheritdoc />
    public abstract Color ModalBackdrop { get; }

    /// <inheritdoc />
    public abstract double BorderThicknessStandard { get; }

    private SolidColorBrush? _surfaceDark;
    private SolidColorBrush? _surfaceMedium;
    private SolidColorBrush? _surfaceLight;
    private SolidColorBrush? _surfaceKey;
    private SolidColorBrush? _surfaceKeyModifier;
    private SolidColorBrush? _surfaceKeySwitch;
    private SolidColorBrush? _surfaceKeyActive;
    private SolidColorBrush? _surfacePrediction;
    private SolidColorBrush? _borderSubtle;
    private SolidColorBrush? _borderFocus;
    private SolidColorBrush? _textPrimary;
    private SolidColorBrush? _textSecondary;
    private SolidColorBrush? _textDim;
    private SolidColorBrush? _markerMove;
    private SolidColorBrush? _accent;
    private SolidColorBrush? _strokeHalo;
    private SolidColorBrush? _accentSuccess;
    private SolidColorBrush? _accentWarning;
    private SolidColorBrush? _accentDanger;
    private SolidColorBrush? _toggleOn;
    private SolidColorBrush? _toggleOff;
    private SolidColorBrush? _modalBackdrop;
    private SolidColorBrush? _accentDim10;

    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceDark => _surfaceDark ??= Frozen(SurfaceDark);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceMedium => _surfaceMedium ??= Frozen(SurfaceMedium);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceLight => _surfaceLight ??= Frozen(SurfaceLight);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKey => _surfaceKey ??= Frozen(SurfaceKey);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeyModifier => _surfaceKeyModifier ??= Frozen(SurfaceKeyModifier);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeySwitch => _surfaceKeySwitch ??= Frozen(SurfaceKeySwitch);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfaceKeyActive => _surfaceKeyActive ??= Frozen(SurfaceKeyActive);
    /// <inheritdoc />
    public SolidColorBrush BrushSurfacePrediction => _surfacePrediction ??= Frozen(SurfacePrediction);
    /// <inheritdoc />
    public SolidColorBrush BrushBorderSubtle => _borderSubtle ??= Frozen(BorderSubtle);
    /// <inheritdoc />
    public SolidColorBrush BrushBorderFocus => _borderFocus ??= Frozen(BorderFocus);
    /// <inheritdoc />
    public SolidColorBrush BrushTextPrimary => _textPrimary ??= Frozen(TextPrimary);
    /// <inheritdoc />
    public SolidColorBrush BrushTextSecondary => _textSecondary ??= Frozen(TextSecondary);
    /// <inheritdoc />
    public SolidColorBrush BrushTextDim => _textDim ??= Frozen(TextDim);
    /// <inheritdoc />
    public SolidColorBrush BrushMarkerMove => _markerMove ??= Frozen(MarkerMove);
    /// <inheritdoc />
    public SolidColorBrush BrushAccent => _accent ??= Frozen(Accent);
    /// <inheritdoc />
    public SolidColorBrush BrushStrokeHalo => _strokeHalo ??= Frozen(StrokeHalo);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentSuccess => _accentSuccess ??= Frozen(AccentSuccess);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentWarning => _accentWarning ??= Frozen(AccentWarning);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentDanger => _accentDanger ??= Frozen(AccentDanger);
    /// <inheritdoc />
    public SolidColorBrush BrushToggleOn => _toggleOn ??= Frozen(ToggleOn);
    /// <inheritdoc />
    public SolidColorBrush BrushToggleOff => _toggleOff ??= Frozen(ToggleOff);
    /// <inheritdoc />
    public SolidColorBrush BrushModalBackdrop => _modalBackdrop ??= Frozen(ModalBackdrop);
    /// <inheritdoc />
    public SolidColorBrush BrushAccentDim10 => _accentDim10 ??= Dimmed(Accent, 0.10);

    /// <inheritdoc />
    public override string ToString() => Name;
}
```

- [x] **Step 5: Create the Midnight theme**

Create `src/EyeCtrl.Controls/Theme/MidnightTheme.cs`. Values are lifted verbatim from `Tokens.cs:40-71`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The shipping palette — docs/design/overlay-unification-spec.md §4.1, measured
/// in §9. Cool charcoal surfaces, cyan accent.
///
/// Alpha appears only where the token composites over live desktop content; 0xFA
/// (98%) bounds worst-case contrast drift to -5% (§4.1.2).
/// </summary>
public sealed class MidnightTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly MidnightTheme Instance = new();

    private MidnightTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Midnight";

    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFA, 0x1A, 0x1A, 0x2E);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFA, 0x2A, 0x2A, 0x44);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0x3A, 0x3A, 0x5E);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0x2A, 0x2A, 0x3E);
    /// <inheritdoc />
    public override Color SurfaceKeyModifier => Color.FromRgb(0x3E, 0x3E, 0x64);
    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0x38, 0x52, 0x7F);
    /// <inheritdoc />
    public override Color SurfaceKeyActive => Color.FromRgb(0x52, 0x78, 0xB8);
    /// <inheritdoc />
    public override Color SurfacePrediction => Color.FromRgb(0x2F, 0x53, 0x40);
    /// <inheritdoc />
    public override Color BorderSubtle => Color.FromRgb(0x61, 0x6B, 0xA0);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0xA5, 0xF3, 0xFC);
    /// <inheritdoc />
    public override Color TextPrimary => Color.FromRgb(0xD6, 0xDE, 0xF5);
    /// <inheritdoc />
    public override Color TextSecondary => Color.FromRgb(0x9A, 0xA4, 0xC8);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0x86, 0x8F, 0xB5);
    /// <inheritdoc />
    public override Color MarkerMove => Color.FromRgb(0xA8, 0xA0, 0xD0);
    /// <inheritdoc />
    public override Color Accent => Color.FromRgb(0xA5, 0xF3, 0xFC);
    /// <inheritdoc />
    public override Color StrokeHalo => Color.FromRgb(0x0A, 0x0A, 0x14);
    /// <inheritdoc />
    public override Color AccentSuccess => Color.FromRgb(0x7E, 0xE7, 0x87);
    /// <inheritdoc />
    public override Color AccentWarning => Color.FromRgb(0xE8, 0xA3, 0x3C);
    /// <inheritdoc />
    public override Color AccentDanger => Color.FromRgb(0xFF, 0x5C, 0x7A);
    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x4A, 0x8F, 0x5C);
    /// <inheritdoc />
    public override Color ToggleOff => Color.FromRgb(0x2E, 0x2E, 0x42);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    /// <inheritdoc />
    public override double BorderThicknessStandard => 1.0;
}
```

- [x] **Step 6: Exempt the new palette file from the raw-literal guard**

`MidnightTheme.cs` now holds colour literals, and `RawColourLiteralTests` scans `src/EyeCtrl.Controls` for exactly that. Skipping this leaves the suite red from here to Task 6 (finding 5). In `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs`, add a third entry to `Exempt` (`:40-46`):

```csharp
        ["src/EyeCtrl.Controls/Theme/MidnightTheme.cs"] =
            "the Midnight palette itself (spec §4.1) — gated by tools/palette_validate.py",
```

Keep both existing entries. `Tokens.cs` is **still** a live exemption at this point — it holds every literal until Task 2 guts it, so removing it now would fail the other half of the guard.

- [x] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ThemeShapeTests" --nologo
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~RawColourLiteralTests" --nologo
```

Expected: 5 passing on the first (3 theory cases across 3 theories + 2 facts; exact count varies with the theory expansion; what matters is **0 failures**), 2 passing on the second. If `EveryColour_HasAMatchingFrozenBrush` reports a mismatch, a `MidnightTheme` override does not match its `Tokens.cs` source — re-check against `Tokens.cs:40-71`.

The second command is not optional padding: Task 1 adds a file to a scanned project, so a `Controls.Tests`-only gate cannot see the guard it breaks. That is exactly how this defect survived a trial run.

- [x] **Step 8: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/ITheme.cs src/EyeCtrl.Controls/Theme/ThemeBase.cs src/EyeCtrl.Controls/Theme/MidnightTheme.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs
git commit -m "feat(theme): introduce ITheme with Midnight as its first implementation"
```

---

## Task 2: `Tokens` becomes a facade

**Files:**
- Modify: `src/EyeCtrl.Controls/Theme/Tokens.cs` (full rewrite, same public surface)
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs:56-78`
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs:55-56`
- Modify: `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs:40-46` (drop the stale `Tokens.cs` exemption — finding 5)

**This is the risk-retirement task.** The entire plan rests on `{x:Static theme:Tokens.SurfaceDark}` binding a static *property* as happily as it bound a static *field*. `TokenDictionaryTests` loads the real `OverlayStyles.xaml` through the pack URI in a headless `[StaFact]` (`TokenDictionaryTests.cs:31-48`) — if `x:Static` could not bind a property, that test throws at dictionary load. So Step 4 below is a genuine proof, not a smoke test, and it costs no app launch. Do not proceed to Task 3 until it is green.

- [x] **Step 1: Update the two reflection-based tests to expect properties**

In `tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs`, delete the whole `[Theory] Token_HasSpecifiedValue` block including all 22 `[InlineData]` lines (lines 17-54) — those hexes move to Task 3's cross-language parity guard, so they end up with one source of truth instead of three. Replace the remainder of the class body (lines 56-78) so the file reads:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Guards the shape of the <see cref="Tokens"/> facade.
///
/// Token *values* are no longer pinned here: they live in tools/palette_validate.py
/// (which runs the ten gates against them) and are checked against the C# themes by
/// ThemeParityTests. Restating them here as well would have been a third copy.
/// </summary>
public class TokenValueTests
{
    [Fact]
    public void EveryColourToken_HasAMatchingFrozenBrush()
    {
        foreach (var property in typeof(Tokens).GetProperties()
                     .Where(p => p.PropertyType == typeof(Color)))
        {
            var brushProperty = typeof(Tokens).GetProperty("Brush" + property.Name);
            Check.WithCustomMessage($"Tokens.Brush{property.Name} is missing")
                .That(brushProperty).IsNotNull();

            var brush = (SolidColorBrush)brushProperty!.GetValue(null)!;
            Check.That(brush.Color).IsEqualTo((Color)property.GetValue(null)!);
            Check.WithCustomMessage($"Tokens.Brush{property.Name} must be frozen")
                .That(brush.IsFrozen).IsTrue();
        }
    }

    [Fact]
    public void Tokens_ExposesEveryMemberOfITheme()
    {
        // The facade must not fall behind the interface: a token added to ITheme
        // and not forwarded here is invisible to all 131 C# call sites.
        foreach (var member in typeof(ITheme).GetProperties()
                     .Where(p => p.Name != nameof(ITheme.Name)))
        {
            Check.WithCustomMessage(
                    $"ITheme.{member.Name} has no forwarding property on Tokens. "
                    + "Add it, or the token is unreachable from C# and XAML.")
                .That(typeof(Tokens).GetProperty(member.Name)).IsNotNull();
        }
    }

    [Fact]
    public void DefaultTheme_IsMidnight()
    {
        // Tests and any pre-startup code path must see the shipping palette.
        Check.That(Tokens.Current.Name).IsEqualTo("Midnight");
    }

    [Fact]
    public void BorderThicknessStandard_IsOnePixelUnderMidnight()
    {
        Check.That(Tokens.BorderThicknessStandard).IsEqualTo(1.0);
    }
}
```

In `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs`, replace lines 55-56:

```csharp
        foreach (var field in typeof(Tokens).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(SolidColorBrush)))
```

with:

```csharp
        foreach (var field in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.PropertyType == typeof(SolidColorBrush)))
```

The loop body needs no other change: `field.Name` and `field.GetValue(null)` are identical on `PropertyInfo`.

- [x] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~TokenValueTests|FullyQualifiedName~TokenDictionaryTests" --nologo
```

Expected: **build failure**, `CS0117: 'Tokens' does not contain a definition for 'Current'`. `Tokens` still holds fields, so the property reflection would also find nothing.

- [x] **Step 3: Rewrite `Tokens.cs` as a facade**

Replace the entire contents of `src/EyeCtrl.Controls/Theme/Tokens.cs`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The static face of the active <see cref="ITheme"/>. Every colour, brush and
/// thickness in the overlay UI is read through here — 131 C# call sites and, via
/// OverlayStyles.xaml, 106 XAML lookups.
///
/// Deliberately static rather than an injected service: EyeCtrl.Controls.Tests and
/// EyeCtrl.Core.Tests run without a WPF Application, so a resource lookup would
/// NullReferenceException, and XAML's {x:Static} needs a static member either way.
/// Members are static *properties*, not fields, so activating a theme is visible to
/// every existing call site without touching one of them.
///
/// Theme changes recreate windows rather than re-resolving live, which is why
/// {StaticResource} throughout remains correct. <see cref="Activate"/> is called
/// once, from App.OnStartup, before any overlay exists.
/// </summary>
public static class Tokens
{
    /// <summary>
    /// The active theme. Defaults to Midnight so tests and any code that runs
    /// before startup see the shipping palette rather than null.
    /// </summary>
    public static ITheme Current { get; private set; } = MidnightTheme.Instance;

    /// <summary>
    /// Makes <paramref name="theme"/> active. Call once, during startup, on the UI
    /// thread and before any overlay window or dwell control is constructed —
    /// afterwards the value is only ever read, so cross-thread reads are safe
    /// (colours are structs, brushes are frozen).
    /// </summary>
    /// <param name="theme">The theme to activate.</param>
    public static void Activate(ITheme theme) => Current = theme;

    // ── Surfaces ────────────────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.SurfaceDark"/>
    public static Color SurfaceDark => Current.SurfaceDark;
    /// <inheritdoc cref="ITheme.SurfaceMedium"/>
    public static Color SurfaceMedium => Current.SurfaceMedium;
    /// <inheritdoc cref="ITheme.SurfaceLight"/>
    public static Color SurfaceLight => Current.SurfaceLight;
    /// <inheritdoc cref="ITheme.SurfaceKey"/>
    public static Color SurfaceKey => Current.SurfaceKey;
    /// <inheritdoc cref="ITheme.SurfaceKeyModifier"/>
    public static Color SurfaceKeyModifier => Current.SurfaceKeyModifier;
    /// <inheritdoc cref="ITheme.SurfaceKeySwitch"/>
    public static Color SurfaceKeySwitch => Current.SurfaceKeySwitch;
    /// <inheritdoc cref="ITheme.SurfaceKeyActive"/>
    public static Color SurfaceKeyActive => Current.SurfaceKeyActive;
    /// <inheritdoc cref="ITheme.SurfacePrediction"/>
    public static Color SurfacePrediction => Current.SurfacePrediction;

    // ── Text and borders ────────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.BorderSubtle"/>
    public static Color BorderSubtle => Current.BorderSubtle;
    /// <inheritdoc cref="ITheme.BorderFocus"/>
    public static Color BorderFocus => Current.BorderFocus;
    /// <inheritdoc cref="ITheme.TextPrimary"/>
    public static Color TextPrimary => Current.TextPrimary;
    /// <inheritdoc cref="ITheme.TextSecondary"/>
    public static Color TextSecondary => Current.TextSecondary;
    /// <inheritdoc cref="ITheme.TextDim"/>
    public static Color TextDim => Current.TextDim;
    /// <inheritdoc cref="ITheme.MarkerMove"/>
    public static Color MarkerMove => Current.MarkerMove;

    // ── Accent and semantics ────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.Accent"/>
    public static Color Accent => Current.Accent;
    /// <inheritdoc cref="ITheme.StrokeHalo"/>
    public static Color StrokeHalo => Current.StrokeHalo;
    /// <inheritdoc cref="ITheme.AccentSuccess"/>
    public static Color AccentSuccess => Current.AccentSuccess;
    /// <inheritdoc cref="ITheme.AccentWarning"/>
    public static Color AccentWarning => Current.AccentWarning;
    /// <inheritdoc cref="ITheme.AccentDanger"/>
    public static Color AccentDanger => Current.AccentDanger;
    /// <inheritdoc cref="ITheme.ToggleOn"/>
    public static Color ToggleOn => Current.ToggleOn;
    /// <inheritdoc cref="ITheme.ToggleOff"/>
    public static Color ToggleOff => Current.ToggleOff;
    /// <inheritdoc cref="ITheme.ModalBackdrop"/>
    public static Color ModalBackdrop => Current.ModalBackdrop;

    // ── Brushes ─────────────────────────────────────────────────────────────
    /// <inheritdoc cref="ITheme.BrushSurfaceDark"/>
    public static SolidColorBrush BrushSurfaceDark => Current.BrushSurfaceDark;
    /// <inheritdoc cref="ITheme.BrushSurfaceMedium"/>
    public static SolidColorBrush BrushSurfaceMedium => Current.BrushSurfaceMedium;
    /// <inheritdoc cref="ITheme.BrushSurfaceLight"/>
    public static SolidColorBrush BrushSurfaceLight => Current.BrushSurfaceLight;
    /// <inheritdoc cref="ITheme.BrushSurfaceKey"/>
    public static SolidColorBrush BrushSurfaceKey => Current.BrushSurfaceKey;
    /// <inheritdoc cref="ITheme.BrushSurfaceKeyModifier"/>
    public static SolidColorBrush BrushSurfaceKeyModifier => Current.BrushSurfaceKeyModifier;
    /// <inheritdoc cref="ITheme.BrushSurfaceKeySwitch"/>
    public static SolidColorBrush BrushSurfaceKeySwitch => Current.BrushSurfaceKeySwitch;
    /// <inheritdoc cref="ITheme.BrushSurfaceKeyActive"/>
    public static SolidColorBrush BrushSurfaceKeyActive => Current.BrushSurfaceKeyActive;
    /// <inheritdoc cref="ITheme.BrushSurfacePrediction"/>
    public static SolidColorBrush BrushSurfacePrediction => Current.BrushSurfacePrediction;
    /// <inheritdoc cref="ITheme.BrushBorderSubtle"/>
    public static SolidColorBrush BrushBorderSubtle => Current.BrushBorderSubtle;
    /// <inheritdoc cref="ITheme.BrushBorderFocus"/>
    public static SolidColorBrush BrushBorderFocus => Current.BrushBorderFocus;
    /// <inheritdoc cref="ITheme.BrushTextPrimary"/>
    public static SolidColorBrush BrushTextPrimary => Current.BrushTextPrimary;
    /// <inheritdoc cref="ITheme.BrushTextSecondary"/>
    public static SolidColorBrush BrushTextSecondary => Current.BrushTextSecondary;
    /// <inheritdoc cref="ITheme.BrushTextDim"/>
    public static SolidColorBrush BrushTextDim => Current.BrushTextDim;
    /// <inheritdoc cref="ITheme.BrushMarkerMove"/>
    public static SolidColorBrush BrushMarkerMove => Current.BrushMarkerMove;
    /// <inheritdoc cref="ITheme.BrushAccent"/>
    public static SolidColorBrush BrushAccent => Current.BrushAccent;
    /// <inheritdoc cref="ITheme.BrushStrokeHalo"/>
    public static SolidColorBrush BrushStrokeHalo => Current.BrushStrokeHalo;
    /// <inheritdoc cref="ITheme.BrushAccentSuccess"/>
    public static SolidColorBrush BrushAccentSuccess => Current.BrushAccentSuccess;
    /// <inheritdoc cref="ITheme.BrushAccentWarning"/>
    public static SolidColorBrush BrushAccentWarning => Current.BrushAccentWarning;
    /// <inheritdoc cref="ITheme.BrushAccentDanger"/>
    public static SolidColorBrush BrushAccentDanger => Current.BrushAccentDanger;
    /// <inheritdoc cref="ITheme.BrushToggleOn"/>
    public static SolidColorBrush BrushToggleOn => Current.BrushToggleOn;
    /// <inheritdoc cref="ITheme.BrushToggleOff"/>
    public static SolidColorBrush BrushToggleOff => Current.BrushToggleOff;
    /// <inheritdoc cref="ITheme.BrushModalBackdrop"/>
    public static SolidColorBrush BrushModalBackdrop => Current.BrushModalBackdrop;
    /// <inheritdoc cref="ITheme.BrushAccentDim10"/>
    public static SolidColorBrush BrushAccentDim10 => Current.BrushAccentDim10;

    /// <inheritdoc cref="ITheme.BorderThicknessStandard"/>
    public static double BorderThicknessStandard => Current.BorderThicknessStandard;
}
```

- [x] **Step 4: Drop the now-stale `Tokens.cs` exemption**

`Tokens.cs` is pure forwarding as of Step 3, so its `Exempt` entry protects nothing and `EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour` fails on it (finding 5). In `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs`, **delete** these two lines from `Exempt`:

```csharp
        ["src/EyeCtrl.Controls/Theme/Tokens.cs"] =
            "the palette itself — the one place a colour literal is correct",
```

`MidnightTheme.cs` (added in Task 1) and `DwellColorPicker.cs` both still hold literals, so both stay.

- [x] **Step 5: Run the full suite — this is the `{x:Static}`-on-property proof**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 build errors and **0 failures**, at **1695 passing**. That is not the 1710 baseline, and the drop is intentional: Task 1 added 5 (`ThemeShapeTests`), and Step 1 here removed 22 `[InlineData]` cases while adding 2 facts, for 1710 + 5 − 20 = 1695. `EyeCtrl.Controls.Tests` alone should read 25. A count of 1717 means the `[InlineData]` block was not deleted; anything with `Fehler` > 0 is a real failure.

The proof that `{x:Static}` can bind a static *property* (not just a field) lives in `TokenDictionaryTests.Load()` — specifically the `new ResourceDictionary { Source = DictionaryUri }` assignment, which forces WPF to parse `OverlayStyles.xaml` and resolve every `Color="{x:Static theme:Tokens.X}"`. If `x:Static` could not bind a static property, that assignment throws `XamlParseException` before any assertion runs; the whole `[StaFact]` fails to reach its body. `EveryTokenBrush_IsExposedAndMatches` then adds a *secondary* check (every brush property has a key and the value matches), but the load itself is the load-bearing mechanism. That is why this task is a genuine proof rather than a smoke test, and why it costs no app launch.

**If it does fail that way, stop.** The fallback is to keep `Tokens`' colour members as fields assigned from `Current` at startup — but that reintroduces exactly the ordering hazard Task 8 removes, so raise it rather than improvising.

Remember: failures print as `Fehler <FQN>`, and long names wrap across lines.

- [x] **Step 6: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/Tokens.cs tests/EyeCtrl.Controls.Tests/Theme/TokenValueTests.cs tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs
git commit -m "refactor(theme): Tokens forwards to ITheme instead of holding literals"
```

---

## Task 3: `EmberTheme`, `ContrastTheme`, and the cross-language parity guard

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/EmberTheme.cs`
- Create: `src/EyeCtrl.Controls/Theme/ContrastTheme.cs`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs`
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs` (`AllThemes` — explicit list, **not** `ThemeRegistry`, which does not exist yet)
- Modify: `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs:40-46` (exempt both new palette files — finding 5)

`tools/palette_validate.py:23-117` is the source of truth for all three token sets — it is what the ten gates actually run against, and two of the Ember/Contrast values were corrected by gate 10 this week. The parity test makes the C# a checked copy rather than a second original.

**This task and Task 4 must not reference each other.** `ThemeRegistry` (Task 4) is built from `EmberTheme.Instance` and `ContrastTheme.Instance` (this task), so if this task's tests reached for the registry, neither task could compile first — there would be no order that produces two green commits. Both test files here therefore hand-list the three themes, and Task 4 Step 4 swaps them onto the registry afterwards. That swap is not busywork: it is what proves the registry agrees with the list the validator already checked.

- [x] **Step 1: Write the failing parity test**

Create `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Cross-language drift guard. tools/palette_validate.py holds each theme's token
/// set and runs ten accessibility gates against it; these classes hold the same
/// values for WPF. If they diverge, the app ships colours nothing ever validated.
///
/// The Python file is the source of truth because it is what the gates execute
/// against — so this test parses it rather than restating the hexes a third time.
/// </summary>
public class ThemeParityTests
{
    private static string ValidatorSource()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(ThemeParityTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.WithCustomMessage("Could not locate the repo root (EyeCtrl.slnx)")
            .That(dir).IsNotNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "palette_validate.py"));
    }

    /// <summary>
    /// Extracts one theme's token map from the THEMES dict. Keys starting with '_'
    /// are validator-internal (e.g. "_core", the opaque surface core) and skipped.
    ///
    /// Format coupling: this assumes the validator declares each theme as
    /// <c>"Name": { ... }</c> with only string-valued keys inside (every token is a
    /// quoted hex or the quoted <c>BorderThicknessStandard</c> number). It is a
    /// checked copy, not a parser — if <c>palette_validate.py</c> ever gains nested
    /// dicts, list values, or trailing commas under a theme, widen this and re-run.
    /// Brace-matching (not a regex for the closing brace) keeps it immune to
    /// indentation changes, which the previous <c>\n    }</c> form was not.
    /// </summary>
    private static Dictionary<string, string> ParseTheme(string source, string themeName)
    {
        var start = Regex.Match(source, $"\"{Regex.Escape(themeName)}\"\\s*:\\s*\\{{");
        Check.WithCustomMessage($"THEMES has no entry named '{themeName}'")
            .That(start.Success).IsTrue();

        // Walk braces from the opening { to its matching close, so indentation or
        // a reformatted file cannot truncate the block.
        var depth = 0;
        var i = start.Index + start.Length - 1;   // position of the opening '{'
        var end = -1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
            i++;
        }
        Check.WithCustomMessage($"Could not find the end of the '{themeName}' block")
            .That(end).IsStrictlyGreaterThan(0);

        var body = source[(start.Index + start.Length)..end];
        return Regex.Matches(body, @"""(\w+)""\s*:\s*""([^""]+)""")
            .Where(m => !m.Groups[1].Value.StartsWith('_'))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Normalises the validator's #RRGGBB form to the #AARRGGBB the WPF side produces.</summary>
    private static string Normalise(string hex) =>
        hex.Length == 7 ? "#FF" + hex[1..].ToUpperInvariant() : hex.ToUpperInvariant();

    public static TheoryData<string> ThemeNames() => new() { "Midnight", "Ember", "Contrast" };

    /// <summary>
    /// Name to theme, local to this file.
    ///
    /// Deliberately **not** <c>ThemeRegistry.Resolve</c>: the registry does not
    /// exist until Task 4, and it is itself built from these same three instances,
    /// so depending on it here would make Tasks 3 and 4 mutually dependent —
    /// neither could compile first. Task 4 Step 4 swaps this for the registry once
    /// there is one, which is also what proves the registry agrees with this list.
    /// </summary>
    private static ITheme ThemeNamed(string name) => name switch
    {
        "Midnight" => MidnightTheme.Instance,
        "Ember" => EmberTheme.Instance,
        "Contrast" => ContrastTheme.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown theme"),
    };

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void EveryColour_MatchesTheValidator(string themeName)
    {
        var expected = ParseTheme(ValidatorSource(), themeName);
        var theme = ThemeNamed(themeName);

        foreach (var property in typeof(ITheme).GetProperties()
                     .Where(p => p.PropertyType == typeof(Color)))
        {
            Check.WithCustomMessage(
                    $"tools/palette_validate.py has no '{property.Name}' for {themeName}. "
                    + "Every ITheme colour must be gated.")
                .That(expected.ContainsKey(property.Name)).IsTrue();

            var actual = Hex((Color)property.GetValue(theme)!);
            Check.WithCustomMessage(
                    $"{themeName}.{property.Name} is {actual} in C# but "
                    + $"{expected[property.Name]} in tools/palette_validate.py. "
                    + "Fix the C# to match, or change the validator and re-run it "
                    + "(python tools/palette_validate.py must exit 0) before changing this.")
                .That(actual).IsEqualTo(Normalise(expected[property.Name]));
        }
    }

    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void BorderThickness_MatchesTheValidator(string themeName)
    {
        var expected = ParseTheme(ValidatorSource(), themeName);
        var theme = ThemeNamed(themeName);

        Check.WithCustomMessage(
                $"{themeName} must declare BorderThicknessStandard in the validator — "
                + "it is the one non-colour theme-varying token (§4.4).")
            .That(expected.ContainsKey("BorderThicknessStandard")).IsTrue();

        Check.That(theme.BorderThicknessStandard)
             .IsEqualTo(double.Parse(expected["BorderThicknessStandard"]));
    }

    [Fact]
    public void ValidatorAndCSharp_DeclareTheSameThemes()
    {
        // A theme gated in Python but missing from C# is unreachable; a theme in C#
        // but not in Python has never passed a single gate.
        var inPython = Regex.Matches(ValidatorSource(), @"^    ""(\w+)"":\s*\{",
                RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n)
            .ToList();

        var inCSharp = ThemeNames().Select(row => (string)row[0]).OrderBy(n => n).ToList();

        Check.That(inCSharp).ContainsExactly(inPython);
    }
}
```

- [x] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ThemeParityTests" --nologo
```

Expected: **build failure**, `CS0246: The type or namespace name 'EmberTheme' could not be found` (and the same for `ContrastTheme`) — the two theme classes arrive in Steps 3 and 4.

**Note what this test does *not* reference.** It resolves names through the local `ThemeNamed` helper, not `ThemeRegistry`. That is deliberate and load-bearing: `ThemeRegistry.All` is built from `EmberTheme.Instance` and `ContrastTheme.Instance`, which this task creates, so a registry reference here would make Tasks 3 and 4 mutually dependent and leave **no** order in which both commits compile. Task 4 Step 4 swaps both this file and `ThemeShapeTests` onto the registry once it exists.

- [x] **Step 3: Create the Ember theme**

Create `src/EyeCtrl.Controls/Theme/EmberTheme.cs`. Values from `tools/palette_validate.py:50-78` (spec §10.2):

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The warm, low-blue-light alternative — spec §10.2, measured in §10.3.
///
/// Surfaces and text move onto the warm axis; the accent deliberately does not
/// (§10.5). The accent covers a few percent of lit pixels, so keeping it cool
/// costs almost nothing in short-wavelength exposure while freeing the warm axis
/// for the semantic triad — which is why that triad is shared with Midnight.
///
/// The opaque surface core #211B14 is matched to Midnight's #1A1A2E at L* 10.2,
/// so the two themes sit at the same perceived depth and only temperature changes.
/// </summary>
public sealed class EmberTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly EmberTheme Instance = new();

    private EmberTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Ember";

    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFA, 0x21, 0x1B, 0x14);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFA, 0x34, 0x2B, 0x20);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0x47, 0x3A, 0x2C);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0x2E, 0x26, 0x19);
    /// <inheritdoc />
    public override Color SurfaceKeyModifier => Color.FromRgb(0x4E, 0x40, 0x29);
    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0x63, 0x38, 0x2A);
    /// <inheritdoc />
    public override Color SurfaceKeyActive => Color.FromRgb(0x9A, 0x68, 0x40);
    /// <inheritdoc />
    public override Color SurfacePrediction => Color.FromRgb(0x33, 0x42, 0x2A);
    /// <inheritdoc />
    public override Color BorderSubtle => Color.FromRgb(0x7A, 0x6C, 0x51);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0x9F, 0xE8, 0xFF);
    /// <inheritdoc />
    public override Color TextPrimary => Color.FromRgb(0xF2, 0xEE, 0xE8);
    /// <inheritdoc />
    public override Color TextSecondary => Color.FromRgb(0xBF, 0xB4, 0xA2);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0xA0, 0x91, 0x7C);

    /// <summary>
    /// Violet, not a warm tone: the warm axis is already spent on surfaces and text,
    /// so a warm marker sat only ΔE 11.4 from TextPrimary and failed gate 10.
    /// </summary>
    public override Color MarkerMove => Color.FromRgb(0xB6, 0xA3, 0xDC);

    /// <summary>Cool on warm — the deliberate exception to the theme's axis (§10.5).</summary>
    public override Color Accent => Color.FromRgb(0x9F, 0xE8, 0xFF);
    /// <inheritdoc />
    public override Color StrokeHalo => Color.FromRgb(0x0E, 0x0A, 0x06);

    /// <summary>Shared with Midnight: semantics encode outcome, not brand (§10.5).</summary>
    public override Color AccentSuccess => Color.FromRgb(0x7E, 0xE7, 0x87);
    /// <summary>Shared with Midnight (§10.5).</summary>
    public override Color AccentWarning => Color.FromRgb(0xE8, 0xA3, 0x3C);
    /// <summary>Shared with Midnight (§10.5).</summary>
    public override Color AccentDanger => Color.FromRgb(0xFF, 0x5C, 0x7A);

    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x67, 0x87, 0x4C);
    /// <inheritdoc />
    public override Color ToggleOff => Color.FromRgb(0x34, 0x2C, 0x21);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xB3, 0x00, 0x00, 0x00);

    /// <inheritdoc />
    public override double BorderThicknessStandard => 1.0;
}
```

- [x] **Step 4: Create the Contrast theme**

Create `src/EyeCtrl.Controls/Theme/ContrastTheme.cs`. Values from `tools/palette_validate.py:83-116` (spec §10.7):

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Windows.Media;

/// <summary>
/// The situational high-contrast theme — spec §10.7. For glare, a bright room or
/// tired eyes; not an all-day palette, since pure white at 21:1 for hours is what
/// Midnight's TextPrimary deliberately avoids.
///
/// Structurally distinct rather than a re-hue: gate 1 is carried entirely by a 21:1
/// border instead of fill steps, gate 5 passes on lightness alone (#00FF00 and
/// #FFFF00 collapse to ΔE 2.7 under protanopia, so §9.4's glyph rule is *more*
/// load-bearing here), and gate 9 caps danger at #FF7777. Surfaces are necessarily
/// opaque: translucency and a contrast guarantee are incompatible.
///
/// The triad is NOT shared with Midnight and Ember — no high-contrast palette can
/// use #FF5C7A and still be high-contrast.
/// </summary>
public sealed class ContrastTheme : ThemeBase
{
    /// <summary>The single shared instance. Themes are immutable.</summary>
    public static readonly ContrastTheme Instance = new();

    private ContrastTheme()
    {
    }

    /// <inheritdoc />
    public override string Name => "Contrast";

    /// <inheritdoc />
    public override Color SurfaceDark => Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    /// <inheritdoc />
    public override Color SurfaceMedium => Color.FromArgb(0xFF, 0x14, 0x14, 0x14);
    /// <inheritdoc />
    public override Color SurfaceLight => Color.FromRgb(0x26, 0x26, 0x26);
    /// <inheritdoc />
    public override Color SurfaceKey => Color.FromRgb(0x0F, 0x0F, 0x0F);

    /// <summary>
    /// Tinted, not outlined: an achromatic key ramp failed gate 8 at ΔE 6.1 because
    /// near-blacks compress badly, so the modifier carries a low-luminance blue.
    /// </summary>
    public override Color SurfaceKeyModifier => Color.FromRgb(0x12, 0x3A, 0x63);

    /// <inheritdoc />
    public override Color SurfaceKeySwitch => Color.FromRgb(0x4A, 0x4A, 0x4A);
    /// <inheritdoc />
    public override Color SurfaceKeyActive => Color.FromRgb(0xFF, 0x88, 0x00);
    /// <summary>Low-luminance green tint, for the same gate-8 reason as the modifier.</summary>
    public override Color SurfacePrediction => Color.FromRgb(0x00, 0x4D, 0x22);

    /// <summary>Pure white at 21:1 — in this theme structure is carried by the border.</summary>
    public override Color BorderSubtle => Color.FromRgb(0xFF, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color BorderFocus => Color.FromRgb(0x00, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color TextPrimary => Color.FromRgb(0xFF, 0xFF, 0xFF);
    /// <inheritdoc />
    public override Color TextSecondary => Color.FromRgb(0xE8, 0xE8, 0xE8);
    /// <inheritdoc />
    public override Color TextDim => Color.FromRgb(0xC8, 0xC8, 0xC8);

    /// <summary>
    /// Violet, not white. #FFFFFF was the original value, but BorderSubtle and
    /// TextPrimary are both already #FFFFFF here, so it collapsed three meanings
    /// into one colour and gate 10 rejected it.
    /// </summary>
    public override Color MarkerMove => Color.FromRgb(0xAA, 0x80, 0xFF);

    /// <summary>
    /// Fully saturated, not a brighter #66FFFF: white is a structural colour in this
    /// theme, and the paler cyan sits only ΔE 24.6 from BorderSubtle, failing gate 4.
    /// </summary>
    public override Color Accent => Color.FromRgb(0x00, 0xFF, 0xFF);

    /// <inheritdoc />
    public override Color StrokeHalo => Color.FromRgb(0x00, 0x00, 0x00);
    /// <inheritdoc />
    public override Color AccentSuccess => Color.FromRgb(0x00, 0xFF, 0x00);
    /// <inheritdoc />
    public override Color AccentWarning => Color.FromRgb(0xFF, 0xFF, 0x00);

    /// <summary>
    /// Red is inherently dark: #FF0000 is only 5.25:1 on black, and pushing past
    /// 10:1 drops chroma under the gate-9 floor (#FF9999 is C* 41.5). This is the
    /// ceiling for "high contrast and still reads red" — 8.16:1 at C* 57.6, still
    /// the strongest AccentDanger of the three themes.
    /// </summary>
    public override Color AccentDanger => Color.FromRgb(0xFF, 0x77, 0x77);

    /// <inheritdoc />
    public override Color ToggleOn => Color.FromRgb(0x00, 0xFF, 0x00);
    /// <inheritdoc />
    public override Color ToggleOff => Color.FromRgb(0x00, 0x00, 0x00);
    /// <inheritdoc />
    public override Color ModalBackdrop => Color.FromArgb(0xE6, 0x00, 0x00, 0x00);

    /// <summary>
    /// 2, not 1 — the one non-colour token a theme overrides (§4.4). High-contrast
    /// structure is carried by the border rather than by fill steps.
    /// </summary>
    public override double BorderThicknessStandard => 2.0;
}
```

- [x] **Step 5: Widen `ThemeShapeTests` to cover all three**

In `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs`, replace the `AllThemes` member:

```csharp
    public static TheoryData<ITheme> AllThemes() => new() { MidnightTheme.Instance };
```

with:

```csharp
    public static TheoryData<ITheme> AllThemes() => new()
    {
        MidnightTheme.Instance,
        EmberTheme.Instance,
        ContrastTheme.Instance,
    };
```

**List the three explicitly here, do not reach for `ThemeRegistry.All`** — the registry does not exist until Task 4 Step 3, and referencing it now would make this task's commit fail to compile. Task 4 Step 4 replaces this body with the registry once there is one, which is also what proves the registry agrees with the hand-written list.

- [x] **Step 6: Exempt the two new palette files from the raw-literal guard**

Same obligation as Task 1 Step 6, for the same reason (finding 5): both new files hold colour literals inside a scanned project. In `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs`, add to `Exempt`:

```csharp
        ["src/EyeCtrl.Controls/Theme/EmberTheme.cs"] =
            "the Ember palette itself (spec §10.2) — gated by tools/palette_validate.py",
        ["src/EyeCtrl.Controls/Theme/ContrastTheme.cs"] =
            "the Contrast palette itself (spec §10.7) — gated by tools/palette_validate.py",
```

`Exempt` now holds four entries: the three theme files plus `DwellColorPicker.cs`, which keeps its literals until Task 6 Step 3.

- [x] **Step 7: Run the tests**

```bash
python tools/palette_validate.py
dotnet test tests/EyeCtrl.Controls.Tests --nologo
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~RawColourLiteralTests" --nologo
```

Expected: validator `ALL GATES PASS - 3 theme(s)`, exit 0; all Controls tests pass; both guard tests pass. This task no longer depends on Task 4 — it compiles and runs standalone. A parity failure names the exact token and both values — trust the Python side and fix the C#.

- [x] **Step 8: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/EmberTheme.cs src/EyeCtrl.Controls/Theme/ContrastTheme.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs
git commit -m "feat(theme): add Ember and Contrast, guarded against the validator"
```

---

## Task 4: `ThemeRegistry`

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/ThemeRegistry.cs`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/ThemeRegistryTests.cs`
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs` (`AllThemes` → `ThemeRegistry.All`)
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs` (drop the local `ThemeNamed` helper → `ThemeRegistry.Resolve`)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/ThemeRegistryTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

public class ThemeRegistryTests
{
    [Fact]
    public void All_ListsMidnightFirst()
    {
        // Order is the picker's order, and Midnight is the shipping default.
        Check.That(ThemeRegistry.All.Select(t => t.Name).ToList())
             .ContainsExactly("Midnight", "Ember", "Contrast");
    }

    [Theory]
    [InlineData("Midnight")]
    [InlineData("Ember")]
    [InlineData("Contrast")]
    public void Resolve_ReturnsTheNamedTheme(string name)
    {
        Check.That(ThemeRegistry.Resolve(name).Name).IsEqualTo(name);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        // settings.json is hand-editable; a lowercase name must not silently
        // downgrade the user to Midnight.
        Check.That(ThemeRegistry.Resolve("ember").Name).IsEqualTo("Ember");
    }

    [Theory]
    [InlineData("Nonexistent")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_FallsBackToMidnight_ForAnUnknownName(string? name)
    {
        // A corrupt or hand-edited settings.json must not leave the UI unthemed.
        Check.That(ThemeRegistry.Resolve(name!).Name).IsEqualTo("Midnight");
    }

    [Fact]
    public void Names_MatchesAll()
    {
        Check.That(ThemeRegistry.Names).ContainsExactly(ThemeRegistry.All.Select(t => t.Name));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ThemeRegistryTests" --nologo
```

Expected: **build failure**, `CS0103: The name 'ThemeRegistry' does not exist in the current context`.

- [ ] **Step 3: Create the registry**

Create `src/EyeCtrl.Controls/Theme/ThemeRegistry.cs`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The set of available themes, and the name-to-theme mapping used by settings and
/// the picker. Order is the picker's order: the shipping default first, the
/// all-day alternative second, the situational one last.
/// </summary>
public static class ThemeRegistry
{
    /// <summary>Every theme, in display order.</summary>
    public static IReadOnlyList<ITheme> All { get; } =
    [
        MidnightTheme.Instance,
        EmberTheme.Instance,
        ContrastTheme.Instance,
    ];

    /// <summary>Theme names in the same order as <see cref="All"/>, for the settings picker.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(t => t.Name).ToArray();

    /// <summary>
    /// Resolves a persisted theme name. Unknown, empty and null names fall back to
    /// Midnight rather than throwing: settings.json is hand-editable, and an
    /// unthemed UI would leave the user without a usable interface to fix it with.
    /// </summary>
    /// <param name="name">The persisted theme name; matched case-insensitively.</param>
    /// <returns>The named theme, or Midnight if it is not recognised.</returns>
    public static ITheme Resolve(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? MidnightTheme.Instance;
}
```

- [ ] **Step 4: Point the two hand-listed test files at the registry**

Task 3 deliberately hand-listed the three themes in two places so that its own commit could compile without a registry. Now that `ThemeRegistry` exists, both switch to it — which also means a fourth theme added to the registry is automatically shape-checked and parity-checked, instead of silently skipped.

**(a)** In `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs`, replace the explicit list Task 3 Step 5 installed:

```csharp
    public static TheoryData<ITheme> AllThemes() => new()
    {
        MidnightTheme.Instance,
        EmberTheme.Instance,
        ContrastTheme.Instance,
    };
```

with:

```csharp
    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }
```

**(b)** In `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs`, delete the whole `ThemeNamed` helper and its doc comment, and change the two `var theme = ThemeNamed(themeName);` lines to:

```csharp
        var theme = ThemeRegistry.Resolve(themeName);
```

Then retarget the theme-set guard so it compares the validator against the registry — which is the stronger statement, and restores the check the plan originally intended. Rename `ValidatorAndCSharp_DeclareTheSameThemes` to `ValidatorAndRegistry_DeclareTheSameThemes` and replace its last two statements:

```csharp
        var inCSharp = ThemeNames().Select(row => (string)row[0]).OrderBy(n => n).ToList();

        Check.That(inCSharp).ContainsExactly(inPython);
```

with:

```csharp
        Check.That(ThemeRegistry.All.Select(t => t.Name).OrderBy(n => n).ToList())
             .ContainsExactly(inPython);
```

Neither test's case count may change — `ThemeShapeTests` still expands to three cases per theory, and `ThemeParityTests` still runs three. If either moves, `ThemeRegistry.All` disagrees with the list Task 3 already verified against the validator, and the registry is what is wrong.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --nologo
```

Expected: all Controls tests pass, with `ThemeShapeTests` and `ThemeParityTests` unchanged in case count.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/ThemeRegistry.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeRegistryTests.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs
git commit -m "feat(theme): add ThemeRegistry with case-insensitive resolve and Midnight fallback"
```

---

## Task 5: Re-scope the colour guards to run per theme

**Files:**
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs` (full rewrite)
- Modify: `tests/EyeCtrl.App.Tests/HaloContrastTests.cs` (full rewrite)

These two guards currently assert against `Tokens.X`, which after Task 2 means "whatever theme is active" — in tests, Midnight. Two themes therefore ship ungated. Both tests are rewritten to iterate `ThemeRegistry.All` and read from the `ITheme` directly, **never** through `Tokens.Current`: the repo runs xUnit v3 with collection parallelization **on** (no `xunit.runner.json`, see Task 10 Step 4), and a test that flips `Tokens.Current` would race any reader — including `SettingsDefaultsMatchTokensTests`, which Task 6 Step 4 de-races by pinning it to `MidnightTheme.Instance`. Reading an immutable `ITheme` instance has no such hazard.

`ToggleOff_IsNotRed` must also change. It asserts `c.R <= c.B` (`ToggleContrastTests.cs:46`), and Ember's `#342C21` gives R=52, B=33 — a straight failure. The rule's intent is "off is not an error", and what actually distinguishes Ember's warm near-neutral from a red is **chroma**: measured C* is Midnight 13.7, Ember 8.6, Contrast 0.0, against a semantic triad that gate 9 holds at C* ≥ 55. A ceiling of 20 encodes the intent with real headroom.

Colour maths is duplicated per test file here; that is the existing convention in all three of these files, and crossing assemblies to share ~20 lines is not worth a new type.

- [ ] **Step 1: Rewrite `ToggleContrastTests`**

Replace the entire contents of `tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Toggle state must be carried by lightness, not hue (spec §4.1.1 rule 7), in
/// every theme. The pre-migration pair managed 1.52:1 and was near-invisible to a
/// deuteranope.
///
/// Reads each ITheme directly rather than through Tokens.Current: these run in
/// parallel with everything else, and mutating the active theme would race.
/// </summary>
public class ToggleContrastTests
{
    /// <summary>
    /// Ceiling on ToggleOff's Lab chroma. "Off is not an error" is really a
    /// statement about saturation: gate 9 holds the semantic triad at C* >= 55, so
    /// a near-neutral off-state cannot be mistaken for one. Measured: Midnight
    /// 13.7, Ember 8.6, Contrast 0.0.
    ///
    /// This replaced an "R &lt;= B" test, which encoded "not red" as "not warm" and
    /// therefore failed Ember's warm neutral #342C21 (R 52, B 33) even though it is
    /// no more an error colour than Midnight's #2E2E42.
    /// </summary>
    private const double NeutralChromaCeiling = 20.0;

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Lab chroma C* = hypot(a*, b*). Mirrors tools/palette_validate.py:210-213.</summary>
    private static double Chroma(Color c)
    {
        double R = Channel(c.R), G = Channel(c.G), B = Channel(c.B);
        var x = R * 0.4124 + G * 0.3576 + B * 0.1805;
        var y = R * 0.2126 + G * 0.7152 + B * 0.0722;
        var z = R * 0.0193 + G * 0.1192 + B * 0.9505;

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;

        double fx = F(x / 0.95047), fy = F(y / 1.0), fz = F(z / 1.08883);
        return Math.Sqrt(Math.Pow(500 * (fx - fy), 2) + Math.Pow(200 * (fy - fz), 2));
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ToggleOnAndOff_DifferByAtLeastThreeToOne(ITheme theme)
    {
        var ratio = Contrast(theme.ToggleOn, theme.ToggleOff);
        Check.WithCustomMessage(
                $"{theme.Name}: ToggleOn/ToggleOff contrast is {ratio:F2}:1; spec "
                + "§4.1.1 rule 7 requires >= 3.0 so state survives any colour-vision "
                + "deficiency.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ToggleOff_IsANeutralNotAnErrorColour(ITheme theme)
    {
        // "Off" is not an error. Saturated hue is reserved for the semantic triad,
        // which gate 9 holds at C* >= 55 (§4.1.2, tools/palette_validate.py:132).
        var chroma = Chroma(theme.ToggleOff);
        Check.WithCustomMessage(
                $"{theme.Name}: ToggleOff is at C* {chroma:F1}, above the {NeutralChromaCeiling} "
                + "ceiling — it has become a hue rather than a neutral, and risks "
                + "reading as a semantic state.")
            .That(chroma).IsStrictlyLessThan(NeutralChromaCeiling);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ToggleOff_IsTheDarkerOfThePair(ITheme theme)
    {
        // Direction matters as much as separation: an "off" brighter than "on"
        // would satisfy the 3:1 rule while inverting the affordance.
        Check.WithCustomMessage($"{theme.Name}: ToggleOff must be darker than ToggleOn")
            .That(Luminance(theme.ToggleOff)).IsStrictlyLessThan(Luminance(theme.ToggleOn));
    }
}
```

- [ ] **Step 2: Rewrite `HaloContrastTests` to run per theme**

In `tests/EyeCtrl.App.Tests/HaloContrastTests.cs`, add the theme source and convert the four `[Fact]`s to `[Theory]`s. Replace lines 63-120 (everything from `[Fact] public void AccentWithHalo_...` to the end of the class, keeping the closing brace) with:

```csharp
    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AccentWithHalo_ClearsTheFloorOnEveryBackdrop(ITheme theme)
    {
        foreach (var b in Backdrops)
        {
            var edge = StrongestVisibleEdge(theme.Accent, theme.StrokeHalo, Parse(b));
            Check.WithCustomMessage(
                    $"{theme.Name}: Accent+StrokeHalo reaches only {edge:F2}:1 over {b}; "
                    + $"§4.1.3 requires >= {EdgeFloor}.")
                .That(edge).IsStrictlyGreaterThan(EdgeFloor);
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AccentAlone_FailsOnAtLeastOneBackdrop(ITheme theme)
    {
        // The reason the halo exists. If this ever passes, the guarantee has
        // become unnecessary and §4.1.3 should be revisited.
        var worst = double.MaxValue;
        foreach (var b in Backdrops)
            worst = Math.Min(worst, Contrast(theme.Accent, Parse(b)));

        Check.WithCustomMessage(
                $"{theme.Name}: Accent alone manages {worst:F2}:1 at worst — if that is "
                + $"above {EdgeFloor} the halo is redundant.")
            .That(worst).IsStrictlyLessThan(EdgeFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void CueBadgeColour_NeedsTheHaloToo(ITheme theme)
    {
        // The cue badge is AccentWarning on a filled shape (spec §4.1.3 case 2).
        var worstAlone = double.MaxValue;
        foreach (var b in Backdrops)
            worstAlone = Math.Min(worstAlone, Contrast(theme.AccentWarning, Parse(b)));
        Check.WithCustomMessage(
                $"{theme.Name}: AccentWarning alone manages {worstAlone:F2}:1 at worst.")
            .That(worstAlone).IsStrictlyLessThan(EdgeFloor);

        foreach (var b in Backdrops)
            Check.WithCustomMessage($"{theme.Name}: cue badge breaks the guarantee over {b}.")
                .That(StrongestVisibleEdge(theme.AccentWarning, theme.StrokeHalo, Parse(b)))
                .IsStrictlyGreaterThan(EdgeFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryColourPickerPreset_KeepsTheGuarantee(ITheme theme)
    {
        // CursorSettings.CircleColor is user-selectable, so the guarantee must
        // survive any preset — not just the default.
        foreach (var preset in EyeCtrl.Controls.DwellColorPicker.PresetsFor(theme))
        {
            var mark = Parse(preset.Hex);
            foreach (var b in Backdrops)
                Check.WithCustomMessage(
                        $"{theme.Name}: preset '{preset.Name}' ({preset.Hex}) breaks the "
                        + $"halo guarantee over {b}.")
                    .That(StrongestVisibleEdge(mark, theme.StrokeHalo, Parse(b)))
                    .IsStrictlyGreaterThan(EdgeFloor);
        }
    }
}
```

Add `using EyeCtrl.Controls.Theme;` — it is already there at line 6 — and confirm `using Xunit;` (line 8) is present for `TheoryData`.

`DwellColorPicker.PresetsFor` does not exist yet; it arrives in Task 6. Expect this file not to compile until then.

- [ ] **Step 3: Run and confirm the expected shape of failure**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: `EyeCtrl.App.Tests` fails to build (`CS0117: 'DwellColorPicker' does not contain a definition for 'PresetsFor'`). `EyeCtrl.Controls.Tests` should be **green**, including the three new `ToggleContrastTests` theories × 3 themes. If `ToggleOff_IsANeutralNotAnErrorColour` fails for a theme, re-measure that theme's chroma before touching the ceiling:

```bash
python -c "import importlib.util; s=importlib.util.spec_from_file_location('pv','tools/palette_validate.py'); pv=importlib.util.module_from_spec(s); s.loader.exec_module(pv); [print(t, '%.1f'%pv.chroma(pv.parse(p['ToggleOff']))) for t,p in pv.THEMES.items()]"
```

- [ ] **Step 4: Commit (with Task 6, since the two files interlock)**

Hold this commit until Task 6 Step 4 — `HaloContrastTests` will not compile until `PresetsFor` exists. Commit the `ToggleContrastTests` half now if you want a clean boundary:

```bash
git add tests/EyeCtrl.Controls.Tests/Theme/ToggleContrastTests.cs
git commit -m "test(theme): gate the toggle pair in every theme, by chroma not by hue"
```

---

## Task 6: Theme-derived presets, and the persisted-colour decision

**Files:**
- Modify: `src/EyeCtrl.Controls/DwellColorPicker.cs:35-39`
- Modify: `tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs` (full rewrite)
- Modify: `tests/EyeCtrl.App.Tests/SettingsDefaultsMatchTokensTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs:40-46`

**The persisted-colour decision.** `CursorSettings.CircleColor`, `CalibrationSettings.TargetColor`, `AutoSelectionSettings.HighlightColor` and `DragSettings.TrailColor` are absolute hex in `settings.json`. They are hereby **explicitly theme-independent**: they are user-chosen absolute values, and silently rewriting a user's chosen colour because they switched theme is worse than leaving it. Their *defaults* continue to track Midnight (`src/EyeCtrl.Core/Settings/SettingsDefaults.cs:14-18`), which is safe because the guarantee that matters — the §4.1.3 halo bar — is measured to hold for every preset against every theme's halo, worst case **9.13:1** against a 7:1 floor. What must change is that `SettingsDefaultsMatchTokensTests` currently compares against `Tokens.Accent`, i.e. against *ambient* `Current`; it is pinned to `MidnightTheme.Instance` so it states what it means.

**The preset list.** Midnight's four presets are exactly `{Accent, AccentSuccess, AccentWarning, TextPrimary}`, so deriving them from the theme reproduces the current Midnight list hex-for-hex (the first swatch's tooltip label changes from "Aqua" to "Accent") while giving Ember and Contrast coherent lists of their own.

- [ ] **Step 1: Rewrite `ColorPickerPresetTests`**

Replace the entire contents of `tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// DwellColorPicker feeds CursorSettings.CircleColor, which colours a haloed ring
/// on a transparent overlay (spec §4.1.3). A dark or mid-tone preset collapses that
/// contrast guarantee, so every preset must clear 7:1 against its theme's
/// StrokeHalo — and the halo differs per theme (#0A0A14 / #0E0A06 / #000000), so
/// the gate runs per theme.
/// </summary>
public class ColorPickerPresetTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryPreset_ClearsSevenToOneAgainstStrokeHalo(ITheme theme)
    {
        foreach (var (hex, name) in DwellColorPicker.PresetsFor(theme))
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var ratio = Contrast(c, theme.StrokeHalo);
            Check.WithCustomMessage(
                    $"{theme.Name}: preset '{name}' ({hex}) reaches only {ratio:F2}:1 "
                    + "against StrokeHalo; §5 step 9 requires >= 7:1 or the halo "
                    + "guarantee on the cursor ring collapses.")
                .That(ratio).IsStrictlyGreaterThan(7.0);
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FirstPreset_IsTheThemeAccent(ITheme theme)
    {
        // Accent is the default dwell colour; it should be the first choice offered.
        var expected = $"#{theme.Accent.R:X2}{theme.Accent.G:X2}{theme.Accent.B:X2}";
        Check.That(DwellColorPicker.PresetsFor(theme)[0].Hex).IsEqualTo(expected);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Accent_IsNeverTheOutgoingLime(ITheme theme)
    {
        // Replaces a blanket "no preset may be #00FF00" rule. That rule was about
        // the *dwell accent* Lime replaced (§9.2) — but Contrast legitimately uses
        // #00FF00 as AccentSuccess and ToggleOn, validated by all ten gates, so the
        // blanket form would have banned a gated palette value.
        Check.That($"#{theme.Accent.R:X2}{theme.Accent.G:X2}{theme.Accent.B:X2}")
             .IsNotEqualTo("#00FF00");
    }

    [Fact]
    public void MidnightPresets_AreUnchangedByTheDerivation()
    {
        // Regression guard on the *derivation*, not on the palette: ThemeParityTests
        // already pins Midnight's four underlying colours to the validator (the one
        // source of truth). This restates the four hexes deliberately, to catch a
        // change to the derivation itself — a reordered `PresetsFor`, a wrong
        // `Hex()` format, a swapped entry — that leaves every palette value correct
        // but ships a different preset list than the one users had. It is the only
        // test that fixes the *order and membership* of the shipped list.
        Check.That(DwellColorPicker.PresetsFor(MidnightTheme.Instance).Select(p => p.Hex))
             .ContainsExactly("#A5F3FC", "#7EE787", "#E8A33C", "#D6DEF5");
    }

    [Fact]
    public void DefaultPresets_FollowTheActiveTheme()
    {
        Check.That(DwellColorPicker.DefaultPresets)
             .ContainsExactly(DwellColorPicker.PresetsFor(Tokens.Current));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ColorPickerPresetTests" --nologo
```

Expected: **build failure**, `CS0117: 'DwellColorPicker' does not contain a definition for 'PresetsFor'`.

- [ ] **Step 3: Derive the presets**

In `src/EyeCtrl.Controls/DwellColorPicker.cs`, replace lines 35-39:

```csharp
    public static readonly (string Hex, string Name)[] DefaultPresets =
    [
        ("#A5F3FC", "Aqua"), ("#7EE787", "Green"),
        ("#E8A33C", "Amber"), ("#D6DEF5", "Light"),
    ];
```

with:

```csharp
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// The swatches offered for <paramref name="theme"/>: its accent first (the
    /// default dwell colour), then success, warning and body text.
    ///
    /// Derived rather than listed, so each theme offers colours from its own
    /// palette instead of Midnight's. Under Midnight this reproduces the four
    /// presets that shipped, hex for hex. Every entry is gated at >= 7:1 against
    /// its own theme's StrokeHalo by ColorPickerPresetTests — worst case across all
    /// three themes is 9.13:1 — because a dark preset would collapse the §4.1.3
    /// halo guarantee on the cursor ring.
    /// </summary>
    /// <param name="theme">The theme whose palette the swatches come from.</param>
    /// <returns>Hex/name pairs, accent first.</returns>
    public static (string Hex, string Name)[] PresetsFor(ITheme theme) =>
    [
        (Hex(theme.Accent), "Accent"),
        (Hex(theme.AccentSuccess), "Green"),
        (Hex(theme.AccentWarning), "Amber"),
        (Hex(theme.TextPrimary), "Light"),
    ];

    /// <summary>Swatches for the active theme. Used when no explicit preset list is passed.</summary>
    public static (string Hex, string Name)[] DefaultPresets => PresetsFor(Tokens.Current);
```

Add `using EyeCtrl.Controls.Theme;` to the file's using block if it is not already present, and confirm `using System.Windows.Media;` is there for `Color`.

`DefaultPresets` changes from a field to a property. Its only consumer is `presets ??= DefaultPresets;` at `src/EyeCtrl.Controls/DwellColorPicker.cs:83`, which is unaffected.

- [ ] **Step 4: Pin the settings defaults to Midnight explicitly**

In `tests/EyeCtrl.App.Tests/SettingsDefaultsMatchTokensTests.cs`, replace the class doc comment and the four assertions (lines 9-36) with the version below.

This rewrite has a second purpose beyond stating what it means: the current class reads `Tokens.Accent` / `Tokens.AccentWarning` (i.e. ambient `Current`), which makes it a reader of process-global state. With xUnit v3 collection parallelization on (see Task 10 Step 4), that races `ThemeStartupTests` once it lands. Pinning every assertion to `MidnightTheme.Instance` removes the `Tokens.X` reads entirely, so this class needs no collection attribution. After this edit, `grep -rn "Tokens\." tests/EyeCtrl.App.Tests` should show only `ThemeStartupTests` (the one controlled mutator) and the exemption-path string in `RawColourLiteralTests`.

```csharp
/// <summary>
/// EyeCtrl.Core cannot reference EyeCtrl.Controls, so these four persisted colour
/// defaults are string literals that must match Tokens by value (spec §5 Phase 2).
/// This test is the only thing keeping them in sync.
///
/// Pinned to <see cref="MidnightTheme"/> rather than to Tokens.Current: these are
/// *defaults for persisted, theme-independent* values (a user's chosen cursor
/// colour is not rewritten when they switch theme), so they track the shipping
/// palette specifically, not whichever theme happens to be active. Every preset a
/// user can pick still clears the §4.1.3 halo bar in every theme — worst case
/// 9.13:1 — which is what makes theme-independence safe here.
/// </summary>
public class SettingsDefaultsMatchTokensTests
{
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    [Fact]
    public void CursorCircleColour_DefaultsToMidnightAccent() =>
        Check.That(Parse(new CursorSettings().CircleColor))
             .IsEqualTo(MidnightTheme.Instance.Accent);

    [Fact]
    public void CalibrationTargetColour_DefaultsToMidnightAccent() =>
        Check.That(Parse(new CalibrationSettings().TargetColor))
             .IsEqualTo(MidnightTheme.Instance.Accent);

    [Fact]
    public void AutoSelectionHighlight_DefaultsToMidnightAccent() =>
        Check.That(Parse(new AutoSelectionSettings().HighlightColor))
             .IsEqualTo(MidnightTheme.Instance.Accent);

    [Fact]
    public void DragTrail_DefaultsToMidnightAccentWarning()
    {
        // Warm and deliberately distinct from Accent, so a motion trail is never
        // mistaken for dwell progress (spec §5 step 15).
        Check.That(Parse(new DragSettings().TrailColor))
             .IsEqualTo(MidnightTheme.Instance.AccentWarning);
    }
}
```

- [ ] **Step 5: Swap the raw-literal exemptions**

This is the **last** of the four exemption edits (finding 5). Tasks 1, 2 and 3 already moved the list onto the theme files; the only entry that goes stale here is `DwellColorPicker.cs`, because Step 3 just replaced its literal presets with a derivation. `EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour` fails on it until it goes.

In `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs`, **delete** these two lines from `Exempt`:

```csharp
        ["src/EyeCtrl.Controls/DwellColorPicker.cs"] =
            "DefaultPresets are user-selectable palette values, gated by ColorPickerPresetTests",
```

`Exempt` should then read exactly:

```csharp
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/EyeCtrl.Controls/Theme/MidnightTheme.cs"] =
            "the Midnight palette itself (spec §4.1) — gated by tools/palette_validate.py",
        ["src/EyeCtrl.Controls/Theme/EmberTheme.cs"] =
            "the Ember palette itself (spec §10.2) — gated by tools/palette_validate.py",
        ["src/EyeCtrl.Controls/Theme/ContrastTheme.cs"] =
            "the Contrast palette itself (spec §10.7) — gated by tools/palette_validate.py",
    };
```

Three entries, one per palette. `Tokens.cs` and `DwellColorPicker.cs` are deliberately absent: neither contains a literal any more, and a stale exemption silently protects nothing. If this block does not already have the three theme files in it, an earlier task skipped its exemption step — go back and find which, rather than pasting them all in here.

- [ ] **Step 6: Run the full suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures. `HaloContrastTests` from Task 5 now compiles and its four theories run across three themes each. If `NoUIFile_ContainsARawColourOutsideThePalette` fails naming a theme file, the exemption path is misspelled — it is repo-relative with forward slashes (`RawColourLiteralTests.cs:117`).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Controls/DwellColorPicker.cs tests/EyeCtrl.Controls.Tests/Theme/ColorPickerPresetTests.cs tests/EyeCtrl.App.Tests/HaloContrastTests.cs tests/EyeCtrl.App.Tests/SettingsDefaultsMatchTokensTests.cs tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs
git commit -m "feat(theme): derive colour-picker presets per theme; gate halo/presets in all three"
```

---

## Task 7: Replace the dead theme flags with one that works

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ThemeSettings.cs` (full rewrite)
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs:36-37`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:447-450,461`
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs:71`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs:31,49`
- Modify: `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceValidationTests.cs:187-191,208-209`
- Test: `tests/EyeCtrl.App.Tests/ThemeSettingsNamesTests.cs` (create)

`ThemeSettings` is already enumerated at `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:66`, so reusing it means `EyeCtrlSettingsEnumerationTests` (which pins 19 entries) needs **no change**. Adding a new sub-setting would have required editing both the field and `EnumerateSubSettings`; reusing avoids that entirely.

`EyeCtrl.Core` cannot reference `EyeCtrl.Controls`, so the valid names are restated in Core and checked against `ThemeRegistry` from `EyeCtrl.App.Tests` — the same arrangement `SettingsDefaults` / `SettingsDefaultsMatchTokensTests` already uses (`src/EyeCtrl.Core/Settings/SettingsDefaults.cs:5-10`).

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/ThemeSettingsNamesTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Linq;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// EyeCtrl.Core cannot reference EyeCtrl.Controls, so ThemeSettings restates the
/// valid theme names. This is the only project referencing both, and the only
/// thing keeping the two lists in step — a name in one but not the other is
/// either an unpickable theme or a setting that fails validation.
/// </summary>
public class ThemeSettingsNamesTests
{
    [Fact]
    public void ValidNames_MatchTheRegistry() =>
        Check.That(ThemeSettings.ValidNames).ContainsExactly(ThemeRegistry.Names);

    [Fact]
    public void DefaultThemeName_ResolvesToTheShippingPalette() =>
        Check.That(ThemeRegistry.Resolve(new ThemeSettings().Name).Name).IsEqualTo("Midnight");

    [Fact]
    public void EveryValidName_Resolves()
    {
        foreach (var name in ThemeSettings.ValidNames)
            Check.WithCustomMessage($"ThemeSettings offers '{name}' but the registry cannot resolve it")
                .That(ThemeRegistry.Resolve(name).Name).IsEqualTo(name);
    }
}
```

Add to `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceValidationTests.cs` (after the existing round-trip test, inside the class):

```csharp
    [Fact]
    public void ThemeName_IsRejected_WhenNotAKnownTheme()
    {
        var settings = new EyeCtrlSettings();
        settings.Theme.Name = "Sepia";

        Check.That(settings.Validate())
             .HasElementThatMatches(e => e.Contains(nameof(ThemeSettings.Name)));
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~ThemeSettingsNamesTests|FullyQualifiedName~ThemeName_IsRejected" --nologo
```

Expected: **build failure**, `CS0117: 'ThemeSettings' does not contain a definition for 'ValidNames'`.

- [ ] **Step 3: Rewrite `ThemeSettings`**

Replace the entire contents of `src/EyeCtrl.Core/Settings/ThemeSettings.cs`:

```csharp
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Which palette the overlay UI uses. See docs/design/overlay-unification-spec.md
/// §4.1 (Midnight), §10.2 (Ember) and §10.7 (Contrast).
///
/// Replaces three flags — Mode ("System"/"Light"/"Dark"), OverlayOpacity and
/// HighContrast — that were persisted and surfaced in the settings UI but read by
/// nothing. Mode's light/dark axis was never buildable anyway: §2 rules out a light
/// theme outright, because eye trackers work poorly against bright surfaces.
/// Legacy keys are simply ignored on load (unknown JSON members), so there is no
/// migration code.
/// </summary>
public partial class ThemeSettings : ObservableObject, IValidatable
{
    /// <summary>
    /// Names accepted by <see cref="Name"/>, in picker order.
    ///
    /// Restated here because EyeCtrl.Core cannot reference EyeCtrl.Controls, where
    /// ThemeRegistry lives; ThemeSettingsNamesTests in EyeCtrl.App.Tests keeps the
    /// two in step, the same way SettingsDefaults is kept in step with Tokens.
    /// </summary>
    public static readonly string[] ValidNames = ["Midnight", "Ember", "Contrast"];

    /// <summary>
    /// The active palette's name. Applied at startup; changing it takes effect on
    /// the next launch, because a theme change recreates windows rather than
    /// re-resolving live (spec §10.6).
    /// </summary>
    [ObservableProperty] private string _name = "Midnight";

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(Name, ValidNames, nameof(Name))
        );
    }
}
```

- [ ] **Step 4: Delete `CursorSettings.CircleHighContrast`**

In `src/EyeCtrl.Core/Settings/CursorSettings.cs`, delete lines 36-37:

```csharp
    /// <summary>Whether the cursor circle uses a high-contrast color scheme.</summary>
    [ObservableProperty] private bool _circleHighContrast = false;
```

No renderer reads it; high contrast is now a theme (`ContrastTheme`), not a per-control flag.

- [ ] **Step 5: Delete the two dead toggles**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, delete lines 447-450:

```csharp
                () => Add(new DwellToggle(_viewModel, "c_hc", "High Contrast Mode",
                    () => _settings.Cursor.CircleHighContrast,
                    v => { _settings.Cursor.CircleHighContrast = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
```

and line 461 inside the cursor reset block:

```csharp
                        _settings.Cursor.CircleHighContrast = defaults.CircleHighContrast;
```

In `src/EyeCtrl.ViewModels/SettingsViewModel.cs`, delete line 71:

```csharp
            new ToggleSettingViewModel("highContrast", "High Contrast", c.CircleHighContrast, v => c.CircleHighContrast = v),
```

Leave `SettingsViewModel.cs:175-177` alone for now — Task 11 replaces those three with the picker.

- [ ] **Step 6: Update the two settings tests**

In `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs`, replace line 31:

```csharp
        original.Theme.Mode = "Dark";
```

with:

```csharp
        original.Theme.Name = "Ember";
```

and line 49:

```csharp
        Check.That(deserialized.Theme.Mode).IsEqualTo("Dark");
```

with:

```csharp
        Check.That(deserialized.Theme.Name).IsEqualTo("Ember");
```

**Leave `"circleHighContrast": false` at line 91 in place.** It sits in the `Settings_BackwardsCompatible_DeserializesExistingJson` fixture, and with the property gone it becomes an unknown member — which both `JsonSerializerOptions` in play ignore by default. The test now demonstrates exactly the no-migration decision: a legacy `settings.json` still loads, the dead key is silently dropped. Add above the JSON literal:

```csharp
        // circleHighContrast was removed when high contrast became a theme; it is
        // retained here deliberately, as an unknown member, to prove a pre-existing
        // settings.json still loads without migration code.
```

In `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceValidationTests.cs`, replace lines 187-191:

```csharp
            Theme = new ThemeSettings
            {
                Mode = "Dark",
                OverlayOpacity = 0.5
            }
```

with:

```csharp
            Theme = new ThemeSettings
            {
                Name = "Contrast"
            }
```

and lines 208-209:

```csharp
        Check.That(loaded.Theme.Mode).IsEqualTo("Dark");
        Check.That(loaded.Theme.OverlayOpacity).IsEqualTo(0.5);
```

with:

```csharp
        Check.That(loaded.Theme.Name).IsEqualTo("Contrast");
```

- [ ] **Step 7: Run the full suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures. In particular `EyeCtrlSettingsEnumerationTests` must still report 19 entries — if it does not, a sub-setting was added or removed by mistake. The build will surface any remaining `CircleHighContrast` reference as `CS1061`.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ThemeSettings.cs src/EyeCtrl.Core/Settings/CursorSettings.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs tests/EyeCtrl.Core.Tests/Settings/SettingsServiceValidationTests.cs tests/EyeCtrl.App.Tests/ThemeSettingsNamesTests.cs
git commit -m "feat(theme): replace three dead theme flags and two dead toggles with Theme.Name"
```

---

## Task 8: Remove the 30 static token captures

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/EmojiGridManager.cs:23-29`
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:22-37` (+ delete the static constructor at 39-52)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:44-51`
- Modify: `src/EyeCtrl.Controls/DwellComboBox.cs:31-32`
- Modify: `src/EyeCtrl.Controls/DwellSlider.cs:21`
- Modify: `src/EyeCtrl.Controls/DwellToggle.cs:21-22`
- Test: `tests/EyeCtrl.App.Tests/StaticTokenCaptureTests.cs` (create)

Each of these reads `Tokens.X` **once**, at type initialisation, whenever the CLR first touches the type. Under a non-Midnight theme they render Midnight if anything touches the type before `Tokens.Activate` runs. 25 are field initialisers — the fix is mechanical, `=` becomes `=>` and `readonly` goes. The other five (`ForegroundFuzzyMatch`, `ForegroundNextWord`, `ForegroundPhrase`, `ForegroundSentenceCompletion`, `ForegroundLMSuggestion`) are assigned from `Tokens.Brush*` inside `KeyboardWindow`'s static constructor (`KeyboardWindow.xaml.cs:33-52`) and the whole block must be deleted: once the fields are forwarding properties, the constructor's assignments no longer compile, which is the mechanical signal that nothing of it may survive. The guard makes a 31st capture impossible to add quietly, in either form.

- [ ] **Step 1: Write the failing guard**

Create `tests/EyeCtrl.App.Tests/StaticTokenCaptureTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// A static field initialised from Tokens snapshots the palette at type-init time,
/// which is whenever the CLR first touches that type — before App.OnStartup has
/// activated the persisted theme, in general. The field then holds Midnight
/// forever, silently, under every other theme. Assigning Tokens brushes inside a
/// static constructor is the same snapshot in a form a `= Tokens.` grep cannot
/// see — KeyboardWindow had five such assignments, and the guard below catches
/// both forms.
///
/// Forwarding properties (=&gt; Tokens.X) have no such moment. This guard keeps it
/// that way; 30 captures existed before the theme system and all were converted.
/// </summary>
public class StaticTokenCaptureTests
{
    private static readonly string[] ScannedProjects =
    [
        "src/EyeCtrl.App",
        "src/EyeCtrl.Controls",
    ];

    /// <summary>
    /// A static field whose initialiser reads Tokens. Matches `= Tokens.` but not
    /// `=&gt; Tokens.`, because the arrow form is the fix, not the defect.
    /// </summary>
    private static readonly Regex Capture = new(
        @"static\s+(?:readonly\s+)?[\w.<>\[\]?]+\s+\w+\s*=\s*Tokens\.",
        RegexOptions.Compiled);

    /// <summary>
    /// A static constructor (or parameterless static method) whose first-level body
    /// reads Tokens — the same type-init snapshot as a field initialiser, invisible
    /// to <see cref="Capture"/>. KeyboardWindow's constructor held five such reads.
    /// The `[^{}]*` span stops at nested braces, so a read nested deeper than one
    /// level is missed — widening that span is a future refinement, not a gap in
    /// what this task removes.
    /// </summary>
    private static readonly Regex CtorCapture = new(
        @"static\s+\w+\(\)\s*\{[^{}]*Tokens\.",
        RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(StaticTokenCaptureTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }

    [Fact]
    public void NoStaticField_SnapshotsAToken()
    {
        var root = RepoRoot().FullName;
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var text = File.ReadAllText(file);
                foreach (Match m in Capture.Matches(text))
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: {m.Value.Trim()}");
                foreach (Match m in CtorCapture.Matches(text))
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: static ctor reads Tokens: {m.Value.Trim()}");
            }
        }

        Check.WithCustomMessage(
                "These static fields and static ctors snapshot the palette at type-init "
                + "time and will hold Midnight under every other theme. Change '= Tokens.X' "
                + "to '=> Tokens.X' and drop 'readonly'; delete any static ctor that reads "
                + "Tokens:\n  "
                + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~StaticTokenCaptureTests" --nologo
```

Expected: **FAIL**, listing 26 offenders across the 6 files above — the 25 field-initialiser captures plus KeyboardWindow's static constructor (one offender per ctor, named by its signature). If the count is not 26, the codebase has moved since `4451cdb` — reconcile before continuing.

- [ ] **Step 3: Convert all 30 captures**

The transformation is identical everywhere: `static readonly T Name = Tokens.X;` → `static T Name => Tokens.X;`. Apply it to each site.

`src/EyeCtrl.App/Overlays/EmojiGridManager.cs:23-29`:

```csharp
    private static Color KeyColorNormal => Tokens.SurfaceKey;
    private static Color KeyColorSwitch => Tokens.SurfaceKeySwitch;
    private static Color KeyColorHighlight => Tokens.ToggleOn;
    private static Color CategoryBarColor => Tokens.SurfaceDark;
```

(and, preserving the intervening comment at line 27, the two that follow)

```csharp
    private static Color CategoryBarActiveColor => Tokens.SurfaceKeyActive;
    private static Color EmojiGridBackgroundColor => Tokens.SurfaceMedium;
```

`src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:22-37`:

```csharp
    private static Color KeyColorNormal => Tokens.SurfaceKey;
    private static Color KeyColorSpecial => Tokens.SurfaceKeyModifier;
    private static Color KeyColorSwitch => Tokens.SurfaceKeySwitch;
```

```csharp
    private static Color KeyColorModifierActive => Tokens.SurfaceKeyActive;
    private static Color PredictionBarColor => Tokens.SurfacePrediction;
```

```csharp
    private static Brush ForegroundExactPrefix => Tokens.BrushTextPrimary;
    private static Brush ForegroundFuzzyMatch => Tokens.BrushTextSecondary;
    private static Brush ForegroundNextWord => Tokens.BrushAccentSuccess;
    private static Brush ForegroundPhrase => Tokens.BrushAccentWarning;
    private static Brush ForegroundSentenceCompletion => Tokens.BrushAccent;
    private static Brush ForegroundLMSuggestion => Tokens.BrushTextPrimary;
```

The last five are currently declared without initialisers (`KeyboardWindow.xaml.cs:33-37`) and assigned from local `Tokens.Brush*` copies in `static KeyboardWindow()` (`:39-52`) — **delete the entire static constructor including its comment**. Once the fields are forwarding properties, its assignments fail to compile ("cannot assign to a property", `CS0200`), which is the signal that nothing of the constructor may survive. The property reads resolve to the theme's cached frozen brushes (`ThemeBase` builds each once), so the "cached frozen brushes" intent of the removed comment still holds with zero per-access allocation. The `// Active modifier…` comment above `KeyColorModifierActive` and the `// Cached frozen brushes…` comment above `ForegroundExactPrefix` stay; only declaration lines and the constructor change.

`src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:44-51`:

```csharp
    private static Color BtnBase => Tokens.SurfaceMedium;
    private static Color TabActive => Tokens.SurfaceKeySwitch;
    private static Color TabInactive => Tokens.SurfaceKey;
    private static Color ResetBase => Tokens.AccentWarning;
    private static Color ModalYes => Tokens.ToggleOn;
    private static Color ModalNo => Tokens.AccentDanger;
    private static Color CloseBase => Tokens.AccentDanger;
    private static Color DeleteBase => Tokens.AccentWarning;
```

`src/EyeCtrl.Controls/DwellComboBox.cs:31-32`:

```csharp
    private static Color BtnBase => Tokens.SurfaceMedium;
    private static Color SelectedColor => Tokens.ToggleOn;
```

`src/EyeCtrl.Controls/DwellSlider.cs:21`:

```csharp
    private static Color BtnBase => Tokens.SurfaceMedium;
```

`src/EyeCtrl.Controls/DwellToggle.cs:21-22`:

```csharp
    private static Color OnColor => Tokens.ToggleOn;
    private static Color OffColor => Tokens.ToggleOff;
```

Preserve each existing doc comment or inline comment above these declarations; only the declaration line changes.

- [ ] **Step 4: Run the full suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures, and `NoStaticField_SnapshotsAToken` green. If the build reports `CS0198`/`CS0200` or "cannot assign to a property", a site was converted to a property but still has an assignment elsewhere — KeyboardWindow's constructor is the known one; the others are read-only by construction, so investigate rather than reverting.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/EmojiGridManager.cs src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.Controls/DwellComboBox.cs src/EyeCtrl.Controls/DwellSlider.cs src/EyeCtrl.Controls/DwellToggle.cs tests/EyeCtrl.App.Tests/StaticTokenCaptureTests.cs
git commit -m "fix(theme): stop 30 static captures snapshotting the palette at type-init"
```

---

## Task 9: `ThemeResourceApplier`

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/ThemeResourceApplier.cs`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/ThemeResourceApplierTests.cs`

`OverlayStyles.xaml` resolves its `{x:Static}` references when WPF parses it, which happens during `Application` construction — potentially *before* `OnStartup` activates a theme. Rather than reason about parse timing, the applier writes the resolved theme into the target dictionary explicitly. Written into `Application.Resources` at the top level, those entries shadow the merged dictionary's, so the result is correct regardless of when parsing happened.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Controls.Tests/Theme/ThemeResourceApplierTests.cs`:

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The applier is what makes a theme visible to the 106 {StaticResource Brush*}
/// lookups. It writes explicitly rather than relying on OverlayStyles.xaml being
/// parsed after the theme is chosen — parse timing is not something startup can
/// control, and getting it wrong would silently ship Midnight.
/// </summary>
public class ThemeResourceApplierTests
{
    private static readonly Uri DictionaryUri = new(
        "pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml",
        UriKind.Absolute);

    private static void EnsurePackScheme()
    {
        if (UriParser.IsKnownScheme("pack")) return;
        if (Application.Current is null) _ = new Application();
    }

    [Fact]
    public void Apply_WritesEveryBrushOfTheGivenTheme()
    {
        var target = new ResourceDictionary();
        ThemeResourceApplier.Apply(target, ContrastTheme.Instance);

        Check.That(((SolidColorBrush)target["BrushAccent"]!).Color)
             .IsEqualTo(ContrastTheme.Instance.Accent);
        Check.That(((SolidColorBrush)target["BrushSurfaceDark"]!).Color)
             .IsEqualTo(ContrastTheme.Instance.SurfaceDark);
        Check.That(((SolidColorBrush)target["BrushAccentDim10"]!).Opacity).IsEqualTo(0.10);
    }

    [Fact]
    public void Apply_WritesBothFormsOfTheThicknessToken()
    {
        var target = new ResourceDictionary();
        ThemeResourceApplier.Apply(target, ContrastTheme.Instance);

        // Contrast is the theme that actually exercises this: 2, not 1 (§4.4).
        Check.That(target["BorderThicknessStandard"]).IsEqualTo(2.0);
        Check.That(target["StandardBorderThickness"]).IsEqualTo(new Thickness(2, 2, 2, 2));
    }

    [Fact]
    public void Apply_IsIdempotentAndSwitchable()
    {
        var target = new ResourceDictionary();
        ThemeResourceApplier.Apply(target, ContrastTheme.Instance);
        ThemeResourceApplier.Apply(target, EmberTheme.Instance);

        Check.That(((SolidColorBrush)target["BrushAccent"]!).Color)
             .IsEqualTo(EmberTheme.Instance.Accent);
        Check.That(target["BorderThicknessStandard"]).IsEqualTo(1.0);
    }

    [StaFact]
    public void Apply_CoversEveryKeyTheDictionaryDeclares()
    {
        // If OverlayStyles.xaml grows a key the applier does not write, that key
        // keeps its parse-time value and silently stays on whatever theme was
        // active when WPF parsed the dictionary.
        EnsurePackScheme();
        var declared = new ResourceDictionary { Source = DictionaryUri }
            .Keys.Cast<object>().Select(k => k.ToString()!).ToList();

        var applied = new ResourceDictionary();
        ThemeResourceApplier.Apply(applied, MidnightTheme.Instance);
        var written = applied.Keys.Cast<object>().Select(k => k.ToString()!).ToList();

        var missing = declared.Except(written).ToList();
        Check.WithCustomMessage(
                "OverlayStyles.xaml declares keys the applier never writes, so a theme "
                + "switch would not reach them: " + string.Join(", ", missing))
            .That(missing).IsEmpty();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --filter "FullyQualifiedName~ThemeResourceApplierTests" --nologo
```

Expected: **build failure**, `CS0103: The name 'ThemeResourceApplier' does not exist`.

- [ ] **Step 3: Create the applier**

Create `src/EyeCtrl.Controls/Theme/ThemeResourceApplier.cs`:

```csharp
namespace EyeCtrl.Controls.Theme;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// Writes a theme's brushes and thickness into a <see cref="ResourceDictionary"/>,
/// so the 106 {StaticResource Brush*} lookups across the overlay XAML resolve to
/// that theme.
///
/// OverlayStyles.xaml pulls the same values through {x:Static}, but those resolve
/// when WPF parses the dictionary — which happens during Application construction,
/// before OnStartup can choose a theme. Writing explicitly makes the result
/// independent of that ordering. Applied to Application.Resources at the top level,
/// these entries take precedence over the merged dictionary's.
///
/// Keys are the ITheme brush property names verbatim (BrushAccent, BrushSurfaceDark,
/// …), which is exactly what OverlayStyles.xaml uses.
/// </summary>
public static class ThemeResourceApplier
{
    /// <summary>The XAML resource key holding the raw pixel value of the border thickness.</summary>
    public const string ThicknessKey = "BorderThicknessStandard";

    /// <summary>The XAML resource key holding the uniform <see cref="Thickness"/> form.</summary>
    public const string UniformThicknessKey = "StandardBorderThickness";

    /// <summary>
    /// Every brush the theme exposes, paired with the resource key it is published
    /// under. Reflected over <see cref="ITheme"/> rather than listed, so a token
    /// added to the interface cannot be forgotten here.
    /// </summary>
    /// <param name="theme">The theme to read.</param>
    /// <returns>Key/brush pairs, in interface declaration order.</returns>
    public static IEnumerable<(string Key, SolidColorBrush Brush)> EnumerateBrushes(ITheme theme) =>
        typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType == typeof(SolidColorBrush))
            .Select(p => (p.Name, (SolidColorBrush)p.GetValue(theme)!));

    /// <summary>
    /// Writes <paramref name="theme"/> into <paramref name="target"/>, replacing any
    /// existing entries under the same keys.
    /// </summary>
    /// <param name="target">The dictionary to write into — normally <c>Application.Resources</c>.</param>
    /// <param name="theme">The theme to publish.</param>
    public static void Apply(ResourceDictionary target, ITheme theme)
    {
        foreach (var (key, brush) in EnumerateBrushes(theme))
            target[key] = brush;

        var t = theme.BorderThicknessStandard;
        target[ThicknessKey] = t;
        target[UniformThicknessKey] = new Thickness(t, t, t, t);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Controls.Tests --nologo
```

Expected: all pass. If `Apply_CoversEveryKeyTheDictionaryDeclares` fails, `OverlayStyles.xaml` declares something outside the brush/thickness set — add it to `Apply` rather than loosening the test.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/ThemeResourceApplier.cs tests/EyeCtrl.Controls.Tests/Theme/ThemeResourceApplierTests.cs
git commit -m "feat(theme): add ThemeResourceApplier so XAML lookups follow the active theme"
```

---

## Task 10: Activate the theme at startup

**Files:**
- Create: `src/EyeCtrl.App/Services/ThemeStartup.cs`
- Create: `tests/EyeCtrl.App.Tests/ThemeStartupTests.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs:101-104`

The wiring is two lines, but they go through an extracted, testable type — the same pattern `AppBootstrapper` and `OverlayManager` already use — because `OnStartup` itself cannot be exercised headlessly.

Placement matters: after `InitializeSettingsAndLogging()` (`App.xaml.cs:101`), which is where `settings` first exists, and **before** `AppBootstrapper.BuildServiceProvider` (`App.xaml.cs:104`), which is upstream of every overlay window and dwell control. `SplashWindow` is shown earlier still (`App.xaml.cs:95-96`) and is therefore always drawn pre-theme; it is a brief, deliberate exception, not something to fix here.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/ThemeStartupTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.Windows;
using System.Windows.Media;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Startup theme selection, exercised without launching the app.
///
/// These mutate Tokens.Current, which is process-global, so they are serialised
/// into one collection and each restores Midnight afterwards. Every other theme
/// test reads an ITheme directly and is unaffected.
/// </summary>
[Collection("ActiveTheme")]
public class ThemeStartupTests
{
    [Fact]
    public void Apply_ActivatesTheNamedTheme()
    {
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Theme.Name = "Contrast";
            var resources = new ResourceDictionary();

            ThemeStartup.Apply(settings, resources);

            Check.That(Tokens.Current.Name).IsEqualTo("Contrast");
            Check.That(Tokens.Accent).IsEqualTo(ContrastTheme.Instance.Accent);
            Check.That(Tokens.BorderThicknessStandard).IsEqualTo(2.0);
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void Apply_PublishesTheThemeToTheResourceDictionary()
    {
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Theme.Name = "Ember";
            var resources = new ResourceDictionary();

            ThemeStartup.Apply(settings, resources);

            Check.That(((SolidColorBrush)resources["BrushAccent"]!).Color)
                 .IsEqualTo(EmberTheme.Instance.Accent);
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }

    [Fact]
    public void Apply_FallsBackToMidnight_ForAnUnknownName()
    {
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Theme.Name = "Sepia";   // e.g. hand-edited settings.json

            ThemeStartup.Apply(settings, new ResourceDictionary());

            Check.That(Tokens.Current.Name).IsEqualTo("Midnight");
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~ThemeStartupTests" --nologo
```

Expected: **build failure**, `CS0103: The name 'ThemeStartup' does not exist`.

- [ ] **Step 3: Create `ThemeStartup`**

Create `src/EyeCtrl.App/Services/ThemeStartup.cs`:

```csharp
namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;

/// <summary>
/// Resolves the persisted theme and makes it active, for both the C# call sites
/// (via <see cref="Tokens"/>) and the XAML lookups (via the resource dictionary).
///
/// Extracted from App.OnStartup so it can be exercised without launching the app.
/// Must run before any overlay window or dwell control is constructed.
/// </summary>
public static class ThemeStartup
{
    /// <summary>
    /// Applies <c>settings.Theme.Name</c>, falling back to Midnight if the name is
    /// not recognised.
    /// </summary>
    /// <param name="settings">Loaded application settings.</param>
    /// <param name="appResources">
    /// The application's top-level resource dictionary. Entries written here shadow
    /// the merged OverlayStyles.xaml, so the theme reaches every
    /// {StaticResource Brush*} regardless of when WPF parsed that dictionary.
    /// </param>
    /// <returns>The theme that was activated.</returns>
    public static ITheme Apply(EyeCtrlSettings settings, ResourceDictionary appResources)
    {
        var theme = ThemeRegistry.Resolve(settings.Theme.Name);
        Tokens.Activate(theme);
        ThemeResourceApplier.Apply(appResources, theme);
        return theme;
    }
}
```

- [ ] **Step 4: Add the xUnit collection so the global-state tests do not race**

This is not belt-and-braces. There is **no `xunit.runner.json` in the repo and no `ParallelizeTestCollections=false` anywhere** (checked: `tests/Directory.Build.props` only pins package versions), so xUnit v3's defaults apply — assembly *and* collection parallelization both **on**. `ThemeStartupTests` mutates the process-global `Tokens.Current` and restores it in `finally`; without serialization it races any test reading `Tokens.X` in parallel. Two things make that set empty by the time these tests run:

- **Task 5** rewrote `HaloContrastTests` to read each `ITheme` directly, never through `Tokens.Current`.
- **Task 6 Step 4** pinned `SettingsDefaultsMatchTokensTests` to `MidnightTheme.Instance`, removing its `Tokens.X` reads entirely.

So the only remaining `Tokens.Current` mutator/reader is `ThemeStartupTests` itself, and `[CollectionDefinition(DisableParallelization = true)]` keeps its three cases serial. `DisableParallelization` is the xUnit v3 attribute that opts a single collection out of collection-parallelization while leaving the rest of the assembly parallel — it is the minimal correct fix. Do not instead set a global `ParallelizeTestCollections=false`, which would serialize the whole `App.Tests` assembly for the sake of three tests.

Create `tests/EyeCtrl.App.Tests/ActiveThemeCollection.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using Xunit;

/// <summary>
/// Serialises the tests that mutate Tokens.Current, which is process-global.
/// Everything else reads an ITheme directly and needs no such coordination.
/// </summary>
[CollectionDefinition("ActiveTheme", DisableParallelization = true)]
public class ActiveThemeCollection
{
}
```

- [ ] **Step 5: Wire it into `OnStartup`**

In `src/EyeCtrl.App/App.xaml.cs`, after line 101 and before the `// ── Build DI container ──` comment at line 103, insert:

```csharp

            // ── Theme ──
            // Before the DI container, because everything downstream — overlays, dwell
            // controls, the settings window — reads Tokens the moment it is constructed.
            // Applies at startup only: a theme change recreates windows rather than
            // re-resolving live (spec §10.6), so the picker is labelled "restart to apply".
            ThemeStartup.Apply(settings, Resources);
```

`Resources` is `Application.Resources` — this file is the `Application` subclass. `ThemeStartup` is in `EyeCtrl.App.Services`, which `App.xaml.cs` already imports for `AppBootstrapper`; if the compiler disagrees, add `using EyeCtrl.App.Services;`.

- [ ] **Step 6: Run the full suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/ThemeStartup.cs src/EyeCtrl.App/App.xaml.cs tests/EyeCtrl.App.Tests/ThemeStartupTests.cs tests/EyeCtrl.App.Tests/ActiveThemeCollection.cs
git commit -m "feat(theme): activate the persisted theme during app startup"
```

---

## Task 11: The theme picker

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (General page, ~line 901)
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs:175-177`

The gaze-driven settings UI is `SettingsWindow`; `SettingsViewModel` carries the parallel item list. Both get the picker, and both lose the dead entries.

The label carries `(restart to apply)`. That is deliberate and is the whole mitigation for the startup-only scope: the user must not dwell on a control and be left guessing why nothing changed.

**Do not add a restart button.** The supervisor treats an authenticated clean exit as "do not relaunch" (`SupervisionStateMachine.cs:101-103`), so exiting would not bring the app back; exiting *uncleanly* would relaunch but book a crash strike and count toward the fallback-AT escalation. See "Why 'restart to apply' rather than a restart button" in the scope boundaries for the full reasoning and for the one viable follow-up design.

- [ ] **Step 1: Replace the three dead items in the ViewModel**

In `src/EyeCtrl.ViewModels/SettingsViewModel.cs`, replace lines 175-177:

```csharp
            new ComboSettingViewModel("themeMode", "Theme", th.Mode, new[] { "System", "Light", "Dark" }, v => th.Mode = v),
            new SliderSettingViewModel("overlayOpacity", "Overlay Opacity", th.OverlayOpacity, 0.1, 1.0, 0.1, v => th.OverlayOpacity = v),
            new ToggleSettingViewModel("themeHighContrast", "High Contrast", th.HighContrast, v => th.HighContrast = v),
```

with:

```csharp
            new ComboSettingViewModel("themeName", "Theme (restart to apply)", th.Name,
                ThemeSettings.ValidNames, v => th.Name = v),
```

`ThemeSettings.ValidNames` is `string[]`, which is what `ComboSettingViewModel` takes — the same shape as the literal array it replaces.

- [ ] **Step 2: Add the picker to the gaze-driven settings window**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, inside `BuildGeneralPages` (line 901), add the picker as the first item on the page — immediately after the opening `[` `[` at lines 906-907 and before the `g_boot` toggle:

```csharp
                () => Add(new DwellComboBox(_viewModel, "g_theme", "Theme (restart to apply)",
                    ThemeSettings.ValidNames,
                    () => _settings.Theme.Name,
                    v => { _settings.Theme.Name = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
```

This uses the single-array `DwellComboBox` overload (`src/EyeCtrl.Controls/DwellComboBox.cs:169-181`), where display and code values are the same — theme names are already user-facing.

`ThemeSettings.ValidNames` is `string[]` and the parameter is `string[]`; `EyeCtrl.Core.Settings` is already imported in this file (it constructs `CursorSettings` and `GeneralSettings` in the reset handlers).

**Page fit — known-unknown.** The General page grows from five rows to six (g_theme, g_boot, g_pause, g_track, g_reset, g_resetall). `SettingsWindow` is a fixed-height window with no scrolling, and the no-launch rule means no headless check can confirm six uniform rows still fit; if the user reports the last row clipped, the picker moves onto its own page. Do not pre-emptively split — this is a user-visual verification on their schedule, not a gate here.

- [ ] **Step 3: Add the coverage test**

Append to `tests/EyeCtrl.App.Tests/ThemeSettingsNamesTests.cs`, inside the class:

```csharp
    [Fact]
    public void EveryRegisteredTheme_IsOfferedByThePicker()
    {
        // Both settings surfaces build their option list from ValidNames, so this
        // is what stops a theme existing in code but being unreachable by gaze.
        Check.That(ThemeSettings.ValidNames.Length).IsEqualTo(ThemeRegistry.All.Count);
    }
```

- [ ] **Step 4: Run the full suite**

```bash
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: 0 errors, 0 failures. Note that `--filter "Category!=UI"` is required: roughly 13 `App.Tests` cases need an interactive desktop and fail headless ("Toolbar_Window did not appear within 15s") on clean `main` too.

**Do not launch the app to look at the picker.** The two surfaces are covered by the build and by `ThemeSettingsNamesTests`; visual confirmation is the user's call, on their schedule.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs src/EyeCtrl.ViewModels/SettingsViewModel.cs tests/EyeCtrl.App.Tests/ThemeSettingsNamesTests.cs
git commit -m "feat(theme): offer the theme picker in both settings surfaces"
```

---

## Task 12: Record the outcome in the spec

**Files:**
- Modify: `docs/design/overlay-unification-spec.md:1069-1096` (§10.6)
- Modify: `docs/design/overlay-unification-spec.md:379-397` (§4.4)

§10.6 poses four structural questions. All four now have answers in code, and the spec should say so rather than leaving a reader to infer it.

- [ ] **Step 1: Rewrite §10.6's status**

Replace the §10.6 introduction at `docs/design/overlay-unification-spec.md:1069-1071`:

```markdown
### 10.6 What a theme system requires structurally

Not in scope for §5, but worth recording before Phase 0 fixes the shape:
```

with:

```markdown
### 10.6 What a theme system requires structurally

**Status: implemented.** The four requirements below were recorded before Phase 0
and are now all satisfied in code; each bullet closes with what was built. See
`docs/superpowers/plans/2026-07-30-itheme-theme-system.md`.
```

Then append to each of the four bullets, in order:

- After the `{StaticResource}` bullet: **Resolved — startup-applied.** The theme is chosen once in `App.OnStartup` via `ThemeStartup.Apply`; changing it takes effect on the next launch, and the picker says so. No `{DynamicResource}` conversion was needed, and none of Phase 4–5's files were touched. Recreating the 15 overlay singletons in place remains unbuilt.
- After the `Tokens.cs becomes a provider` bullet: **Resolved.** `ITheme` + `ThemeBase` + three theme classes; `Tokens` keeps its 45-member static surface as forwarding properties, so all 131 C# call sites and 106 XAML lookups were unchanged. `BorderThicknessStandard` is on the interface from the start. The statics-without-`Application` property is preserved — `Tokens.Current` defaults to Midnight with no resource lookup.
- After the `A theme setting means a fifth persisted colour-adjacent key` bullet: **Resolved — and it cost three keys rather than adding one.** `ThemeSettings.Mode`, `.OverlayOpacity` and `.HighContrast` were persisted, gaze-reachable, and read by nothing; so was `CursorSettings.CircleHighContrast`, which was surfaced twice. All four were deleted and replaced by `ThemeSettings.Name`. The four persisted colours (`CircleColor`, `TargetColor`, `HighlightColor`, `TrailColor`) are **explicitly theme-independent**: they are user-chosen absolute values, and their defaults track Midnight.
- After the halo-constraint sentence: **Re-checked, and it is portable after all.** All four shipping presets clear 7:1 against all three halos (worst case 9.13:1, Amber vs Midnight's `#0A0A14`). What was not portable is the *first* preset, which must be the theme's accent — so the list is now derived from the theme as `{Accent, AccentSuccess, AccentWarning, TextPrimary}`, which reproduces Midnight's four values exactly.

- [ ] **Step 2: Note the two gate corrections in §4.4**

Append to §4.4, after the two existing consequence bullets at `docs/design/overlay-unification-spec.md:389-395`:

```markdown
- Implemented as `ITheme.BorderThicknessStandard`, published to XAML by
  `ThemeResourceApplier` in both forms (`BorderThicknessStandard`, a `double`, and
  `StandardBorderThickness`, a `Thickness`), and pinned per theme against the
  validator by `ThemeParityTests`.

Two existing guards had to be re-scoped when the other two themes became real, and
both are worth recording because they were assertions that only held for Midnight:

- `ToggleContrastTests` asserted `ToggleOff.R <= ToggleOff.B` as a stand-in for "off
  is not an error colour". Ember's warm neutral `#342C21` (R 52, B 33) fails that
  while being no more an error colour than Midnight's `#2E2E42`. The rule is now a
  Lab chroma ceiling of 20 — measured 13.7 / 8.6 / 0.0 — against the C* ≥ 55 floor
  gate 9 holds the semantic triad to.
- `ColorPickerPresetTests` banned `#00FF00` from the preset list, as a memory of the
  outgoing Lime accent. Contrast uses `#00FF00` as `AccentSuccess` and `ToggleOn`,
  validated by all ten gates. The rule now applies to the *accent*, which is what it
  always meant.
```

- [ ] **Step 3: Verify the whole gate one last time**

```bash
python tools/palette_validate.py
dotnet build EyeCtrl.slnx --nologo
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: `ALL GATES PASS - 3 theme(s): Midnight, Ember, Contrast` and exit 0; 0 build errors; 0 test failures. The test count will exceed the 1710 baseline — roughly 40 new cases, mostly theories expanded across three themes.

Then, per this repo's conventions, before opening the PR:

```bash
node .gitnexus/run.cjs analyze
```

and run `detect_changes({scope: "compare", base_ref: "main"})` to confirm the affected symbols are the ones this plan names.

- [ ] **Step 4: Commit**

```bash
git add docs/design/overlay-unification-spec.md
git commit -m "docs(theme): record §10.6 as implemented and note the two re-scoped gates"
```

---

## Verification summary

| Gate | Command | Expected |
|---|---|---|
| Palette | `python tools/palette_validate.py` | `ALL GATES PASS - 3 theme(s)`, exit 0 |
| Build | `dotnet build EyeCtrl.slnx --nologo` | 0 errors |
| Tests | `dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo` | 0 failures (baseline 1710 + ~40) |

`Category=UI` needs an interactive desktop and is excluded by design. **No task in this plan requires launching the app.** Test output is German — per-test failures read `Fehler <FQN>`, and long names wrap across lines, so a naive grep for `Failed` reports a green run that is not green.

**CI will run on this PR**, because it branches from `main` and the workflow triggers on `branches: [main]`. That is a change from the overlay-unification work, which was stacked and therefore local-only.

## What this slice does not do

- **No live theme switching.** Changing the theme takes effect on next launch. Recreating the 15 overlay singletons in place is the remaining half of §10.6's first bullet and needs its own plan — plus, unavoidably, a supervised app launch to verify.
- **No "Restart now" button.** The supervisor cannot supply one for free: a clean exit puts it in `IdleWatch` and it deliberately does not relaunch. Making it safe means the supervisor doing the relaunch where one is running, a child-readiness handshake where none is, and the app refusing to exit until a successor is proven either way. Planned separately in `docs/superpowers/plans/2026-07-30-restart-now.md`.
- **No fourth theme.** §10.1 argues two alternatives are the useful limit; each additional theme multiplies the validation surface, and §9.4's glyph rule and §4.1.3's halo have to hold in every one.
- **No radius or typography work.** Still deferred (§8).
- **No settings migration.** Legacy keys are ignored on load; the user resets manually if they want a clean file.
