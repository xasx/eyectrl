# Cursor ring style — design

**Date:** 2026-09-03
**Status:** approved design, not yet planned
**Scope:** `CursorCircleWindow` only

## 1. Problem

The gaze cursor ring must stay visible on both light and dark desktop content. It
is drawn on a click-through transparent overlay with nothing beneath it, so this
is the §4.1.3 case: no single colour holds contrast against arbitrary backdrops.

The ring already carries the §4.1.3 halo — `CursorCircleWindow.xaml` draws a
`StrokeHalo` ellipse at `StrokeThickness + 2`, then the accent ring over it, so
the stack is 1px dark / 2px accent / 1px dark. The internal edge is therefore
never absent, and `HaloContrastTests` proves it clears the 7.0 floor on every
backdrop, in every theme, for every colour-picker preset.

**What the guarantee does not cover is weight.** The edge survives; the *amount*
of ring you can see does not. Of the 4px stack, only ever 2px clears 3:1 against
a given backdrop.

The table below is computed at the **shipped `CircleOpacity` of 0.7**, which is
load-bearing: the accent is composited against the backdrop before its ratio is
taken. Over mid grey that is 2.35:1 and the accent does *not* carry; at full
opacity the same accent would reach 3.16:1 and would. Recomputing this table at
opacity 1.0 gives different answers and is the likeliest way to mis-read it.

| Backdrop | Carried weight | Strongest band |
| --- | --- | --- |
| White document `#FFFFFF` | 2 of 4px | 19.69:1 |
| Light app chrome `#F3F4F6` | 2 of 4px | 17.89:1 |
| Body text on white | 2 of 4px | 19.69:1 |
| Mid grey `#808080` | 2 of 4px | 4.99:1 |
| App surface `#1A1A2E` | 2 of 4px | 13.67:1 |
| Black `#000000` | 2 of 4px | 16.83:1 |

Over white the accent composites to 1.17:1 and only the two 1px halo rims are
left; over the app surface the halo falls to 1.15:1 and the accent alone carries.
At a 250px diameter that is a hairline, and which hairline it is changes as the
user's gaze crosses a window boundary.

Ratios throughout this document are WCAG 2.x contrast arithmetic computed from
the palette values in `src/EyeCtrl.Controls/Theme/MidnightTheme.cs`. They are not
screen measurements. The 3:1 threshold is WCAG 2.1 SC 1.4.11 (non-text contrast),
used here as the floor for a graphic to be perceivable at all; the 7.0 edge floor
is the existing `HaloContrastTests.EdgeFloor`.

## 2. Decisions taken

Three questions were settled before this design:

1. **All three treatments ship as a permanent user setting**, not as a temporary
   evaluation aid and not as an escape hatch behind one blessed default. Each
   needs full support: settings copy, sane behaviour of the neighbouring knobs,
   and test coverage.
2. **The style acts as a preset.** Choosing one writes that style's tuned
   thickness and opacity into the existing settings and leaves them editable. The
   one control a style cannot use is swapped out rather than left dead.
3. **Cursor circle only.** `ActivationButtonWindow`,
   `ManualSelectionGazeIndicatorWindow`, `ScrollOverlayWindow` and
   `DragTrailOverlay` keep today's treatment.

Consequence of (3), stated so it is a known cost rather than a surprise: under
`AchromaticReticle` the resting ring is achromatic while the activation arc
remains accent-coloured. The overlays stop matching each other.

## 3. The three styles

All three fit the two `Ellipse` elements `CursorCircleWindow.xaml` already
declares — `CircleHalo` (drawn first, wider) and `Circle` (drawn over it). No new
visual elements.

| Style | Outer band | Core band |
| --- | --- | --- |
| `WeightedSandwich` | `StrokeHalo`, `thickness + 4`, opacity 1, solid | `CircleColor`, `thickness`, `CircleOpacity`, solid |
| `BiTonalSegments` | `StrokeHalo`, `thickness`, opacity 1, dash period `P`, offset 0 | `CircleColor`, `thickness`, `CircleOpacity`, dash period `P`, offset `P` |
| `AchromaticReticle` | `TextPrimary`, `thickness + 4`, opacity 1, solid | `StrokeHalo`, `thickness`, `CircleOpacity`, solid |

