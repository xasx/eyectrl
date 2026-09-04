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
2. **The style acts as a preset — superseded, see §7.** As first approved,
   choosing a style wrote that style's tuned thickness and opacity into the
   existing settings and left them editable. Two same-day revisions (§7) walked
   that back to nothing: the setter now changes only `RingStyle`. The one
   control a style cannot use is still swapped out rather than left dead.
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
`StrokeDashOffset` are both expressed in multiples of `StrokeThickness`.
There is **no precedent in the repo** for converting an absolute arc length into
those units. Earlier drafts cited `DragTrailOverlay.xaml.cs:164-170`; two
reviewers independently checked it and it does not do this — its dash values are
already in thickness multiples and it only scales one stroke's pattern against
the other's by a thickness ratio. The conversion here is correct WPF semantics
regardless, but it stands on its own rather than on that file.

A dash length rounded independently of the diameter leaves a visible
discontinuity at the wrap point; this was caught in the design mockup and must
not be reintroduced here.

**Known limitation at the bottom of the size range.** Because the dash length
scales with the diameter while the stroke width does not, `BiTonalSegments`
degenerates on a very small ring: at `CircleSize` 10 with the style's preset
thickness of 5, the dash is `π · 10 / 32 ≈ 0.98px` against a 5px stroke, so the
segments are far shorter than they are wide and the ring reads as solid rather
than alternating. Nothing is broken — the contrast guarantee is unaffected, since
both colours are still present — but the style stops being visibly bi-tonal
somewhere above the 10px floor. Not addressed: the default is 250px, `CircleSize`
is a slider the user drives, and no test can see the difference. Worth knowing
before anyone reports it as a bug.

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

**The preset write, on the UI side — writes nothing.** Revised a second time,
still 2026-09-04: the setter assigns `RingStyle`, then calls `ApplyAndSave()` +
`RebuildContent()` so the swapped colour row refreshes. It touches no other
property, for any style. There is no per-style table, and no forced value at
all.

**History: it never touched `CircleThickness`, and no longer touches
`CircleOpacity` either.** The first revision (still 2026-09-04, earlier the
same day) removed the thickness write after the plan owner ran all three
styles on the real desktop and found the ring too heavy. The original design
had each style write a tuned thickness as well (3, or 5 for
`BiTonalSegments`), which made the Circle Thickness slider a lie: any thinner
value the user chose was destroyed by their next style change.

That first revision kept forcing `CircleOpacity` to 1.0, reasoning that
opacity is different from thickness: it is applied to the core band alone and
composites it toward the backdrop, so — unlike thickness, which only changes
how much weight carries while the internal edge (halo always at
`thickness + 4`) survives at any width — a low opacity can genuinely weaken
that edge. The preset forced the one value that looked like a safety knob and
left the one that was taste.

**Superseded the same day.** Live testing of the opacity-only preset showed
the user wants full control of the ring's appearance, not partial control —
the "safety knob" framing assumed a user who would not deliberately choose a
weaker ring, and that assumption did not hold. The setter now leaves
`CircleOpacity` alone too. §8 records what that costs: a persisted low opacity
is no longer corrected by a style change, and nothing else in the code stops a
low opacity from weakening the ring on a dark backdrop — the opacity slider's
own `Description` is the only guard left on that axis.

Consequence worth knowing: `BiTonalSegments` no longer gets its own wider
thickness, so at a thin setting its segments are long and hairline rather than
chunky, and a low `CircleOpacity` is never corrected by a style change either.
Both are the user's call to make with the sliders now.

This deliberately does **not** live in a `partial void OnRingStyleChanged` on
`CursorSettings`, even though the setter no longer forces any appearance
value. A model-side hook fires during JSON deserialization, which is reason
enough on its own to keep the setter's side effects (`ApplyAndSave`,
`RebuildContent`) firing only on an explicit user gesture, never during load.

