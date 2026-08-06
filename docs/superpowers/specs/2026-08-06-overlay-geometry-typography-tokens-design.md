# Overlay Geometry and Typography Tokens — Design

**Date:** 2026-08-06
**Status:** Design, awaiting review
**Closes:** `docs/design/overlay-unification-spec.md` §8 "Deferred, with reason"
**Supersedes:** §4.2 and §4.3 of that spec, which proposed token sets that were never implemented
and are insufficient as written.

---

## 1. Problem Statement

The overlay unification pass delivered a validated three-theme colour system — 22 colours, 20
brushes and one theme-varying thickness, gated by ten accessibility checks. It deliberately did
**not** deliver corner radii or typography. §8 records why: the three radius tokens §4.2 proposed
cover `4`, `8` and `12`, while seven distinct radii are in use; the `22` on a 44px button "would
visibly break if forced to `8`"; and `6` versus `8` for key caps was an open question, not an
oversight.

That deferral was correct, and it named what closing it needs: decisions. This spec makes them,
and extends the scope to typography, which is more fragmented than the radii ever were —
**17 distinct font sizes across 67 sites**, against zero tokens.

Two properties of this application shape every decision below:

- **Geometry cannot affect usability here.** Gaze targets are registered as a centre point plus a
  physical radius (`DwellTarget`), never as a `Border`'s rendered geometry. No corner radius in
  this document can make anything harder to hit. This removes the accessibility dimension from
  the radius decisions and leaves them purely visual.
- **Text size *can*.** The user drives this machine solely by gaze and the product ships an
  eye-strain module. A type ramp that shrinks 9px captions further, or that grows text until it
  clips its container, is a real regression. §8 below therefore carries a per-container clip
  analysis rather than an assurance.

---

## 2. Scope

### In scope

| Area | Work |
|---|---|
| Corner radii | 4 tokens; migrate 30 literal sites |
| Font family | 1 token; migrate 1 literal; wire an app-wide default |
| Font weights | 2 tokens; migrate 17 sites |
| Font sizes | 6-token text ramp; migrate 46 text sites; delete 4 dead consts |
| Glyph/label sizing | 4 ratio constants; convert 14 container-proportional sites (2 more already correct) |
| Theme layer | `ITheme` / `ThemeBase` / `Tokens` / `OverlayStyles.xaml` / `ThemeResourceApplier` |
| Validator | `tools/palette_validate.py` — gate 1's non-colour tail |
| Guards | 1 new drift guard, 1 new behavioural pin, 3 existing tests extended, 1 generalised |
| Docs | `overlay-unification-spec.md` §3.3, §3.4, §4.2, §4.3, §8; the style guide HTML |

### Amends the parent spec's §8

The parent spec lists **"font size changes"** as out of scope. §8 of this document reverses that
deliberately: the type scale cannot be unified without changing some sizes, and freezing all 17
values as tokens would make a drift guard *enforce* the fragmentation. This is a scope decision
taken with the concern stated, not an oversight. Everything else §8 excludes stays excluded:
layout (margins, padding, control sizes), light mode, animation, settings file format, EyeCare
scoring UI, `EyeCtrl.Supervisor`, `EyeCtrl.Sdk.WinUI.Sample`.

### Explicitly out of scope

- **Layout.** Where the clip analysis in §8.5 shows a growth is unsafe, the token assignment
  changes — never the padding or the control size.
- **`EyeCtrl.Supervisor`.** It has one `CornerRadius="4"` at `UI/SupervisorSetupWindow.xaml:41`
  and 15 `FontSize`/`FontFamily` sites. The parent §8 excludes it and it is not in the guard's
  scanned projects.
- **A declared symbol font.** §7.4 records a genuine latent fragility found while measuring —
  22 of the app's UI symbols come from undeclared per-glyph font fallback — but fixing it means
  choosing a symbol face, which is its own decision.
- **Converging `BorderThicknessStandard` onto the derived-property form.** §6.5 explains the
  resulting inconsistency and why the working, guarded thickness path is left alone.
- **`CursorCircleWindow.xaml.cs`'s remaining `new SolidColorBrush`.** Needs a settings-keyed
  brush cache; unrelated to geometry or type.

---

## 3. Verified Current State

Verified on branch `claude/lucid-raman-cec339` (parent `2d3b1ae`). Where this contradicts earlier
notes, this section is the measurement.

### 3.1 Corner radii

**32 `CornerRadius` occurrences** in `src/EyeCtrl.App` + `src/EyeCtrl.Controls`, of which
**30 carry a literal value**. The other two — `ToolbarWindow.xaml.cs:81` and `:124` — consume the
`BorderCornerRadius` const declared at `:18`, so they are already single-sourced.

| Value | Sites | Locations |
|---|---|---|
| `2` | 1 | `Overlays/TargetHighlightWindow.xaml:10` |
| `4` | 4 | `Overlays/DwellFeedbackOverlay.xaml:7`, `Overlays/ToolbarWindow.xaml:9`, `Overlays/ToolbarWindow.xaml.cs:18` (const), + the two consumers |
| `6` | 11 | `Overlays/DriftNoticeWindow.xaml:15`, `Overlays/EmojiGridManager.cs:151,318,466`, `Overlays/KeyboardWindow.xaml.cs:195,496`, `Overlays/NoteWindow.xaml.cs:69,83`, `Overlays/QuickActionsGridWindow.xaml:47`, `Controls/DwellColorPicker.cs:122`, `Controls/DwellComboBox.cs:140` |
| `8` | 11 | `Overlays/EmojiGridManager.cs:430`, `Overlays/KeyboardWindow.xaml:9`, `Overlays/QuickActionsGridWindow.xaml:11`, `Overlays/TaskSwitcherWindow.xaml:22`, `Overlays/TrackStatusWindow.xaml:8`, `Views/SettingsWindow.xaml:20`, `Views/SettingsWindow.xaml.cs:284,408,1458`, `Controls/DwellButton.cs:72`, `Controls/DwellToggle.cs:71` |
| `12` | 2 | `Overlays/SplashWindow.xaml:8`, `Views/SettingsWindow.xaml:58` |
| `14` | 1 | `Overlays/EyeCareExerciseWindow.xaml:39` |
| `22` | 1 | `Overlays/QuickActionsGridWindow.xaml:23` |

