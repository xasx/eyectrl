# Overlay Geometry and Typography Tokens — Design

**Date:** 2026-08-06
**Status:** Reviewed — source-verified rounds 1 and 2 complete; corrections applied (§15, §16)
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
**16 distinct font sizes across 60 live sites**, against zero tokens.

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
| Font sizes | 5-token text ramp with a 12px floor; migrate 45 text sites; delete 8 dead consts (§8.7) |
| Glyph/label sizing | 5 ratio constants; convert 15 container-proportional sites (2 more already correct) |
| Layout | **One scoped exception:** `TrackStatusWindow.Height` 110 → 116 (§8.6) |
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

- **Layout, with exactly one measured exception.** `TrackStatusWindow.Height` 110 → 116, because
  the 12px ramp floor overflows it by 1.88px and it is the only window whose ramp-affected content
  overflows its fixed size (§8.6 carries the arithmetic). It is not the app's only fixed-size window
  — `QuickActionsGridWindow` (400×600), `EyeCareBannerWindow` (`Height="120"`) and
  `TaskSwitcherWindow` are all fixed or non-resizable — it is the only one that does not fit. Everywhere else, where a growth is unsafe **the token
  assignment changes** — never the padding or the control size. This exception is named here so it
  cannot expand quietly into a second one.
- **`EyeCtrl.Supervisor`.** It has one `CornerRadius="4"` at `UI/SupervisorSetupWindow.xaml:41`
  and 17 `FontSize`/`FontFamily` sites across two files (15 there, 2 in `UI/FallenBackDialog.xaml`). The parent §8 excludes it and it is not in the guard's
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
- **`DwellComboBox.cs:140` nests non-concentrically** — an outer `Border` at radius `6` wrapping a
  `DwellButton` at radius `8`. The child is cut *deeper* than its parent, so the child's corner
  recedes inside the parent's tighter one. An earlier draft called this the child "protruding
  through" the parent, which is backwards: protrusion would need child < parent, and `Border` does
  not clip in any case. The defect is the non-concentric nesting itself.
  `DwellColorPicker.cs:122` is a plain override of `DwellButton`'s `8` down to `6`, with no nesting
  involved.

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

On a **non-square** element it is an inscribed *ellipse*, not a stadium. Coverage is measured as
the fraction of pixels with **alpha > 128** — the 50 % cutoff, which is the correct way to count a
rasterised area and reproduces the analytic value exactly:

| Element | Stadium (r = min/2), measured | `9999`, measured | Inscribed ellipse, analytic |
|---|---|---|---|
| 120×40 | 0.9279 | **0.7854** | 0.7854 (π/4) |

(The stadium's analytic area is 0.9285; the 0.9279 above is what rasterises at this threshold. An
earlier draft printed the analytic 0.9285 in the measured column — a number computed, not observed,
sitting in a table of observations.)

**The threshold must be stated, because the number is meaningless without it.** An earlier draft
published 0.7671 for this cell and a reviewer independently measured 0.8094; both were correct for
their own cutoff, and neither matched π/4:

| alpha > | 0 | 128 | 200 | 254 |
|---|---|---|---|---|
| fraction | 0.8094 | **0.7854** | 0.7671 | 0.7527 |

§10.3 therefore asserts a tolerance band around π/4 at the 50 % cutoff, never a bare fraction.

Also: **assigning** a negative radius throws `ArgumentException` — the `CornerRadius(-1)`
*constructor* does not; the `Border.CornerRadius` dependency-property validate callback does. An
earlier draft attributed the throw to the constructor. The distinction matters for §9's gate, which
guards the value before it ever reaches a `Border`. `double.MaxValue / 4` renders without throwing.

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
| glyph count (`GlyphTypeface.GlyphCount`) | 5394 | 2530 |
| mapped characters (`CharacterToGlyphMap.Count`) | 3996 | 2417 |

It is genuinely rendered, not silently substituted: **29–72 % of inked pixels differ** across
five strings at 9–34px, against **95.6 %** for a true control (Segoe UI vs Arial).

Advance widths and line heights are **identical to 0.01px** at 9, 10, 12, 14, 22, 24 and 34px —
a 0.0 % delta at every size. The line-height ratio is exactly **1.33 ×** the font size in both
families, which §8.5's clip arithmetic uses.

**Consequence.** The family swap cannot reflow, re-wrap or clip anything. It changes glyph shapes
only.

WPF also honours a comma-separated fallback list: `"Not A Real Font, Segoe UI"` resolves to
Segoe UI.

**What an unresolvable *single* name does is genuinely undefined, and that is the point.** Measured
three ways, which disagree:

- `Typeface.TryGetGlyphTypeface` returns **False** — no glyph face resolves at all.
- `FontFamily.FamilyNames` reports **Arial**.
- Rendering at a fixed origin is pixel-identical to **nothing**: vs Arial 590 differing px,
  vs Segoe UI 535, vs Segoe UI Variable Text 631, vs Tahoma 651, for one 14px string.
  `SystemFonts.MessageFontFamily` is Segoe UI on this machine, but the render does not match it
  either.

An earlier draft of this spec claimed the fallback "is Arial", generalising the `FamilyNames`
reading into a rendering claim it had not tested. A reviewer then claimed it is Segoe UI, on the
grounds that 535 is the smallest of the four — unsound by the same standard, since 535 is not 0.
The defensible statement is that WPF composites a fallback matching no single installed face, so
**the degradation path is undefined rather than merely surprising.** That is a stronger reason to
declare the chain than the original claim was.

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
| 8 | Font family value | **`"Segoe UI Variable Text, Segoe UI"`** | §4.2 verifies it resolves, renders differently, and is metrically identical, so it is a zero-risk change. The chain pins Segoe UI as the fallback explicitly, which matters because §4.2 shows an unresolvable bare name produces a composite matching no single installed face — an undefined degradation path, not a predictable one. |
| 9 | Typography depth | **Family + weights + a real size ramp + a glyph-ratio rule** | A family-only pass would leave 17 sizes fragmented and simply re-create the deferral this document exists to close. |
| 10 | Slicing | **One combined spec and plan** | Chosen over splitting geometry from the type ramp. The trade-off is recorded in §11: the plan mixes provably-safe mechanical edits with changes that needed measurement to justify. |

---

## 6. Token Design

### 6.1 `ITheme` additions — 12 primitives

