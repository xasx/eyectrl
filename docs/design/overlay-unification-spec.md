# EyeCtrl Overlay Design Unification Spec

**Status:** Draft  
**Date:** 2026-07-25

---

## 1. Problem Statement

EyeCtrl has 29 overlay windows and 7 dwell controls — 24 with XAML under
`Overlays/`, the code-only `NoteWindow`, and `SettingsWindow` under `Views/`
(which also derives from `OverlayWindow`). Re-counted against merged `main` at
b8295b6, which includes PRs #70 and #71, and enforced by
`OverlayInventoryDriftTests`. Each was built at a different time, introducing accumulated style drift:

- **10 different background colors** used across overlays
- **7 different corner-radius values** (2, 4, 6, 8, 12, 14, 22)
- **8 different accent-color families** for dwell feedback
- **Zero shared resources** — `App.xaml` is empty
- **Repeated magic color literals** in XAML and C# code-behind

The result is a UI that feels like a patchwork of different apps rather than one cohesive product.

---

## 2. Design Constraints (Hard)

| Constraint | Reason |
|---|---|
| **Dwell-friendly above all** | Users interact by staring. Feedback must be instant, high-contrast, and unambiguous. Targets must not shrink. |
| **Dark-only backgrounds** *(amended by §10.8)* | Eye trackers work poorly on bright surfaces; users stare at this UI for hours. Unchanged for the shipping default and the dark alternatives; **Daylight (§10.8)** deliberately reopens the light axis for bright rooms, with the ruling recorded there |
| **No layout changes** | Target positions, sizes, and spacing are tuned for gaze accuracy — don't move them |
| **Must not break UI tests** | FlaUI tests reference `AutomationId` strings — these must stay unchanged |
| **No new dependencies** | Resource dictionary must be pure WPF/XAML, no new NuGet packages |

---

## 3. Current State Audit

### 3.1 Overlay Backgrounds

| Overlay | Class | Current Background | Format | D17 collision |
|---|---|---|---|---|
| Keyboard | `KeyboardWindow` | `#F0181818` | XAML | collision-zone |
| Toolbar | `ToolbarWindow` | `#E0202020` | XAML | exempt:anchor source — §3.3 places the panel beyond the strip end by construction |
| QuickActionsGrid | `QuickActionsGridWindow` | `#1A1A2E` + `Opacity="0.95"` | XAML | exempt:modal |
| Settings | `SettingsWindow` | `#E01E1E1E` | XAML | exempt:modal |
| Splash | `SplashWindow` | `#F0181818` | XAML | exempt:startup, non-interactive |
| TrackStatus | `TrackStatusWindow` | `#80000000` | XAML | exempt:non-interactive feedback |
| TaskSwitcher | `TaskSwitcherWindow` | `#E6000000` | XAML | exempt:modal |
| ModesMenu | `ModesMenuWindow` | transparent window; panel border `BrushSurfaceDark` (palette token) | XAML | collision-zone |
| RepeatCursor | `RepeatCursorWindow` | transparent | code-only | exempt:non-interactive indicator |
| PauseIndicator | `PauseIndicatorWindow` | `#C0204060` | XAML | exempt:non-interactive feedback |
| EyeCareBanner | `EyeCareBannerWindow` | `#F0181830` | XAML | exempt:modal |
| EyeCareExercise | `EyeCareExerciseWindow` | `#E8000000` | XAML | exempt:modal |
| Calibration | `CalibrationWindow` | `#DD000000` | XAML | exempt:modal |
| ActivationButton | `ActivationButtonWindow` | `#80000000` | code-behind | exempt:offers are suspended while the panel is up (D16) |
| DwellFeedback | `DwellFeedbackOverlay` | `#1A3399FF` | XAML | exempt:feedback ring — the dwell targets are the SDK client's own rects, inventoried via `ExternalDwellService.LiveTargetRects` |
| ScrollOverlay | `ScrollOverlayWindow` | `Transparent` | XAML | exempt:non-interactive — scroll's exits are edge-bar buttons |
| GlassBackdrop | `GlassBackdropWindow` | `Transparent` | XAML | exempt:non-interactive backdrop |
| ManualSelection | `ManualSelectionOverlayWindow` | `Transparent` | XAML | exempt:modal magnifier — its exits are edge-bar buttons |
| CursorCircle | `CursorCircleWindow` | `Transparent` | XAML | exempt:the gaze cursor itself |
| ActionMenu | `ActionMenuWindow` | `Transparent` | XAML | collision-zone |
| EdgeButtonBar | `EdgeButtonBarWindow` | `Transparent` | XAML | collision-zone |
| TargetHighlight | `TargetHighlightWindow` | `Transparent` | XAML | exempt:non-interactive highlight |
| ManualGazeIndicator | `ManualSelectionGazeIndicatorWindow` | `Transparent` | XAML | exempt:non-interactive indicator |
| DragTrail | `DragTrailOverlay` | `Transparent` | XAML | exempt:non-interactive trail |
| **AdaptationCue** | `CueOverlayWindow` | `Transparent` + `Ellipse Fill="#FFB300"` | XAML | exempt:non-interactive cue |
| **DriftNotice** | `DriftNoticeWindow` | `Transparent` + `Border #E0202020` | XAML | exempt:non-interactive banner |
| **CaregiverAlert** | `CaregiverAlertWindow` | `Transparent` + `Border #E0202020` | XAML | exempt:non-interactive banner |
| **AdaptationPreview** | `AdaptationPreviewWindow` | `Transparent` + token-bordered card (all values from `Tokens`) | XAML | exempt:it is the panel itself |
| **Note** | `NoteWindow` | `#1A1A2E` | code-only, no XAML | collision-zone |

The **Class** column is the match key for the drift-guard test
(`tests/EyeCtrl.App.Tests/OverlayInventoryDriftTests.cs`): every concrete
`OverlayWindow` subclass in `EyeCtrl.App` must appear here, and every name here
must resolve to a real type. Adding an overlay without a row fails the build.

The **D17 collision** column is the same guard's second rule (Task 11): every row
must end with `collision-zone` or `exempt:<reason>`, and the `collision-zone` set
must equal the named sources of `GazeConsumerInventory.Live()` — the adaptation
preview panel's collision inventory — in BOTH directions. A class marked
`collision-zone` that the inventory does not report is a claim nothing enforces;
a reported source classified `exempt` is silent unprotection. `EdgeButtonBarWindow`
counts once as a class but covers BOTH of its instances (the shared bar and the
action-menu modifier bar). `NoteWindow`'s rects come from `NoteManager`'s live
dwell-target set (its own detector — the note buttons ARE the window's interactive
surface; review F1 reclassified it from a circular exemption that recorded the
implementation instead of a safety property). External dwell
contributes no row of its own because its rects belong to the SDK client apps,
not to an `OverlayWindow` of ours. A new overlay must make the call explicitly;
there is no default classification.

**Finding:** 14 overlays carry a dark fill; 12 are `Background="Transparent"` at
the window level. The transparent ones are *not* out of scope — they composite
accent content over live desktop pixels and therefore need §4.1.3's contrast
guarantee, not merely a token swap. Three of the twelve (`AdaptationCue`,
`DriftNotice`, `CaregiverAlert`) are transparent windows hosting a *filled* shape, which is the case
§4.1.3 originally missed.