Two facts the parent spec's §8 did not have:

- **`NoteWindow.xaml.cs:83` is asymmetric** — `new CornerRadius(6, 6, 0, 0)`, the note title bar.
  It is the only asymmetric site, and it is C#, not XAML.
- **`DwellComboBox.cs:140` inverts its nesting** — an outer `Border` at radius `6` wrapping a
  `DwellButton` at radius `8`, so the child's corner protrudes through the parent's.
  `DwellColorPicker.cs:122` similarly overrides `DwellButton`'s `8` down to `6`.

### 3.2 Typography

**No font token of any kind exists.** `Theme/Tokens.cs` and `Theme/OverlayStyles.xaml` contain no
`CornerRadius`, `FontFamily`, `FontSize` or `FontWeight` entry. `App.xaml` merges
`OverlayStyles.xaml` and sets no font default. §4.2 and §4.3 of the parent spec were never
implemented; this is greenfield token work, not a rename.

- **1 `FontFamily` literal:** `Overlays/CalibrationWindow.xaml:30` (`FontFamily="Segoe UI"`).
- **68 `FontSize` occurrences**, which decompose as:
  - **60 live literal sites** across **16 values** — 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24,
    28, 30, 32, 34, 36. Enumerated per site in Appendix A.
  - **4 dead consts** in `Views/SettingsWindow.xaml.cs` — `TabIconFontSize = 30` (:35),
    `TabSymbolFontSize = 22` (:37), `PaginationCounterFontSize = 24` (:38),
    `PaginationSymbolFontSize = 30` (:42). Each occurs exactly once in the file: its own
    declaration. Their values are duplicated as literals at `:273`, `:299`, `:382` and `:415`.
    An unused `private const` produces no compiler warning, which is why they survived. **They
    are deleted, not tokenised** — see §8.7.
  - **4 computed occurrences**, being 2 real sites: `ToolbarWindow.xaml.cs:70,113`
    (`_buttonSize * ButtonSymbolSizeRatio`) and `DwellArcButton.cs:41,115` (a defaulted
    parameter fed by the `DefaultSymbolFontSize = 22` const at `:15`, itself counted among the
    60).
- **17 `FontWeight` sites**, 2 values: `SemiBold` (9), `Bold` (8).

### 3.3 The token layer this builds on

`ITheme` already carries one non-colour value. Its own doc comment states the intent:
`BorderThicknessStandard` is theme-varying "so the interface is non-colour-capable from the
start rather than being widened later." The supporting machinery exists and is the pattern this
spec extends:

- `ThemeBase` derives brushes lazily from abstract colour properties.
- `ThemeResourceApplier.Apply` publishes brushes by reflection over `ITheme` — "so a token added
  to the interface cannot be forgotten here" — and the thickness by two hard-coded keys.
- `UniformThicknessExtension` converts the `double` token to a XAML-consumable `Thickness`,
  because WPF cannot convert a `double` resource to one.
- `TokenDictionaryTests`, `ThemeShapeTests`, `ThemeParityTests` and `ThemeResourceApplierTests`
  guard the result; `tools/palette_validate.py` excludes the thickness from colour gates via
  `NON_COLOUR_TOKENS` and checks it inside **gate 1**.

### 3.4 Every window derives from `OverlayWindow`

All **25** window classes in `EyeCtrl.App` derive from `Overlays/OverlayWindow.cs` — including
`Views/SettingsWindow` and the XAML-less `Overlays/NoteWindow`. This is load-bearing for §8.6:
one assignment in that constructor reaches the entire application UI.

---

## 4. Measurements

Every empirical claim in this spec comes from a headless WPF probe run on this machine
(WPF 10.0.0.0, Windows 11 Pro 26200) using `FormattedText`, `GlyphTypeface` and
`RenderTargetBitmap` on an STA thread. **No window was shown and no input was simulated.** The
probe is reproduced as two permanent tests in §10 so the results stay true rather than becoming
folklore.

### 4.1 WPF clamps an over-large `CornerRadius` to an inscribed ellipse

A `Border` at `CornerRadius = 9999` renders **pixel-identical** to one at `size / 2` when the
element is square:

| Element | Compared against | Differing pixels |
|---|---|---|
| 44×44 @ 96 dpi | radius 22 | **0** of 1936 |
| 45×45 @ 96 dpi | radius 22.5 | **0** |
| 44×44 @ 144 dpi | radius 22 | **0** |
| 44×44 @ 192 dpi | radius 22 | **0** |
| 66×66, 100×100, 30×30 @ 96 dpi | radius size/2 | **0** each |

On a **non-square** element it is an inscribed *ellipse*, not a stadium — the opaque area
converges on π/4 = 0.7854 of the box rather than a pill's much higher fraction:

| Element | Stadium (r = min/2) | `9999` | Inscribed ellipse |
|---|---|---|---|
| 120×40 | 0.9200 | 0.7671 | 0.7854 |
| 150×70 | 0.8915 | 0.7737 | 0.7854 |
| 80×44 | 0.8682 | 0.7665 | 0.7854 |

Also: `CornerRadius(-1)` throws `ArgumentException`; `double.MaxValue / 4` renders without
throwing.

**Consequence.** The token is a **circle** token, valid only on square elements. The parent §8
predicted the name `CornerRadiusPill`; that name would be actively misleading, because applying
it to a rectangle silently yields an ellipse. §5 decision 3 names it `CornerRadiusCircle` and
§10.2 pins this behaviour so a future WPF change breaks the build instead of the UI.

### 4.2 `Segoe UI Variable Text` resolves, and is a metric-compatible drop-in

| | Segoe UI | Segoe UI Variable Text |
|---|---|---|
| font file | `SEGOEUI.TTF` | `SEGUIVAR.TTF` |
| reported family / face | Segoe UI / Normal | Segoe UI Variable / **Text** |
| version | 5.71 | 2.03 |
| glyph count | 5394 | 2530 |

It is genuinely rendered, not silently substituted: **29–72 % of inked pixels differ** across
five strings at 9–34px, against **95.6 %** for a true control (Segoe UI vs Arial).

Advance widths and line heights are **identical to 0.01px** at 9, 10, 12, 14, 22, 24 and 34px —
a 0.0 % delta at every size. The line-height ratio is exactly **1.33 ×** the font size in both
families, which §8.5's clip arithmetic uses.