**`WeightedSandwich`** keeps today's structure and moves only the proportions:
the dark rim grows from 1px to 2px per side, so the band carrying on a light
backdrop is 4px rather than 2px, and the accent runs at full opacity so it stops
compositing toward the backdrop on the dark side. Carried weight across the six
backdrops of §1 becomes 4 / 4 / 4 / 7 / 3 / 3 of 7px — never below 3px, against
a flat 2px today.

**`BiTonalSegments`** puts both lightness poles on the ring itself instead of
stacking them. The two ellipses share a radius and a thickness and carry
complementary dash phases, so neither sits under the other and half the
circumference is always at full weight. Because both strokes share a thickness,
the `StrokeDashArray`-is-in-multiples-of-`StrokeThickness` trap that bit the drag
trail's settle ring (§4.1.3) does not arise — the two patterns are identical
apart from the offset.

**`AchromaticReticle`** drops the hue entirely and lets lightness alone carry the
ring: near-black on every light backdrop, the light rims on every dark one.

Its band order is the **inverse** of the precision-marker form the overlay
spec uses elsewhere. `overlay-unification-spec.md:398-404` describes the
action-menu crosshair and the magnifier rim as "a `TextPrimary` mark over a
`StrokeHalo` under-stroke: the conventional precision-marker form (light mark,
dark outline)" — light core, dark outer. This style is dark core, light outer.
The two are contrast-equivalent (the internal edge is the same pair either way),
and the choice here is which backdrop gets the thicker carrying band: as
specified, 3px of dark core over white and 4px of light rim over the app
surface; flipped to match the precedent, 4px over white and 3px over the app
surface. Either is defensible. Do not "correct" this to the precedent's order
without deciding that trade deliberately — the citation is the thing that was
wrong in an earlier draft, not the arrangement.

Its internal edge is
`TextPrimary` against `StrokeHalo`, which clears the 7.0 floor in all three
themes — Midnight 14.65:1, Ember 17.07:1, Contrast 21.00:1.

## 4. The setting

New enum, `src/EyeCtrl.Core/Models/CursorRingStyle.cs`:

```csharp
public enum CursorRingStyle
{
    WeightedSandwich,
    BiTonalSegments,
    AchromaticReticle
}
```

New property on `CursorSettings`, following the `PositionIndicatorStyle`
precedent exactly — `[ObservableProperty]`, no `OnXChanging` guard (an enum
cannot be out of range from the picker, and the existing enum property has none),
and no entry in `Validate()`.

```csharp
[ObservableProperty] private CursorRingStyle _ringStyle = CursorRingStyle.WeightedSandwich;
```

`EnumerateSubSettings()` is untouched: it enumerates sub-settings *classes*, not
properties, and `CursorSettings` is already in it.

## 5. Core descriptor

The style-to-geometry decision lives in Core as a pure function, so the contrast
and geometry reasoning is unit-testable without WPF.

New file, `src/EyeCtrl.Core/Models/CursorRingStack.cs`:

```csharp
public readonly record struct RingBand(
    string Hex, double Thickness, double Opacity, double DashPeriod, double DashOffset);

public readonly record struct CursorRingStack(RingBand Outer, RingBand Core);

public static class CursorRingStacks
{
    public static CursorRingStack For(
        CursorRingStyle style, double size, double thickness, double opacity,
        string markHex, string haloHex, string lightHex);
}
```

`DashPeriod` is `0` for a solid band.

**Core stays theme-free.** `ITheme` lives in `EyeCtrl.Controls`, which references
WPF, so the halo and light colours are passed *in* as hex strings by the caller
in `EyeCtrl.App`; Core never reaches for `Tokens`.