**Post-merge additions (PRs #70/#71).** `CueOverlayWindow` and `DriftNoticeWindow`
arrived after this audit was first written. `DriftNoticeWindow` ships `#E0202020` —
the exact legacy Toolbar background §5 removes — plus another `CornerRadius="6"`,
so a brand-new file already carries two of the values this work exists to
eliminate. `CueOverlayWindow` introduces `#FFB300`, an 8th accent family, on a
filled shape.

### 3.2 Dwell Accent / Feedback Colors

| Location | Current Accent | Notes |
|---|---|---|
| `DwellArcButton` default arc | `Brushes.Lime` (`#00FF00`) | Used by ActionMenu, EdgeButtonBar |
| `ActivationButtonWindow` progress arc | `Lime` | XAML hardcoded |
| `CursorCircleWindow` ring | `Lime` | XAML hardcoded |
| `ManualSelectionGazeIndicatorWindow` | `Lime` | XAML hardcoded |
| `ToolbarWindow` progress bar | `Brushes.Lime` | code-behind `Rectangle.Fill` |
| `KeyboardWindow` dwell highlight | Green blend `(0, 100+155*progress, 0)` | code-behind |
| `QuickActionsGridWindow` progress | `#FFEAA7` (warm yellow) | XAML |
| `DwellFeedbackOverlay` | `#3399FF` (blue) | XAML |
| `EyeCareBanner` rings | `#6c8cff` / `#888888` | XAML |
| `EyeCareExercise` target/dot | `#6c8cff` / `#7ee787` | XAML |
| `CalibrationWindow` target | `Green` / `Lime` | XAML |
| `ScrollOverlayWindow` ring | `#4af` (teal) | XAML |
| `DwellToggle` on/off | `#C81E6428` / `#C8502828` | code-behind `Color` |
| `DwellButton` highlight | `ColorBlend.ToHighlightBrush` → white blend | code-behind |
| `DwellSlider` button base | `#C83C3C50` | code-behind |
| `SettingsWindow` buttons | Various: `#C83C3C50`, `#C8285AA0`, `#C8323232` | code-behind |
| `CueOverlayWindow` badge | `#FFB300` (amber) | XAML, filled Ellipse |
| `NoteWindow` state ring | `#4488FF` / `#FFEAA7` | code-only overlay |
| `CursorSettings.CircleColor` | `#00FF00` default | **persisted setting** |
| `CalibrationSettings.TargetColor` | `#00FF00` default | **persisted setting** |
| `AutoSelectionSettings.HighlightColor` | `#0078D4` default | **persisted setting** |
| `DragSettings.TrailColor` | `#ff9800` default | **persisted setting** |

**Finding:** 8 distinct accent families — Lime green, warm yellow, sky blue, blue-purple, teal, MS blue (`#0078D4`), orange (`#ff9800`) and amber (`#FFB300`). No single colour communicates "this is EyeCtrl." Four of the eight are **persisted settings in `EyeCtrl.Core`**, not XAML, and are read back over the XAML value at runtime — see §7.1.

### 3.3 Corner Radii

**Corrected — the original nine-row table undercounted by more than a factor of three.**
It listed one row per *overlay*, which is the wrong unit: a radius is a property of a
`Border`, and most of them are set in C# code-behind rather than in the XAML a
per-overlay sweep reads. The nine rows resolve to at most **eight** distinct code sites
(and one of the nine — "DwellSlider buttons `8` (inherited from DwellButton)" — is not a
site at all, but a note about inheritance). **Twenty-two of the thirty were missing.**

The verified inventory, re-derived at merge base `2d3b1ae`:

| | Count |
|---|---|
| `CornerRadius` lines in `src/EyeCtrl.App` + `src/EyeCtrl.Controls` | **32** |
| …of which carry a literal value | **30** |
| …of which read `ToolbarWindow.xaml.cs`'s `BorderCornerRadius` const | 2 |
| Distinct values | **7** — `2`, `4`, `6`, `8`, `12`, `14`, `22` |

Literal sites per value: `2` → 1, `4` → 3, `6` → 11, `8` → 11, `12` → 2, `14` → 1,
`22` → 1. Re-derive with:

```bash
git grep -n CornerRadius 2d3b1ae -- src/EyeCtrl.App src/EyeCtrl.Controls
```

The **full per-site table** lives in §3.1 of
`docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md`, which
is the spec that closes §8. (Note that spec's §3.1 "Sites" column reads `4` for the value
`4`, counting the const's two consumers alongside its three literal sites; `30` is the
reconciled total and the figure to trust.)

**Two facts the original audit lacked**, both because it never looked at C#:

- **One site is asymmetric.** `NoteWindow.xaml.cs:83` is `new CornerRadius(6, 6, 0, 0)` —
  the note's title bar. It is the only asymmetric site in the app, and it is C#, not XAML,
  which is why §8's worry about "a single `CornerRadius` resource cannot be partially
  overridden" turned out not to bite.
- **One pair nests non-concentrically.** `DwellComboBox.cs:140` puts an outer `Border` at
  radius `6` around a `DwellButton` at radius `8`, so the child's corner is cut *deeper*
  than its parent's. `DwellColorPicker.cs:122` separately overrides `DwellButton`'s `8`
  down to `6` with no nesting involved.

### 3.4 Typography

**Corrected on two counts.** "No font family is explicitly set anywhere" was simply false —
`CalibrationWindow.xaml:30` carried `FontFamily="Segoe UI"` the whole time, which §5 step 29
already flagged as a contradiction. And "everything inherits the WPF default (`Segoe UI`)"
names a face that WPF does not actually promise. The verified inventory at merge base
`2d3b1ae`:

| | Count |
|---|---|
| `FontFamily` literals | **1** — `Overlays/CalibrationWindow.xaml:30` |
| `FontSize` lines | **68**, decomposing as below |
| … live literal sites | **60**, across **16** values: 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 30, 32, 34, 36 |
| … dead `private const`s in `Views/SettingsWindow.xaml.cs` | **4** (`:35`, `:37`, `:38`, `:42` — each occurring only in its own declaration) |
| … computed occurrences, being 2 real sites | **4** (`ToolbarWindow.xaml.cs:70,113` and `DwellArcButton.cs:41,115`) |
| `FontWeight` sites | **17** — `SemiBold` 9, `Bold` 8 |

Re-derive with:

```bash
git grep -n 'FontSize\|FontWeight\|FontFamily' 2d3b1ae -- src/EyeCtrl.App src/EyeCtrl.Controls
```

Every one of the 68 is classified per site in Appendix A of
`docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md`.

**The sites are not one population**, which is the finding that mattered. Appendix A of the
successor spec classifies **66 rows** — 45 text, 17 container-proportional, 4 dead consts —
and reconciles them to the 68 grep hits. Of the **60 live literal** sites, 45 are text and 15
are container-proportional; the 17 total includes `ToolbarWindow.xaml.cs:70,113`, whose sizes
are *computed* rather than literal, which is why the literal split is 45 + 15 and the
row split is 45 + 17.

A container-proportional site sizes a glyph or label to fit its box, and several of those
boxes are settings-driven — so the size is computed at runtime, not declared.
`ToolbarWindow` had already invented the right mechanism for its own two
(`_buttonSize * ButtonSymbolSizeRatio`). Snapping those sites onto a text ramp would have
resized icons relative to the boxes they sit in — a visible regression, and backwards.

**"Everything inherits the WPF default (`Segoe UI`)" is replaced by a measurement.** WPF's
inherited default is the *system message font*, not a fixed face
(`SystemFonts.MessageFontFamily`, which is Segoe UI on this machine but is a system
setting). And what happens when a declared family fails to resolve is **undefined**, not
merely surprising: for a wholesale-nonsense family name, measured three ways, the three
disagree — `Typeface.TryGetGlyphTypeface` reports no face, `FontFamily.FamilyNames` reports
Arial, and the actual render is pixel-identical to *nothing* installed (535–651 differing
pixels against each of Arial, Segoe UI, Segoe UI Variable Text and Tahoma for one 14px
string).
An earlier draft of the successor spec generalised the `FamilyNames` reading into
"**falls back to Arial**"; that claim was disproved and must not be repeated. Because no
single face can be named as the fallback, the token declares the chain explicitly rather
than trusting a bare name — see §4.3.

**Related, and worth knowing before trusting a font name in this repo:**
`Typeface.TryGetGlyphTypeface` is **not** a spelling guard. Measured, it
returns `true` for a typo'd family — WPF near-matches
`"Segoe UI Varible Text, Segoo UI"` onto `SEGOEUI.TTF` (5394 glyphs) — and returns
`false` only for wholesale garbage (`"Qwertyuiop Nonexistent"`, `""`, `"####"`, all of
which also move `LineSpacing` to `1.14990234375`). The shipped chain resolves to
`SEGUIVAR.TTF` (2530 glyphs). What survives either way is the line-spacing ratio:
**1.330078125** for the chain, for `"Segoe UI Variable Text"` alone and for `"Segoe UI"`
alone — which is why the layout arithmetic in `TrackStatusFitTests` can read it live
instead of hard-coding `1.33`. The full record is in `OverlayFontTests`
(`TheFontChain_ResolvesToARealGlyphTypeface`).

### 3.5 App.xaml

```xml
<Application.Resources>
    <!-- completely empty -->
</Application.Resources>
```

---

## 4. Proposed Unified Design System

### 4.1 Color Palette

Every value below is validated by `tools/palette_validate.py`, which encodes the
ten design rules of §4.1.1 as pass/fail gates. **The script must exit 0 before
this palette is considered valid.** All numbers in §9 are its output — do not
hand-edit them.

```
┌─────────────────────┬───────────┬──────────────────────────────────┐
│ Token               │ Value     │ Usage                            │
├─────────────────────┼───────────┼──────────────────────────────────┤
│ SurfaceDark         │ #FA1A1A2E │ All overlay container backgrounds│
│ SurfaceMedium       │ #FA2A2A44 │ Cards, button fills              │
│ SurfaceLight        │ #FF3A3A5E │ Elevated surfaces (modals)       │
│ SurfaceKey          │ #2A2A3E   │ Keyboard key resting state       │
│ SurfaceKeyModifier  │ #3E3E64   │ Keyboard modifier keys           │
│ SurfaceKeySwitch    │ #38527F   │ Keyboard sub-layout switch keys  │
│ SurfaceKeyActive    │ #5278B8   │ Held modifier, active category    │
│ SurfacePrediction   │ #2F5340   │ Prediction bar chips             │
│ BorderSubtle        │ #616BA0   │ Container borders                │
│ BorderFocus         │ #A5F3FC   │ = Accent (see §4.1.2)            │
│ TextPrimary         │ #D6DEF5   │ All primary text                 │
│ TextSecondary       │ #9AA4C8   │ Subtext, hints, labels           │
│ TextDim             │ #868FB5   │ Disabled/placeholder text        │
│ MarkerMove          │ #A8A0D0   │ Note Move state — border + glyph │
│ Accent              │ #A5F3FC   │ **Dwell progress — reserved**    │
│ StrokeHalo          │ #0A0A14   │ Outline under Accent on          │
│                     │           │ transparent overlays (§4.1.3)    │
│ AccentSuccess       │ #7EE787   │ Positive state, completion       │
│ AccentWarning       │ #E8A33C   │ Warnings, delete confirmation    │
│ AccentDanger        │ #FF5C7A   │ Close, cancel, destructive       │
│ ToggleOn            │ #4A8F5C   │ Toggle enabled state             │
│ ToggleOff           │ #2E2E42   │ Toggle disabled state            │
│ ModalBackdrop       │ #B3000000 │ Modal overlay dim                │
└─────────────────────┴───────────┴──────────────────────────────────┘
```

Tokens carrying an alpha channel are composited over live desktop content.
Tokens without one (`SurfaceKey*`, `Text*`, `Border*`, accents) only ever sit on
another token and are therefore specified opaque.

#### 4.1.1 The ten rules

These are the design rules the palette must satisfy. Each is a gate in
`tools/palette_validate.py`; changing a value without re-running the script
invalidates §9.

1. **Layering** — adjacent surfaces show a fill step, and `BorderSubtle` reaches
   3:1 against the worst composited surface (WCAG 1.4.11 for non-text UI
   components). The border carries the boundary; the fill only suggests depth.
2. **Elevation raises opacity.** A modal must separate itself from what is behind
   it, so `SurfaceLight` is fully opaque and `SurfaceDark` is the most
   translucent of the three — the inverse of the previous draft.
3. **Text is measured against the worst composited surface**, not an idealised
   opaque one. `TextPrimary` ≥ 7:1 (AAA), `TextSecondary` and `TextDim` ≥ 4.5:1
   (AA at normal text size, since both are used on small text).
4. **The dwell accent must beat Lime in normal vision and in all three
   dichromacies.** Replacing the current colour may not regress the one signal
   the interaction model depends on. (Restated as an absolute 9.5:1 floor for
   multi-theme use — see §10.4. Midnight's outcome is unchanged either way.)
5. **The accent is reserved.** No meaning-bearing token within ΔE2000 25 of it;
   no text token within 15. Text is separated from the accent by *form* — the
   accent only ever draws arcs, rings and progress fills, never glyphs — so the
   lower floor is deliberate, not a relaxation.
6. **Semantic state is separated by lightness, not hue alone** — ≥ 9 L\* between
   any two of success/warning/danger, so the triad survives red-green deficiency.
7. **Binary state never rests on hue.** `ToggleOn`/`ToggleOff` hold ≥ 3:1 against
   each other in normal vision *and* all three dichromacies.
8. **Keyboard surfaces are mutually distinct** at ΔE2000 ≥ 8 across all five ramp
   steps, so resting, modifier, sub-layout, held-modifier and prediction keys are
   separable at a glance.
9. **Semantic colours are saturated enough to read as their own hue** — Lab
   chroma C\* ≥ 55 for success/warning/danger. Rules 1–8 are all satisfiable by
   desaturated pastels that no longer *mean* anything: a near-white "warning" at
   C\* 10 or a pastel-pink "danger" at C\* 41 can pass every other gate. The real
   triad sits at C\* 62–66. Deliberately **not** applied to `Accent`, which is a
   high-luminance signal and is correctly low-chroma (C\* ≈ 25 in both themes).
   Added after an optimiser search over §10 exploited its absence.
10. **A `Marker*` is visible on its surface and not confusable with text or a
    border.** Markers are the third category of mark — neither the reserved
    `Accent` nor semantic state — so they need their own floors: ≥ 3:1 against the
    worst composited surface *and* against `SurfaceMedium` (WCAG 1.4.11, since a
    marker is a glyph or a 2px border, not text), ΔE2000 ≥ 15 from both
    `TextPrimary` and `BorderSubtle`, ≥ 7:1 against `StrokeHalo`, and ΔE2000 ≥ 25
    from any other marker. Note the text floor applies in *both* directions here:
    rule 5 lets the accent sit closer to text because form separates them — the
    accent never draws glyphs — but a marker glyph *is* a glyph, so that argument
    does not transfer. Added because `MarkerMove` was introduced to fix an ungated
    failure (a state border at 1.77:1 on its own surface) and was itself ungated
    for the property it existed for: two of the three themes then failed the new
    gate, one of them by setting the marker to the same `#FFFFFF` as `TextPrimary`
    and `BorderSubtle`.

#### 4.1.2 Rationale

- **`SurfaceDark` keeps `#1A1A2E`** — the navy already used by QuickActionsGrid.
  It reads as intentional rather than grey mush, and it is the one part of the
  previous draft that was locally sourced. Opacity rises from `#E6` (90%) to
  `#FA` (98%), which bounds the composited contrast drift to −5% instead of −27%.
  Desktop awareness in this UI comes from overlays being *small*, not from
  see-through fills; genuine translucency now lives only in `ModalBackdrop` and
  the full-screen dims.

- **`Accent` = `#A5F3FC`** (replaces Lime `#00FF00`). A light aqua, not the
  mid-tone cyan of the previous draft. It **strictly dominates Lime** — higher
  contrast in normal vision *and* in all three dichromacies (§9.2) — so the swap
  costs nothing in signal strength while gaining a lower green-receptor load for
  hours-long sessions and a colour that is not "terminal green." The earlier
  `#7DCFFF` was a regression on every one of those axes.

- **`BorderFocus` = `Accent`.** Two distinct light blues for "dwelling" and
  "focused" were confusable (ΔE 8.6–14.8) and semantically redundant. Selection
  and active state are now carried by *fill* (`SurfaceLight` / `SurfaceMedium`),
  leaving the accent to mean exactly one thing: gaze dwell in progress. Separate
  the channel, not the hue.

- **`ToggleOn`/`ToggleOff` are no longer a dark red/green pair.** The old values
  differed by 1.52:1 on the red-green axis alone — the least reliable possible
  encoding. The new pair differs by 3.39:1 in lightness and holds ≥ 3.26:1 under
  every dichromacy. "Off" is also no longer red: off is not an error, and red is
  freed for `AccentDanger`.

- **`TextPrimary` = `#D6DEF5`** (not `#FFFFFF`): pure white is needlessly harsh
  over long sessions; a soft blue-white stays AAA (12.14:1 worst case) while
  sitting in the same hue family as the surfaces.

- **`AccentSuccess`/`AccentWarning`/`AccentDanger`** are staged by lightness
  (L\* 83.6 / 72.0 / 61.9) rather than chosen for hue harmony, so the triad
  degrades gracefully. See §9.4 for the residual limit and the glyph rule.

#### 4.1.3 Transparent overlays require `StrokeHalo`

`CursorCircleWindow`, `ActivationButtonWindow`, `ManualSelectionGazeIndicatorWindow`
and `ScrollOverlayWindow` draw directly onto desktop content with no surface
beneath them. No single colour can hold contrast there — the current Lime ring
falls to 1.37:1 over a white document, and any replacement fares the same
(§9.5). **A token swap cannot fix this; it is a contrast-guarantee problem.**

Every accent stroke on a transparent overlay is therefore drawn twice: a
`StrokeHalo` outline at `StrokeThickness + 2`, then the `Accent` stroke over it.
The visible feature becomes the *internal edge* between the two, which is fixed
at 15.78:1 regardless of what is behind the window. Measured across nine
backdrops, the worst-case edge contrast never drops below 15.78:1 — compared
with 1.37:1 for a single-stroke ring today.

This applies to the dwell ring, the activation progress arc, the manual-selection
gaze dot and the scroll velocity ring.

`DragTrailOverlay` belongs here too, and did not once it was migrated: the trail line
takes a user-chosen colour from `DragSettings.TrailColor` at 0.7 opacity on a
click-through transparent window, so it has the same problem as the cursor ring and by
the same argument. The line and the settle ring now carry under-strokes; the grab
glyph, being a `TextBlock` with no stroke to sit under, takes a zero-depth
`StrokeHalo` shadow instead. Note that `StrokeDashArray` is expressed in *multiples of
`StrokeThickness`*, so the settle ring's halo pattern is scaled by
`thickness / (thickness + 2)` — otherwise the halo's dashes run longer than the
accent's and it shows past each dash end.

**Two more sites belong here, and the original list missed them** because it
reasoned about *window* transparency rather than about what the mark is actually
drawn on. `ActionMenuWindow`'s position indicator (crosshair or pin) and
`ManualSelectionOverlayWindow`'s magnifier rim are drawn on top of a **captured
screen image** — arbitrary content, exactly like the raw desktop, and the reason a
red crosshair could vanish on a photograph. Both now use a `TextPrimary` mark over a
`StrokeHalo` under-stroke: the conventional precision-marker form (light mark, dark
outline), backdrop-independent by the same internal-edge argument.

**Two sites in the list are belt-and-braces, not load-bearing.** The
`DwellArcButton` progress arc (`ActionMenu`, `EdgeButtonBar`) and
`ActivationButtonWindow`'s arc both sit *inside* a fill their own overlay draws —
`SurfaceMedium` and `SurfaceDark` respectively, each 98 % opaque — because the arc
radius is inset from the button edge. Their accent already has a guaranteed backdrop,
so their halo is defence-in-depth rather than the contrast guarantee. It is retained
(it sharpens the arc against the navy fill and costs one extra `Path`), but the
backdrop-independence argument does **not** rest on those two.

**Filled shapes need the same guarantee by a different means.** The rule above
covers *strokes*; a filled shape has none to sit under. `CueOverlayWindow`
(PR #71) is a 40px `Ellipse` with `Fill="#FFB300"` and no stroke at all, on a
transparent window — so it inherits the full problem:

| Cue badge `#FFB300` on raw desktop | |
|---|---|
| black | 11.70 |
| mid grey | **2.20** |
| white | **1.79** |
| amber-ish content | **1.06** |

At 1.06:1 the struggle cue is invisible over amber content — on the one overlay
whose entire purpose is to get attention when the user is already struggling.

**Rule:** a filled accent shape on a transparent overlay carries a `StrokeHalo`
**outline ring** (`StrokeThickness` 2) around its fill. Measured across five
backdrops the badge/halo edge then holds ≥ **10.97:1** in every theme (Midnight
10.97, Ember 10.99, Contrast 11.70). Stroked shapes get an under-stroke; filled
shapes get an outline. Both end up guaranteeing an *internal* edge, which is what
makes the result backdrop-independent.

**The ring and the fill must be separate elements.** A single `Ellipse` with both
`Fill` and `Stroke` looks equivalent but is not: `CueOverlayWindow` animates the
badge's `Opacity`, and on one element that dims the ring along with the fill, which
puts the backdrop back into *both* terms of the edge. Measured at the badge's rest
intensity that collapsed the edge to **1.48:1** over white and **1.67:1** over amber
— i.e. the halo bought nothing on the overlay it was introduced for. Split into an
opaque `BadgeHalo` ring plus a fading `Badge` fill, the same backdrops give **19.69**
and **9.13**. The same applies to every mark whose opacity is animated or
user-configurable: the halo keeps `Opacity` 1 and only the accent side dims.

**Known limit — fading over dark content.** The guarantee is over the *internal*
edge, and an opaque `StrokeHalo` ring is itself invisible against near-black
backdrops (halo-vs-black is 1.07:1). So while the cue badge fades toward zero over
near-black content, its strongest edge falls to **1.50:1** at the 0.25 rest
intensity. No halo can fix that: fading a mark's opacity trades away contrast by
definition. Options if it matters in practice are to fade *colour* or *size* rather
than opacity, or to raise `MinPeakOpacity` — recorded here rather than left implied
by the ≥ 10.97 figure above, which assumes a fully-opaque fill.

### 4.2 Corner Radii — **superseded**

**Superseded by
`docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md` §6.1.**
Neither §4.2 nor §4.3 was ever implemented, and both were insufficient as written. Both are
kept, superseded rather than deleted, as the record of what was proposed and why it was not
enough.

This section proposed three tokens — `CornerRadiusStandard` `8`, `CornerRadiusSmall` `4`,
`CornerRadiusLarge` `12` — on the rationale that `8` "is the most common current value and
works at every scale," `4` "covers the Toolbar's current look," and `12` is "reserved for
prominent cards."

**Why that was insufficient.** Three tokens cover `4`, `8` and `12` against the **seven**
values §3.3 actually measures. `2`, `6`, `14` and `22` had no home, which is precisely
what §8 recorded when it deferred the migration rather than shipping a partial map. The
successor spec resolves all four by decision — `2 → 4`, `6 → 8` (11 sites), `14 → 12`,
and `22` becomes a fourth token, `CornerRadiusCircle`, rather than a fixed number (see the
§8 delivery note for why the name is not `CornerRadiusPill`).

### 4.3 Typography — **superseded**

**Superseded by
`docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md` §8.**

This section proposed a **family-only** pass: set `FontFamily="Segoe UI"` as an
application-level default, and leave every overlay's font sizes alone because "those are
functional, not decorative."

**Why that was insufficient.** A family token would have left **16 distinct sizes across
60 live sites** (§3.4) fragmented, with nothing to stop the next one being written — and a
drift guard added over that state would have *enforced* the fragmentation rather than
removing it. The successor spec therefore delivers family **plus** two weight tokens, a
five-step size ramp with a 12px accessibility floor, and a container-ratio rule for the 17
sites that size a glyph to its box. It also reverses this document's "font size changes are
out of scope" (§8), deliberately and with the concern stated: the scale cannot be unified
without moving some sizes.

Two details of the family value that this section could not have known:

- The value is the **chain** `"Segoe UI Variable Text, Segoe UI"`, not a bare name, because
  §3.4 measured that an unresolvable bare name degrades along an **undefined** path.
- `Segoe UI Variable Text` is metrically identical to `Segoe UI` — advance widths and line
  heights agree to 0.01px at 9, 10, 12, 14, 22, 24 and 34px — so the swap cannot reflow,
  re-wrap or clip anything. It changes glyph shapes only. `CalibrationWindow.xaml:30`'s
  literal is deleted; the value arrives by inheritance from one assignment in
  `OverlayWindow`'s constructor, pinned by `OverlayFontTests`.

### 4.4 Thickness

| Token | Midnight | Usage |
|---|---|---|
| `BorderThicknessStandard` | `1` | All container borders |

**This token is theme-varying** — the only non-colour token that is. High-contrast
convention is a heavier outline because in that theme structure is carried by the
border rather than by fill steps (§10.7 gate 1); `Contrast` uses `2`. Consequences
for the token layer:

- `Tokens.cs` cannot be colours-only. `ITheme` must expose a
  `double BorderThicknessStandard` alongside the 20 brushes, so plan for it in
  Phase 0 rather than widening the interface later (§10.6).
- The validator excludes it from every colour gate (`NON_COLOUR_TOKENS`) and gates
  it only for being ≥ 1, reported next to the theme's border contrast so the two
  are read together.
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

---

## 5. Migration Plan

Phase order is a dependency chain: tokens must exist before anything consumes
them, and `EyeCtrl.Controls` must be migrated before `EyeCtrl.App` so the shared
dwell controls carry the new defaults into every overlay that hosts them.

### Phase 0: Token foundation (3 new files, 1 modify)

Tokens live in **`EyeCtrl.Controls`**, not `EyeCtrl.App`. The reference direction
is `App → Controls` (`EyeCtrl.App.csproj:10`) and `Controls → Core` only, so a
dictionary in `src/EyeCtrl.App/` is invisible to `DwellArcButton` and friends.
Placing tokens in `App` would make Phase 1 uncompilable.

1. **CREATE** `src/EyeCtrl.Controls/Theme/Tokens.cs`
   - `public static class Tokens` exposing one `public static readonly Color` per
     §4.1 token, plus a frozen `SolidColorBrush` per token (`BrushAccent`, …).
   - Plain C# — no `Application.Current` lookup. `EyeCtrl.Controls.Tests` and
     `EyeCtrl.Core.Tests` run without a WPF `Application`; a resource-lookup
     approach would `NullReferenceException` there.
   - This is the **single source of truth** for all colour values.

2. **CREATE** `src/EyeCtrl.Controls/Theme/OverlayStyles.xaml`
   - `ResourceDictionary` exposing each token as `{StaticResource BrushXxx}` for
     XAML consumers, deriving its value from `Tokens` rather than restating it:
     ```xml
     <SolidColorBrush x:Key="BrushSurfaceDark"
                      Color="{x:Static theme:Tokens.SurfaceDark}"/>
     ```
   - Deriving via `x:Static` makes drift between the C# and XAML views
     structurally impossible — there is one literal per colour, in `Tokens.cs`.

3. **CREATE** `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs`
   - Loads `OverlayStyles.xaml` and asserts every `Brush*` key resolves and equals
     its `Tokens` counterpart. Guards a mistyped `x:Static` path (which fails at
     runtime, not build time) and catches anyone later pasting a raw literal into
     the dictionary.
   - Mirrors the existing drift-guard convention of
     `EyeCtrlSettingsEnumerationTests`.

4. **MODIFY** `src/EyeCtrl.App/App.xaml`
   - Merge the library dictionary by pack URI:
     ```xml
     <ResourceDictionary Source="pack://application:,,,/EyeCtrl.Controls;component/Theme/OverlayStyles.xaml"/>
     ```

### Phase 1: Shared dwell controls (7 files, `EyeCtrl.Controls`)

Changes here propagate to `ActionMenuWindow` and `EdgeButtonBarWindow` without
touching those files.

5. **MODIFY** `DwellArcButton.cs`
   - Arc brush: `Brushes.Lime` → `Tokens.BrushAccent`
   - Default fill: `#C81E1E1E` → `Tokens.SurfaceMedium`
   - Default stroke: `#64646464` → `Tokens.BorderSubtle`
   - **Add the §4.1.3 halo**: draw a `StrokeHalo` arc at `StrokeThickness + 2`
     beneath the accent arc. Both `ActionMenuWindow` and `EdgeButtonBarWindow`
     are `Background="Transparent"`, so their arcs currently have no contrast
     guarantee; this is where they get one.

6. **MODIFY** `DwellToggle.cs`
   - `OnColor`: `#C81E641E` → `Tokens.ToggleOn` (`#4A8F5C`)
   - `OffColor`: `#C8502828` → `Tokens.ToggleOff` (`#2E2E42`)
   - **This is a deliberate value change, not a constant extraction.** The old
     pair differed by 1.52:1 on the red-green axis alone (§9.4 rationale); the new
     pair carries state in lightness at 3.39:1.

7. **MODIFY** `DwellSlider.cs`
   - `BtnBase`: `#C83C3C50` → `Tokens.SurfaceMedium`

8. **MODIFY** `DwellComboBox.cs`
   - `BtnBase` (line 30): `#C83C3C50` → `Tokens.SurfaceMedium`
   - `SelectedColor` (line 31): `#C81E641E` → `Tokens.ToggleOn`

9. **MODIFY** `DwellColorPicker.cs`
   - Replace the 8-swatch preset list (lines 31–33) with palette-aligned presets,
     `Accent` first: `#A5F3FC` "Aqua" (15.78), `#7EE787` "Green" (12.82),
     `#E8A33C` "Amber" (9.13), `#D6DEF5` "White" (14.65).
   - The current list offers `#00FF00` labelled "Green" as the first option — the
     exact colour this migration removes everywhere else.
   - **Every preset must clear 7:1 against `StrokeHalo`** (figures above). This
     picker feeds `CursorSettings.CircleColor`, which colours a haloed ring on a
     transparent overlay; a preset that is dark or mid-tone collapses the §4.1.3
     guarantee from 15.78:1 to whatever that colour manages against `#0A0A14`.
     `AccentDanger` (`#FF5C7A`) fails this at 6.62 and is deliberately **not**
     offered — it is also semantically wrong for a cursor ring, being reserved for
     destructive actions.
   - See the note in §7 on why an already-persisted `#0088FF` (5.59:1 vs halo)
     matters more than a stale default would.

10. **MODIFY** `DwellLabel.cs`
    - Foreground (line 30): `Color.FromRgb(180, 180, 180)` → `Tokens.TextSecondary`

11. **MODIFY** `ColorBlend.cs`
    - `ToHighlightBrush` blends the base colour toward `Colors.White`. Change the
      target to `Tokens.Accent` so dwell highlight is cyan-tinted everywhere.
    - **Scope warning:** this single change alters dwell feedback on every
      `DwellButton`, `DwellToggle`, settings tab, and pagination control in the
      app — the largest visual delta in the migration, from a three-line edit. It
      is what makes §4.1's "accent = dwell in progress" true rather than aspirational.

### Phase 2: Settings colour defaults (4 files + 1 test, `EyeCtrl.Core`)

**This phase is new and non-optional.** Four accent colours are persisted
settings, not XAML. Without it, the cursor ring and calibration target stay Lime
no matter what the XAML says — `CursorCircleWindow.xaml.cs:32` and
`CalibrationWindow.xaml.cs:70` overwrite the XAML value at runtime from settings.
Two of these (`HighlightColor`, `TrailColor`) were absent from the §3.2 audit
entirely, making the real count seven accent families rather than five.

12. **MODIFY** `src/EyeCtrl.Core/Settings/CursorSettings.cs:22`
    - `CircleColor`: `"#00FF00"` → `"#A5F3FC"` (`Accent`)

13. **MODIFY** `src/EyeCtrl.Core/Settings/CalibrationSettings.cs:19`
    - `TargetColor`: `"#00FF00"` → `"#A5F3FC"` (`Accent`)

14. **MODIFY** `src/EyeCtrl.Core/Settings/AutoSelectionSettings.cs:9`
    - `HighlightColor`: `"#0078D4"` → `"#A5F3FC"` (`Accent`)

15. **MODIFY** `src/EyeCtrl.Core/Settings/DragSettings.cs:9`
    - `TrailColor`: `"#ff9800"` → `"#E8A33C"` (`AccentWarning`) — keeps the warm,
      deliberately-distinct-from-`Accent` character of the drag trail so a motion
      trail is never mistaken for dwell progress.

16. **MODIFY** `tests/EyeCtrl.Core.Tests/Settings/DragSettingsTests.cs:13`
    - `Check.That(s.TrailColor).IsEqualTo("#ff9800")` → `"#E8A33C"`. This is the
      only test in the suite that hard-asserts a colour default and it **will**
      fail otherwise. (`SettingsServiceValidationTests.cs:69` compares against
      `defaults.Cursor.CircleColor` and is self-relative; the `#00FF00` literals
      in `SettingsSerializationTests` and `SettingsPropertyValidationTests` are
      explicit inputs and hex-format fixtures, both unaffected.)

`EyeCtrl.Core` contains no UI rendering, but it does own these values — see the
correction in §6.

### Phase 3: Code-behind colour constants (4 files, `EyeCtrl.App`)

17. **MODIFY** `Overlays/KeyboardWindow.xaml.cs`
    - `KeyColorNormal` (line 20): `#C8323232` → `Tokens.SurfaceKey`
    - `KeyColorSpecial` (line 21): `#C83C3C50` → `Tokens.SurfaceKeyModifier`
    - `KeyColorSwitch` (line 22): `#C832466E` → `Tokens.SurfaceKeySwitch`
    - `KeyColorModifierActive` (line 23): `#E650B4FF` → `Tokens.Accent`
    - `PredictionBarColor` (line 24): `#C8285028` → `Tokens.SurfacePrediction`
    - Dwell highlight blend → `Tokens.Accent`. **There are two call sites**, not
      one: line 573 (prediction chip) and line 602 (key). Both use
      `Color.FromArgb(a, 0, (byte)(100 + 155 * progress), 0)`.
    - Prediction-kind brushes (lines 37–53) are five semantic colours absent from
      §3.2: `#C8C8FF` fuzzy, `#C8FFC8` next-word, `#FFFFC8` phrase, `#64C8FF`
      sentence-completion, `#C896FF` LM suggestion. Retarget onto the palette
      (`TextSecondary`, `AccentSuccess`, `AccentWarning`, `Accent`, `TextPrimary`)
      or document them as an intentional out-of-palette semantic set — but do not
      leave them unaddressed. `Brushes.Gray` (line 559) → `Tokens.TextDim`.

18. **MODIFY** `Overlays/ToolbarWindow.xaml.cs`
    - Button background (line 82): `FromArgb(NormalButtonAlpha, 60, 60, 60)` →
      `Tokens.SurfaceMedium`
    - Indicator background (line 125): `FromArgb(IndicatorButtonAlpha, 40, 40, 40)`
      → `Tokens.SurfaceDark`
    - Progress bar (line 62): `Brushes.Lime` → `Tokens.BrushAccent`
    - ManualSelection active (line 217): `FromArgb(180, 0, 100, 0)` → `Tokens.ToggleOn`
    - AutoSelection active (line 218): `FromArgb(180, 0, 100, 180)` → `Tokens.SurfaceKeySwitch`
    - The three alpha constants (lines 19–21: 180 / 120 / 180) become redundant
      once fills come from tokens; remove them rather than leaving dead constants.

19. **MODIFY** `Views/SettingsWindow.xaml.cs`
    - `BtnBase` (line 43): `#C83C3C50` → `Tokens.SurfaceMedium`
    - `TabActive` (line 44): `#C8285AA0` → `Tokens.SurfaceKeySwitch`
    - `TabInactive` (line 45): `#C8323232` → `Tokens.SurfaceKey`
    - `ResetBase` (line 46): `#C8805020` → `Tokens.AccentWarning`
    - `ModalYes` (line 47): `#C81E641E` → `Tokens.ToggleOn`
    - `ModalNo` (line 48): `#C8641E1E` → `Tokens.AccentDanger`
    - `CloseBase` (line 49): `#C8641E1E` → `Tokens.AccentDanger`
    - `DeleteBase` (line 50): `#C8C88228` → `Tokens.AccentWarning`
    - Note `TabActive` is `#C8285AA0` (`FromArgb(200,40,90,160)`); earlier drafts
      of this document recorded it as `#C82859A0`.

20. **MODIFY** `Overlays/NoteWindow.xaml.cs`
    - **A 22nd overlay window, absent from every table in §3.** It is
      `class NoteWindow : OverlayWindow` with no `.xaml` file, so a XAML-only
      audit misses it. Ten hardcoded colours:
      `#1A1A2E` (line 67) → `Tokens.SurfaceDark`; `#2D2D3F` (79) →
      `Tokens.SurfaceMedium`; `#999999` (85) → `Tokens.TextSecondary`; `#252540`
      (117) → `Tokens.SurfaceKey`; `#FFEAA7` (137) → `Tokens.AccentWarning`;
      `#4488FF` (212) → `Tokens.Accent`; `#333344` (213) → `Tokens.SurfaceMedium`;
      the `#FF6666`/`#66FF66`/`#6688FF` symbol triple (224–226) →
      `AccentDanger`/`AccentSuccess`/`Accent`; `#33FFFFFF` (237) →
      `Tokens.BorderSubtle`.

### Phase 4: Filled overlay XAML (12 files)

All backgrounds below are on an inner `<Border>`/`<Rectangle>` with the `Window`
itself `Background="Transparent"`, except `SettingsWindow`, `TaskSwitcherWindow`
and `EyeCareExerciseWindow` where it is on the `Window`.

21. **`KeyboardWindow.xaml:9`** — `#F0181818` → `{StaticResource BrushSurfaceDark}`
22. **`ToolbarWindow.xaml:9`** — `#E0202020` → `{StaticResource BrushSurfaceDark}`
23. **`QuickActionsGridWindow.xaml`**
    - RootBorder bg (11): `#1A1A2E` → `BrushSurfaceDark`; border (12): `#334` → `BrushBorderSubtle`
    - Remove `Opacity="0.95"` (12) — alpha now lives in the token
    - Tile/close bg (23, 47): `#2A2A3E` → `BrushSurfaceMedium`
    - Progress + text foreground (31, 63): `#FFEAA7` → `BrushAccent` / `BrushTextPrimary`
24. **`SplashWindow.xaml`** — bg (8): `#F0181818` → `BrushSurfaceDark`; progress (18): `#FF00AA00` → `BrushAccentSuccess`; track `#FF333333` → `BrushSurfaceMedium`
25. **`TrackStatusWindow.xaml`** — bg (8): `#80000000` → `BrushSurfaceDark`; box border (13): `White` → `BrushBorderSubtle`; box fill: `#20FFFFFF` → `BrushSurfaceMedium`
26. **`TaskSwitcherWindow.xaml`** — bg (5): `#E6000000` → `BrushSurfaceDark`; tile (22): `#40FFFFFF` → `BrushSurfaceMedium`; text → `BrushTextPrimary` / `BrushTextSecondary`
27. **`EyeCareBannerWindow.xaml`** — banner (17): `#F0181830` → `BrushSurfaceDark`; **bottom `BorderBrush` (18): `#6c8cff` → `BrushBorderSubtle`** (omitted from earlier drafts); WorkDim (14): `#73000000` → `BrushModalBackdrop`; SnoozeRing (29): `#888888` → `BrushTextDim`; StartRing + glyphs (36–39): `#FF6c8cff` → `BrushAccent`; SubText (24, 30): `#FFAAAAAA` → `BrushTextSecondary`; "snooze" label (32): `#FF777777` → `BrushTextDim`
28. **`EyeCareExerciseWindow.xaml`** — window bg (7): `#E8000000` → `BrushSurfaceDark`; snooze fg/border (31): `#FF888888`/`#FF444444` → `BrushTextDim`/`BrushBorderSubtle`; summary card (39): `#F0181830` → `BrushSurfaceLight`, border `#FF6c8cff` → `BrushBorderSubtle`; target dot → `BrushAccent`; gaze ring `#FF7ee787` → `BrushAccentSuccess`; metric values → `BrushAccent`
29. **`CalibrationWindow.xaml`** — Backdrop `Rectangle.Fill` (15): `#DD000000` → `BrushModalBackdrop`; target dot (20): `Green` → runtime-set from `CalibrationSettings.TargetColor` (Phase 2), XAML value is a fallback only; progress text: `#B0FFFFFF` → `BrushTextPrimary`; completion `Lime` → `BrushAccentSuccess`; error (52): `#FFAA00` → `BrushAccentWarning`; hint (55): `#88FFFFFF` → `BrushTextSecondary`; **`FontFamily="Segoe UI"` (30) is redundant once §4.3 sets it app-wide — remove it** (this is the declaration that falsifies §3.4's "no font family is set anywhere")
30. **`PauseIndicatorWindow.xaml:12`** — `#C0204060` → `BrushSurfaceDark`
31. **`Views/SettingsWindow.xaml`** — bg (8): `#E01E1E1E` → `BrushSurfaceDark`; sidebar (14): `#C0141414` → `BrushSurfaceMedium`; close (20): `#C8641E1E` → `BrushAccentDanger`; modal dim (54): `#A0000000` → `BrushModalBackdrop`; modal content (59): `#E0282828` → `BrushSurfaceLight` (elevated — §4.1.1 rule 2)
32. **`Overlays/DriftNoticeWindow.xaml`** *(new — PR #71)* — inner `Border` (12):
    `#E0202020` → `{StaticResource BrushSurfaceDark}`, and add
    `BorderBrush="{StaticResource BrushBorderSubtle}"` +
    `BorderThickness="{StaticResource BorderThicknessStandard}"` (it currently has
    none, so its edge is undefined against the desktop); text (15):
    `Foreground="White"` → `BrushTextPrimary`. Leave `CornerRadius="6"` — radius
    unification is deferred (§8), but note this file adds a fourth instance of it.
    **Superseded:** §8 is no longer a deferral. This site is now
    `{StaticResource StandardCornerRadius}` (`6 → 8`).

### Phase 5: Transparent overlays — halo pass (6 files)

**This is a rendering change, not a colour swap.** Each accent stroke becomes two
strokes per §4.1.3. The former "accent only" framing was wrong: no token value
can give these overlays a contrast guarantee, because they composite against
arbitrary desktop content (§9.5).

33. **`ActivationButtonWindow.xaml`** — bg circle (11): `#80000000` → `BrushSurfaceDark`; **add a `StrokeHalo` arc beneath `ProgressArc`** (16); arc `Stroke="Lime"` → `BrushAccent`; centre icon (28) `Fill="White"` → `BrushTextPrimary`
34. **`CursorCircleWindow.xaml` + `.xaml.cs`** — **add a halo `Ellipse` beneath `Circle`** at `StrokeThickness + 2`; XAML `Stroke="Lime"` (11) → `BrushAccent` (fallback only — the runtime value comes from Phase 2); `.xaml.cs:36` parse-failure fallback `Brushes.Lime` → `Tokens.BrushAccent`. Keep the hidden `TextBlock` UIA anchor and its `AutomationId` untouched.
35. **`ManualSelectionGazeIndicatorWindow.xaml`** — all `Lime` → `BrushAccent`, + halo
36. **`ScrollOverlayWindow.xaml`** — crosshairs/cursor/anchor (13–18): `White` → `BrushTextPrimary`; velocity ring (19): `#4af` → `BrushAccent`; + halo on the ring
37. **`DwellFeedbackOverlay.xaml:8`** — `BorderBrush`/`Background`: `#3399FF`/`#1A3399FF` → `BrushAccent` / `Accent` at low alpha
38. **`Overlays/CueOverlayWindow.xaml`** *(new — PR #71)* — badge `Fill` (14):
    `#FFB300` → `{StaticResource BrushAccentWarning}` (`#E8A33C`, only ΔE 7.0
    away, so this is a substitution rather than a redesign). **Add the §4.1.3
    outline ring**: `Stroke="{StaticResource BrushStrokeHalo}" StrokeThickness="2"`.
    This is the filled-shape case, not the stroked one. `Opacity` is animated in
    code-behind and must stay untouched, as must the
    `AdaptationCue_Badge` UIA anchor.

### Phase 6: Verification

39. **RUN** `dotnet build EyeCtrl.slnx` — must compile clean
40. **RUN** `python tools/palette_validate.py` — must exit 0 (§9 is only valid if it does)
41. **RUN** `dotnet test --filter "Category!=UI"` — must pass, including
    `EyeCtrl.App.Tests --filter "Category!=UI"` and the new
    `TokenDictionaryTests`
42. **RUN** `dotnet test --filter "Category=UI"` — **requires an interactive
    desktop session.** Roughly 13 of these fail headless on clean `main` with
    "Toolbar_Window did not appear within 15s"; they are environmental, not
    regressions. This is not a usable CI gate — do not treat a headless failure
    here as a migration defect.
43. **VISUAL** — launch once and confirm the halo renders on `CursorCircle`,
    `ActivationButton`, `ActionMenu` and `EdgeButtonBar` over both a dark and a
    light background window. This is the only check that can catch a z-order
    mistake putting the halo *above* the accent stroke.

---

## 6. Files NOT Changed

- `EdgeButtonBarWindow.xaml` + `.cs` — inherits accent **and halo** from `DwellArcButton` (Phase 1)
- `GlassBackdropWindow.xaml` + `.cs` — fully dynamic; no hardcoded colours
- `TargetHighlightWindow.xaml` — opacity-driven; `CornerRadius="2"` left as-is (§8).
  **Superseded:** now `{StaticResource SmallCornerRadius}` (`2 → 4`), per §8's delivery note
- `DragTrailOverlay.xaml` + `.cs` — the trail line now reads `DragSettings.TrailColor`
  via `Configure` (wired in `EventCoordinator.OnDragStarted`); the settle/release greens
  and release background are on tokens. (An earlier draft wrongly claimed the files needed
  no edit — the overlay had hardcoded `#ff9800`/`#4caf50`/`#1a3a1a` and never read the
  setting; the wiring fix corrected both the dead default and the no-op "Trail Color" picker.)
- `EyeCtrl.LM`, `EyeCtrl.Platform`, `EyeCtrl.ViewModels`, `EyeCtrl.EyeCare`,
  `EyeCtrl.Contracts`, `EyeCtrl.Sdk.*`, `EyeCtrl.Supervisor` — no colour literals
  in scope

**Correction to earlier drafts:** `EyeCtrl.Core` was previously listed here as
"no UI rendering." That is true of its rendering but false of its *ownership* — it
holds four persisted accent colours, and Phase 2 changes them. Excluding Core is
what would have left the cursor ring Lime.

**Three further exclusions were wrong for the same reason** — the rationale was true
of *part* of the file. All three are now migrated, and the pattern is worth naming: an
exclusion must be justified against every colour in the file, not against its headline
purpose.

| Was excluded because | Actually contained |
|---|---|
| `DragTrailOverlay` "renders from `DragSettings.TrailColor`" | hardcoded `#ff9800`/`#4caf50`/`#1a3a1a`, and never read the setting |
| `ActionMenuWindow` "inherits accent and halo from `DwellArcButton`" | true of the arc buttons; the position indicator had its own 4× `Brushes.Red` + `Brushes.White` |
| `ManualSelectionOverlayWindow` "screen capture; no decorative colours" | a decorative `Stroke="White"` 2px magnifier rim |

The last two are also **§4.1.3 cases the original list missed**: both draw marks *on
top of a captured screen image*, which is arbitrary content exactly like the desktop.
They now carry `StrokeHalo` under-strokes, and §4.1.3's overlay list includes them.

---

## 7. Risk Assessment

| Risk | Likelihood | Mitigation |
|---|---|---|
| **A persisted `cursor.circleColor` survives Phase 2 and weakens the halo.** Settings persist to `%APPDATA%\EyeCtrl\settings.json`, and changing a code default does not touch keys already in that file. | Low — one key, see §7.1 | **No migration code.** Single-user project; change the value once through the existing Circle Color picker. Details and the reason it matters in §7.1. |
| `ColorBlend.ToHighlightBrush` retarget silently changes dwell feedback on every button, toggle, tab and pagination control | Medium | Largest visual delta per line changed in the migration. Review it as its own commit; it is easy to mistake for a regression during Phase 1 review. |
| Halo z-order inverted — `StrokeHalo` drawn *above* the accent stroke, hiding it | Medium | Renders as a dark ring on every backdrop, so it is obvious once seen but invisible to any headless test. Step 41 exists for this. |
| Pack URI fails to resolve `OverlayStyles.xaml` from the `EyeCtrl.Controls` assembly | Medium | Fails at runtime, not build time. `TokenDictionaryTests` (Phase 0 step 3) loads the dictionary explicitly and fails fast in a headless test. |
| `x:Static` path typo in the dictionary | Medium | Same mitigation — the dictionary test compares each brush against `Tokens`. |
| `DragSettingsTests` fails | **Certain** | Step 16 updates it in the same phase as the default change. |
| Halo doubles stroke geometry on overlays that redraw at gaze rate (`CursorCircle`, `ActivationButton`) | Low | Two `Freeze()`d brushes and one extra `Ellipse`/`Path` per overlay; no per-frame allocation. Confirm no new allocations in the gaze path. |
| `DwellColorPicker` preset change conflicts with a persisted user choice | Low | The picker only offers presets; a saved out-of-list colour still round-trips. Existing selection is preserved, just no longer highlighted as a preset. |
| `AccentDanger` is AA (5.49:1), not AAA | Accepted | Used only on ≥22px close/cancel glyphs, where AA applies. Deliberate trade to preserve the semantic triad's lightness separation — see §9.1 and §9.4. |
| Warning/danger indistinguishable under tritanopia | Accepted | Unsolvable in a three-colour triad (§9.4). Mitigated by the mandatory glyph rule, which existing UI already satisfies. |
| Missing one of the two green dwell blends in `KeyboardWindow` | Medium | Two call sites (lines 573, 602) with near-identical code; earlier drafts of this document recorded only one. Grep for `100 + 155 * progress` to confirm both are gone. |
| UI test selector breakage | Low | No `AutomationId` changes; no test asserts on any brush or colour except `DragSettingsTests`. `CursorCircle_Ellipse_Main`'s hidden `TextBlock` anchor must survive the halo edit in step 33. |
| Removing QuickActions' `Opacity="0.95"` shifts the fill | Low | `SurfaceDark` is `#FA` (98%) against the old effective ~85% (`0.95 × #1A1A2E` composited); the overlay becomes slightly more opaque, which §4.1.1 rule 3 intends. |

### 7.1 Persisted settings — decision: no migration code

**Decided:** this is a single-user project, so Phase 2 ships no settings-migration
code. The behaviour below was checked against the actual install rather than
assumed.

`%APPDATA%\EyeCtrl\settings.json` is a **partial** document — it contains only
`actionMenu`, `cursor`, `dwell`, `general`, `keyboard`, `selectionMode`, `theme`,
`toolbar` and `zoom`. Every absent section deserializes from the code defaults, so:

| Setting | In the file? | Effect of Phase 2 |
|---|---|---|
| `calibration.targetColor` | no | picks up `#A5F3FC` automatically |
| `autoSelection.highlightColor` | no | picks up `#A5F3FC` automatically |
| `drag.trailColor` | no | picks up `#E8A33C` automatically |
| `cursor.circleColor` | **yes — `#0088FF`** | unchanged; needs one manual edit |

Three of the four therefore need no action at all. **Correction to earlier drafts
of this document:** they claimed an existing install "will still show a Lime cursor
ring," rated High/certain. That is wrong — the persisted value is `#0088FF`, a
deliberate user choice, not a stale `#00FF00` default, and it is the only one of
the four that persists.

**Why the remaining one still matters.** `#0088FF` is not merely off-palette; it
is a poor ring colour and it undermines §4.1.3:

| | on `#1A1A2E` | prot | deut | trit | **vs `StrokeHalo`** |
|---|---|---|---|---|---|
| `#0088FF` (persisted) | 4.85 | 5.29 | 4.62 | 3.87 | **5.59** |
| `#00FF00` (old default) | 12.43 | 14.20 | 11.52 | 9.66 | 14.35 |
| `#A5F3FC` (`Accent`) | 13.67 | 14.60 | 13.20 | 11.60 | **15.78** |

The halo guarantees a backdrop-independent edge only to the extent the ring
contrasts with `StrokeHalo`. At 5.59:1 the guarantee drops from 15.78:1 to 5.59:1 —
below the 7:1 gate — so the transparent-overlay fix is substantially weakened for
the one overlay the user looks at most. On raw desktop without the halo, `#0088FF`
manages 1.12:1 over mid-grey.

**Action:** after Phase 2 lands, set Circle Color to Aqua (`#A5F3FC`) once via the
existing picker in Settings → Cursor — a single dwell interaction, no JSON editing,
no migration code. The alternative "Reset ALL Settings" button
(`SettingsViewModel.ResetAllSettings()`) would also do it, but it rebuilds every
sub-setting from defaults and would discard unrelated tuning; prefer the picker.

---

## 8. Out of Scope

Genuinely excluded from this work:

- Layout changes (margins, padding, control sizes) — **amended:** the geometry/typography
  successor spec takes exactly one named exception, `TrackStatusWindow.Height` 110 → 116
- Font size changes — **reversed** by that spec's §8; see §4.3 above for why
- Light mode
- Animation curves / timing
- Settings **file format** (Phase 2 changes four default *values*, not the schema)
- EyeCare scoring UI
- Supervisor UI (`EyeCtrl.Supervisor`) — see the decision recorded below
- WinUI SDK sample (`EyeCtrl.Sdk.WinUI.Sample`)

**The Supervisor exclusion is a decision, not an oversight — and it means the *product*
is not uniformly themed.** `EyeCtrl.Supervisor` is a separate `WinExe`
(`src/EyeCtrl.Supervisor/EyeCtrl.Supervisor.csproj`) that *does* reference
`EyeCtrl.Controls`, so it could read `Tokens`. It does not. Its two window classes —
`UI/FallenBackDialog.xaml.cs:21` and `UI/SupervisorSetupWindow.xaml.cs:31` — derive from
`Window`, not `OverlayWindow`, so they miss both the palette and the one-assignment font
default, and they keep the system font. Between them they hold **22 geometry and typography
literals** (19 in `SupervisorSetupWindow.xaml`, 3 in `FallenBackDialog.xaml`; re-derive with
`grep -rn "CornerRadius\|FontSize\|FontFamily\|FontWeight" src/EyeCtrl.Supervisor/UI`).
`src/EyeCtrl.Supervisor` is therefore **deliberately absent** from the drift guard's
`ScannedProjects` (`tests/EyeCtrl.App.Tests/SourceScanner.cs:29`, which records the reason in
its own doc comment so the omission cannot be read as an oversight and quietly widened).

Two consequences worth stating plainly rather than leaving implied:

- The successor spec's §3.4 claim that "all **25** window classes derive from
  `OverlayWindow`" is scoped to **`EyeCtrl.App`** and is correct as written — it is pinned
  by `OverlayInventoryDriftTests` (which also holds §1's count to reflection) and by
  `OverlayFontTests.NoWindowClass_BypassesOverlayWindow`. Neither guard sees another
  assembly, by design.
- `FallenBackDialog` is what the user sees **when something has already gone wrong**. It is
  the one unthemed surface in the product that appears at the worst moment. Bringing the
  supervisor UI onto the tokens is a small, self-contained follow-up; it is recorded here so
  that it is a choice rather than a gap.

**No longer out of scope.** Earlier drafts excluded "new controls or behaviors,"
which would have barred the §4.1.3 halo. The halo is a rendering change and it is
in scope: it is the only fix for the transparent overlays, whose accent strokes
fall to 1.37:1 over light desktop content (§9.5). Under the §2 constraint that
dwell feedback be "instant, high-contrast, and unambiguous," shipping a palette
that leaves them unguaranteed would miss the point of the exercise.

**Was deferred; now delivered.** This paragraph originally read "Deferred, with reason":
corner-radius unification was *not* delivered by this pass, §4.2's three tokens were
insufficient for it (seven values in use against tokens covering `4`, `8` and `12`), the
`22` on `QuickActionsGridWindow.xaml:23` "would visibly break if forced to `8`", and `6`
versus `8` for key caps was an open question rather than an oversight. That deferral was
correct, and it named what closing it needed: decisions. (Its site citations have since
drifted — the `6`s it pointed at are now `KeyboardWindow.xaml.cs:195,496`,
`NoteWindow.xaml.cs:69,83`, `DwellComboBox.cs:140` and `DwellColorPicker.cs:122`; §3.3 above
carries the re-derivable inventory instead of a hand-kept list.)

**The decisions are made and the work is done.** See
`docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md`, which
extends the token layer with 12 non-colour primitives (4 radii, 1 family, 2 weights, 5 font
sizes) plus 5 derived values, migrates every literal site in `EyeCtrl.App` and
`EyeCtrl.Controls`, and closes the loop with a *whitelist* drift guard —
`RawGeometryLiteralTests` — that permits only values reaching the token surface, so an
unmigrated site fails the build instead of relying on a reviewer to spot it.

Three corrections this document owes to that work:

- **The predicted token name was wrong, in a way that mattered.** This section anticipated
  `CornerRadiusPill`. Measured headlessly, WPF clamps an over-large `CornerRadius` to an
  inscribed **ellipse**, not a stadium: on a *square* element `CornerRadius = 9999` renders
  pixel-identically to `size / 2` (0 differing pixels at 30/44/45/66/100px and 96/144/192
  dpi), but on a 120×40 element the covered fraction is π/4 — the ellipse — not the
  stadium's ≈0.93. A token called *Pill* would therefore have invited a silently
  wrong ellipse on any non-square element. The token is **`CornerRadiusCircle`** (`9999`),
  documented square-only, and the WPF behaviour it rests on is pinned by
  `CornerRadiusClampTests` — including a check that the real 44×44 `CloseButton` carrying it
  still has `Width == Height` — so a future WPF change or a resize breaks a test rather than
  the UI. Coverage there is asserted as a band around π/4 at the alpha > 128 cutoff, never as
  a bare fraction: the measured value swings from 0.75 to 0.81 with the threshold alone.
- **`6` vs `8` resolved to `8`**, moving 11 sites. Neither value is principled by the
  concentric rule (a radius-8 root with 8px padding implies `0` for its child), so the
  tie-break was "fewer tokens" — which also removes §3.3's non-concentric nesting by
  construction.
- **The asymmetric-radius worry did not bite.** "A single `CornerRadius` resource cannot be
  partially overridden" is true in XAML and irrelevant here, because the app's only
  asymmetric site is C# (§3.3), and now reads
  `new CornerRadius(Tokens.CornerRadiusStandard, Tokens.CornerRadiusStandard, 0, 0)`.

**Two defects the successor plan's own census would have shipped**, recorded because both
were caught during implementation rather than review, and both are the same mistake — a size
treated as a constant when something upstream makes it a variable:

- **`DwellArcButton.Create` has three callers, not the two the census named.**
  `EdgeButtonBarWindow.xaml.cs:83` passes a *variable* radius —
  `EdgeButtonState.RadiusLogical`, defaulting to `UIConstants.DragButtonRadius = 20.0`.
  Under the prescribed `Small` ratio (0.23) the two live drag controls (`Drag_cancel` ✕ and
  `Drag_back` ↩, both built in `EventCoordinator`) would have gone from 22px to **9.2px** —
  a legibility regression delivered by a legibility change. Resolved by a size-conditional
  default: `Badge` (0.60) below a 64px diameter, giving **24px** at diameter 40; `Small` at
  or above it.
- **`KeySizeMultiplier` makes four glyph sizes runtime-variable.** It is user-facing, ranges
  `0.5`–`2.0` (`KeyboardSettings.cs:58`) and is offered for accessibility — so a
  container-proportional glyph in a `BaseKeySize = 70` cell is not sized once but per
  setting. At the `0.5` floor the three `Small` sites compute to **8.05px** and the `Medium`
  category tab to **11.20px**, all below the ramp's 12px floor, i.e. the setting could undo
  the accessibility improvement it exists to serve. Resolved by clamping each at
  `Tokens.FontSizeBody` — reading the token that *defines* the floor rather than a
  hand-written `12` (`KeyboardWindow.xaml.cs:192,454`; `EmojiGridManager.cs:335,471`).

Both are instances of what §6 already names in the colour context: an exclusion — or here, a
census — must be justified against every path into the file, not against its headline case.

---

## 9. Appendix: Measured Colour Metrics

All figures below are the output of `tools/palette_validate.py` (WCAG 2.1
relative luminance, CIEDE2000 perceptual distance, Viénot 1999 dichromat
simulation). Re-run it after any palette change:

```bash
python tools/palette_validate.py
```

### 9.1 Contrast — opaque core vs worst composited case

Two backgrounds matter. The **opaque core** `#1A1A2E` is what a token sits on
when the overlay is stacked on itself. The **worst composited** case is
`SurfaceDark` (`#FA1A1A2E`, 98%) over a white document — `#1E1E32` — which is
what actually happens when the overlay floats above a browser or editor. The
previous draft measured only the former, overstating every ratio.

| Token | Opaque core | Worst composited | Level (worst case) |
|---|---|---|---|
| `#D6DEF5` TextPrimary | 12.69 | **12.14** | AAA |
| `#A5F3FC` Accent | 13.67 | **13.07** | AAA |
| `#7EE787` AccentSuccess | 11.10 | **10.62** | AAA |
| `#E8A33C` AccentWarning | 7.91 | **7.56** | AAA |
| `#FF5C7A` AccentDanger | 5.74 | **5.49** | AA |
| `#9AA4C8` TextSecondary | 6.92 | **6.62** | AA (normal text) |
| `#868FB5` TextDim | 5.37 | **5.14** | AA (normal text) |
| `#616BA0` BorderSubtle | 3.35 | **3.20** | WCAG 1.4.11 non-text |
| `#00FF00` Lime (current) | 12.43 | — | AAA |

`AccentDanger` is the one token that does not reach AAA (5.49:1 worst case). It
is used exclusively on close/cancel glyphs at 22px and above, where AA is the
applicable threshold; raising it further would collapse the L\* gap that keeps
the semantic triad separable under red-green deficiency (§9.4). This is a
deliberate trade, not an oversight.

### 9.2 The accent swap does not regress — it dominates

Contrast against the opaque core, as each vision type perceives it:

| Vision | Lime `#00FF00` | Accent `#A5F3FC` | Δ |
|---|---|---|---|
| Normal | 12.43 | **13.67** | +1.24 |
| Protanopia | 14.20 | **14.60** | +0.40 |
| Deuteranopia | 11.52 | **13.20** | +1.68 |
| Tritanopia | 9.66 | **11.60** | +1.94 |

The accent is at least as visible as Lime for every vision type. For reference,
the earlier `#7DCFFF` proposal was *worse* than Lime on all four rows
(10.69 / 9.56 / 8.28 against 14.20 / 11.52 / 9.66) — the claim that the two had
"nearly identical" contrast was incorrect, and the swap as originally specified
would have degraded the primary dwell signal.

### 9.3 Accent isolation (ΔE2000)

The accent must not be confusable with anything that carries meaning. Floor is
25 for meaning-bearing tokens, 15 for text (separated by form — the accent draws
only geometry, never glyphs).

| Against | ΔE2000 | Floor |
|---|---|---|
| AccentSuccess | 29.0 | 25 |
| AccentWarning | 42.3 | 25 |
| AccentDanger | 61.0 | 25 |
| ToggleOn | 35.6 | 25 |
| BorderSubtle | 42.1 | 25 |
| SurfaceKeySwitch | 51.8 | 25 |
| TextSecondary | 26.9 | 15 |
| TextPrimary | 20.0 | 15 |

`TextPrimary` at 20.0 is the tightest pair. ΔE 25 is unreachable between a bright
accent and near-white text — for two colours of near-equal lightness the CIEDE2000
lightness term vanishes and the achievable distance plateaus around 20 regardless
of hue choice. 20.0 is nearly nine times the just-noticeable difference (≈2.3), and
the two never appear in the same role.

### 9.4 Semantic triad under colour-vision deficiency

Staged by lightness so the triad does not depend on hue:

| Token | L\* | ΔE prot | ΔE deut | ΔE trit |
|---|---|---|---|---|
| AccentSuccess `#7EE787` | 83.6 | — | — | — |
| AccentWarning `#E8A33C` | 72.0 | 13.3 | 10.8 | 58.6 |
| AccentDanger `#FF5C7A` | 61.9 | 33.9 (vs success) | 12.4 (vs success) | 58.3 (vs success) |

Success↔warning ΔL\* = 11.6, success↔danger 21.7, warning↔danger 10.1 — all above
the 9 L\* floor. Compare the previous draft, where `AccentSuccess` and
`AccentWarning` sat at **ΔE 1.1** under deuteranopia: indistinguishable.

**Residual limit, stated plainly:** `AccentWarning` and `AccentDanger` still
collapse under tritanopia (ΔE 1.6), separated only by 10.1 L\* / 1.38:1. Tritanopia
is rare (≈0.01%) and the blue-yellow axis cannot carry both an amber and a red,
so this is not solvable within a three-colour triad.

**Therefore: semantic colour is never the sole carrier of meaning.** Every use of
the triad must be accompanied by a distinct glyph (✓ / ⚠ / ✕). The app already
uses Unicode symbols throughout the toolbar and action menu, so this is a
constraint on new code rather than a change to existing UI.

### 9.5 Transparent overlays — why `StrokeHalo` is required

Contrast of a single-stroke ring against the desktop it floats on:

| Backdrop | Lime (current) | Accent (alone) | Accent + `StrokeHalo` |
|---|---|---|---|
| Black | 15.30 | 16.83 | **15.78** |
| Mid grey `#808080` | 2.88 | 3.16 | **15.78** |
| White document | 1.37 | 1.25 | **19.69** |
| Cyan-ish content | — | 1.33 | **15.78** |

A single stroke of *any* colour fails over mid-tone and light content; this is the
most serious unaddressed defect in the original draft, which treated it as a
palette question. With the halo, the visible feature is the accent/halo edge,
which is independent of the backdrop. Measured across nine backdrops the
worst-case edge never drops below **15.78:1**.

### 9.6 Layering

| Pair | Contrast | ΔE2000 |
|---|---|---|
| SurfaceDark → SurfaceMedium | 1.23 | 5.6 |
| SurfaceMedium → SurfaceLight | 1.29 | 6.2 |
| SurfaceKey → SurfaceKeyModifier | 1.39 | 9.3 |
| SurfaceKeyModifier → SurfaceKeySwitch | 1.29 | 9.8 |

Surface fill steps are deliberately modest. In the dark region ΔE2000 compresses
sharply — `#1A1A2E → #3C3C63` spans only 12.8 — so a large perceptual step
between adjacent surfaces is unachievable without abandoning the dark-only
constraint of §2. Layering is therefore carried by `BorderSubtle` at 3.20:1
(§9.1), with the fill providing a supporting cue. The keyboard ramp clears
ΔE 8 on every pair, which the previous draft's `SurfaceKeyModifier`/
`SurfaceKeySwitch` (ΔE 7.2) did not.

## 10. Appendix: Alternative Themes — "Ember" and "Contrast"

**Status:** proposals for a future theme system. **Nothing in §5 changes.** The
palette in §4.1 remains the sole shipping palette; this section adds two validated
alternative themes so the token layer can be built theme-ready rather than
retrofitted.

The §4.1 palette is now named **Midnight**. Both themes are validated by the same
script, which takes an optional theme name:

```bash
python tools/palette_validate.py Ember
```

### 10.1 Why warm

A second theme is only worth having if it does something the first cannot. A light
theme is excluded outright by §2 (eye trackers work poorly on bright surfaces), so
the useful axis is **colour temperature**, not lightness: Ember warms every
surface and text token, cutting short-wavelength emission across the large areas
that dominate exposure. That has a functional rationale in this product rather
than a decorative one — the app already ships an `EyeCtrl.EyeCare` subsystem for
break scheduling and eye strain, and a low-blue-light variant is the same concern
expressed in the palette.

The accent is the deliberate exception: it stays cool (§10.5). It covers a few
percent of lit pixels, so it barely affects exposure, and keeping it off the warm
axis leaves that axis free for the semantic triad.

Ember is the *comfort* axis. §10.7 adds **Contrast**, which is the *legibility*
axis and a different kind of theme again — situational rather than all-day. Beyond
those two there is no third axis worth the validation surface: every theme
multiplies it, and §9.4's glyph rule and §4.1.3's halo have to hold in each.

### 10.2 Palette

| Token | Midnight | Ember |
|---|---|---|
| `SurfaceDark` | `#FA1A1A2E` | `#FA211B14` |
| `SurfaceMedium` | `#FA2A2A44` | `#FA342B20` |
| `SurfaceLight` | `#FF3A3A5E` | `#FF473A2C` |
| `SurfaceKey` | `#2A2A3E` | `#2E2619` |
| `SurfaceKeyModifier` | `#3E3E64` | `#4E4029` |
| `SurfaceKeySwitch` | `#38527F` | `#63382A` |
| `SurfaceKeyActive` | `#5278B8` | `#9A6840` |
| `SurfacePrediction` | `#2F5340` | `#33422A` |
| `BorderSubtle` | `#616BA0` | `#7A6C51` |
| `BorderFocus` | `#A5F3FC` | `#9FE8FF` |
| `TextPrimary` | `#D6DEF5` | `#F2EEE8` |
| `TextSecondary` | `#9AA4C8` | `#BFB4A2` |
| `TextDim` | `#868FB5` | `#A0917C` |
| `MarkerMove` | `#A8A0D0` | `#B6A3DC` |
| **`Accent`** | **`#A5F3FC`** | **`#9FE8FF`** |
| `StrokeHalo` | `#0A0A14` | `#0E0A06` |
| `AccentSuccess` | `#7EE787` | `#7EE787` (shared) |
| `AccentWarning` | `#E8A33C` | `#E8A33C` (shared) |
| `AccentDanger` | `#FF5C7A` | `#FF5C7A` (shared) |
| `ToggleOn` | `#4A8F5C` | `#67874C` |
| `ToggleOff` | `#2E2E42` | `#342C21` |
| `ModalBackdrop` | `#B3000000` | `#B3000000` |
| `BorderThicknessStandard` | `1` | `1` |

Opaque surface core `#211B14` (L\* 10.2) is deliberately matched to Midnight's
`#1A1A2E` (L\* 10.2), so the two themes sit at the same perceived depth and only
temperature changes.

### 10.3 Measured results

All ten gates pass. Worst composited surface `#251F19`.

| Gate | Midnight | Ember |
|---|---|---|
| TextPrimary (composited) | 12.14 | 14.10 |
| TextSecondary | 6.62 | 7.97 |
| TextDim | 5.14 | 5.30 |
| BorderSubtle | 3.20 | 3.17 |
| Accent — normal | 13.67 | 12.55 |
| Accent — protanopia | 14.60 | 13.33 |
| Accent — deuteranopia | 13.20 | 12.12 |
| Accent — tritanopia | 11.60 | 10.69 |
| Toggle on/off (worst dichromacy) | 3.26 | 3.30 |
| Halo edge, worst backdrop | 15.78 | 14.52 |
| Triad ΔL\* (min pair) | 10.08 | 10.08 (shared triad) |

### 10.4 Gate 3, restated

Midnight's rule 4 was "the accent must beat Lime in every dichromacy" — a
no-regression proof valid only while Lime was the incumbent. For a theme system it
is restated as an **absolute floor of 9.5:1 in normal vision and all three
dichromacies**, which is the bar Lime itself set at its worst (9.66 under
tritanopia). Midnight's outcome is unchanged: its worst case is 11.60. §9.2's
Lime comparison stands as the evidence for the original swap.

### 10.5 Where Ember stands — and a corrected claim

**Correction.** An earlier draft of this section claimed that Ember's
`AccentDanger` was stuck at AA-large (4.12:1) and that this was *structural* —
"no warm-accent arrangement keeps danger at AA while satisfying gates 4 and 5."
**That was wrong.** It was tested by sweeping accent hue across the circle with
warm surfaces and warm text held fixed, and arrangements satisfying every gate
exist well above AA. The cause was a choice, not a constraint: spending the warm
hue axis on the accent left the semantics competing with it for ΔE 25. Moving the
accent off that axis frees it entirely.

Ember therefore uses a **cool accent on warm surfaces** (`#9FE8FF`), and its
semantic triad is now identical to Midnight's. Measured difference between the
two themes after the change:

| | Midnight | Ember |
|---|---|---|
| `AccentDanger` | 5.74 (AA) | **5.74 (AA)** |
| `AccentWarning` | 7.91 (AAA) | **7.90 (AAA)** |
| `AccentSuccess` | 11.10 (AAA) | **11.10 (AAA)** |
| Accent, worst vision type | 11.60 | 10.69 |
| Halo edge, worst backdrop | 15.78 | 14.52 |

The only residual gap is accent headroom under tritanopia (10.69 vs 11.60) and a
slightly weaker halo edge — both far above their floors (9.5 and 7.0). **Ember is
not meaningfully weaker than Midnight.**

**Why a cool accent does not defeat the point of the theme.** The accent is
small-area — rings, arcs, progress bars, a few percent of lit pixels. Short-
wavelength exposure is dominated by *surfaces*, which remain warm. The low-blue-
light rationale of §10.1 survives essentially intact, and a warm charcoal UI with
a single cool accent is a deliberate combination rather than a compromise.
`#9FE8FF` rather than Midnight's `#A5F3FC` keeps the two themes distinguishable.

**Semantic colours are shared across themes, by design.** Success, warning and
danger are *functional* — they encode outcome, not brand — so they do not vary by
theme. Theme identity lives in surfaces, text and the accent. This also means
§9.4's measurements and the glyph rule transfer unchanged, and there is one fewer
triad to re-validate per theme.

The §9.4 tritanopia limitation applies identically, since the triad is the same:
warning and danger collapse at ΔE 1.6, so the glyph rule (✓ / ⚠ / ✕) is not
optional in either theme.

### 10.5.1 Gate 9 came out of this

The hue sweep above initially produced "passing" arrangements with a near-white
`#F0D6D1` as *warning* (ΔE 10.3 from `TextPrimary`) and a pastel pink as *danger*.
Both satisfied rules 1–8 while ceasing to read as amber or red. Rules 1–8 never
required a semantic colour to be saturated enough to signify — so rule 9, a Lab
chroma floor of C\* ≥ 55 on the triad, was added (§4.1.1). Without it the
validator will certify palettes that are numerically sound and semantically
meaningless. It is scoped to the triad only; `Accent` is correctly low-chroma at
C\* ≈ 25 in both themes, being a luminance signal rather than a categorical one.

### 10.6 What a theme system requires structurally

**Status: implemented.** The four requirements below were recorded before Phase 0
and are now all satisfied in code; each bullet closes with what was built. See
`docs/superpowers/plans/2026-07-30-itheme-theme-system.md`.

- **`{StaticResource}` cannot switch at runtime.** §5 Phase 4–5 specify
  `StaticResource` throughout, which resolves once when a window loads. That is
  fine for a theme chosen at startup, and fine for overlays that are created and
  destroyed per use — but a live "switch theme now" affecting an already-open
  Settings or Keyboard window needs `{DynamicResource}`, or those windows must be
  recreated on change. **Decide restart-vs-live before Phase 4**, because
  converting the resource references afterwards touches every file in that phase.
  **Resolved — startup-applied.** The theme is chosen once in `App.OnStartup` via
  `ThemeStartup.Apply`; changing it takes effect on the next launch, and the
  picker says so. No `{DynamicResource}` conversion was needed, and none of
  Phase 4–5's files were touched. Recreating the 15 overlay singletons in place
  remains unbuilt.
- **`Tokens.cs` becomes a provider, not a set of constants.** A theme system needs
  the values behind an indirection (`ITheme` with a current-theme accessor), while
  the §5 design deliberately uses plain statics so `EyeCtrl.Controls.Tests` can
  run without a WPF `Application`. Both are satisfiable — keep the statics as the
  Midnight implementation of `ITheme` — but the interface should exist from
  Phase 0 rather than being introduced by a later refactor. Note it carries more
  than the 20 brushes: `BorderThicknessStandard` is theme-varying (§4.4), so
  `ITheme` needs at least one non-colour member from the start. **Resolved.**
  `ITheme` + `ThemeBase` + three theme classes (a fourth, Daylight, joined later — §10.8); `Tokens` keeps its 45-member
  static surface as forwarding properties, so all 131 C# call sites and 106 XAML
  lookups were unchanged. `BorderThicknessStandard` is on the interface from the
  start. The statics-without-`Application` property is preserved —
  `Tokens.Current` defaults to Midnight with no resource lookup.
- **A theme setting means a fifth persisted colour-adjacent key.** `ThemeSettings`
  already exists in `EyeCtrlSettings`; a theme name belongs there, and per §7.1
  the user-chosen `CursorSettings.CircleColor` would then need to be
  theme-relative or explicitly theme-independent. **Resolved — and it cost three
  keys rather than adding one.** `ThemeSettings.Mode`, `.OverlayOpacity` and
  `.HighContrast` were persisted, gaze-reachable, and read by nothing; so was
  `CursorSettings.CircleHighContrast`, which was surfaced twice. All four were
  deleted and replaced by `ThemeSettings.Name`. The four persisted colours
  (`CircleColor`, `TargetColor`, `HighlightColor`, `TrailColor`) are **explicitly
  theme-independent**: they are user-chosen absolute values, and their defaults
  track Midnight. The halo constraint from §5 item 9 (presets ≥ 7:1 against
  `StrokeHalo`) must be re-checked per theme — Ember's halo is `#0E0A06`, so the
  preset list is not automatically portable. **Re-checked, and it is portable
  after all.** All four shipping presets clear 7:1 against their OWN theme's
  halo (worst case across the four themes is still Midnight's Amber at 9.13:1;
  Daylight's closest is its clamped ring green at 9.52:1 vs its white halo,
  §10.8) — but a preset does NOT survive a theme switch: Midnight's accent
  measures 1.25:1 against Daylight's halo. The persisted haloed marks (ring,
  trail) are therefore re-derived at theme application; see §10.8 consequence 5.
  What was not portable is the *first* preset, which must be the theme's
  accent — so the list is now derived from the theme as `{Accent, green,
  Warning, TextPrimary}` (the green is `AccentSuccess` on the dark themes and
  a clamped darker value on Daylight, §10.8; the captions are role names, D6),
  which reproduces Midnight's four values exactly.

---

### 10.7 "Contrast" — the high-contrast theme

Situational, not a daily driver: for glare, a bright room, or tired eyes. Pure
white at 21:1 for hours is exactly what §4.1.2 rejects for `TextPrimary`. Its
pure-black surfaces are the friendliest of the three to the eye tracker, so it
costs nothing on that axis.

| Token | Value | Token | Value |
|---|---|---|---|
| `SurfaceDark` | `#FF000000` | `Accent` | `#00FFFF` |
| `SurfaceMedium` | `#FF141414` | `StrokeHalo` | `#000000` |
| `SurfaceLight` | `#FF262626` | `AccentSuccess` | `#00FF00` |
| `SurfaceKey` | `#0F0F0F` | `AccentWarning` | `#FFFF00` |
| `SurfaceKeyModifier` | `#123A63` | `AccentDanger` | `#FF7777` |
| `SurfaceKeySwitch` | `#4A4A4A` | `ToggleOn` | `#00FF00` |
| `SurfaceKeyActive` | `#FF8800` | `ToggleOff` | `#000000` |
| `SurfacePrediction` | `#004D22` | `TextPrimary` | `#FFFFFF` |
| `BorderSubtle` | `#FFFFFF` | `TextSecondary` / `TextDim` | `#E8E8E8` / `#C8C8C8` |
| `BorderFocus` | = `Accent` | `MarkerMove` | `#AA80FF` |
| `ModalBackdrop` | `#E6000000` | | |

All ten gates pass. Text 21.00 / 17.14 / 12.55; accent 16.75 normal and 11.89 at
its worst dichromacy; halo edge 16.75; `MarkerMove` 7.24 on surface and ΔE 37.5
from `TextPrimary`.

`MarkerMove` is the one token where high contrast is *not* achieved by pushing to
pure white. `#FFFFFF` would have been the obvious choice and was the original
value — but `BorderSubtle` and `TextPrimary` are both already `#FFFFFF` here, so it
collapsed three meanings into one colour and gate 10 rejects it. The violet is the
theme admitting that "maximum contrast" and "distinguishable" are different goals.

**Three gates pass for different reasons than in the other themes** — which is
what makes this theme structurally distinct rather than a re-hue:

1. **Gate 1 is carried entirely by the border.** Fill steps are ΔE 3.7 and 5.6 —
   effectively invisible — while `BorderSubtle` reaches **21:1** against a 3:1
   floor. Structure by outline, not by fill, which is the authentic HC idiom.
   Surfaces are also necessarily **opaque**: translucency and a contrast guarantee
   are incompatible. So "elevation raises opacity" reads a trivial 255→255 and the
   worst-composited case equals the surface itself.
2. **Gate 5 passes on lightness alone.** `#00FF00` and `#FFFF00` collapse to
   **ΔE 2.7 under protanopia**, against Midnight's 13.3 for the same pair. Maximum
   saturation pushes colours toward the confusion-line endpoints, so *the property
   that makes the theme high-contrast makes its hues less separable for CVD.* The
   pair survives only on ΔL\* 9.40 — a 1.28:1 luminance ratio. **§9.4's glyph rule
   is more load-bearing here, not less:** for a protanope, ✓ versus ⚠ is very
   nearly the only thing separating success from warning.
3. **Gate 9 is the binding constraint on danger.** `#FF0000` is only **5.25:1** on
   black — red is inherently dark. Raising it past 10:1 drops chroma below the
   C\* 55 floor (`#FF9999` is C\* 41.5), at which point it stops reading as red.
   `#FF7777` is the ceiling: 8.16:1 at C\* 57.6. So danger is the weakest colour
   *within* this theme (8.16 against 15–21) while being the **strongest
   `AccentDanger` of all four themes** (Midnight and Ember are 5.74; Daylight's is
   the weakest at 4.44 vs its core, §10.8).

**The triad is therefore NOT shared with Midnight and Ember.** That is a
consequence of the above, not an inconsistency — no high-contrast palette can use
`#FF5C7A` and still be high-contrast.

Two choices worth recording:

- **Keys are tinted, not outlined.** Gate 8 first *failed* with an achromatic
  ramp (ΔE 6.1 and 7.0 — near-blacks compress badly), so `SurfaceKeyModifier` and
  `SurfacePrediction` carry low-luminance blue and green tints. A strict HC
  reading would differentiate by border instead and need gate 8 restated as
  "distinguishable by fill ΔE ≥ 8 **or** by a ≥ 7:1 border."
- **The accent is fully saturated `#00FFFF`, not a brighter `#66FFFF`.** In this
  theme white is a *structural* colour, and the paler cyan sits only ΔE 24.6 from
  `BorderSubtle` — failing gate 4. HC constrains the accent more than the other
  themes do.

**`BorderThicknessStandard` is theme-varying** (`2` here, `1` elsewhere) — see
§4.4. It is the one non-colour token a theme overrides, which is why `ITheme` has
to carry more than the 20 brushes.

---

### 10.8 "Daylight" — the bright-room theme

**Status:** added 2026-10-09, fourth registered theme. Situational, like
Contrast but for the opposite environment: glare, sunlit rooms, white-desk
offices — the conditions under which three dark overlays read as heavy voids on
an already-bright screen. Review round 1 (2026-10-10, FIX-THEN-SHIP) processed:
the both-desktops validator rule, TextSecondary darkening, the persisted-mark
migration (consequence 5), the full decision ledger below, and corrected
thresholds and figures throughout. Review round 2 (2026-10-10, FIX-THEN-SHIP)
processed: the calibration overlay made theme-safe (cards + ring halos), resets
re-deriving haloed marks at runtime, D1's rationale corrected against the live
tracker, and ledger D8. Review round 3 (2026-10-10, FIX-THEN-SHIP) processed:
the persisted TargetColor finally applied + dot rimmed (D9), the third reset
path (Reset-ALL) migrated, cards moved to SurfaceDark, marker halos, and the
round-3 test pins. Review round 4 (2026-10-11, FIX-THEN-SHIP — "the Daylight
content itself is done") processed the r4 minors and MERGED the branch onto
current main (32952f11, the l10n line): the picker kept main's colour-only
shape with RingGreen inside it, the swatch captions became host string keys
(swatch.warning / swatch.text — D6 now lives in strings.json), the theme row
gained option.daylight and a true description, and the cancel-hint card kept
main's loc:T text. T10 German-draft obligations from this merge: re-key
swatch.amber → swatch.warning and swatch.light → swatch.text, ADD
option.daylight, and re-translate the theme description (the draft's
signed-off "Alle drei sind dunkel…" is now false) — see the hand-off note
in docs/release-notes/german-localization.md.

**The §2 ruling, reopened (owner decision D1).** "Dark-only backgrounds — eye
trackers work poorly on bright surfaces" is amended, not silently dropped. The
case for reopening is the comfort half of the ruling, which *inverts* in a bright
room: there, a dark UI is the glare hazard, and the white documents Daylight is
for are what the user is already staring at behind a 0xFA alpha either way. The
tracking half is NOT settled and must not be hand-waved: the app's own logs
(2026-10-09/10) show `provider="StreamEngine"` with a Tobii PCEye connected and
live gaze, so §2's concern applies directly and Daylight's effect on tracking
quality is **untested** — the revert A/B in the ledger's D1 row can be run
today. Removal is a `git revert` of the branch's commits — with the caveat the
ledger notes about stranded mark colours.

**The inversion principle.** Every contrast relation flips rather than shifts:

| Relation | Dark themes | Daylight |
|---|---|---|
| Accent vs surfaces | brightest thing on screen | darkest saturated thing |
| StrokeHalo | near-black | `#FFFFFF` white |
| Elevation ramp | darker → lighter | lighter → darker (white top step) |
| Text ramp | light text, dim = darkest | dark text, dim = lightest |
| Triad ladder | warning lightest | warning darkest (§10.8.1) |

Gate 7 is polarity-free — the guarantee is the accent/halo *edge*, and a dark
teal on a white halo clears it at 13.38:1 worst-case over the nine backdrops,
exactly as cyan on near-black does.

**Palette** (source of truth: `tools/palette_validate.py`, entry `"Daylight"`;
C# mirror `DaylightTheme.cs`, pinned by ThemeParityTests):

| Token | Value | | Token | Value |
|---|---|---|---|---|
| SurfaceDark | `#FAE7E8F0` | | MarkerMove | `#634C7D` |
| SurfaceMedium | `#FAF3F4F9` | | Accent | `#07343E` |
| SurfaceLight | `#FFFFFFFF` | | BorderFocus | `#07343E` |
| SurfaceKey | `#F0F1F7` | | StrokeHalo | `#FFFFFF` |
| SurfaceKeyModifier | `#CFDEF8` | | AccentSuccess | `#00620C` |
| SurfaceKeySwitch | `#E9D5EF` | | AccentWarning | `#7A1000` |
| SurfaceKeyActive | `#9FBDE6` | | AccentDanger | `#C5313B` |
| SurfacePrediction | `#D3EDD8` | | ToggleOn | `#1F6B3E` |
| BorderSubtle | `#667A9F` | | ToggleOff | `#D8DAE4` |
| TextPrimary | `#0C0D12` | | ModalBackdrop | `#B3000000` |
| TextSecondary | `#484D5E` | | _core | `#E7E8F0` |
| TextDim | `#5E6477` | | | |

Non-colour tokens identical to every other theme; `BorderThicknessStandard` 1.
The opaque core `#E7E8F0` is Midnight's `#1A1A2E` on the same blue-violet hue
axis at L\* 92.1 — the two defaults sit on one axis, at opposite ends.

**Measured results** (all ten gates pass; margins on the binding ones):

- Gate 2, under the both-desktops rule (review round 1): the translucent base
  composites over a white AND a black worst desktop, and each gate takes the
  worse per token — a light theme's binding surface is the black composite
  `#E2E3EB`, not the white one. TextPrimary 15.18 / TextSecondary 6.57 /
  TextDim 4.61; Dim's 0.11 of headroom on its 4.5 floor is the thinnest text
  margin of the four themes.
- Gate 3, the reason the accent is *darker than instinct*": 10.96 normal /
  **10.49 protanopia** / 11.19 deuteranopia / 11.09 tritanopia against the 9.5
  floor. Every step lighter loses protanopia first — the red-channel loss dims
  teal faster than it dims neutral text.
- Gate 4: smallest accent distances are ΔE 26.3 (MarkerMove, floor 25) and 16.6
  (TextPrimary, floor 15).
- Gate 5: pairwise ΔL\* 10.24 / 9.26 / 19.49 — see §10.8.1 for why the ladder
  runs warning-dark→success-light.
- Gate 8: smallest key-ramp pair ΔE 9.40 (Key/KeyModifier). The ramp tints
  (blue modifier, violet switch, green prediction) do the separating, as in
  Contrast — an achromatic light ramp compresses exactly like an achromatic
  dark one.
- Gate 9: chroma 56.59 / 57.01 / 65.51 against the 55 floor — the tightest
  triad of the four themes.
- Gate 10: MarkerMove clears its white-halo floor at 7.34 (floor 7); it is
  deliberately muted (C\* 31.5), because against this theme's low-chroma accent a
  *richer* violet tests closer, not further — CIEDE2000's cross-term cancels
  chroma difference at low average chroma.

**Structural consequences outside the palette:**

1. **`ModalButtonPalette` bright-kind labels become measured.** `AccentDanger`
   against the light base is 4.44:1 — under the 4.5 Negative is held to — and
   the gates pin danger's lightness from below (§10.8.1), so the *fill* cannot
   move. The label rule is now "whichever surface brush contrasts more with the
   fill", which resolves to the dark base in all three dark themes (preserving
   them exactly) and to white `SurfaceLight` in Daylight (5.42:1).
2. **The ring's green is not the semantic green.** Bi-tonal ring segments place the
   preset beside the halo with nothing underneath, so over a mid-light backdrop
   (`#FF5C7A`) each family must clear 3:1 alone — a bar no green satisfying gate
   9's chroma floor can reach (that needs L\* ≈ 31.8; this bar needs L\* ≤ 31.3;
   disjoint by 0.05 of contrast — full-sRGB enumeration puts the most saturated
   clearing green at C\* 54.28, review round 1). The dark themes never notice because their near-black halo
   carries the mid backdrops. `DwellColorPicker` therefore clamps a light theme's
   ring green to the darkest green that still reads green (`#005200`, C\* 52.5 —
   a ring decoration carries no gate-9 obligation), while `AccentSuccess` stays
   `#00620C` for filled surfaces. Bi-tonal worst case across all four themes:
   3.20:1, Daylight's ring green over `#FF5C7A`.
3. **TextSecondary and TextDim were near-twins — retired in review round 1.**
   The branch as first cut shipped `#5A6076` / `#5E6477` (ΔE 1.9) and called
   the pairing forced; review proved darker Secondary candidates clear gate 4's
   accent reservation, so it was a choice, not a constraint. Secondary is now
   `#484D5E`: ΔE 17.2 from the accent (floor 15), ΔE 8.2 from Dim, and the
   AAA-vs-SurfaceMedium pairing lifts from 5.67 to 7.65. Dim's 4.61 over the
   black worst-desktop is the binding text margin (see the gate-2 bullet).
4. **On Daylight the calibration map's shape grammar inverts.** The done
   markers read as white rings (halo rim dominant) and the upcoming ones as
   filled light discs (halo interior) — the opposite of the dark themes'
   filled-is-done convention, and the upcoming markers are the more salient
   pair, which happens to serve the map's purpose (removing the search).
   Both states are visible and distinguishable, which was the finding; no
   polarity-free single-ellipse fix exists (round 4, m1).
5. **Persisted haloed marks re-derive at theme application.** `CircleColor`
   and `TrailColor` are absolute hexes by the §7.1 ruling, so a theme switch
   carried the old theme's mark onto the new theme's halo — Midnight's accent
   on Daylight's white halo: 1.25:1, the gaze cursor invisible over the very
   documents Daylight exists for (review round 1, I1). `ThemeStartup.MigrateHaloedMarks`
   maps a persisted value that is a preset of ANY other theme to the same-index
   preset of the new theme (green stays green), keeps a custom value while it
   clears 7:1 against the new halo, and otherwise snaps to the setting's own
   default preset (the ring's is the accent, the trail's the warning — the
   trail is deliberately not accent-coloured). The mapping is symmetric, so
   switching back and forth is stable, and it is pinned by
   `ThemeStartupMarkMigrationTests`. One bound to know: no colour clears 7:1
   against both a dark and a light halo (the best possible is 4.44:1 against
   Midnight's halo and 4.58 against pure black), so a custom pick cannot
   survive a light↔dark switch — it resets to the default preset (ledger D8).
   ALL THREE settings resets re-run the same rule at runtime (round 2 N2 for
   cursor and advanced, round 3 R2 for Reset-ALL), so a reset on Daylight
   writes Daylight's presets, never Midnight's absolutes. `CalibrationSettings.TargetColor` and
   `AutoSelectionSettings.HighlightColor` stay absolute. The calibration dot is
   user-coloured and now draws on the scrim with a StrokeHalo rim — and its
   persisted value is finally APPLIED (D9; a latent main-branch bug) — so it
   needs no migration; the selection highlight remains an unhaloed mark over
   raw desktop content, backdrop-dependent on every theme alike.

**Owner decision ledger.** These decisions were taken while the owner was not
available to ratify them; each row is vetoable. D1's removal reverts the whole
feature (`git revert` of the branch's commits). Two consequences of a revert,
both easy to miss: a persisted `"Daylight"` theme name is repaired back to
Midnight by the settings loader's section repair, but the mark colours are NOT —
the migration reverts with the branch, so a Daylight ring/trail hex would be
stranded on Midnight's halo at 1.47:1 (the trail at 1.78:1) until the user
re-picks a swatch or drops the two keys from settings.json. (Switching back through the picker, by
contrast, maps everything back symmetrically — that path is fine.)

| # | Decision | Rationale | Undo |
|---|---|---|---|
| D1 | Reopen the light axis (§2 amendment) | The three prior themes assume a dim room; bright rooms are the uncovered case, and the glare argument for a light UI there is the positive case. The §2 tracking claim is **open, not refuted**: the tracker is LIVE (StreamEngine on a Tobii PCEye, per the app logs of 2026-10-09/10), so the claim applies directly and Daylight's effect on tracking quality is simply unmeasured — "overlays are a fraction of screen luminance" is asserted, not measured. | Revert the branch (see the stranding note above the table). **Revert trigger — runnable now:** compare TrackStatus validity/quality rates under Daylight vs Midnight across one session on the live tracker; measurable degradation reverts this row. |
| D2 | Picker order: Midnight, Ember, Daylight, Contrast | Default first, the all-day alternative next, then the situational ones — Contrast last as the boldest extreme. | Reorder `ThemeRegistry.All` + `ThemeSettings.ValidNames`. |
| D3 | The semantic triad ladder inverts (§10.8.1) | Forced jointly by the halo cap and the chroma floor; the alternative fails a gate. Consequence: TrackStatus "Fair"/"Poor" renders dark red vs red under Daylight — ΔL\* 19.5 carries the pair. | Not separately undoable without failing a gate; veto via D1. |
| D4 | Modal bright-kind labels are measured, not hardcoded | Daylight's `AccentDanger` on the light base is 4.44:1, under the 4.5 floor Negative is held to; all three dark themes resolve identically to the old rule. | Restore the hardcoded `SurfaceDark` labels and drop the pin test. |
| D5 | The ring's green ≠ `AccentSuccess` on Daylight | Bi-tonal segments need 3:1 alone over mid backdrops; no C\* ≥ 55 green reaches it (max 54.28). | Veto via D1, or give the ring its own token. |
| D6 | Preset captions are role names: "Light"→"Text", "Amber"→"Warning" | Visible on all four themes; a colour caption is wrong whenever a theme's token isn't that colour (Daylight's text is near-black, its warning a brick red — hue 41°, 13° from the danger red). | Since the l10n merge the captions are host-owned string keys: rename `settings.control.swatch.warning`/`.text` back to `.amber`/`.light` (texts "Amber"/"Light") in `strings.json`, `SettingsWindow.ColorSwatchNames`, and the name pin in `SettingsHostTextTests`. |
| D7 | TextSecondary/Dim near-twins | Originally shipped as "forced" and **retired in review round 1** by darkening Secondary to `#484D5E` — review proved it was a choice. | — (resolved). |
| D8 | The mark migration overrides §7.1's theme-independence for preset-valued colours | A persisted value that is exactly another theme's preset is almost certainly a stale default, not a deliberate choice — legacy pre-theme values included; only exact preset hexes map by index, so a stray `#00FF00` (a Contrast preset) maps to the same index, while arbitrary colours such as red or blue are nobody's preset and snap to the setting's default preset (the accent for the ring, the warning for the trail). That holds on the dark halos; blue clears Daylight's white halo and is kept there. Custom colours are kept while they clear 7:1 against the live halo; note that **no colour clears 7:1 against both a dark and a light halo** (best possible: 4.44 against Midnight's halo, 4.58 against pure black), so a custom pick resets on a light↔dark switch. | Drop `ThemeStartup.MigrateHaloedMarks` and its call sites; §7.1 then holds literally again (with I1's invisibility back). |
| D9 | `TargetColor` is now actually applied, and the dot carries a halo rim (round 3, R1) | The view model stored the persisted colour once without announcing it, so the window kept the theme accent forever — the saved colour never rendered, on any theme (a latent MAIN-branch bug this branch fixes). The rim keeps any pick visible on the black scrim under every theme. **Owner-visible on Midnight: the dot becomes your saved colour (amber `#E8A33C`) instead of cyan** — that is the setting working, not a palette change. | Revert; the latent bug returns with it. |

#### 10.8.1 The triad ladder inverts

Two app-side bars, both correct individually, squeeze the semantic triad from
opposite ends on a light surface:

- Success and warning are colour-picker presets and the cue-badge colour, so
  each must clear **7:1 against the white halo** — capping both at L\* ≈ 37.8.
- Gate 9 requires chroma C\* ≥ 55, and **a green below L\* ≈ 31.8 cannot hold
  it** (dark greens desaturate faster than dark reds; review round 1's
  full-sRGB enumeration puts the most saturated 3:1-clearing green at C\* 54.28
  — the two constraints miss by 0.05 of contrast, not comfortably).

So success and warning must both sit under L\* 38 while staying ΔL\* ≥ 9 apart —
possible only if one of them goes *very* dark, and the one that can is warning:
a dark BRICK RED keeps its chroma in the dark. Not ochre, whatever the first
draft said — hue 41°, thirteen degrees from the danger red (Midnight's warning
sits at 75°); this is why the picker caption is the role name "Warning" (D6). The resulting ladder — warning 25.3 /
success 35.5 / danger 44.8 — runs in the opposite order from every dark theme,
where warning is the *lightest* of the three. The pair to watch (printed by
the validator, deliberately not gated) is **success/warning: ΔE 4.4 under
deuteranopia**, against 10.8 on Midnight and Ember — the lightness gap
(ΔL\* 10.24) and §9.4's glyph shapes carry the distinction, as they already do
for Contrast's protanopia 2.7. Warning/danger, by contrast, separates at 17.4
under deuteranopia — better than Midnight's and Ember's 14.5 for that pair.