**Consequence.** The family swap cannot reflow, re-wrap or clip anything. It changes glyph shapes
only.

WPF also honours a comma-separated fallback list: `"Not A Real Font, Segoe UI"` resolves to
Segoe UI. An unresolvable *single* name falls back to **Arial**, not to the system font — which
is why declaring the chain has value.

### 4.3 The glyph-coverage drop costs nothing

Of 36 symbols taken from the source inventory, **0 are present in Segoe UI but missing from
Segoe UI Variable Text**. The reason is more interesting than the result: **22 of them are absent
from Segoe UI too** — ⏸ ⊕ ⌨ ⚙ ✓ ✕ ✊ ✋ ▶ ◀ ★ ☐ ☑ ↩ ↺ ↻ ⋯ ⚠ ✦ ◉ ﹢ and the astral
emoji. They already render through WPF's undeclared per-glyph font fallback. The family token
cannot affect them either way. See §7.4.

---

## 5. Decisions

| # | Question (parent §8) | Decision | Rationale |
|---|---|---|---|
| 1 | `6` vs `8` for key caps | **`8`** | 11 sites move. The concentric rule (child = parent − padding) gives `0` for a radius-8 root with 8px padding, so neither value is principled; fewer tokens wins. Also removes §3.1's nesting inversion by construction. |
| 2 | The unmapped `2` | **→ `4`** | One site. Real Windows controls use 2–4px, so a highlight ring still reads correctly at `4`. |
| 3 | The unmapped `14` | **→ `12`** | One site. The EyeCare summary card joins Splash and the Settings modal card, which is what `Large` is for. |
| 4 | The `22` pill | **`CornerRadiusCircle = 9999`** | Verified in §4.1 as pixel-identical to `size / 2` on squares, and therefore size-independent — a fixed `22` would silently duplicate the button's `44`. Named *Circle*, not *Pill*, because §4.1 shows non-square use gives an ellipse. |
| 5 | Theme-varying or invariant | **Theme-varying, on `ITheme`** | Extends the precedent `ITheme`'s own doc comment sets out. All three themes declare identical values today; the mechanism exists so a future `Contrast` revision can change geometry without touching a single overlay. Consequence in §11. |
| 6 | Contrast's values | **Identical to the others** | No measurement says high contrast wants different geometry, and `Contrast`'s structural claim is already carried by `BorderThicknessStandard = 2` and a 21:1 border. Changing an accessibility theme on aesthetic reasoning alone would be unjustified. |
| 7 | Where the validator checks them | **Gate 1's non-colour tail** | Follows the existing placement of `BorderThicknessStandard`. Keeps "ten gates" true across the spec, the style guide and test comments — no doc churn for a numbering change. |
| 8 | Font family value | **`"Segoe UI Variable Text, Segoe UI"`** | §4.2 verifies it resolves, renders differently, and is metrically identical, so it is a zero-risk change. The chain declares the degradation path, which matters because §4.2 shows the bare-name fallback is Arial. |
| 9 | Typography depth | **Family + weights + a real size ramp + a glyph-ratio rule** | A family-only pass would leave 17 sizes fragmented and simply re-create the deferral this document exists to close. |
| 10 | Slicing | **One combined spec and plan** | Chosen over splitting geometry from the type ramp. The trade-off is recorded in §11: the plan mixes provably-safe mechanical edits with changes that needed measurement to justify. |

---

## 6. Token Design

### 6.1 `ITheme` additions — 13 primitives

```csharp
// ── Geometry ────────────────────────────────────────────────────────────
double CornerRadiusSmall    { get; }   //  4  compact bars and thin outlines
double CornerRadiusStandard { get; }   //  8  the default: containers, keys, tiles, controls
double CornerRadiusLarge    { get; }   // 12  prominent cards and modal dialogs
double CornerRadiusCircle   { get; }   // 9999  fully round — SQUARE elements only (§4.1)

// ── Typography ──────────────────────────────────────────────────────────
string     FontFamilyStandard { get; } // "Segoe UI Variable Text, Segoe UI"
FontWeight FontWeightMedium   { get; } // SemiBold
FontWeight FontWeightStrong   { get; } // Bold

double FontSizeCaption   { get; }      // 10
double FontSizeBody      { get; }      // 12
double FontSizeBodyLarge { get; }      // 14
double FontSizeSubtitle  { get; }      // 18
double FontSizeTitle     { get; }      // 24
double FontSizeDisplay   { get; }      // 34
```

`CornerRadiusCircle`'s XML doc must carry the §4.1 restriction — that WPF yields an inscribed
ellipse on a non-square element — because the token is otherwise an invitation to misuse.

**Why family is a `string` but weight is a `FontWeight`.** The family's raw value *is* a string
concept — the fallback list has no typed equivalent — and the `FontFamily` object is worth
caching, so it splits into a raw value plus a derived object, exactly as colours split into
`Color` plus frozen `SolidColorBrush`. `FontWeight` is a cheap struct with a meaningful
`ToString()`, so splitting it would add machinery and remove clarity.

### 6.2 `ThemeBase` derivations — 5

```csharp
CornerRadius SmallCornerRadius    => new(CornerRadiusSmall);
CornerRadius StandardCornerRadius => new(CornerRadiusStandard);
CornerRadius LargeCornerRadius    => new(CornerRadiusLarge);
CornerRadius CircleCornerRadius   => new(CornerRadiusCircle);
FontFamily   FontStandard         => _font ??= new FontFamily(FontFamilyStandard);   // cached
```

Naming mirrors the established `BorderThicknessStandard` (raw `double`) /
`StandardBorderThickness` (WPF type) pair. `CornerRadius` is a struct, so the four need no
caching or freezing; `FontStandard` is cached on first access like the brushes, for the same
reason — overlays rebuild at gaze rate.

### 6.3 `Tokens` and `OverlayStyles.xaml`

`Tokens` gains a forwarding property per member, matching the existing file exactly.
`OverlayStyles.xaml` gains one `<x:Static>` entry per token — 13 primitives plus 5 derived
values — under the property names verbatim, which is what `{StaticResource}` lookups already use.

### 6.4 `ThemeResourceApplier.Apply` is generalised