**`size` is a parameter because the dash geometry depends on it.** For the
`BiTonalSegments` pattern to close rather than leave a seam at the wrap point:

```
P = π · size / 32        // P is the DASH LENGTH, and the gap length equals it
```

**`P` is a dash length, not a full on/off cycle.** One full cycle is `2P`, so the
ring carries 16 cycles — 16 dark segments and 16 accent segments — at any legal
`CircleSize`. Spelling this out because the failure it prevents is silent:

- Correct: outer band `StrokeDashArray = {P/t, P/t}` with `StrokeDashOffset = 0`;
  core band the same array with `StrokeDashOffset = P/t`. The half-cycle offset
  puts the accent segments in the halo's gaps — the two never overlap, which is
  the entire point of the style.
- Wrong, and it compiles: reading `P` as a full cycle gives `{P/2t, P/2t}` with
  `StrokeDashOffset = P/t`. That offset is then a *whole* cycle, both ellipses
  dash **in phase**, the accent core lands exactly on top of the halo, and the
  bi-tonal property vanishes. The §9 closure identity still holds and every
  contrast test still passes, because no colour changed. Nothing catches it but
  looking at the screen.

The `/t` divisions are WPF's units, not a fudge: `StrokeDashArray` and
`StrokeDashOffset` are both expressed in multiples of `StrokeThickness`. The
repo's own precedent for this is `DragTrailOverlay.xaml.cs:166-170`.

A dash length rounded independently of the diameter leaves a visible
discontinuity at the wrap point; this was caught in the design mockup and must
not be reintroduced here.

## 6. Renderer

`CursorCircleWindow.UpdateAppearance` stops deciding anything. It keeps its
current responsibilities — parse `CircleColor` with the existing try/catch
fallback to `Tokens.BrushAccent`, size the window and both ellipses — and gains
one step: ask Core for the stack, then apply `Outer` to `CircleHalo` and `Core`
to `Circle`.

Two constraints carried forward:

- **Brushes stay frozen and are built once per settings change**, never per
  frame. The comment at `tests/EyeCtrl.App.Tests/HaloZOrderTests.cs:141`
  explains why `ActivationButton_NeverAllocatesABrush` is deliberately *not*
  widened to this file; that reasoning is unchanged.
- **Converting the px dash period into WPF units happens here**, not in Core:
  `StrokeDashArray` is expressed in multiples of `StrokeThickness`, so the value
  written is `P / thickness`, and `StrokeDashOffset` likewise.

Declaration order in the XAML is unchanged, so `HaloZOrderTests.HaloedPairs`
continues to pass without edit.

## 7. Settings UI

All three changes are in
`src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs`.

**The combo.** `ComboBoxSpec` takes a `Display[]` / `Code[]` pair, so the row
shows "Weighted sandwich", "Bi-tonal segments", "Achromatic reticle" while
persisting enum names via `Enum.GetNames<CursorRingStyle>()` as the codes.

**The preset write, on the UI side.** The setter assigns `RingStyle`, then writes
that style's tuned values into the neighbouring settings, then
`ApplyAndSave()` + `RebuildContent()` so the sliders and the swapped row refresh:

| Style | `CircleThickness` | `CircleOpacity` |
| --- | --- | --- |
| `WeightedSandwich` | 3 | 1.0 |
| `BiTonalSegments` | 5 | 1.0 |
| `AchromaticReticle` | 3 | 1.0 |

All three values are inside the `SliderSpec` ranges already on the page
(thickness 1–6, opacity 0.1–1).

This deliberately does **not** live in a `partial void OnRingStyleChanged` on
`CursorSettings`. A model-side hook fires during JSON deserialization and would
silently overwrite a user's tuned thickness and opacity depending on property
order in the settings file. Keeping it in the combo setter means the preset only
ever fires on an explicit user gesture.

Because all three presets want opacity 1.0, the opacity slider loses most of its
purpose. It stays live — a user may legitimately want a subtler ring — and its
description says plainly that lowering it dims the mark side of the edge and
weakens the guarantee it exists to provide.