```csharp
// ── Geometry ────────────────────────────────────────────────────────────
double CornerRadiusSmall    { get; }   //  4  compact bars and thin outlines
double CornerRadiusStandard { get; }   //  8  the default: containers, keys, tiles, controls
double CornerRadiusLarge    { get; }   // 12  prominent cards and modal dialogs
double CornerRadiusCircle   { get; }   // 9999  fully round — SQUARE elements only (§4.1)

// ── Typography ──────────────────────────────────────────────────────────
string     FontFamilyStandard { get; } // "Segoe UI Variable Text, Segoe UI"
FontWeight FontWeightMedium   { get; } // SemiBold (600) — NB *not* FontWeights.Medium, which is 500
FontWeight FontWeightStrong   { get; } // Bold (700)

double FontSizeBody      { get; }      // 12  — the ramp floor; see §8.4
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

The four `CornerRadius` lines are on `Tokens`, not `ThemeBase` — `Tokens.Current` is typed
`ITheme`, so a `ThemeBase` member would be unreachable through it. Only `FontStandard` is a
`ThemeBase` member (see §6.6); `Tokens` then forwards it, as it does every other member.

Naming mirrors the established `BorderThicknessStandard` (raw `double`) /
`StandardBorderThickness` (WPF type) pair. `CornerRadius` is a struct, so the four need no
caching or freezing; `FontStandard` is cached on first access like the brushes, for the same
reason — overlays rebuild at gaze rate.

### 6.3 `Tokens` and `OverlayStyles.xaml`

`Tokens` gains a forwarding property per member, matching the existing file exactly.
`OverlayStyles.xaml` gains one `<x:Static>` entry per token — 12 primitives plus 5 derived
values — under the property names verbatim, which is what `{StaticResource}` lookups already use.

### 6.4 `ThemeResourceApplier.Apply` is generalised

Today it reflects over `SolidColorBrush` properties and hard-codes the thickness pair. It becomes:
publish **every `ITheme` property except `Name`** under its own property name, still by
reflection, **plus the five derived keys and the derived `StandardBorderThickness`, written
explicitly.** The reflection half preserves the property the current doc comment claims — a token
added to the interface cannot be forgotten here. The explicit half is not optional; §6.6 explains why.

**Exclude `Name` by name, not by type**, and publish only the non-`Color` properties and the
brushes. `FontFamilyStandard` is also a `string` on `ITheme`, so a type-based filter would drop it;
conversely "every property except `Name`" would publish 22 raw `Color`s under keys the dictionary
never declares and no XAML consumes.

**One correction to an earlier draft of this section:** it said "the two existing thickness keys
which are not `ITheme` properties". Only one of them is off-interface.
`BorderThicknessStandard` **is** on `ITheme` (`ITheme.cs:130`), so reflection publishes it;
`StandardBorderThickness` is the derived form and is written explicitly. That is exactly the
distinction §6.6 turns on, so describing it wrongly mattered.

Note which key carries which type, because two are easy to confuse: `FontFamilyStandard` is
published as the **raw string**, while the derived `FontFamily` object is **`FontStandard`**. A
consumer writing `{StaticResource FontFamilyStandard}` where it wants a `FontFamily` does not
silently receive a string — it throws `XamlParseException` at load, because no type converter runs on
a `StaticResource` assignment. `OverlayResourceResolutionTests` already catches that class of error.
`TokenDictionaryTests` must still assert both keys and both types.

### 6.6 The derived values must be published by `Apply`, or they freeze to Midnight

The five derived values in §6.2 are **not** on `ITheme` — they are `ThemeBase`/`Tokens` members, so
`Apply`'s reflection over the interface will not see them. Left there, they reach XAML only through
`{x:Static}`, which resolves **when WPF parses the dictionary** — during `Application` construction,
before `App.OnStartup` can choose a theme, with `Tokens.Current` still at its Midnight default
(`Tokens.cs:26`). A geometry-varying theme would then render **Midnight's radii and font in XAML
while C# read the active theme's through `Tokens`** — one token, two consumers, divergent values.

This is exactly the failure `ThemeResourceApplier` exists to prevent; its own doc comment
(`ThemeResourceApplier.cs:13-17`) says so, and it is why the derived `StandardBorderThickness` is
already written explicitly at `:53-55` rather than left to `{x:Static}`. **The five new derived keys
get the same treatment.** No current theme reveals the bug — all three declare identical geometry
(§5 decision 6) — but decision 5's whole rationale is that a theme *can* differ later, and the
mechanism would be broken the first time one did.

**The repo already enforces this.** `ThemeResourceApplierTests.Apply_CoversEveryKeyTheDictionaryDeclares`
(`ThemeResourceApplierTests.cs:71-89`) diffs `OverlayStyles.xaml`'s declared keys against the keys
`Apply` writes, and fails on any the applier misses — with a comment describing precisely this
freeze. Adding §6.3's five `<x:Static>` entries without extending `Apply` turns that test **red**.
§10.1 names it.

**Why not put the derived five on `ITheme`**, matching the derived *brushes*, which are on the
interface? Because §10.2 generalises the parity test over every non-`Color` `ITheme` property, and
that would demand `tools/palette_validate.py` entries for values that are pure restatements of the
primitives beside them. Keeping derivations off the interface holds §9 at 13 gated tokens and mirrors
the one derived value that already exists.

### 6.5 No markup extension is needed, and the resulting inconsistency

`UniformThicknessExtension` exists because the derived `Thickness` was deliberately kept *off*
`ITheme`, leaving XAML with only a `double` it cannot convert. The radii do not have that problem:
their derived `CornerRadius` values are `Tokens` members, so a plain `<x:Static>` reads them —
the same mechanism `BrushAccentDim10` already uses.

**This was verified, not reasoned about.** `CornerRadius` is a struct, exactly like `Thickness`,
so the analogy with the reference-type `BrushAccentDim10` was not sufficient evidence. A probe
built a `ResourceDictionary` containing `<x:Static x:Key="StandardCornerRadius" Member="…"/>` and
the `FontFamily` equivalent, then loaded a root element via `XamlReader.Load` binding
`Border.CornerRadius="{StaticResource StandardCornerRadius}"` and
`TextBlock.FontFamily="{StaticResource FontStandard}"`. Both resolved — `8,8,8,8` and
`"Segoe UI Variable Text, Segoe UI"` — with no throw, while the `Thickness`-from-`double` case
stayed a raw `double` as its own doc comment records. §10.1 covers both keys permanently.

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
is deleted; the value arrives by inheritance from §8.8.

### 8.2 Weights

17 sites move to `FontWeightMedium` (SemiBold, 9 sites) or `FontWeightStrong` (Bold, 8 sites).
**Zero visual change** — the values are exactly today's.

### 8.3 The two kinds of site

The 60 live sites are not one population. **15 of them size a glyph or label to fit a container**,
and several of those containers change size at runtime. `ToolbarWindow` already models the correct
treatment with `_buttonSize * ButtonSymbolSizeRatio`. Snapping those onto a text ramp would resize
icons relative to the boxes they sit in — a visible regression, and backwards.

So the work splits: **45 text sites** take the ramp in §8.4, **17 container-proportional sites**
take the ratio rule in §8.5, and **8 dead consts** are deleted per §8.7 — the 4 dead font-size ones
plus 4 dead layout ones found beside them. Appendix A classifies
every site individually and reconciles to 66 rows.

### 8.4 The text ramp — 45 sites

**Five tokens, with a 12px floor.** A deliberate **6px gap between 18 and 24**: a scale whose steps
are 2px apart is a list, not a scale, and reproducing 14/16/18 as three tokens would have the guard
enforce the fragmentation this document exists to remove.

| Token | px | Absorbs | Sites | Movement |
|---|---|---|---|---|
| `FontSizeBody` | 12 | 9, 10, 11, 12, 13 | 22 | **9→12 raises 6; 10→12 raises 6; 11→12 raises 3**; 13→12 lowers 1; 12 unchanged at 6 |
| `FontSizeBodyLarge` | 14 | 14 | 3 | unchanged |
| `FontSizeSubtitle` | 18 | 16, 18 | 5 | 16→18 raises 2; 18 unchanged at 3 |
| `FontSizeTitle` | 24 | 22, 24 | 12 | 22→24 raises 7; 24 unchanged at 5 |
| `FontSizeDisplay` | 34 | 32, 34, 36 | 3 | 32→34 raises 1; 36→34 lowers 1; 34 unchanged at 1 |

Totals: **25 sites grow, 2 shrink, 18 unchanged** — 45. Largest movement is **+3px** (9→12).

**Why the floor is 12 and not 10.** A 10px floor was the first proposal, and it merely consolidated:
`9 → 10` raised six sites by a pixel. **12 is a legibility decision, not a consolidation** — the
smallest text in the product goes from 9px to 12px, a 33 % increase, on a machine driven entirely
by the eyes and shipping an eye-strain module. It is the only change in this document that makes
the product materially easier to use.

`FontSizeCaption` therefore does not exist. At 12 it would have been identical to `FontSizeBody`,
and two named tokens holding one value is the same fragmentation in a different coat: the guard
cannot tell them apart and nothing stops them drifting. The ramp is five steps.

The cost is one layout change, to the one fixed-size window in the app — see §8.6 and §2.

### 8.5 The container-ratio rule — 17 sites

A `ContainerRatio` static class in `EyeCtrl.Controls` holds five ratios. **Named for containers,
not glyphs**, because the rule covers keyboard letters and prediction words as well as icons —
"glyph" would be inaccurate for those.

Each ratio is a value the existing code already converged on independently. That is the evidence
the rule describes practice rather than inventing one:

| Ratio | Value | Observed ratios it covers |
|---|---|---|
| `Badge` | 0.60 | 0.615 — a glyph in a circular badge under ~32px, where 0.45 would be illegible |
| `Dominant` | 0.45 | 0.455 — the glyph *is* the control |
| `Large` | 0.40 | 0.400, 0.375 — includes today's `ButtonSymbolSizeRatio = 0.4` verbatim |
| `Medium` | 0.32 | 0.344, 0.343, 0.314, 0.300 |
| `Small` | 0.23 | 0.233, 0.233, 0.229, 0.229, 0.229, 0.220 — six sites |

Per-site results:

| Site | Container | Ratio | Today → New |
|---|---|---|---|
| `DragTrailOverlay.xaml.cs:177` ✊/✋ | 26 (`CreateButtonBg` ellipse) | `Badge` | 16 → 15.6 |
| `QuickActionsGridWindow.xaml:26` ✕ | 44 (fixed) | `Dominant` | 20 → 19.8 |
| `ToolbarWindow.xaml.cs:70` | 80 (setting) | `Large` | already 0.4 — **no change** |
| `ToolbarWindow.xaml.cs:113` | 80 (setting) | `Large` | already 0.4 — **no change** |
| `EmojiGridManager.cs:140` emoji cell | 70 (setting) | `Large` | 28 → 28.0 |
| `SettingsWindow.xaml.cs:415` pagination ◀▶ | 80 (literal at `:406`) | `Large` | 30 → 32 |
| `EmojiGridManager.cs:322` category | 70 (setting) | `Medium` | 22 → 22.4 |
| `EmojiGridManager.cs:419` skin tone | 105 (setting × 1.5) | `Medium` | 36 → **33.6** |
| `EyeCareBannerWindow.xaml:30` ✕ | 64 (fixed) | `Medium` | 22 → 20.5 |
| `EyeCareBannerWindow.xaml:37` ▶ | 64 (fixed) | `Medium` | 22 → 20.5 |
| `SettingsWindow.xaml.cs:273` tab icon | 100 (literal at `:282`) | `Medium` | 30 → 32 |
| `QuickActionsGridWindow.xaml:54` tile icon | 120 (fixed) | `Small` | 28 → 27.6 |
| `KeyboardWindow.xaml.cs:183` key label | `_keySize` 70 (setting) | `Small` | 16 → 16.1 |
| `KeyboardWindow.xaml.cs:441` prediction | `_keySize` 70 (setting) | `Small` | 16 → 16.1 |
| `EmojiGridManager.cs:455` switch label | 70 (setting) | `Small` | 16 → 16.1 |
| `NoteWindow.xaml.cs:257` button symbol | 60 (`NoteButtonRadius` × 2) | `Small` | 14 → 13.8 |
| `DwellArcButton.cs:15` default | 100 (`ButtonRadius` 50 × 2) | `Small` | 22 → 23.0 |

Largest change is **−2.4px** (skin tone 36 → 33.6); then ±2 on the two Settings glyphs, −1.5 on the
banner pair, and **+1.0 on `DwellArcButton`'s default** (22 → 23.0), which reaches
`ActionMenuWindow.xaml.cs:187` and `GlassBackdropWindow.xaml.cs:224`. Every remaining site moves
under 0.5px.

Two mechanical notes:

- **`DwellArcButton.Create`'s `fontSize` is a defaulted parameter**, so it cannot reference the
  `radius` argument. It becomes `double? fontSize = null`, resolved inside the method to
  `radius * 2 * ContainerRatio.Small`. Callers passing an explicit size are unaffected.
- **`KeyboardWindow` and `EmojiGridManager` gain a behavioural fix.** `_keySize` is
  `BaseKeySize * keySizeMultiplier`, so today enlarging the keys produces bigger keys carrying
  the *same small letters*. Under the ratio rule the labels scale with the setting. This is the
  one accessibility improvement in the typography half beyond the `9 → 12` floor.

**`DragTrailOverlay` earned the fifth ratio, and finding it caught a latent bug.** An earlier
draft classified `CreateIcon` (`:177`) as text on the grounds that it sets no size — which is true
of the `TextBlock` but false of the layout. `PositionButton` (`:195-199`) centres the icon and the
26×26 `CreateButtonBg` ellipse on the *same* logical point:

```csharp
Canvas.SetLeft(bg,   logicalPos.X - 13);   // half of 26
Canvas.SetLeft(icon, logicalPos.X - 8);    // half of 16 — the font size, hard-coded
```

So the icon **is** bounded, at 16/26 = 0.615, and the ramp would have grown it to 18 inside a 26px
circle. Worse, the `-8` offset is half the font size written as a literal: any size change
**silently de-centres the glyph**, by 1px in each axis at 18. That is a defect the ramp would have
introduced and no test would have caught.

Under `Badge` the size becomes 15.6 (−0.4px), and **`PositionButton`'s offset must be derived from
the same size**: `-(size / 2)`, i.e. `-7.8`, replacing the literal `-8`.

Two points about that fix, because an earlier draft said only "derive from the icon's measured size",
which is both under-specified and subtly wrong:

- **Do not centre on the measured box.** `DesiredSize.Height` is 1.33 × the font size (§8.6), so
  offsetting by half of *that* would move the glyph ≈2.6px from where it sits today — a visible
  change presented as pure defect removal. Today's `-8` is half the *font size*, and `-(size / 2)`
  preserves that relationship exactly while making it track the token.
- **`DesiredSize` is unusable here in any case.** `PositionButton` (`:195-201`) is `static` and
  receives a `TextBlock` that has not been measured, so `DesiredSize` is `(0,0)` until layout runs.
  Deriving from the size constant needs no measurement pass at all, which is the second reason to
  prefer it.

`Draw`'s grab-icon offset (`:130-131`, a literal `-10` for the same glyph) needs the same derivation,
or one overlay ends up positioning its two icons by different rules.

This site therefore leaves §8.6's flag list entirely.

**Two sites genuinely have no container and take the ramp:**
`CalibrationWindow.xaml:36` (✓ at 32, centred in a full-screen overlay) → `Display`, and
`KeyboardWindow.xaml.cs:545` (modifier indicator at 12, in an auto-sizing `Border` with no
Width/Height) → `Body`, unchanged. Forcing a ratio where nothing bounds the glyph would mean
inventing a container.

### 8.6 Clip analysis

The family swap needs no analysis: §4.2 measured identical advance widths and line heights, so no
string's box changes. Size growth does.

**The line-height model is `1.33 × font size`, and it was verified against the right API.** The
ratio was first taken from `FormattedText.Height`, which is not what WPF uses to lay out a
`TextBlock`. A reviewer re-measured `TextBlock.DesiredSize.Height` after
`Measure(new Size(∞, ∞))` at 9/10/11/12/13/14/18/24/34 px and got exactly **1.3300** at every
size, so the two agree and every verdict below stands on the layout API rather than a proxy for it.

| Site | Container | Change | Line height | Verdict |
|---|---|---|---|---|
| **`TrackStatusWindow` — 5 sites** | **fixed 160×110**, `Padding="8"`, `ResizeMode="NoResize"` | 10 → 12 | 13.30 → 15.96 | **✗ Overflows by 1.88px.** Resolved by the window-height change below — the one scoped layout exception. |
| `NoteWindow.xaml.cs:87` "PINNED NOTE" | title bar `Height = 24`, `Margin(8,4,0,0)` | 10 → 12 | 13.30 → 15.96 | Safe. 15.96 + 4 = 19.96 in 24 — 4.04 slack. |
| `EyeCareBannerWindow.xaml:32,39` "snooze"/"start" | 64×64 grid in a fixed 80px strip, `Margin="0,0,0,-16"` | 9 → 12 | 11.97 → 15.96 | **Safe by measurement** — glyph ink keeps 5–7px of clearance from the dwell ring. See below; an earlier draft flagged this on a figure that measured the wrong thing. |
| `CalibrationWindow.xaml:46,61` hints | centred, full-screen overlay | 11 → 12 | 14.63 → 15.96 | Safe — unbounded. |
| `EyeCareBannerWindow.xaml:24` subtitle | in the 80px strip | 11 → 12 | 14.63 → 15.96 | Safe. Two-line stack ≈ 40px in 80px. |
| `EyeCareBannerWindow.xaml:22` message | vertically centred in the 80px strip | 16 → 18 | 21.28 → 23.94 | Safe, per the same stack. |
| `QuickActionsGridWindow.xaml:57` tile label | 120×120 tile, **wrapping** | 14 → 14 | unchanged | **Safe — unchanged.** The wrapping risk this analysis was written to catch does not materialise. |
| `DwellSlider.cs:87` value text | **fixed `Width = 140`** | 24 → 24 | unchanged | Safe — unchanged. |
| `TaskSwitcherWindow.xaml:25,35` | card, auto height | 12 → 12, 14 → 14 | unchanged | Safe. |
| `DwellToggle`, `DwellLabel`, `DwellComboBox`, `SettingsWindow.xaml.cs:299` | auto-width, ≥ 70px tall | 22 → 24 | 29.26 → 31.92 | Safe. Width is auto so it grows; 31.92 < 70. |
| `EyeCareExerciseWindow` summary card | auto-sized, `Padding="28,18"` | 9→12, 13→12, 22→24 | grows +9.31px (analytic) | Safe — the card sizes to content. |
| `SplashWindow.xaml:14` title | `SizeToContent="WidthAndHeight"` | 36 → 34 | shrink | Safe. |
| `CalibrationWindow.xaml:36` ✓ | centred, full-screen overlay | 32 → 34 | grows 2px | Safe — unbounded. |
| `SettingsWindow.xaml.cs:273,415` glyphs | 100 / 80 px fixed containers | ratio | 30 → 32 | Safe — §8.5 sizes them from the container that bounds them, so the ratio cannot exceed it. |
| `KeyboardWindow`, `EmojiGridManager` labels | key/cell, setting-driven | ratio | ≈ unchanged | Safe, and fixes the multiplier bug in §8.5. |

#### The one scoped layout change: `TrackStatusWindow.Height` 110 → 116

Measured content height against the 94px its `Padding="8"` leaves inside a 110px window:

| Caption size | "Track Status" + 4 | Canvas | Distance row + 4 | Quality row | Total | vs 94 |
|---|---|---|---|---|---|---|
| 10 (today) | 17.30 | 40 | 17.30 | 13.30 | 87.90 | 6.10 slack |
| 11 | 18.63 | 40 | 18.63 | 14.63 | 91.89 | 2.11 slack |
| **12** | 19.96 | 40 | 19.96 | 15.96 | **95.88** | **1.88 over** |

At 110px the bottom row clips by 1.88px, cutting the descender from "Quality" and from a value
such as "Unknown". **`Height` becomes 116**, giving 100px of content space and 4.12px slack.

This is a deliberate, single, named exception to §2's layout exclusion, taken because the
alternative is surrendering the legibility improvement over 1.88px. It is scoped tightly: one
property on one window, justified by the measurement above, and it changes nothing else. Two
alternatives were considered and rejected — an 11px floor (fits, but is an odd step in an even
ramp and would not merge with `Body`), and shrinking the 60×40 eye-dot canvas to 36 (buys text
size by shrinking a diagnostic the user reads to check tracker position).

Horizontal fit is unaffected: "Quality: Unknown" measures 78.18px at 10 and 93.82 at 12, against
144px available.

#### `EyeCareBannerWindow`'s "snooze"/"start" labels — resolved by measuring the glyphs, not the box

These sit in a 64×64 `Grid` whose `Ellipse` fills it, with
`VerticalAlignment="Bottom" Margin="0,0,0,-16"` — so the label's *bottom* is pinned 16px below the
grid and growing the font pushes its *top* upward, toward the ring.

An earlier draft flagged this site on the layout box alone:

| Size | Label box height | Ring bottom → **box** top |
|---|---|---|
| 9 (today) | 11.97 | 16 − 11.97 = 4.03px |
| 12 | 15.96 | 16 − 15.96 = **0.04px** |

0.04px reads alarming, and it is the wrong measurement — the box top is invisible. What matters is
the first inked row. Measured by rendering each `TextBlock` and scanning for it:

| Text | Size | Box height | First inked row | Ring bottom → **ink** |
|---|---|---|---|---|
| "snooze" | 9 | 11.97 | 5 | 9.03px |
| "snooze" | 12 | 15.96 | 7 | **7.04px** |
| "start" | 9 | 11.97 | 4 | 8.03px |
| "start" | 12 | 15.96 | 5 | **5.04px** |

"snooze" is all x-height so its ink starts 7px down; "start" has the `t` ascender so it starts 5px
down. Visible clearance therefore falls from 8–9px to **5–7px** — comfortable, not marginal. The
figures are identical in `Segoe UI Variable Text`, consistent with §4.2's metric identity.

**No site is left flagged.** Every clip verdict in the table above is now either "unchanged" or
measured. Where a future growth is unsafe, **the token assignment changes** — no further padding or
control-size edits beyond the one `TrackStatusWindow` height, per §2.

Note what the ratio rule bought here: the two `SettingsWindow` glyphs were the highest-risk
entries when they were headed for `Display` (34), a +4px growth inside an 80px button. Sizing them
from their own container removes the risk instead of measuring it.

### 8.7 The four dead consts

`Views/SettingsWindow.xaml.cs:35,37,38,42` declare `TabIconFontSize`, `TabSymbolFontSize`,
`PaginationCounterFontSize` and `PaginationSymbolFontSize`, and nothing reads any of them. They
are **deleted**. Tokenising a dead const would preserve a value no code consults while making the
guard's whitelist report the file as clean.

**Four more dead consts sit beside them**, found in round 2 when the spec was caught citing two of
them as if they were live: `TabContainerHeight = 100` (:36), `PaginationCounterMinWidth = 80` (:39),
`PaginationButtonWidth = 100` (:40) and `PaginationButtonHeight = 80` (:41). Each occurs once — its
own declaration — and the containers they appear to describe are literals at `:282` and `:405-406`.

These are layout values, not tokens, so they are outside §2's remit in every sense **except that
deleting them changes nothing**: no code reads them, so no layout moves. Removing four dead consts
while leaving four identical ones in the same file would be arbitrary, so all **eight** go in one
task. §8.5's ratio table now cites the literals that actually bound those glyphs.

Note that §10.4's guard cannot catch this class of staleness — it scans `CornerRadius`, `FontFamily`,
`FontSize` and `FontWeight`, not `Width`/`Height` literals. The dead layout consts were found by
reading, and a future one would be too.

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

1. **`NON_COLOUR_TOKENS`** grows from 1 name to 13: the thickness, 4 radii, 5 font sizes, 2 font
   weights and the font family. Without this, the token dict comprehension at lines 294-295 would
   try to parse `"8"` as a colour.
2. **Each of the three theme dicts** gains 12 quoted entries, in the same quoted-string form
   `BorderThicknessStandard` already uses, so `ThemeParityTests.ParseTheme` keeps working
   unchanged.
3. **Gate 1's non-colour tail** gains, next to the existing thickness check:
   - every radius ≥ 0 — `CornerRadius(-1)` throws per §4.1, so this is a real failure mode
   - `CornerRadiusSmall ≤ Standard ≤ Large`
   - `CornerRadiusCircle ≥ CornerRadiusLarge` — the sentinel must outrank the real steps
   - `FontSizeBody ≤ BodyLarge ≤ Subtitle ≤ Title ≤ Display`
   - **`FontSizeBody ≥ 12`** — a genuine accessibility floor, pinned at the ramp floor this
     spec chose. No theme may reintroduce text smaller than the 12px §8.4 settled on, which is
     the guarantee that makes the 9→12 raise permanent rather than one release's decision
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
| `ThemeShapeTests` | Per theme: radius ordering and non-negativity; font-size ordering and the **≥ 12** floor, matching §9's gate exactly — a C# floor of 9 would pass a theme the validator rejects, which is the disagreement §10.2 exists to prevent; `FontStandard.Source == FontFamilyStandard`; `FontStandard` returns the same reference twice (cached, per §6.2). |
| `TokenDictionaryTests` | Each of the 12 primitives and 5 derived values resolves from `OverlayStyles.xaml` and equals its `Tokens` counterpart. Generalised over reflection rather than 17 hand-written cases. |
| `ThemeResourceApplierTests` | `Apply` publishes every non-`Color` `ITheme` property, the brushes, and the six derived keys. **`Apply_CoversEveryKeyTheDictionaryDeclares` (`:71-89`) already enforces this** — it diffs the dictionary's declared keys against what `Apply` writes, so §6.3's five new `<x:Static>` entries turn it red until §6.6's tail is extended. It is the guard that catches the §6.6 freeze, and it exists today. |

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
  rather than merely written down. **Coverage is counted at alpha > 128 and asserted as a band
  (π/4 ± 0.01), never as a bare fraction** — §4.1 shows the value swings from 0.75 to 0.81 with the
  threshold alone, so a hard-coded fraction would encode the measurement method as if it were the
  behaviour.
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

**The known-token list is derived by reflection over `Tokens` and `ContainerRatio`, never
hard-coded.** A whitelist's own failure mode is its allow-list going stale: a hard-coded list of
member names would silently start rejecting a newly added token, or keep accepting a renamed one.
Reflecting over the two static classes means the guard cannot disagree with the token surface it
guards. (A misspelled `Tokens.` member is a C# compile error, and a misspelled XAML resource key is
already covered by `OverlayResourceResolutionTests`, so those two holes are shut elsewhere.)

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
any site §8.6 leaves open is requested, not assumed. As of round 2 there are none — §8.6's last
flag was resolved by measurement — so the only visual approval outstanding is the font specimens.

---

## 11. Risks and Consequences

| Risk | Mitigation |
|---|---|
| **116 mechanical edit sites plus one window height** — 30 radii, 45 font sizes, 17 weights, 15 ratio conversions, 1 family literal, 8 const deletions, `TrackStatusWindow.Height`. The largest surface in this codebase's history of style passes. | The whitelist guard makes completeness machine-checked rather than reviewed. An unmigrated site fails the build. |
| **`CornerRadiusCircle` relies on undocumented WPF normalisation.** | §10.3 pins it across sizes and DPIs. A future WPF change breaks a test, not the UI. |
| **Clip risk from the ramp.** | Every §8.6 verdict is now "unchanged" or measured; **no site is left flagged.** Where a future growth is unsafe, the token assignment changes — never the layout. |
| **The type ramp changes what the user reads all day.** | Every move is enumerated in §8.3 with its site count and direction, and the specimens are rendered at true scale in `docs/design/corner-radius-comparison.html` for approval by eye before implementation. |
| **`ITheme` grows from 23 members to 35 primitives + 5 derived.** It stops being a palette and becomes a design-system interface. | Consistent with its own stated intent to be "non-colour-capable from the start", and kept navigable by the `// ── section ──` grouping the file already uses. If it grows again, splitting a metrics interface out is the natural next step — noted, not done. |
| **Theme-varying costs three identical declarations per token** and 13 validator entries per theme, for values no theme currently differentiates. | Accepted deliberately (decision 5). §10.2's generalisation absorbs the per-token test cost so the marginal price of the *next* token is zero. |
| **Combining geometry with the type ramp** means one plan mixes provably-safe edits with ones justified by measurement. | Accepted deliberately (decision 10). The plan orders tasks so the token layer and the radius migration land before any size change, keeping the risky work last and separately revertible. |
| **No visual regression tests exist**, and UI tests cannot be run unprompted. | Verification is headless tests, the measured probes in §4, and the comparison HTML. Stated plainly rather than papered over. |