Today it reflects over `SolidColorBrush` properties and hard-codes the thickness pair. It becomes:
publish **every `ITheme` property except `Name`** under its own property name, still by
reflection, plus the two existing thickness keys which are not `ITheme` properties. This preserves
the property the current doc comment claims — a token added to the interface cannot be forgotten
here — which hard-coding 18 new keys would quietly break.

### 6.5 No markup extension is needed, and the resulting inconsistency

`UniformThicknessExtension` exists because the derived `Thickness` was deliberately kept *off*
`ITheme`, leaving XAML with only a `double` it cannot convert. The radii do not have that problem:
their derived `CornerRadius` values are `Tokens` members, so a plain `<x:Static>` reads them —
the same mechanism `BrushAccentDim10` already uses.

This leaves **two different derivation styles in one dictionary**, which is a genuine
inconsistency and is recorded here rather than left for a reviewer to find. The thickness path is
not converged onto the new style because it works, it is drift-guarded, and rewriting it would put
a passing guard at risk for a cosmetic gain. Converging them is named as a follow-up in §2.

---

## 7. Radius Migration

### 7.1 The map — 30 sites

| Token | Value | Sites | Moves |
|---|---|---|---|
| `Small` | 4 | 4 | `TargetHighlightWindow.xaml:10` **2→4**. `DwellFeedbackOverlay.xaml:7`, `ToolbarWindow.xaml:9`, `ToolbarWindow.xaml.cs:18` unchanged in value. |
| `Standard` | 8 | 22 | All eleven `6` sites **6→8**. All eleven `8` sites unchanged in value. |
| `Large` | 12 | 3 | `EyeCareExerciseWindow.xaml:39` **14→12**. `SplashWindow.xaml:8`, `SettingsWindow.xaml:58` unchanged in value. |
| `Circle` | 9999 | 1 | `QuickActionsGridWindow.xaml:23` **22→9999**, rendering identically per §4.1. |

`ToolbarWindow.xaml.cs`'s `BorderCornerRadius` const is deleted; its two consumers read the token
directly.

### 7.2 The asymmetric site needs no exemption

`NoteWindow.xaml.cs:83` becomes:

```csharp
CornerRadius = new CornerRadius(Tokens.CornerRadiusStandard, Tokens.CornerRadiusStandard, 0, 0)
```

The parent §8 worried that "a single `CornerRadius` resource cannot be partially overridden". True
in XAML — and irrelevant, because the only asymmetric site is C#. No new resource, no documented
literal, and the asymmetry stays derived from the one token.

### 7.3 The nesting inversion resolves itself

`DwellComboBox.cs:140`'s outer `6` and `DwellColorPicker.cs:122`'s override both become
`Standard`, matching the `DwellButton` they contain. §3.1's inversion cannot recur once one token
serves both.

### 7.4 Recorded, not fixed: the app has no declared symbol font

§4.3 established that 22 of the app's UI symbols are absent from Segoe UI and render through
WPF's per-glyph fallback. Every toolbar icon, action-menu gesture glyph and note-button symbol is
therefore drawn in whatever face Windows selects, which can differ across machines and Windows
updates. This is out of scope — fixing it means choosing a symbol face (`Segoe Fluent Icons` is
the obvious candidate) and is a decision of its own — but it is a real fragility and should not
stay undocumented.

---

## 8. Typography Migration

### 8.1 Family

`FontFamilyStandard = "Segoe UI Variable Text, Segoe UI"`. `CalibrationWindow.xaml:30`'s literal
is deleted; the value arrives by inheritance from §8.6.

### 8.2 Weights

17 sites move to `FontWeightMedium` (SemiBold, 9 sites) or `FontWeightStrong` (Bold, 8 sites).
**Zero visual change** — the values are exactly today's.

### 8.3 The two kinds of site

The 60 live sites are not one population. **15 of them size a glyph or label to fit a container**,
and several of those containers change size at runtime. `ToolbarWindow` already models the correct
treatment with `_buttonSize * ButtonSymbolSizeRatio`. Snapping those onto a text ramp would resize
icons relative to the boxes they sit in — a visible regression, and backwards.

So the work splits: **46 text sites** take the ramp in §8.4, **16 container-proportional sites**
take the ratio rule in §8.5, and **4 dead consts** are deleted per §8.7. Appendix A classifies
every site individually and reconciles to 66 rows.

### 8.4 The text ramp — 46 sites

Six tokens. A deliberate **6px gap between 18 and 24**: a scale whose steps are 2px apart is a
list, not a scale, and reproducing 14/16/18 as three tokens would have the guard enforce the
fragmentation this document exists to remove.

| Token | px | Absorbs | Sites | Movement |
|---|---|---|---|---|
| `FontSizeCaption` | 10 | 9, 10, 11 | 15 | **9→10 raises 6**; 11→10 lowers 3; 10 unchanged at 6 |
| `FontSizeBody` | 12 | 12, 13 | 7 | 13→12 lowers 1; 12 unchanged at 6 |
| `FontSizeBodyLarge` | 14 | 14 | 3 | unchanged |
| `FontSizeSubtitle` | 18 | 16, 18 | 6 | 16→18 raises 3; 18 unchanged at 3 |
| `FontSizeTitle` | 24 | 22, 24 | 12 | 22→24 raises 7; 24 unchanged at 5 |
| `FontSizeDisplay` | 34 | 32, 34, 36 | 3 | 32→34 raises 1; 36→34 lowers 1; 34 unchanged at 1 |

Every movement is ±1 or ±2 px. Totals: **17 sites grow, 5 shrink, 24 unchanged** — 46.

`9 → 10` is the one change that improves rather than consolidates — it raises the smallest text in
the product, on a machine driven by the eyes. See §13.

### 8.5 The container-ratio rule — 16 sites

A `ContainerRatio` static class in `EyeCtrl.Controls` holds four ratios. **Named for containers,
not glyphs**, because the rule covers keyboard letters and prediction words as well as icons —
"glyph" would be inaccurate for those.

Each ratio is a value the existing code already converged on independently. That is the evidence
the rule describes practice rather than inventing one:

| Ratio | Value | Observed ratios it covers |
|---|---|---|
| `Dominant` | 0.45 | 0.455 — the glyph *is* the control |
| `Large` | 0.40 | 0.400, 0.375 — includes today's `ButtonSymbolSizeRatio = 0.4` verbatim |
| `Medium` | 0.32 | 0.344, 0.343, 0.314, 0.300 |
| `Small` | 0.23 | 0.233, 0.233, 0.229, 0.229, 0.220 — five sites |