**The Circle Color row swap.** Under `AchromaticReticle` the mark colour is
unused, so `Build()` emits either the existing `ColorPickerSpec` or, for that one
style, a disabled-looking replacement row. `RebuildContent()` from the combo
setter makes the swap land immediately.

That replacement row **does not exist yet and is new work**. The nearest existing
type, `LabelSpec(Func<string> Text, bool Wrap)` at
`src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs:57`, derives with `("", "")`
for Id and Label — it is a full-width status line, not a labelled row, so it
cannot render "Circle Color" in the label column beside its explanation. This
design therefore adds one record next to it:

```csharp
public sealed record DisabledRowSpec(string Id, string Label, string Reason)
    : SettingsRowSpec(Id, Label);
```

with three consequences the plan must carry:

1. `SettingsWindow.CreateControl` needs a case for it — its default arm throws
   `"Unknown settings row spec"` (`SettingsWindow.xaml.cs:1086`).
2. The pagination measurer constructs every row's control to measure it, and the
   header comment on `SettingsRowSpec.cs:8-10` warns that every spec's delegates
   run twice per rebuild. The new row must be cheap and side-effect-free.
3. It is a settings row, so it falls under the description gate in §11.

**The reset button.** `ButtonSpec("c_reset", …)` hand-copies every field from a
fresh `CursorSettings`. It needs `c.RingStyle = d.RingStyle;` added, or reset
silently skips the new setting. This is the single easiest thing to miss.

## 8. Default values

`CursorSettings` defaults change:

- `CircleThickness` 2.0 → 3.0
- `CircleOpacity` 0.7 → 1.0

Without this a fresh install gets `WeightedSandwich`'s *structure* at the weights
that fail today, which is the defect this work exists to remove.

The XML doc comment on `CircleOpacity` currently argues for 0.7 on the grounds
that it keeps the halo edge above the 7.0 floor while 0.6 does not. That
reasoning is superseded: at full opacity the edge is `Accent` against
`StrokeHalo` — 15.78:1 in Midnight — and the argument for dimming the mark no
longer applies. The comment must be rewritten in the same change, not left
contradicting the value beside it.

Existing installs keep their persisted values and see no change until they touch
the setting. The separately-flagged persisted `cursor.circleColor` of `#0088FF`
(overlay-unification spec §7) is not addressed here and remains a one-time manual
fix through the colour picker.

## 9. Testing

**`EyeCtrl.Core.Tests` — geometry, pure, no WPF.**

- For each style, `CursorRingStacks.For` returns the band widths in the table of
  §3, given a thickness and the mark/halo/light hexes.
- `BiTonalSegments`' dash geometry closes — asserted against the `DashPeriod`
  the function *returns*, not against the formula restated in the test. Written
  as pure arithmetic, `π · size / (2 · (π · size / 32))` is identically 16 for
  every input and the test has no teeth; it must guard rounding inside the
  implementation. Sweep `CircleSize` across 10–500 including both bounds.
- `BiTonalSegments` returns the two bands a half-cycle apart: equal
  `DashPeriod`, `Outer.DashOffset == 0`, `Core.DashOffset == DashPeriod`. This is
  the assertion that catches the in-phase failure described in §5, which no
  contrast test can see.
- Solid styles return `DashPeriod == 0` for both bands.
- The outer band is never narrower than the core band, for any style or
  thickness in range — the property that makes the stack a stack.

**`EyeCtrl.App.Tests` — contrast, beside `HaloContrastTests`.**

The existing `StrongestVisibleEdge(mark, halo, backdrop)` harness is reused and
extended: for every `CursorRingStyle` × every theme × the nine §9.5 backdrops ×
every `DwellColorPicker.PresetsFor(theme)` preset, the style's own colour pair
clears `EdgeFloor`. For `WeightedSandwich` and `BiTonalSegments` that pair is the
preset colour against `StrokeHalo` — the claim the current tests already make.
For `AchromaticReticle` it is `TextPrimary` against `StrokeHalo`, which the
presets do not affect, so that case is asserted once per theme.