---

## 12. Documentation Updates

| File | Change |
|---|---|
| `docs/design/overlay-unification-spec.md` §3.3 | Radius audit corrected: 30 literal sites over 7 values, not the 9-row table it lists. |
| … §3.4 | Typography audit corrected: 68 `FontSize` occurrences = 60 live literal sites over 16 values + 4 dead consts + the ratio mechanism; 17 `FontWeight` sites. The claim "everything inherits the WPF default (Segoe UI)" is replaced by §4.2's measurement, including that an unresolvable bare name has an **undefined** degradation path — three measurements disagree and none identifies a single face — which is why the family token declares a chain. |
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
| 1 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:32` | 9 | T | `Body` 12 ⚠ |
| 2 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:39` | 9 | T | `Body` 12 ⚠ |
| 3 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:48` | 9 | T | `Body` 12 |
| 4 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:52` | 9 | T | `Body` 12 |
| 5 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:56` | 9 | T | `Body` 12 |
| 6 | `EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml:59` | 9 | T | `Body` 12 |
| 7 | `EyeCtrl.App/Overlays/NoteWindow.xaml.cs:87` | 10 | T | `Body` 12 |
| 8 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:10` | 10 | T | `Body` 12 |
| 9 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:18` | 10 | T | `Body` 12 |
| 10 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:19` | 10 | T | `Body` 12 |
| 11 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:22` | 10 | T | `Body` 12 |
| 12 | `EyeCtrl.App/Overlays/TrackStatusWindow.xaml:23` | 10 | T | `Body` 12 |
| 13 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:46` | 11 | T | `Body` 12 |
| 14 | `EyeCtrl.App/Overlays/CalibrationWindow.xaml:61` | 11 | T | `Body` 12 |
| 15 | `EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml:24` | 11 | T | `Body` 12 |
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
| 46 | `EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs:177` | 16 | C | `Badge` × 26 = 15.6 |
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