Per-site results:

| Site | Container | Ratio | Today → New |
|---|---|---|---|
| `QuickActionsGridWindow.xaml:26` ✕ | 44 (fixed) | `Dominant` | 20 → 19.8 |
| `ToolbarWindow.xaml.cs:70` | 80 (setting) | `Large` | already 0.4 — **no change** |
| `ToolbarWindow.xaml.cs:113` | 80 (setting) | `Large` | already 0.4 — **no change** |
| `EmojiGridManager.cs:140` emoji cell | 70 (setting) | `Large` | 28 → 28.0 |
| `SettingsWindow.xaml.cs:415` pagination ◀▶ | 80 (`PaginationButtonHeight`) | `Large` | 30 → 32 |
| `EmojiGridManager.cs:322` category | 70 (setting) | `Medium` | 22 → 22.4 |
| `EmojiGridManager.cs:419` skin tone | 105 (setting × 1.5) | `Medium` | 36 → **33.6** |
| `EyeCareBannerWindow.xaml:30` ✕ | 64 (fixed) | `Medium` | 22 → 20.5 |
| `EyeCareBannerWindow.xaml:37` ▶ | 64 (fixed) | `Medium` | 22 → 20.5 |
| `SettingsWindow.xaml.cs:273` tab icon | 100 (`TabContainerHeight`) | `Medium` | 30 → 32 |
| `QuickActionsGridWindow.xaml:54` tile icon | 120 (fixed) | `Small` | 28 → 27.6 |
| `KeyboardWindow.xaml.cs:183` key label | `_keySize` 70 (setting) | `Small` | 16 → 16.1 |
| `KeyboardWindow.xaml.cs:441` prediction | `_keySize` 70 (setting) | `Small` | 16 → 16.1 |
| `EmojiGridManager.cs:455` switch label | 70 (setting) | `Small` | 16 → 16.1 |
| `NoteWindow.xaml.cs:257` button symbol | 60 (`NoteButtonRadius` × 2) | `Small` | 14 → 13.8 |
| `DwellArcButton.cs:15` default | 100 (`ButtonRadius` 50 × 2) | `Small` | 22 → 23.0 |

Largest change is **−2.4px** (skin tone 36 → 33.6); next is ±2 on the two Settings glyphs and
−1.5 on the banner pair. All others move under 0.5px.

Two mechanical notes:

- **`DwellArcButton.Create`'s `fontSize` is a defaulted parameter**, so it cannot reference the
  `radius` argument. It becomes `double? fontSize = null`, resolved inside the method to
  `radius * 2 * ContainerRatio.Small`. Callers passing an explicit size are unaffected.
- **`KeyboardWindow` and `EmojiGridManager` gain a behavioural fix.** `_keySize` is
  `BaseKeySize * keySizeMultiplier`, so today enlarging the keys produces bigger keys carrying
  the *same small letters*. Under the ratio rule the labels scale with the setting. This is the
  one accessibility improvement in the typography half beyond `9 → 10`.

**Three sites that look like glyphs but take the ramp**, because they have no container to be a
ratio of: `CalibrationWindow.xaml:36` (✓ at 32, centred in a full-screen overlay) →
`Display`; `DragTrailOverlay.xaml.cs:177` (`CreateIcon`, 16, no size set) → `Subtitle`, a +2px
growth flagged in §8.6; `KeyboardWindow.xaml.cs:545` (modifier indicator, 12) → `Body`,
unchanged. Forcing a ratio where there is no container would mean inventing one.

### 8.6 Clip analysis

The family swap needs no analysis: §4.2 measured identical advance widths and line heights, so no
string's box changes. Size growth does. Using the measured **1.33 × font size** line height
against container sizes read from source:

| Site | Container | Change | Line height | Verdict |
|---|---|---|---|---|
| `TrackStatusWindow` — 6 text sites | **fixed 160×110**, padding 8 | 10 → 10 | 13.3 unchanged | **Safe — nothing moves.** The only rigidly bounded window in the app, and no size in it changes. |
| `QuickActionsGridWindow.xaml:57` tile label | 120×120 tile, **wrapping** | 14 → 14 | unchanged | **Safe — unchanged.** The wrapping risk this analysis was written to catch does not materialise. |
| `DwellSlider.cs:87` value text | **fixed `Width = 140`** | 24 → 24 | unchanged | Safe — unchanged. |
| `EyeCareBannerWindow.xaml:32,39` "snooze"/"start" | 64×64 grid in a fixed 80px strip, `Margin="0,0,0,-16"` | 9 → 10 | 11.97 → 13.30 | **⚠ Flagged.** Already hangs below its grid by negative margin; grows 1.33px further. `Border` does not clip by default so it renders rather than truncates, but needs visual confirmation. |
| `DragTrailOverlay.xaml.cs:177` `CreateIcon` | none — no size set | 16 → 18 | 21.28 → 23.94 | **⚠ Flagged.** A gaze-drag affordance grows 2px. Needs visual confirmation; falls back to `BodyLarge` (14) if it reads too heavy. |
| `EyeCareBannerWindow.xaml:22` message | vertically centred in the 80px strip | 16 → 18 | 21.28 → 23.94 | Safe. The two-line stack totals ≈ 39px in 80px. |
| `DwellToggle`, `DwellLabel`, `DwellComboBox`, `SettingsWindow.xaml.cs:299` | auto-width, ≥ 70px tall | 22 → 24 | 29.26 → 31.92 | Safe. Width is auto so it grows; 31.92 < 70. |
| `EyeCareExerciseWindow` summary card | auto-sized, `Padding="28,18"` | 9→10, 13→12, 22→24 | grows | Safe — the card sizes to content. |
| `SplashWindow.xaml:14` title | `SizeToContent="WidthAndHeight"` | 36 → 34 | shrink | Safe. |
| `CalibrationWindow.xaml:36` ✓ | centred, full-screen overlay | 32 → 34 | grows 2px | Safe — unbounded. |
| `SettingsWindow.xaml.cs:273,415` glyphs | 100 / 80 px fixed containers | ratio | 30 → 32 | Safe — §8.5 sizes them from the container that bounds them, so the ratio cannot exceed it. |
| `KeyboardWindow`, `EmojiGridManager` labels | key/cell, setting-driven | ratio | ≈ unchanged | Safe, and fixes the multiplier bug in §8.5. |