**Why the combo still sits beside the sliders it no longer rewrites.** The
Ring Style combo originally sat on page 1 of the Cursor tab while
`CircleThickness` and `CircleOpacity` sat on page 2. That split mattered when
the combo was a preset: a user who had tuned thickness to 6 could lose it the
instant they dwelled the style combo, with no on-screen feedback, and
re-selecting the already-selected style — the returning user's most likely
gesture — was exactly the case that could destroy it. Neither preset survives
to this revision, so that destructive case no longer exists, but the row move
is kept anyway: `c_ring_style`, `c_opacity` and `c_thick` all shape the same
ring, and grouping controls that shape one thing is worth doing whether or not
any of them rewrites another.

Verified rather than assumed: `DwellComboBox` renders one dwell button per option
and wires each to `_setCode(capturedCode)` unconditionally, with no guard
comparing against the current selection, so dwelling the already-selected style
still re-runs the setter — harmlessly now, since there is nothing left for it
to write.

**Decision (plan owner, 2026-09-04): move Ring Style beside the sliders it
touches**, rather than adding a confirmation modal or accepting the split. The
combo is now the first row of the second `PageSpec`, immediately above
`c_opacity` and `c_thick`.

One thing that discovery changed about how to *pin* that. `CursorPageSource`
sets `PrefersAutoPagination`, and for such a source
`SettingsWindow.MaybeRepaginateByHeight` (`SettingsWindow.xaml.cs:767-781`)
flattens every literal `PageSpec` with `pages.SelectMany(p => p.Rows)` and
re-chunks the flat list by measured height through
`SettingsAutoPagination.ChunkByHeight` (`SettingsAutoPagination.cs:27-51`, a
greedy sequential fill). **The page boundaries this source declares are therefore
advisory: only the flat row order survives to the screen.** The guard test
accordingly asserts the three rows are contiguous in flat order, not that they
share a declared page — the latter would have passed even if the rendered
pagination split them, and failed on a harmless regrouping.

Residual, and not closable by any test here: a height-chunk boundary can still
fall between two adjacent rows if the budget runs out exactly there. Adjacency
makes that unlikely; only looking at the screen can confirm it.

The opacity slider now carries the ring's only opacity guarantee — no preset
backs it up. It stays live — a user may legitimately want a subtler ring — and
its description says plainly that lowering it dims the mark side of the edge
and weakens the guarantee it exists to provide; that text is unchanged and is
now the sole thing guarding this axis.

**The Circle Color row swap.** Under `AchromaticReticle` the mark colour is
unused, so `Build()` emits either the existing `ColorPickerSpec` or, for that one
style, a disabled-looking replacement row. `RebuildContent()` from the combo
setter makes the swap land immediately.

That replacement is the **existing** `LabelSpec(Func<string> Text, bool Wrap)`
(`src/EyeCtrl.App/Views/Settings/SettingsRowSpec.cs:57`), carrying the sentence
"Circle Color is not used by the Achromatic reticle ring style."

`LabelSpec` derives with `("", "")` for Id and Label, so it renders as a
full-width status line rather than a two-column row with "Circle Color" in the
name column. That is the deliberate trade, and it buys the whole framework cost:
`LabelSpec` already returns a `DwellLabel`, which `SettingsWindow.RenderSpec`
already accepts (`SettingsWindow.xaml.cs:1298`), so there is **no new record, no
`CreateControl` case, and no new class**.

A labelled two-column row was considered and rejected on cost. `LabelledRow`
requires an `IDwellComposite` inner (`SettingsWindow.xaml.cs:1100-1103`), so a
`DisabledRowSpec` would need a new no-dwell-target composite for the control
column *in addition to* the record and the `CreateControl` case, and all of it
must stay side-effect-free under the double-run pagination measurer
(`SettingsRowSpec.cs:8-10`). Not worth a dead control column.

`LabelSpec` is also outside the description gate of §11, which filters to
`SliderSpec`, `ToggleSpec`, `ComboBoxSpec` and `ColorPickerSpec`.

**The reset button.** `ButtonSpec("c_reset", …)` hand-copies every field from a
fresh `CursorSettings`. It needs `c.RingStyle = d.RingStyle;` added, or reset
silently skips the new setting. This is the single easiest thing to miss.

## 8. Default values

`CursorSettings` defaults change:

- `CircleThickness` 2.0 → **1.0** (revised down from 3.0 on 2026-09-04)
- `CircleOpacity` 0.7 → 1.0

The opacity change is what removes the defect: at 0.7 the accent composited
toward the backdrop, reaching 1.17:1 over a white document.

The thickness default went to 3.0 first, on the reasoning that
`WeightedSandwich`'s *structure* at the old weights would still under-deliver.
Running it proved that wrong in the other direction — the plan owner tested all
three styles and found the ring too heavy — so the default is now 1.0, the bottom
of the legal range. At 1.0 the stack is a 1px core inside a 5px halo, so 2px of
near-black rim carries each side. Less weight than 3.0, and the internal edge is
unchanged: it is a colour relationship, not a width one.

The XML doc comment on `CircleOpacity` currently argues for 0.7 on the grounds
that it keeps the halo edge above the 7.0 floor while 0.6 does not. That
reasoning is superseded: at full opacity the edge is `Accent` against
`StrokeHalo` — 15.78:1 in Midnight — and the argument for dimming the mark no
longer applies. The comment must be rewritten in the same change, not left
contradicting the value beside it.

**Existing installs get nothing from this change, and that is nearly everyone.**
Verified 2026-09-03 rather than assumed: `SettingsService.Save` serializes the
*whole* `EyeCtrlSettings` snapshot (`SettingsService.cs:243`), so every property
is written explicitly, and `Load` deserializes onto a fresh instance
(`SettingsService.cs:128`) where any persisted value overrides the field
initializer. Anyone who has ever run the app therefore has explicit
`circleThickness` and `circleOpacity` keys, and the new defaults are invisible to
them. The live settings file on the development machine reads
`circleThickness: 2` and `circleOpacity: 0.6` — and 0.6 is the value the old doc
comment recorded at 6.04:1, below the 7.0 floor, so the current install is
running a configuration this codebase already documented as failing its own
contrast gate.

§7's preset write no longer delivers anything to existing installs. It
delivered opacity-only for one revision on 2026-09-04, then that write was
removed too, the same day: choosing any Ring Style — the already-selected one
included — now changes only `RingStyle`. An install with a persisted
`circleOpacity: 0.6` (the default composited to 1.17:1 over a white document,
cited above) keeps that value forever unless the user moves the slider
themselves; a style change no longer notices or corrects it.

Both `CircleThickness` and `CircleOpacity` are now never corrected
automatically, by design: they are the user's knobs, and a preset that
overwrote either was the reason for the two revisions that got here. Existing
installs keep whatever width and opacity they had, and the new 1.0/1.0
defaults apply to fresh installs only. Anyone wanting either new default moves
the corresponding slider, which is one dwell in the same place.

That resolves the load-time-nudge question left open here, in the opposite
direction from the answer recorded just above: no nudge, and no forced value
on style selection either. The only thing standing between a low
`CircleOpacity` and a weakened ring on a dark backdrop is the opacity slider's
own `Description` — the halo band stays opaque regardless, so the
light-backdrop case is unaffected either way.

The separately-flagged persisted `cursor.circleColor` of `#0088FF`
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

**`EyeCtrl.App.Tests` — the renderer, added after a merge-readiness review.**

`CursorRingRenderTests` constructs a `CursorCircleWindow` (never shown, `[StaFact]`,
in the `WpfApplication` collection so `WpfApplicationGate` can supply the merged
token dictionary the XAML resolves against) and calls `UpdateAppearance`. It pins
the three things a mutation sweep proved were guarded by nothing: that the outer
band paints `CircleHalo` and the core band paints `Circle` (swapping the two
`Apply` calls hides the core entirely and voided the whole guarantee with all
three suites green); that the dash values are the band's arc length divided by
its thickness, derived from `CursorRingStacks.For`'s own output rather than
restated; and that switching from `BiTonalSegments` to a solid style clears the
dash array rather than leaving it painted.

This closes the gap §5's dash warning had displaced one layer down: the arc-space
geometry was well tested, but nothing checked that the renderer transcribed it
into WPF correctly.

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
honoured by every row renderer. Swapping in an existing `LabelSpec` costs
nothing in the framework (§7) at the price of the label column.

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