**Totals: 45 T, 17 C, 4 D = 66 rows.** No row carries a ⚠ any more: §8.6's last flag — the
`EyeCareBannerWindow` pair — was resolved by measuring glyph ink rather than the layout box.

Row ordinals are not contiguous and one number is used twice, both because round 1 moved
`DragTrailOverlay.xaml.cs:177` from T to C. **The row count reconciles; the ordinals are not
identifiers.**

**Reconciliation with the raw grep's 68 occurrences.** 66 rows + `DwellArcButton.cs:41` and `:115`,
which are the parameter default and its use — the mechanism for row 62's const, not separate sites.
Within the 66: 60 rows carry a live literal value, 4 are the dead consts, and 2 (rows 48–49) are
`ToolbarWindow`'s already-correct ratios.

**Edit count.** All 45 T rows need an edit even where the value is unchanged, because a literal
must become a token for the guard to pass. Of the 17 C rows, `ToolbarWindow.xaml.cs:70` and `:113`
already read a ratio and need none, giving 15. Plus 4 deletions. That is the **45 / 15 / 4** in §2's
scope table, and the 45 T reconciles with §8.4's per-token sum (22 + 3 + 5 + 12 + 3).

---

## 14. Resolved: the ramp floor is 12, not 10

Recorded because the reasoning matters more than the number, and because it is the one place this
spec accepts a layout change.