**Two flagged sites**, both needing visual confirmation during implementation rather than a layout
change. Where a flagged site does not fit, **the token assignment changes** — padding and control
sizes stay untouched per §2.

Note what the ratio rule bought here: the two `SettingsWindow` glyphs were the highest-risk
entries when they were headed for `Display` (34), a +4px growth inside an 80px button. Sizing them
from their own container removes the risk instead of measuring it.

### 8.7 The four dead consts

`Views/SettingsWindow.xaml.cs:35,37,38,42` declare `TabIconFontSize`, `TabSymbolFontSize`,
`PaginationCounterFontSize` and `PaginationSymbolFontSize`, and nothing reads any of them. They
are **deleted**. Tokenising a dead const would preserve a value no code consults while making the
guard's whitelist report the file as clean.

Their live counterparts — the duplicated literals at `:273`, `:299`, `:382`, `:415` — are migrated
by §8.4 and §8.5 like any other site. The plan must delete the consts in the same task that
migrates those four literals, so the file never ends up with the const retained "just in case".

### 8.8 Application-wide wiring

`OverlayWindow`'s constructor sets `FontFamily = Tokens.FontStandard`. Per §3.4 all 25 window
classes derive from it, so this reaches the whole UI in one assignment. Three properties make this
the right mechanism:

- **No `OverrideMetadata` hazard.** Overriding `TextElement.FontFamilyProperty`'s metadata is
  process-wide and single-shot, which collides across tests in one runner.
- **Correct precedence.** The base constructor runs before the derived class calls
  `InitializeComponent`, so any XAML-set `FontFamily` still wins — a deliberate local override
  keeps working.
- **No `Application` required.** `Tokens` is static precisely so tests without a WPF
  `Application` can read it.

Font sizes and weights are set per element, not inherited, so they migrate site by site.

---

## 9. Validator Changes

`tools/palette_validate.py`:

1. **`NON_COLOUR_TOKENS`** grows from 1 name to 14: the thickness, 4 radii, 6 font sizes, 2 font
   weights and the font family. Without this, the token loop at line 295 would try to parse `"8"`
   as a colour.
2. **Each of the three theme dicts** gains 13 quoted entries, in the same quoted-string form
   `BorderThicknessStandard` already uses, so `ThemeParityTests.ParseTheme` keeps working
   unchanged.
3. **Gate 1's non-colour tail** gains, next to the existing thickness check:
   - every radius ≥ 0 — `CornerRadius(-1)` throws per §4.1, so this is a real failure mode
   - `CornerRadiusSmall ≤ Standard ≤ Large`
   - `CornerRadiusCircle ≥ CornerRadiusLarge` — the sentinel must outrank the real steps
   - `FontSizeCaption ≤ Body ≤ BodyLarge ≤ Subtitle ≤ Title ≤ Display`
   - **`FontSizeCaption ≥ 9`** — a genuine accessibility floor: no theme may set text smaller
     than today's smallest
   - `FontFamilyStandard` non-empty

The gate count stays at ten. `python tools/palette_validate.py` must exit 0.

---

## 10. Test Design

TDD throughout: each test is written failing first. xUnit v3 with **NFluent** (`Check.That`, never
`Assert.*`). `dotnet test` output is German — per-test failures read `Fehler <FQN>`, so
verification greps for that, never for "Failed".

### 10.1 Extended

| Test | Addition |
|---|---|
| `ThemeShapeTests` | Per theme: radius ordering and non-negativity; font-size ordering and the ≥ 9 floor; `FontStandard.Source == FontFamilyStandard`; `FontStandard` returns the same reference twice (cached, per §6.2). |
| `TokenDictionaryTests` | Each of the 13 primitives and 5 derived values resolves from `OverlayStyles.xaml` and equals its `Tokens` counterpart. Generalised over reflection rather than 18 hand-written cases. |
| `ThemeResourceApplierTests` | `Apply` publishes every `ITheme` property except `Name`. |

### 10.2 Generalised

`ThemeParityTests.BorderThickness_MatchesTheValidator` is one hand-written test for one token.
Adding 13 more by hand invites exactly the omission this suite exists to prevent. It becomes a
reflection-driven theory over **every non-`Color` `ITheme` property**, asserting the validator
declares it and the values agree — comparing `double` by parse, `string` directly, and anything
else by `ToString()`. The thickness stays covered as one case among many.

### 10.3 New — `CornerRadiusClampTests`

Pins §4.1, the WPF behaviour `CornerRadiusCircle` depends on. `[StaFact]`, headless
`RenderTargetBitmap`, no window shown, no input simulated, no `Category=UI` trait so it runs in
the CI subset.

- Square borders at 30/44/45/66/100 px and 96/144/192 dpi: `CornerRadiusCircle` renders
  pixel-identically to `size / 2`.
- A non-square border at `CornerRadiusCircle` differs from its stadium equivalent, with an opaque
  fraction near π/4 — pinning the *restriction*, so the token's documented limitation is enforced
  rather than merely written down.
- The real 44×44 `CloseButton` in `QuickActionsGridWindow.xaml`, loaded via pack URI, has
  `Width == Height` and carries the token. This is what stops a future resize silently turning
  the circle into an ellipse.

### 10.4 New — `RawGeometryLiteralTests`

The drift guard, in `EyeCtrl.App.Tests` alongside `RawColourLiteralTests`, scanning
`src/EyeCtrl.App` and `src/EyeCtrl.Controls`, with an `Exempt` dictionary requiring a specific
reason per entry and a companion test that fails on a stale exemption.

**It is a whitelist, and that is the point.** `RawColourLiteralTests` enumerates *bad* forms, and
commit `9e252e1` records the two holes that follow from it: `Color.From A? rgb` never matches
`FromRgb`, and the pattern requires a literal first argument so a computed one escapes even once
the casing is fixed. The second hole is not a typo — it is structural. Any blacklist has a form
its author did not think of.

So this guard inverts the test. It finds **every** occurrence of `CornerRadius`, `FontFamily`,
`FontSize` and `FontWeight`, then allows only those that reference the token surface:

- XAML — `"{StaticResource <known key>}"`
- C# — `Tokens.<known member>`, including inside `new CornerRadius(Tokens.…, Tokens.…, 0, 0)`
- C# — `ContainerRatio.<ratio>` multiplied by a size expression, for §8.5's sites

Everything else is an offender, including a computed argument. There is no forgotten form, because
forms are not enumerated.

Two further points taken from the existing guard's recorded weaknesses:

- **No `RegexOptions.IgnorePatternWhitespace`.** `RawColourLiteralTests:56-64` documents how that
  mode makes whitespace inside the pattern silently insignificant. The patterns here are written
  plainly instead.
- **Mutation-proved, not assertion-proved.** Following `9e252e1`'s method: a probe file containing
  each escaping form — a bare literal, a computed `new CornerRadius(x * 2)`, a raw
  `FontFamily="Arial"`, a literal `FontSize="13"` — is added temporarily, the guard is confirmed
  to fail and to *name the file*, and the probe is then deleted. A guard that has never failed is
  not known to work.

`StripComments`, `RepoRoot` and the file walk are extracted into one internal helper shared with
`RawColourLiteralTests`. Duplicating a comment stripper whose behaviour is subtly documented — the
`(?<!:)//` rule that keeps `pack://` intact — is how two guards drift apart.

### 10.5 New — font wiring

An `[StaFact]` constructs a bare `OverlayWindow` (never shown) and asserts its `FontFamily` is
`Tokens.FontStandard`. A companion test asserts the value differs from WPF's default, so the
assignment is proved to *do* something — the failure mode of a font token is being a silent no-op.

### 10.6 Verification gates

```bash
dotnet build EyeCtrl.slnx
```

```bash
python tools/palette_validate.py
```

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

The last one must include `EyeCtrl.App.Tests`, not just Core and ViewModels — CI runs that subset
and has previously caught stale App.Tests assertions. CI does **not** run on stacked PRs (its
trigger is `branches: [main]`), so local verification is the only gate here. `detect_changes()`
before committing, per `CLAUDE.md`.

No UI test is run and the app is not launched without explicit permission. Visual confirmation of
the two flagged sites in §8.5 is requested, not assumed.

---

## 11. Risks and Consequences

| Risk | Mitigation |
|---|---|
| **112 mechanical edit sites** — 30 radii, 46 font sizes, 17 weights, 14 ratio conversions, 1 family literal, 4 const deletions. The largest surface in this codebase's history of style passes. | The whitelist guard makes completeness machine-checked rather than reviewed. An unmigrated site fails the build. |
| **`CornerRadiusCircle` relies on undocumented WPF normalisation.** | §10.3 pins it across sizes and DPIs. A future WPF change breaks a test, not the UI. |
| **Two flagged clip sites** (§8.5). | Both are identified with the specific measurement needed. Where one does not fit, the token assignment changes — never the layout. |
| **The type ramp changes what the user reads all day.** | Every move is enumerated in §8.3 with its site count and direction, and the specimens are rendered at true scale in `docs/design/corner-radius-comparison.html` for approval by eye before implementation. |
| **`ITheme` grows from 23 members to 36 primitives + 5 derived.** It stops being a palette and becomes a design-system interface. | Consistent with its own stated intent to be "non-colour-capable from the start", and kept navigable by the `// ── section ──` grouping the file already uses. If it grows again, splitting a metrics interface out is the natural next step — noted, not done. |
| **Theme-varying costs three identical declarations per token** and 13 validator entries per theme, for values no theme currently differentiates. | Accepted deliberately (decision 5). §10.2's generalisation absorbs the per-token test cost so the marginal price of the *next* token is zero. |
| **Combining geometry with the type ramp** means one plan mixes provably-safe edits with ones justified by measurement. | Accepted deliberately (decision 10). The plan orders tasks so the token layer and the radius migration land before any size change, keeping the risky work last and separately revertible. |
| **No visual regression tests exist**, and UI tests cannot be run unprompted. | Verification is headless tests, the measured probes in §4, and the comparison HTML. Stated plainly rather than papered over. |

---

## 12. Documentation Updates

| File | Change |
|---|---|
| `docs/design/overlay-unification-spec.md` §3.3 | Radius audit corrected: 30 literal sites over 7 values, not the 9-row table it lists. |
| … §3.4 | Typography audit corrected: 68 `FontSize` occurrences = 60 live literal sites over 16 values + 4 dead consts + the ratio mechanism; 17 `FontWeight` sites. The claim "everything inherits the WPF default (Segoe UI)" is replaced by §4.2's measurement, including that a bare unresolvable name falls back to Arial, not to the system font. |
| … §4.2 | Replaced by §6.1's four radius tokens. Records that the three proposed tokens were insufficient and why. |
| … §4.3 | Replaced by §8. Records the reversal of the font-size exclusion and its justification. |
| … §8 | "Deferred, with reason" becomes "Delivered" with a pointer here. The `CornerRadiusPill` prediction is corrected to `CornerRadiusCircle` with §4.1's measurement as the reason. |
| `docs/design/overlay-unification-style-guide.html` | §2 Typography gains the ramp, weights and the family; §3 gains the four radius tokens; the deferral paragraph at `:419-425` is replaced. |
| `docs/design/corner-radius-comparison.html` | Kept as the decision record — it is the artefact the radius and family choices were approved from. |

---

## 13. Appendix A: Every `FontSize` Site, Classified

All paths relative to `src/`. `T` = text, takes the §8.4 ramp. `C` = container-proportional,
takes the §8.5 ratio. `D` = dead const, deleted per §8.7. Regenerate with:

```bash
grep -rn 'FontSize' --include=*.xaml --include=*.cs src/EyeCtrl.App src/EyeCtrl.Controls
```