`HaloContrastTests` contains no UI-category tests, so all of this runs in CI's
`Category!=UI` headless subset.

**Snapshots — checked, no action.**
`tests/EyeCtrl.App.Tests/Services/SettingsSnapshotsTests.cs` reflects over
`typeof(KeyboardSettings)` only (line 43), so a new `CursorSettings` property
cannot affect it.

## 10. Judgment calls

Two decisions inside this design could reasonably go the other way. They are
recorded so a reviewer can overturn them cheaply rather than rediscover them.

**Ellipse names kept as-is.** Under `AchromaticReticle` the element named
`CircleHalo` carries the *light* colour, so the names become misleading.
Renaming to `RingOutline` / `RingCore` would mean editing the deliberately
curated `HaloedPairs` list in `HaloZOrderTests`, whose comment explains why that
list is shaped the way it is. Decision: keep the names, add a comment in the XAML
saying the two elements are outer band and core band and that which one is dark
depends on the style.

The cost of that decision, stated rather than left to be discovered:
`EveryHaloElement_UsesTheStrokeHaloBrush` (`HaloZOrderTests.cs:77-98`) asserts
that `CircleHalo`'s **XAML tag** contains `BrushStrokeHalo`. Under
`AchromaticReticle` the runtime stroke is `TextPrimary`, so that assertion
becomes vacuous for this file — it keeps passing while checking a value the
renderer overwrites, in a test whose own header warns its failure mode is
invisible to everything else. Two implications: leave the `Stroke=` attribute in
the XAML (removing the now-redundant literal fails the test), and treat the Core
band-colour tests of §9 as the real guard for what `CircleHalo` actually paints.

**Row swap rather than a disabled-control concept.** Adding `Enabled` to
`SettingsRowSpec` would serve one row on one page and would then have to be
honoured by every row renderer. The swap is not free either — it adds one record
and one `CreateControl` case (§7) — but it confines the change to a new type
instead of altering a base every existing row already flows through.

## 11. Risks

**Row descriptions are gated.**
`tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs`
covers `CursorPageSource` and fails any `ComboBoxSpec` / `ColorPickerSpec` /
`SliderSpec` / `ToggleSpec` without a `Description`. The new Ring Style combo
must ship with one, and the §7 replacement row is a settings row too. This is
the gate that actually binds — cheap to satisfy, but only if it is known about.

**Page fit — the two obvious tests do not cover this tab.**
`SettingsContentFitTests` measures control widths, and
`HandChunkedPageFitTests` filters to `!source.PrefersAutoPagination` (line 285)
while `CursorPageSource.PrefersAutoPagination` is `true` — so neither guards
Cursor repagination. The Cursor tab's second page already carries seven rows
plus the reset button and a new combo may repaginate it; the tests that would
notice are the Cursor-specific ones below, not the two named in earlier drafts.

**`CursorPageSourceTests` has an unfiltered `.Single()`.**
`Build_ColorPickerUsesSwatchRadius` does `.OfType<ColorPickerSpec>().Single()`
with no predicate. It survives only because the default style keeps the picker;
any test that builds the page under `AchromaticReticle` must not reuse that
helper. `Build_HasAdvancedRows_ResetStillLast` pins seven sliders and filters its
`Single` by `Id == "c_pos_style"`, so a second combo is safe there.

**Test output is German.** Per-test failures read `Fehler <FQN>` and long names
wrap across lines; a naive grep of `dotnet test` output will report green on a
red run.

## 12. Out of scope

- The other four §4.1.3 transparent overlays. `AchromaticReticle` will visibly
  disagree with them; widening the setting is a separate piece of work.
- Migrating the persisted `#0088FF` `cursor.circleColor`.
- Any adaptive or automatic style switching based on what is behind the cursor.
- Changing the 3:1 or 7.0 thresholds, or the nine-backdrop list.