A 10px floor was proposed first and was **only a consolidation**: it raised six 9px sites by a
single pixel and lowered three 11px sites, netting almost nothing. **12 was chosen instead**, which
makes the smallest text in the product 33 % larger and is the only change in this document that
makes the application materially easier to use. It also collapsed `FontSizeCaption` into
`FontSizeBody`, taking the ramp from six tokens to five — two names holding one value would have
been the same fragmentation in a different coat.

The cost is one measured overflow. `TrackStatusWindow` is the only window whose ramp-affected
content overflows its fixed size, and at a 12px floor its content exceeds the 94px its padding
leaves by **1.88px**,
clipping the descenders on the bottom row. §8.6 carries the arithmetic. Resolution: **`Height`
110 → 116**, a single property on a single window, named in §2 so it cannot expand into a second
exception. Rejected alternatives: an 11px floor (fits with 2.11px slack, but is an odd step in an
even ramp and would not merge with `Body`), and shrinking the 60×40 eye-dot canvas to 36 (buys text
size by shrinking a tracking diagnostic the user reads to check head position).

**Still open for review, and not decidable from measurement:**

**`Segoe UI Variable Text`** was chosen from a description. §4.2 proves it is a metrically identical
drop-in that cannot clip anything, but it changes every glyph in the application. The specimens are
rendered at true scale in §9 of `docs/design/corner-radius-comparison.html` — worth looking at before
45 text sites move. **This is the only outstanding item.**