| # | Site | Now | Kind | Becomes |
|---|---|---|---|---|
| 1 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:32` | 9 | T | `Caption` 10 ⚠ |
| 2 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:39` | 9 | T | `Caption` 10 ⚠ |
| 3 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:48` | 9 | T | `Caption` 10 |
| 4 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:52` | 9 | T | `Caption` 10 |
| 5 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:56` | 9 | T | `Caption` 10 |
| 6 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:59` | 9 | T | `Caption` 10 |
| 7 | `EyeCtrl.App/Overlays/NoteWindow.xaml.cs:87` | 10 | T | `Caption` — |
| 8 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:10` | 10 | T | `Caption` — |
| 9 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:18` | 10 | T | `Caption` — |
| 10 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:19` | 10 | T | `Caption` — |
| 11 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:22` | 10 | T | `Caption` — |
| 12 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:23` | 10 | T | `Caption` — |
| 13 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:46` | 11 | T | `Caption` 10 |
| 14 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:61` | 11 | T | `Caption` 10 |
| 15 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:24` | 11 | T | `Caption` 10 |
| 16 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:24` | 12 | T | `Body` — |
| 17 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:44` | 12 | T | `Body` — |
| 18 | `EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:545` | 12 | T | `Body` — |
| 19 | `EyeCtrl.App/Overlays/NoteWindow.xaml.cs:124` | 12 | T | `Body` — |
| 20 | `EyeCtrl.App/Overlays/NoteWindow.xaml.cs:139` | 12 | T | `Body` — |
| 21 | `EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml:25` | 12 | T | `Body` — |
| 22 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:42` | 13 | T | `Body` 12 |
| 23 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:43` | 14 | T | `BodyLarge` — |
| 24 | `EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml:57` | 14 | T | `BodyLarge` — |
| 25 | `EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml:35` | 14 | T | `BodyLarge` — |
| 26 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:29` | 16 | T | `Subtitle` 18 |
| 27 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:22` | 16 | T | `Subtitle` 18 |
| 28 | `EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs:177` | 16 | T | `Subtitle` 18 ⚠ |
| 29 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:40` | 18 | T | `Subtitle` — |
| 30 | `EyeCtrl.App/Overlays/DriftNoticeWindow.xaml:18` | 18 | T | `Subtitle` — |
| 31 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:22` | 18 | T | `Subtitle` — |
| 32 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:47` | 22 | T | `Title` 24 |
| 33 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:51` | 22 | T | `Title` 24 |
| 34 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:55` | 22 | T | `Title` 24 |
| 35 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:299` | 22 | T | `Title` 24 |
| 36 | `EyeCtrl.Controls/DwellComboBox.cs:112` | 22 | T | `Title` 24 |
| 37 | `EyeCtrl.Controls/DwellLabel.cs:32` | 22 | T | `Title` 24 |
| 38 | `EyeCtrl.Controls/DwellToggle.cs:60` | 22 | T | `Title` 24 |
| 39 | `EyeCtrl.Controls/DwellButton.cs:83` | 24 | T | `Title` — |
| 40 | `EyeCtrl.Controls/DwellSlider.cs:87` | 24 | T | `Title` — |
| 41 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:382` | 24 | T | `Title` — |
| 42 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:1465` | 24 | T | `Title` — |
| 43 | `EyeCtrl.App/Views/SettingsWindow.xaml:64` | 24 | T | `Title` — |
| 44 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:36` | 32 | T | `Display` 34 |
| 45 | `EyeCtrl.App/Views/SettingsWindow.xaml:23` | 34 | T | `Display` — |
| 46 | `EyeCtrl.App/Overlays/SplashWindow.xaml:14` | 36 | T | `Display` 34 |
| 47 | `EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml:26` | 20 | C | `Dominant` × 44 = 19.8 |
| 48 | `EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:70` | ratio | C | `Large` — already correct |
| 49 | `EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:113` | ratio | C | `Large` — already correct |
| 50 | `EyeCtrl.App/Overlays/EmojiGridManager.cs:140` | 28 | C | `Large` × 70 = 28.0 |
| 51 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:415` | 30 | C | `Large` × 80 = 32 |
| 52 | `EyeCtrl.App/Overlays/EmojiGridManager.cs:322` | 22 | C | `Medium` × 70 = 22.4 |
| 53 | `EyeCtrl.App/Overlays/EmojiGridManager.cs:419` | 36 | C | `Medium` × 105 = 33.6 |
| 54 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:30` | 22 | C | `Medium` × 64 = 20.5 |
| 55 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:37` | 22 | C | `Medium` × 64 = 20.5 |
| 56 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:273` | 30 | C | `Medium` × 100 = 32 |
| 57 | `EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml:54` | 28 | C | `Small` × 120 = 27.6 |
| 58 | `EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:183` | 16 | C | `Small` × `_keySize` |
| 59 | `EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs:441` | 16 | C | `Small` × `_keySize` |
| 60 | `EyeCtrl.App/Overlays/EmojiGridManager.cs:455` | 16 | C | `Small` × `keySize` |
| 61 | `EyeCtrl.App/Overlays/NoteWindow.xaml.cs:257` | 14 | C | `Small` × 60 = 13.8 |
| 62 | `EyeCtrl.Controls/DwellArcButton.cs:15` | 22 | C | `Small` × dia (23.0 at dia 100) |
| 63 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:35` | 30 | D | deleted |
| 64 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:37` | 22 | D | deleted |
| 65 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:38` | 24 | D | deleted |
| 66 | `EyeCtrl.App/Views/SettingsWindow.xaml.cs:42` | 30 | D | deleted |

**Totals: 46 T, 16 C, 4 D = 66 rows.** ⚠ marks the three clip-flagged sites from §8.6 (the
`EyeCareBannerWindow` pair counts as two).

**Reconciliation with the raw grep's 68 occurrences.** 66 rows + `DwellArcButton.cs:41` and `:115`,
which are the parameter default and its use — the mechanism for row 62's const, not separate sites.
Within the 66: 60 rows carry a live literal value, 4 are the dead consts, and 2 (rows 48–49) are
`ToolbarWindow`'s already-correct ratios.

**Edit count.** All 46 T rows need an edit even where the value is unchanged, because a literal
must become a token for the guard to pass. Of the 16 C rows, rows 48–49 already read a ratio and
need none, giving 14. Plus 4 deletions. That is the **46 / 14 / 4** in §2's scope table, and the
46 T reconciles with §8.4's per-token sum (15 + 7 + 3 + 6 + 12 + 3).

---

## 14. Open Question for Review

**The `9 → 10` caption raise is the only change here that improves legibility rather than merely
consolidating.** Six sites gain 1px. If a larger caption is wanted — 11 or 12 — that is a
different `FontSizeCaption` value and a different clip analysis for `EyeCareBannerWindow`'s
negative-margin labels, which are the one site already overflowing. Worth deciding before
implementation, not after.