An earlier version of this section listed two ⚠ clip sites as open. Both are closed:
`EyeCareBannerWindow`'s labels were resolved by measuring glyph ink instead of the layout box
(§8.6), and `DragTrailOverlay`'s icon moved to a container ratio at 15.6px rather than growing to 18
(§8.5). This section went stale because round 1's corrections landed in §8.5 and §8.6 without
updating the cross-references — round 2 caught it in four places (here, §10.6, §11 and Appendix A's
legend).

---

## 15. Review Round 1 — What Changed

An independent reviewer verified this spec against source with their own headless WPF probe. Their
report is summarised here because two of my claims were wrong, one of their corrections was also
wrong, and one of their observations found a defect this spec would have introduced.

### Confirmed, and worth stating because they were the load-bearing risks

- **§6.5's `<x:Static>` mechanism.** Verified by probe: a `CornerRadius` struct and a `FontFamily`
  both resolve through `{StaticResource}` from a `ResourceDictionary`. This was the one thing I had
  argued by analogy rather than tested, and I flagged it as Priority 1 for exactly that reason. It
  holds — **no `UniformCornerRadiusExtension` is needed.**
- **§8.6's `TrackStatusWindow` arithmetic**, re-derived independently: four `StackPanel` children
  and no fifth, no `BorderThickness` on the `Border`, 95.88 total against 94 available, 4.12px slack
  at 116. The layout exception is justified.
- **The 1.33× line-height model**, re-measured against `TextBlock.DesiredSize.Height` rather than
  the `FormattedText.Height` proxy I used. Exactly 1.3300 at every size tested — so §8.6 now rests
  on the layout API, not a stand-in for it.
- **All inventories and reconciliations**: 30 radius literals, 68 `FontSize` occurrences over 16
  values, 17 `FontWeight`, the four dead consts, and every arithmetic sum.
- **§1's "radius cannot affect targeting"**, traced to `DwellTarget.cs:13-15` and `:91-98`
  (`DistanceSquaredTo(Center) <= Radius²`), and **§3.4's 25 `OverlayWindow` subclasses.**

### Corrected — two of my claims were wrong

1. **The font-fallback claim (§4.2, §5 decision 8).** I wrote that an unresolvable family name
   "falls back to Arial", generalising a `FontFamily.FamilyNames` reading into a rendering claim I
   never tested. The reviewer countered that the fallback is Segoe UI, because their pixel diff
   against Segoe UI was the smallest of four candidates. **Both are wrong.** Re-measured at a fixed
   origin, the unresolvable family is pixel-identical to *nothing* — Arial 590, Segoe UI 535,
   Variable Text 631, Tahoma 651 differing pixels — and `TryGetGlyphTypeface` returns `False`. 535
   is not 0, so "closest wins" fails by the same standard my claim did. §4.2 now states all three
   disagreeing measurements and concludes the degradation path is **undefined**, which is a better
   reason to declare the chain than the original claim was.
2. **§4.1's non-square opaque fractions.** My 0.7671 and the reviewer's 0.8094 are both correct —
   for different alpha cutoffs, neither of which either of us stated. At the 50 % cutoff the value
   is **0.7854, exactly π/4**. §4.1 now publishes that figure with the threshold and a sensitivity
   table, and §10.3 asserts a band rather than a fraction.

### Not a discrepancy

The reviewer measured 3996 / 2417 "glyphs" against my 5394 / 2530. Different APIs:
`GlyphTypeface.GlyphCount` versus `CharacterToGlyphMap.Count`. Both are now in §4.2's table,
labelled.

### The finding that changed the design

**`DragTrailOverlay.xaml.cs:177` was misclassified, and the ramp would have introduced a bug.** I
called it text because its `TextBlock` sets no size. The reviewer noticed the adjacent 26×26
`CreateButtonBg` ellipse. `PositionButton` centres both on the same point — `bg` at `pos − 13`,
`icon` at `pos − 8` — so the icon *is* bounded at 16/26 = 0.615, and `-8` is **half the font size
written as a literal**. Growing it to 18 would have put an 18px glyph in a 26px circle *and*
de-centred it by 1px in each axis, silently, with no test able to catch it.

Consequences: a fifth ratio `ContainerRatio.Badge = 0.60`; the site moves from the ramp to §8.5 at
15.6px; `PositionButton` must derive its offset from the measured icon size; and the site leaves
§8.6's flag list. Counts shift to **45 T / 17 C / 4 D**, and `FontSizeSubtitle` drops to 5 sites.

This is the strongest argument in the review for a per-site classification appendix: the error was
invisible at the level of "a fifth of sites are container-proportional" and only surfaced when one
row was checked against its layout code.

### Adopted from the review

- **§10.4's whitelist derives its known-token list by reflection** over `Tokens` and
  `ContainerRatio` rather than hard-coding member names. The reviewer's point stands: a whitelist's
  own failure mode is a stale allow-list, and reflection means the guard cannot disagree with the
  surface it guards.
- **§6.4 now records the key-versus-type trap** they spotted: `FontFamilyStandard` publishes a raw
  `string`, while the derived `FontFamily` reaches XAML as `FontStandard`. Both get asserted.
- **§9's line reference** corrected to the 294-295 dict comprehension.

### Found while applying the review — two of my own round-1 edits were defective

1. **§4.1's stadium column contained an analytic number in a measured table.** I wrote 0.9285,
   which is `(80·40 + π·20²)/(120·40)` — computed, not observed. Measured at the stated alpha > 128
   threshold it is **0.9279**. The column now says which is which.
2. **§8.6's "roughly 3px of internal leading" was a guess, and it was wrong in the safe
   direction.** Measured by rendering the `TextBlock` and scanning for the first inked row, leading
   is **5px for "start" and 7px for "snooze"** at 12px. Visible clearance from the dwell ring is
   therefore 5–7px, not the 0.04px the box-edge arithmetic implied. **The last flagged site is
   resolved** — no site in §8.6 is flagged any more.

The pattern in both is the same one that produced the Arial error: a number derived by reasoning,
presented in a place that implies it was observed.

### Still open

The `Segoe UI Variable Text` specimens want a look before implementation — it is the one decision
here that touches every glyph in the application and the one I would least like to reverse
afterwards.

---

## 16. Review Round 2 — What Changed

Round 2 re-checked every source claim by hand and re-derived every measurement with an independent
probe. **All round-1 measurements held exactly** — the TrackStatus 87.90/95.88, the 1.33× ratio, the
ink-clearance 5.04/7.04, the π/4 threshold table, the square clamp identity, and the whole of
Appendix A's reconciliation. Two of the three dropped §4.1 rows were re-derived (150×70 → 0.7830,
80×44 → 0.7830), both inside §10.3's band, so dropping them cost nothing.

What it found instead was one design hole and a spec that had gone internally inconsistent.

### The hole: derived values freeze to Midnight (§6.6, new)

I suspected this when writing the round-2 prompt and made it Priority 1. It is confirmed, and it is
worse than I suspected: **the repo already has a test that would go red.**
`ThemeResourceApplierTests.Apply_CoversEveryKeyTheDictionaryDeclares` (`:71-89`) diffs the
dictionary's declared keys against what `Apply` writes, with a comment describing exactly this
freeze. Adding §6.3's five `<x:Static>` entries without extending `Apply` fails it at implementation
time.

The reviewer demonstrated the freeze in the real topology — merged dictionary inside a real
`Application`, primitives written at top level, theme switched: `{StaticResource CornerRadiusStandard}`
tracked to the new theme while `{StaticResource StandardCornerRadius}` and
`{StaticResource FontStandard}` stayed at their parse-time values. With the derived keys also written,
both tracked.

§6.6 now states the mechanism, the fix (extend `Apply`'s explicit tail, mirroring the derived
`StandardBorderThickness` that is already there for this reason), and why the alternative — putting
the derived five on `ITheme` — is worse: §10.2's parity generalisation would then demand validator
entries for values that merely restate the primitives beside them.

It also corrected §6.4, which claimed "the two existing thickness keys **which are not `ITheme`
properties**". `BorderThicknessStandard` *is* on `ITheme` (`ITheme.cs:130`); only the derived
`StandardBorderThickness` is off it. Getting that backwards mattered, because it is the exact
distinction the hole turns on.

### The inconsistency: four stale cross-references

Round 1's corrections landed in §8.5 and §8.6 without updating anything that pointed at them. §14
still listed two ⚠ clip sites as open, §10.6 and §11 still referred to "the two flagged sites in
§8.5" (they were in §8.6, and none remain), and Appendix A's legend still described a ⚠ marker no row
carried. A reader could not tell what was actually open. All four are repaired; **the only
outstanding item is the font specimens.**

Also stale: §12 would have written the disproved Arial claim into the parent spec, the one thing §4.2
and §15 exist to correct.

### Two errors that came from citing without reading

1. **§8.5 cited dead consts as container sizes.** `TabContainerHeight = 100` and
   `PaginationButtonHeight = 80` are declaration-only; the real containers are literals at `:282` and
   `:405-406`. The ratio *values* were right, the citations pointed at code nothing reads.
2. **That exposed four more dead consts** in the same file, beside §8.7's four dead font consts.
   All eight are now deleted in one task — deleting four and leaving four identical ones would be
   arbitrary. §8.7 also records that §10.4's guard cannot catch this class: it scans `CornerRadius`,
   `FontFamily`, `FontSize` and `FontWeight`, not `Width`/`Height` literals.

### Corrections to specific claims

- **§8.5's "all others move under 0.5px" was wrong** — `DwellArcButton`'s default moves +1.0px
  (22 → 23.0), reaching two call sites. Its `Small` row also listed five observed ratios where the
  table has six sites, three of them at 0.229.
- **§4.1's `CornerRadius(-1)` throw was mis-attributed.** The constructor does not throw; the
  `Border.CornerRadius` validate callback does. §9's gate still has a real failure mode to guard.
- **§8.6's `TrackStatusWindow` row said 6 sites; it has 5.**
- **§3.1 described the nesting inversion backwards.** With child r8 inside parent r6 the child
  recedes; protrusion would require child < parent, and `Border` does not clip anyway. The defect is
  the non-concentric nesting itself. `DwellColorPicker.cs:122` has no nesting at all.
- **§10.1's `≥ 9` font floor contradicted §9's `≥ 12` gate** — two guards disagreeing by design, the
  disease §10.2 exists to cure.
- **"The only fixed-size, non-resizable window in the app" was literally false** —
  `QuickActionsGridWindow`, `EyeCareBannerWindow` and `TaskSwitcherWindow` are all fixed or
  non-resizable. The true claim is narrower: the only one whose ramp-affected content *overflows*.
- **§8.1 pointed at §8.6 for the font wiring**, which is §8.8. §2's Supervisor count was 15, not 17.
  §8.6's EyeCare growth was "≈ 12px"; analytic is +9.31. `FontWeightMedium` is `SemiBold` (600), not
  WPF's `FontWeights.Medium` (500) — now noted, since the name invites the confusion.

### m10, which changed the fix rather than the prose

§8.5 said `PositionButton` should "derive its offset from the icon's measured size". Round 2 showed
that is under-specified *and* wrong in effect: `DesiredSize.Height` is 1.33 × the font size, so
box-centring would shift the glyph ≈2.6px from where it sits today — a visible change I had presented
as pure defect removal. And `DesiredSize` is `(0,0)` there anyway, since `PositionButton` is `static`
and receives an unmeasured `TextBlock`. The fix is `-(size / 2)`, which preserves today's geometry
exactly. The sibling `-10` offset at `:130-131` needs it too.

### The pattern worth naming

Round 1 corrected two claims of mine; round 2 corrected two more, plus the two I caught myself in
`f9ac2b0`. Six of those eight were the same failure: **a number or relationship derived by reasoning,
placed where it reads as observed** — Arial, 0.9285, "roughly 3px", "under 0.5px", the dead-const
citations, and box-centring. The measurements themselves have held every time they were re-derived.
The lesson for the plan is that the risk is not in the probes; it is in the prose around them.
