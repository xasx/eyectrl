# Overlay Geometry and Typography Tokens Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace 30 hard-coded corner radii and 60 hard-coded font values across `EyeCtrl.App` and `EyeCtrl.Controls` with theme-varying tokens, closing the deferral in `docs/design/overlay-unification-spec.md` §8.

**Architecture:** 12 new primitives on `ITheme` (4 radii, 5 font sizes, 2 weights, 1 family), with `ThemeBase` deriving 5 WPF-typed forms exactly as it already derives brushes from colours. All three themes declare identical values today; the mechanism exists so one can diverge later. A separate `ContainerRatio` static class handles the ~16 sites whose glyph size is a function of their container rather than a step on the type ramp. A repo-wide **whitelist** drift guard prevents re-fragmentation.

**Tech Stack:** C# / .NET 10 / WPF, xUnit v3 + NFluent, Python 3 for the palette validator.

**Source spec:** `docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md`. Read §5 (the ten decisions), §6 (token design, especially **§6.6**), §8 (typography) and **Appendix A** (all 66 `FontSize` sites classified) before starting. §15 and §16 record two source-verified review rounds.

---

## Conventions you must follow

These are not optional and getting them wrong wastes a whole task.

- **Tests use NFluent, never xUnit assertions.** `Check.That(actual).IsEqualTo(expected)`. There is no `Assert.Equal` in this codebase. Add a custom message on anything non-obvious: `Check.WithCustomMessage("...").That(x).IsTrue()`.
- **`dotnet test` output is German.** A failing test prints `Fehler <FullyQualifiedName>`, **not** "Failed". Long names wrap across lines. **Never grep for "Failed" and conclude green** — grep for `Fehler` or read the summary line.
- Solution file is **`EyeCtrl.slnx`**, not `.sln`.
- Headless test command, used at the end of every task:
  ```bash
  dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
  ```
  It **must** include `EyeCtrl.App.Tests` — CI runs that subset and has caught stale App.Tests assertions before. CI does **not** run on stacked PRs (trigger is `branches: [main]`), so local verification is the only gate.
- **Never launch the app. Never run UI tests. Never simulate input.** The user drives this machine solely by eye-tracking; taking the cursor removes their only input channel. Tests that construct a `Window` are fine as long as they never call `Show()`.
- After any theme edit, `python tools/palette_validate.py` must exit 0.
- Per `CLAUDE.md`: run `impact({target, direction: "upstream"})` before editing a symbol, and `detect_changes()` before committing. **The GitNexus index is stale** (last indexed `b590a87`) — run `node .gitnexus/run.cjs analyze` once before Task 1.

---

## File Structure

**Token layer** — `src/EyeCtrl.Controls/Theme/`
| File | Responsibility | Change |
|---|---|---|
| `ITheme.cs` | the 12 new primitives | modify |
| `ThemeBase.cs` | derive 5 WPF-typed forms | modify |
| `MidnightTheme.cs`, `EmberTheme.cs`, `ContrastTheme.cs` | declare identical values | modify ×3 |
| `Tokens.cs` | static facade, forwards all 17 | modify |
| `OverlayStyles.xaml` | XAML surface, 17 `<x:Static>` entries | modify |
| `ThemeResourceApplier.cs` | publish primitives by reflection **+ 5 derived explicitly** | modify |
| `ContainerRatio.cs` | 5 glyph-to-container ratios | **create** |

**Validator** — `tools/palette_validate.py`: `NON_COLOUR_TOKENS` + 12 entries × 3 themes + gate 1 tail.

**Tests**
| File | Change |
|---|---|
| `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs` | ordering + floor per theme |
| `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs` | generalise the non-`Color` theory |
| `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs` | all 17 keys resolve |
| `tests/EyeCtrl.Controls.Tests/Theme/ContainerRatioTests.cs` | **create** |
| `tests/EyeCtrl.App.Tests/CornerRadiusClampTests.cs` | **create** — pins §4.1 |
| `tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs` | **create** — the whitelist guard |
| `tests/EyeCtrl.App.Tests/SourceScanner.cs` | **create** — shared with `RawColourLiteralTests` |
| `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs` | modify — use the shared scanner |
| `tests/EyeCtrl.App.Tests/OverlayFontTests.cs` | **create** — §8.8 wiring |

**Migration** — 19 files in `src/EyeCtrl.App`, 5 in `src/EyeCtrl.Controls`. Enumerated per task.

**Docs** — `docs/design/overlay-unification-spec.md` §3.3/§3.4/§4.2/§4.3/§8; `docs/design/overlay-unification-style-guide.html`.

---

## Task Order and Why

Tasks 1–6 build the token layer and the guard **before** any site moves, so every migration has
something to migrate *onto* and something that fails if it is missed. Tasks 7–9 migrate radii
alongside the type values in the same files (provably invisible for radii — §1 of the spec
establishes radius cannot affect gaze targeting). Task 10 wires the font family. Tasks 11–12 finish
the type ramp and convert the container ratios, the only visible changes. Task 13 is the single
layout edit, Task 14 the docs, Task 15 the verification. The risky work is last and separately
revertible.

---

### Task 1: The 12 `ITheme` primitives (plus the derived `FontStandard`)

**Files:**
- Modify: `src/EyeCtrl.Controls/Theme/ITheme.cs`
- Modify: `src/EyeCtrl.Controls/Theme/ThemeBase.cs`
- Modify: `src/EyeCtrl.Controls/Theme/MidnightTheme.cs`, `EmberTheme.cs`, `ContrastTheme.cs`
- Modify: `src/EyeCtrl.Controls/Theme/Tokens.cs`
- Test: `tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these to `ThemeShapeTests.cs`, inside the existing class (it already has an `AllThemes()` `TheoryData<ITheme>` member you reuse):

```csharp
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void CornerRadii_AreOrderedAndNonNegative(ITheme theme)
    {
        // Mirrors the validator's gate-1 non-colour tail. A negative radius throws
        // when it reaches Border.CornerRadius (spec §4.1), so 0 is a real floor.
        Check.That(theme.CornerRadiusSmall).IsGreaterOrEqualThan(0.0);
        Check.That(theme.CornerRadiusStandard).IsGreaterOrEqualThan(theme.CornerRadiusSmall);
        Check.That(theme.CornerRadiusLarge).IsGreaterOrEqualThan(theme.CornerRadiusStandard);
        Check.WithCustomMessage(
                "CornerRadiusCircle is a fully-round sentinel and must outrank every real step.")
            .That(theme.CornerRadiusCircle).IsGreaterOrEqualThan(theme.CornerRadiusLarge);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FontSizes_AreOrderedAndClearTheAccessibilityFloor(ITheme theme)
    {
        // 12 is the ramp floor spec §8.4 chose, and §9 gates it in the validator.
        // Keep this number identical to the validator's or the two guards disagree.
        Check.WithCustomMessage(
                $"{theme.Name} sets body text below the 12px floor, which is the guarantee "
                + "that makes the 9->12 raise permanent rather than one release's decision.")
            .That(theme.FontSizeBody).IsGreaterOrEqualThan(12.0);
        Check.That(theme.FontSizeBodyLarge).IsGreaterOrEqualThan(theme.FontSizeBody);
        Check.That(theme.FontSizeSubtitle).IsGreaterOrEqualThan(theme.FontSizeBodyLarge);
        Check.That(theme.FontSizeTitle).IsGreaterOrEqualThan(theme.FontSizeSubtitle);
        Check.That(theme.FontSizeDisplay).IsGreaterOrEqualThan(theme.FontSizeTitle);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FontFamily_IsDeclaredAndResolves(ITheme theme)
    {
        Check.That(theme.FontFamilyStandard).IsNotEmpty();
        Check.WithCustomMessage(
                "FontStandard must derive from FontFamilyStandard, never restate it.")
            .That(theme.FontStandard.Source).IsEqualTo(theme.FontFamilyStandard);
    }

    [Fact]
    public void FontStandard_IsCachedNotRebuiltPerAccess()
    {
        // Same reasoning as BrushesAreCachedNotRebuiltPerAccess: overlays rebuild at gaze rate.
        Check.That(MidnightTheme.Instance.FontStandard)
             .IsSameReferenceAs(MidnightTheme.Instance.FontStandard);
    }

    [Fact]
    public void AllThreeThemes_DeclareIdenticalGeometryAndType()
    {
        // Spec §5 decision 6: the mechanism is theme-varying, the values are not — yet.
        // If a theme deliberately diverges later, delete this test and say why in the commit.
        foreach (var theme in ThemeRegistry.All)
        {
            Check.That(theme.CornerRadiusSmall).IsEqualTo(4.0);
            Check.That(theme.CornerRadiusStandard).IsEqualTo(8.0);
            Check.That(theme.CornerRadiusLarge).IsEqualTo(12.0);
            Check.That(theme.CornerRadiusCircle).IsEqualTo(9999.0);
            Check.That(theme.FontSizeBody).IsEqualTo(12.0);
            Check.That(theme.FontSizeBodyLarge).IsEqualTo(14.0);
            Check.That(theme.FontSizeSubtitle).IsEqualTo(18.0);
            Check.That(theme.FontSizeTitle).IsEqualTo(24.0);
            Check.That(theme.FontSizeDisplay).IsEqualTo(34.0);
            Check.That(theme.FontFamilyStandard).IsEqualTo("Segoe UI Variable Text, Segoe UI");
            Check.That(theme.FontWeightMedium).IsEqualTo(FontWeights.SemiBold);
            Check.That(theme.FontWeightStrong).IsEqualTo(FontWeights.Bold);
        }
    }
```

Add `using System.Windows;` to the file's using block (for `FontWeights`).

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~ThemeShapeTests" --nologo
```

Expected: **compile errors** — `'ITheme' does not contain a definition for 'CornerRadiusSmall'` and similar. A compile failure is the correct first failure here.

- [ ] **Step 3: Add the 12 primitives to `ITheme`**

In `src/EyeCtrl.Controls/Theme/ITheme.cs`, replace the closing section (currently ending with the `BorderThicknessStandard` property) so the file ends with:

```csharp
    /// <summary>
    /// The one non-colour theme-varying token (§4.4). Midnight and Ember use 1;
    /// Contrast uses 2, because high-contrast structure is carried by the border
    /// rather than by fill steps.
    /// </summary>
    double BorderThicknessStandard { get; }

    // ── Geometry ────────────────────────────────────────────────────────────
    /// <summary>Compact bars and thin outlines: the toolbar, the dwell-feedback bar.</summary>
    double CornerRadiusSmall { get; }
    /// <summary>The default: overlay container roots, cards, keys, tiles, dwell controls.</summary>
    double CornerRadiusStandard { get; }
    /// <summary>Prominent cards and modal dialogs: splash, the settings card.</summary>
    double CornerRadiusLarge { get; }
    /// <summary>
    /// Fully round. **Valid on square elements only.** WPF clamps an over-large
    /// CornerRadius to an inscribed *ellipse*, so on a non-square Border this produces
    /// an ellipse, never a stadium — measured in spec §4.1 and pinned by
    /// CornerRadiusClampTests. On a square element it renders pixel-identically to
    /// size / 2 at every size and DPI tested.
    /// </summary>
    double CornerRadiusCircle { get; }

    // ── Typography ──────────────────────────────────────────────────────────
    /// <summary>
    /// Family name, or a comma-separated fallback chain. A chain is deliberate: an
    /// unresolvable bare name has an undefined degradation path (spec §4.2).
    /// </summary>
    string FontFamilyStandard { get; }
    /// <summary>Emphasis weight. SemiBold (600) — *not* FontWeights.Medium, which is 500.</summary>
    FontWeight FontWeightMedium { get; }
    /// <summary>Strong weight. Bold (700).</summary>
    FontWeight FontWeightStrong { get; }

    /// <summary>The ramp floor. Captions, body text, list rows. Gated at >= 12 (§9).</summary>
    double FontSizeBody { get; }
    /// <summary>Slightly emphasised body: tile labels, secondary rows.</summary>
    double FontSizeBodyLarge { get; }
    /// <summary>Section and notice text.</summary>
    double FontSizeSubtitle { get; }
    /// <summary>Headings and dwell-control labels.</summary>
    double FontSizeTitle { get; }
    /// <summary>The largest step: splash and window titles.</summary>
    double FontSizeDisplay { get; }

    // ── Derived (implemented by ThemeBase; see §6.2 and §6.6) ───────────────
    /// <summary><see cref="FontFamilyStandard"/> as a cached WPF object.</summary>
    FontFamily FontStandard { get; }
}
```

Add `using System.Windows;` to the top of `ITheme.cs` (it currently has only `using System.Windows.Media;`). `FontWeight` and `FontFamily` both live in `System.Windows` / `System.Windows.Media` respectively — `FontFamily` is already covered by the existing using.

> **Note on `FontStandard`:** it is declared on `ITheme` because `TokenValueTests.Tokens_ExposesEveryMemberOfITheme` then forces a `Tokens` forwarder for it. The four derived `CornerRadius` structs are **not** on `ITheme` — see Task 3 and spec §6.6 for why.

- [ ] **Step 4: Implement the primitives in `ThemeBase`**

In `src/EyeCtrl.Controls/Theme/ThemeBase.cs`, after the existing `public abstract double BorderThicknessStandard { get; }` line, add:

```csharp
    /// <inheritdoc />
    public abstract double CornerRadiusSmall { get; }
    /// <inheritdoc />
    public abstract double CornerRadiusStandard { get; }
    /// <inheritdoc />
    public abstract double CornerRadiusLarge { get; }
    /// <inheritdoc />
    public abstract double CornerRadiusCircle { get; }

    /// <inheritdoc />
    public abstract string FontFamilyStandard { get; }
    /// <inheritdoc />
    public abstract FontWeight FontWeightMedium { get; }
    /// <inheritdoc />
    public abstract FontWeight FontWeightStrong { get; }

    /// <inheritdoc />
    public abstract double FontSizeBody { get; }
    /// <inheritdoc />
    public abstract double FontSizeBodyLarge { get; }
    /// <inheritdoc />
    public abstract double FontSizeSubtitle { get; }
    /// <inheritdoc />
    public abstract double FontSizeTitle { get; }
    /// <inheritdoc />
    public abstract double FontSizeDisplay { get; }
```

Then, next to the `_accentDim10` backing field, add:

```csharp
    private FontFamily? _fontStandard;
```

and next to `BrushAccentDim10`, add:

```csharp
    /// <inheritdoc />
    public FontFamily FontStandard => _fontStandard ??= new FontFamily(FontFamilyStandard);
```

Add `using System.Windows;` to `ThemeBase.cs`.

- [ ] **Step 5: Declare the values in all three themes**

The block is **identical** in all three files (spec §5 decision 6). Append it before the closing brace of `MidnightTheme.cs`, `EmberTheme.cs` and `ContrastTheme.cs`:

```csharp
    // ── Geometry and typography: identical in all three themes (spec §5 decision 6).
    // Theme-varying by mechanism so a future revision can diverge without touching a
    // single overlay; no measurement today says any theme wants different geometry.

    /// <inheritdoc />
    public override double CornerRadiusSmall => 4.0;
    /// <inheritdoc />
    public override double CornerRadiusStandard => 8.0;
    /// <inheritdoc />
    public override double CornerRadiusLarge => 12.0;
    /// <inheritdoc />
    public override double CornerRadiusCircle => 9999.0;

    /// <inheritdoc />
    public override string FontFamilyStandard => "Segoe UI Variable Text, Segoe UI";
    /// <inheritdoc />
    public override FontWeight FontWeightMedium => FontWeights.SemiBold;
    /// <inheritdoc />
    public override FontWeight FontWeightStrong => FontWeights.Bold;

    /// <inheritdoc />
    public override double FontSizeBody => 12.0;
    /// <inheritdoc />
    public override double FontSizeBodyLarge => 14.0;
    /// <inheritdoc />
    public override double FontSizeSubtitle => 18.0;
    /// <inheritdoc />
    public override double FontSizeTitle => 24.0;
    /// <inheritdoc />
    public override double FontSizeDisplay => 34.0;
```

Add `using System.Windows;` to each of the three files.

- [ ] **Step 6: Forward all 13 new members on `Tokens`**

`TokenValueTests.Tokens_ExposesEveryMemberOfITheme` fails without this. In `src/EyeCtrl.Controls/Theme/Tokens.cs`, after the existing `BorderThicknessStandard` forwarder, add:

```csharp
    /// <inheritdoc cref="ITheme.CornerRadiusSmall"/>
    public static double CornerRadiusSmall => Current.CornerRadiusSmall;
    /// <inheritdoc cref="ITheme.CornerRadiusStandard"/>
    public static double CornerRadiusStandard => Current.CornerRadiusStandard;
    /// <inheritdoc cref="ITheme.CornerRadiusLarge"/>
    public static double CornerRadiusLarge => Current.CornerRadiusLarge;
    /// <inheritdoc cref="ITheme.CornerRadiusCircle"/>
    public static double CornerRadiusCircle => Current.CornerRadiusCircle;

    /// <inheritdoc cref="ITheme.FontFamilyStandard"/>
    public static string FontFamilyStandard => Current.FontFamilyStandard;
    /// <inheritdoc cref="ITheme.FontStandard"/>
    public static FontFamily FontStandard => Current.FontStandard;
    /// <inheritdoc cref="ITheme.FontWeightMedium"/>
    public static FontWeight FontWeightMedium => Current.FontWeightMedium;
    /// <inheritdoc cref="ITheme.FontWeightStrong"/>
    public static FontWeight FontWeightStrong => Current.FontWeightStrong;

    /// <inheritdoc cref="ITheme.FontSizeBody"/>
    public static double FontSizeBody => Current.FontSizeBody;
    /// <inheritdoc cref="ITheme.FontSizeBodyLarge"/>
    public static double FontSizeBodyLarge => Current.FontSizeBodyLarge;
    /// <inheritdoc cref="ITheme.FontSizeSubtitle"/>
    public static double FontSizeSubtitle => Current.FontSizeSubtitle;
    /// <inheritdoc cref="ITheme.FontSizeTitle"/>
    public static double FontSizeTitle => Current.FontSizeTitle;
    /// <inheritdoc cref="ITheme.FontSizeDisplay"/>
    public static double FontSizeDisplay => Current.FontSizeDisplay;
```

Add `using System.Windows;` to `Tokens.cs`.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: PASS. If you see `Fehler` lines, read them — do **not** grep for "Failed".

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Controls/Theme tests/EyeCtrl.Controls.Tests/Theme/ThemeShapeTests.cs
git commit -m "feat(theme): add 12 geometry and typography primitives to ITheme"
```

---

### Task 2: Validator entries and gate 1's non-colour tail

**Files:**
- Modify: `tools/palette_validate.py`
- Test: `tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs`

- [ ] **Step 1: Write the failing test — generalise the parity theory**

In `ThemeParityTests.cs`, **replace** the whole `BorderThickness_MatchesTheValidator` method with:

```csharp
    [Theory]
    [MemberData(nameof(ThemeNames))]
    public void EveryNonColourToken_MatchesTheValidator(string themeName)
    {
        // Reflected rather than listed. The hand-written predecessor covered one token;
        // adding twelve more by hand is exactly the omission this suite exists to prevent.
        // Derived values are deliberately NOT on ITheme (spec §6.6), so they are not gated
        // here — they restate the primitives beside them.
        var expected = ParseTheme(ValidatorSource(), themeName);
        var theme = ThemeRegistry.Resolve(themeName);

        var gated = typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType != typeof(Color)
                     && p.PropertyType != typeof(SolidColorBrush)
                     && p.PropertyType != typeof(FontFamily)
                     && p.Name != nameof(ITheme.Name))
            .ToList();

        Check.WithCustomMessage("Reflected no non-colour tokens — wrong filter")
            .That(gated).Not.IsEmpty();

        foreach (var property in gated)
        {
            Check.WithCustomMessage(
                    $"tools/palette_validate.py has no '{property.Name}' for {themeName}. "
                    + "Every non-colour ITheme token must be gated, or it ships unvalidated. "
                    + "Add it to the theme dict AND to NON_COLOUR_TOKENS.")
                .That(expected.ContainsKey(property.Name)).IsTrue();

            var actual = property.GetValue(theme)!;
            var expectedText = expected[property.Name];

            if (property.PropertyType == typeof(double))
            {
                Check.WithCustomMessage($"{themeName}.{property.Name} disagrees with the validator")
                    .That((double)actual)
                    .IsEqualTo(double.Parse(expectedText, CultureInfo.InvariantCulture));
            }
            else
            {
                // string directly; FontWeight and anything else via ToString(),
                // which yields "SemiBold" / "Bold" — what the validator stores.
                var actualText = actual as string ?? actual.ToString()!;
                Check.WithCustomMessage(
                        $"{themeName}.{property.Name} is '{actualText}' in C# but "
                        + $"'{expectedText}' in tools/palette_validate.py")
                    .That(actualText).IsEqualTo(expectedText);
            }
        }
    }
```

Add `using System.Globalization;` to the file.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~ThemeParityTests" --nologo
```

Expected: FAIL, three times (once per theme), with `tools/palette_validate.py has no 'CornerRadiusSmall' for Midnight`.

- [ ] **Step 3: Add the 12 entries to each theme dict**

In `tools/palette_validate.py`, inside **each** of the three theme dicts, immediately after that theme's `"BorderThicknessStandard": "...",` line, insert:

```python
        "CornerRadiusSmall":       "4",
        "CornerRadiusStandard":    "8",
        "CornerRadiusLarge":       "12",
        "CornerRadiusCircle":      "9999",
        "FontFamilyStandard":      "Segoe UI Variable Text, Segoe UI",
        "FontWeightMedium":        "SemiBold",
        "FontWeightStrong":        "Bold",
        "FontSizeBody":            "12",
        "FontSizeBodyLarge":       "14",
        "FontSizeSubtitle":        "18",
        "FontSizeTitle":           "24",
        "FontSizeDisplay":         "34",
```

- [ ] **Step 4: Extend `NON_COLOUR_TOKENS`**

Replace the `NON_COLOUR_TOKENS` assignment (currently one name) with:

```python
# Tokens that are not colours. Theme-varying but parsed as numbers or strings, so they
# are excluded from every colour gate; parse() raises on anything listed here that it
# still sees. BorderThicknessStandard was the first: high-contrast convention is a 2px
# outline because structure there is carried by the border rather than by fill steps
# (gate 1), while the tuned palettes use 1px. The geometry and typography tokens joined
# it in spec §9. Any future non-colour token must be added here or parse() will reject it.
NON_COLOUR_TOKENS = {
    "BorderThicknessStandard",
    "CornerRadiusSmall", "CornerRadiusStandard", "CornerRadiusLarge", "CornerRadiusCircle",
    "FontFamilyStandard", "FontWeightMedium", "FontWeightStrong",
    "FontSizeBody", "FontSizeBodyLarge", "FontSizeSubtitle", "FontSizeTitle",
    "FontSizeDisplay",
}
```

- [ ] **Step 5: Extend gate 1's non-colour tail**

In `validate_theme`, immediately after the existing `check(f"[{name}] BorderThicknessStandard", ...)` call, insert:

```python
    # Non-colour geometry and type. check() is a ">= floor" assertion, so orderings are
    # expressed as differences. Radii live here rather than in a new gate so that "ten
    # gates" stays true across the spec, the style guide and the test comments (§9).
    r_small = float(palette["CornerRadiusSmall"])
    r_std = float(palette["CornerRadiusStandard"])
    r_large = float(palette["CornerRadiusLarge"])
    r_circle = float(palette["CornerRadiusCircle"])
    check(f"[{name}] CornerRadiusSmall >= 0", r_small, 0.0,
          "a negative radius throws when it reaches Border.CornerRadius")
    check(f"[{name}] radius ramp Small<=Standard", r_std - r_small, 0.0,
          f"{r_small} -> {r_std}")
    check(f"[{name}] radius ramp Standard<=Large", r_large - r_std, 0.0,
          f"{r_std} -> {r_large}")
    check(f"[{name}] CornerRadiusCircle outranks Large", r_circle - r_large, 0.0,
          f"{r_circle} is the fully-round sentinel")

    f_body = float(palette["FontSizeBody"])
    f_bodyl = float(palette["FontSizeBodyLarge"])
    f_sub = float(palette["FontSizeSubtitle"])
    f_title = float(palette["FontSizeTitle"])
    f_disp = float(palette["FontSizeDisplay"])
    check(f"[{name}] FontSizeBody floor", f_body, 12.0,
          "px — the accessibility floor; keep identical to ThemeShapeTests")
    check(f"[{name}] type ramp Body<=BodyLarge", f_bodyl - f_body, 0.0, f"{f_body} -> {f_bodyl}")
    check(f"[{name}] type ramp BodyLarge<=Subtitle", f_sub - f_bodyl, 0.0, f"{f_bodyl} -> {f_sub}")
    check(f"[{name}] type ramp Subtitle<=Title", f_title - f_sub, 0.0, f"{f_sub} -> {f_title}")
    check(f"[{name}] type ramp Title<=Display", f_disp - f_title, 0.0, f"{f_title} -> {f_disp}")
    check(f"[{name}] FontFamilyStandard declared",
          1.0 if palette["FontFamilyStandard"].strip() else 0.0, 1.0,
          palette["FontFamilyStandard"])
```

- [ ] **Step 6: Run the validator**

```bash
python tools/palette_validate.py
```

Expected: `ALL GATES PASS`, exit 0. The gate banner still reads ten gates.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add tools/palette_validate.py tests/EyeCtrl.Controls.Tests/Theme/ThemeParityTests.cs
git commit -m "feat(theme): gate the 12 non-colour tokens in gate 1's tail"
```

---

### Task 3: The derived values, and the freeze that §6.6 prevents

**Read spec §6.6 before starting.** `OverlayStyles.xaml` resolves `{x:Static}` when WPF *parses* it — during `Application` construction, before a theme is chosen. Anything reaching XAML only that way is frozen to Midnight. `ThemeResourceApplierTests.Apply_CoversEveryKeyTheDictionaryDeclares` already enforces this and **will go red** if you add the dictionary entries without extending `Apply`.

**Files:**
- Modify: `src/EyeCtrl.Controls/Theme/ThemeBase.cs`, `Tokens.cs`, `OverlayStyles.xaml`, `ThemeResourceApplier.cs`
- Test: `tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs`

- [ ] **Step 1: Write the failing tests**

**Replace only `TokenDictionaryTests.Dictionary_ExposesBorderThickness`** with the generalised test
below, then add the two new ones. **Keep `Dictionary_ExposesTheThicknessFormDerivedFromTheToken`
exactly as it is** — `StandardBorderThickness` is produced by `UniformThicknessExtension` and is not
a `Tokens` property, so the generalised test cannot see it. Deleting it would leave the dictionary's
`StandardBorderThickness` *value* unasserted by anything.

```csharp
    [StaFact]
    public void EveryNonBrushToken_IsExposedAndMatches()
    {
        var dict = Load();

        foreach (var property in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static)
                     .Where(p => p.PropertyType != typeof(SolidColorBrush)
                              && p.PropertyType != typeof(Color)
                              && p.Name != nameof(Tokens.Current)))
        {
            var key = property.Name;
            Check.WithCustomMessage($"OverlayStyles.xaml is missing key '{key}'")
                .That(dict.Contains(key)).IsTrue();
            Check.WithCustomMessage(
                    $"'{key}' must be DERIVED from Tokens via {{x:Static}}, never restated.")
                .That(dict[key]).IsEqualTo(property.GetValue(null));
        }
    }

    [StaFact]
    public void Dictionary_ExposesTheFourCornerRadiusForms()
    {
        // WPF cannot convert a double resource to a CornerRadius, so the CornerRadius
        // form must be its own key. Verified resolvable through {StaticResource} in the
        // real merged-dictionary topology (spec §6.5) — no markup extension needed.
        var dict = Load();
        foreach (var (key, expected) in new (string, CornerRadius)[]
                 {
                     ("SmallCornerRadius", new CornerRadius(Tokens.CornerRadiusSmall)),
                     ("StandardCornerRadius", new CornerRadius(Tokens.CornerRadiusStandard)),
                     ("LargeCornerRadius", new CornerRadius(Tokens.CornerRadiusLarge)),
                     ("CircleCornerRadius", new CornerRadius(Tokens.CornerRadiusCircle)),
                 })
        {
            Check.WithCustomMessage(
                    $"OverlayStyles.xaml must expose {key}, or every "
                    + $"CornerRadius=\"{{StaticResource {key}}}\" throws at load.")
                .That(dict.Contains(key)).IsTrue();
            Check.That(dict[key]).IsEqualTo(expected);
        }
    }

    [StaFact]
    public void Dictionary_ExposesBothFontKeysWithTheRightTypes()
    {
        // FontFamilyStandard is the raw string; FontStandard is the FontFamily object.
        // Using the wrong one throws XamlParseException at load — no type converter runs
        // on a StaticResource assignment (spec §6.4).
        var dict = Load();
        Check.That(dict["FontFamilyStandard"]).IsInstanceOf<string>();
        Check.That(dict["FontStandard"]).IsInstanceOf<FontFamily>();
        Check.That(((FontFamily)dict["FontStandard"]!).Source).IsEqualTo(Tokens.FontFamilyStandard);
    }
```

Add `using System.Windows.Media;` if not present (it is) — `CornerRadius` is in `System.Windows`, already imported.

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~TokenDictionaryTests" --nologo
```

Expected: FAIL — `OverlayStyles.xaml is missing key 'CornerRadiusSmall'`.

- [ ] **Step 3: Add the four derived `CornerRadius` values to `Tokens` only**

These are **not** on `ITheme` (spec §6.6: §10.2's parity theory would otherwise demand validator entries for values that merely restate their primitives), and they are **not** on `ThemeBase` either — nothing could reach them there, because `Tokens.Current` is typed as `ITheme`. They live on `Tokens`, built from the primitives, so a theme implementing `ITheme` directly still works.

In `Tokens.cs`, add:

```csharp
    /// <summary><see cref="ITheme.CornerRadiusSmall"/> as a uniform WPF CornerRadius.</summary>
    public static CornerRadius SmallCornerRadius => new(Current.CornerRadiusSmall);
    /// <summary><see cref="ITheme.CornerRadiusStandard"/> as a uniform WPF CornerRadius.</summary>
    public static CornerRadius StandardCornerRadius => new(Current.CornerRadiusStandard);
    /// <summary><see cref="ITheme.CornerRadiusLarge"/> as a uniform WPF CornerRadius.</summary>
    public static CornerRadius LargeCornerRadius => new(Current.CornerRadiusLarge);
    /// <summary><see cref="ITheme.CornerRadiusCircle"/> as a uniform WPF CornerRadius.</summary>
    public static CornerRadius CircleCornerRadius => new(Current.CornerRadiusCircle);
```

`CornerRadius` is a struct, so no caching is needed.

- [ ] **Step 4: Add the 17 entries to `OverlayStyles.xaml`**

Before the closing `</ResourceDictionary>`, after the existing `StandardBorderThickness` entry, add:

```xml
    <!-- Geometry. Raw doubles for code and the validator; the CornerRadius forms for XAML,
         which cannot convert a double resource. Both are pulled from Tokens with {x:Static}
         so there is exactly one literal per value, in the theme classes.
         NOTE: {x:Static} resolves at PARSE time, before a theme is chosen — so every key
         here must also be written by ThemeResourceApplier.Apply, or it silently stays on
         Midnight. Apply_CoversEveryKeyTheDictionaryDeclares enforces that. See spec §6.6. -->
    <x:Static x:Key="CornerRadiusSmall"    Member="theme:Tokens.CornerRadiusSmall"/>
    <x:Static x:Key="CornerRadiusStandard" Member="theme:Tokens.CornerRadiusStandard"/>
    <x:Static x:Key="CornerRadiusLarge"    Member="theme:Tokens.CornerRadiusLarge"/>
    <x:Static x:Key="CornerRadiusCircle"   Member="theme:Tokens.CornerRadiusCircle"/>

    <x:Static x:Key="SmallCornerRadius"    Member="theme:Tokens.SmallCornerRadius"/>
    <x:Static x:Key="StandardCornerRadius" Member="theme:Tokens.StandardCornerRadius"/>
    <x:Static x:Key="LargeCornerRadius"    Member="theme:Tokens.LargeCornerRadius"/>
    <x:Static x:Key="CircleCornerRadius"   Member="theme:Tokens.CircleCornerRadius"/>

    <!-- Typography. FontFamilyStandard is the raw chain string; FontStandard is the
         FontFamily object. Assigning the wrong one throws at load — no type converter
         runs on a StaticResource assignment. -->
    <x:Static x:Key="FontFamilyStandard" Member="theme:Tokens.FontFamilyStandard"/>
    <x:Static x:Key="FontStandard"       Member="theme:Tokens.FontStandard"/>
    <x:Static x:Key="FontWeightMedium"   Member="theme:Tokens.FontWeightMedium"/>
    <x:Static x:Key="FontWeightStrong"   Member="theme:Tokens.FontWeightStrong"/>

    <x:Static x:Key="FontSizeBody"      Member="theme:Tokens.FontSizeBody"/>
    <x:Static x:Key="FontSizeBodyLarge" Member="theme:Tokens.FontSizeBodyLarge"/>
    <x:Static x:Key="FontSizeSubtitle"  Member="theme:Tokens.FontSizeSubtitle"/>
    <x:Static x:Key="FontSizeTitle"     Member="theme:Tokens.FontSizeTitle"/>
    <x:Static x:Key="FontSizeDisplay"   Member="theme:Tokens.FontSizeDisplay"/>
```

- [ ] **Step 5: Extend `Apply` — the §6.6 fix**

In `src/EyeCtrl.Controls/Theme/ThemeResourceApplier.cs`, replace the `Apply` method and `EnumerateBrushes` with:

```csharp
    /// <summary>
    /// Every value the theme publishes, paired with its resource key. Reflected over
    /// <see cref="ITheme"/> rather than listed, so a token added to the interface cannot be
    /// forgotten here. <c>Name</c> is excluded by name, not by type — <c>FontFamilyStandard</c>
    /// is also a string. Raw <see cref="Color"/>s are excluded because no XAML consumes them.
    /// </summary>
    /// <param name="theme">The theme to read.</param>
    /// <returns>Key/value pairs, in interface declaration order.</returns>
    public static IEnumerable<(string Key, object Value)> EnumeratePublishedValues(ITheme theme) =>
        typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType != typeof(Color) && p.Name != nameof(ITheme.Name))
            .Select(p => (p.Name, p.GetValue(theme)!));

    /// <summary>
    /// Every brush the theme exposes. Kept as its own accessor for callers that want only the
    /// brushes; nothing outside this class calls it today.
    /// </summary>
    /// <param name="theme">The theme to read.</param>
    /// <returns>Key/brush pairs.</returns>
    public static IEnumerable<(string Key, SolidColorBrush Brush)> EnumerateBrushes(ITheme theme) =>
        typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType == typeof(SolidColorBrush))
            .Select(p => (p.Name, (SolidColorBrush)p.GetValue(theme)!));

    /// <summary>
    /// Writes <paramref name="theme"/> into <paramref name="target"/>, replacing any
    /// existing entries under the same keys.
    ///
    /// The reflected half covers everything on <see cref="ITheme"/>. The explicit tail covers
    /// the *derived* values, which are not interface members and would otherwise reach XAML
    /// only through <c>{x:Static}</c> — resolved at parse time, before a theme is chosen, and
    /// therefore frozen to Midnight. See the design spec §6.6;
    /// <c>Apply_CoversEveryKeyTheDictionaryDeclares</c> fails if this tail falls behind
    /// <c>OverlayStyles.xaml</c>.
    /// </summary>
    /// <param name="target">The dictionary to write into — normally <c>Application.Resources</c>.</param>
    /// <param name="theme">The theme to publish.</param>
    public static void Apply(ResourceDictionary target, ITheme theme)
    {
        foreach (var (key, value) in EnumeratePublishedValues(theme))
            target[key] = value;

        var t = theme.BorderThicknessStandard;
        target[UniformThicknessKey] = new Thickness(t, t, t, t);

        target["SmallCornerRadius"] = new CornerRadius(theme.CornerRadiusSmall);
        target["StandardCornerRadius"] = new CornerRadius(theme.CornerRadiusStandard);
        target["LargeCornerRadius"] = new CornerRadius(theme.CornerRadiusLarge);
        target["CircleCornerRadius"] = new CornerRadius(theme.CornerRadiusCircle);
    }
```

> `ThicknessKey` ("BorderThicknessStandard") is now written by the reflected half, since it is an `ITheme` property — keep the const for callers, but the explicit write is no longer needed for it.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: PASS, including `Apply_CoversEveryKeyTheDictionaryDeclares`. **If that test fails, a dictionary key has no `Apply` write** — that is the §6.6 freeze, not a flaky test.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Controls/Theme tests/EyeCtrl.Controls.Tests/Theme/TokenDictionaryTests.cs
git commit -m "feat(theme): publish derived geometry via Apply, not just parse-time x:Static"
```

---

### Task 4: Pin the WPF clamp behaviour `CornerRadiusCircle` depends on

**Files:**
- Create: `tests/EyeCtrl.App.Tests/CornerRadiusClampTests.cs`

- [ ] **Step 1: Write the test**

`CornerRadiusCircle = 9999` is only correct because WPF clamps an over-large radius to an inscribed ellipse. Nothing in WPF's contract promises that, so pin it: a future runtime change must break the build, not the UI.

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Pins the WPF behaviour <see cref="ITheme.CornerRadiusCircle"/> rests on, measured in
/// design spec §4.1: an over-large CornerRadius clamps to an inscribed ellipse.
///
/// On a SQUARE element that is a circle, rendering pixel-identically to size / 2 — which is
/// what makes the token size-independent, and what lets QuickActionsGridWindow's 44x44 close
/// button drop its literal 22. On a non-square element it is an ellipse, NOT a stadium, which
/// is why the token is named Circle rather than Pill.
///
/// Renders to a RenderTargetBitmap on an STA thread. No window is shown and no input is
/// simulated — safe to run headless, and it carries no UI category so CI runs it.
/// </summary>
public class CornerRadiusClampTests
{
    private static byte[] Render(double w, double h, double radius, double dpi)
    {
        var border = new Border
        {
            Width = w,
            Height = h,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(radius),
        };
        border.Measure(new Size(w, h));
        border.Arrange(new Rect(0, 0, w, h));
        border.UpdateLayout();

        var scale = dpi / 96.0;
        var pw = (int)Math.Round(w * scale);
        var ph = (int)Math.Round(h * scale);
        var rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(border);

        var stride = pw * 4;
        var pixels = new byte[stride * ph];
        rtb.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static int DifferingPixels(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                n++;
        return n;
    }

    /// <summary>
    /// Fraction of pixels at least half covered. The 50% cutoff is the correct way to count a
    /// rasterised area and is the only one that reproduces the analytic value — spec §4.1
    /// records the same shape measuring 0.8094 / 0.7854 / 0.7671 / 0.7527 at cutoffs of
    /// 0 / 128 / 200 / 254, so a bare fraction without its threshold is meaningless.
    /// </summary>
    private static double CoveredFraction(byte[] pixels)
    {
        var covered = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 128) covered++;
        return (double)covered / (pixels.Length / 4);
    }

    public static TheoryData<double, double> SquareCases() => new()
    {
        { 30, 96 }, { 44, 96 }, { 45, 96 }, { 66, 96 }, { 100, 96 }, { 44, 144 }, { 44, 192 },
    };

    [StaTheory]
    [MemberData(nameof(SquareCases))]
    public void OnASquare_TheCircleToken_RendersIdenticallyToHalfTheSize(double size, double dpi)
    {
        var half = Render(size, size, size / 2, dpi);
        var token = Render(size, size, Tokens.CornerRadiusCircle, dpi);

        Check.WithCustomMessage(
                $"A {size}x{size} Border at CornerRadiusCircle must render exactly as one at "
                + $"{size / 2} ({dpi} dpi). If this fails, WPF no longer clamps an over-large "
                + "CornerRadius and the token is not size-independent — see design spec §4.1.")
            .That(DifferingPixels(half, token)).IsEqualTo(0);
    }

    [StaFact]
    public void OnANonSquare_TheCircleToken_IsAnEllipseNotAStadium()
    {
        // Pins the token's documented RESTRICTION, so misuse is caught rather than merely
        // discouraged in a doc comment.
        var stadium = Render(120, 40, 20, 96);
        var token = Render(120, 40, Tokens.CornerRadiusCircle, 96);

        Check.WithCustomMessage("A 120x40 at the Circle token must NOT match its stadium form.")
            .That(DifferingPixels(stadium, token)).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                "Coverage should sit on the inscribed ellipse's pi/4, confirming an ellipse "
                + "rather than a stadium. Counted at the 50% alpha cutoff.")
            .That(CoveredFraction(token)).IsCloseTo(Math.PI / 4, 0.01);
    }

    [Fact]
    public void TheOneCircleConsumer_IsSquare()
    {
        // The token is only valid on a square element. QuickActionsGridWindow's close button
        // is 44x44 today; if someone resizes it non-uniformly the circle silently becomes an
        // ellipse, so couple the two facts here.
        var xaml = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "src", "EyeCtrl.App", "Overlays",
                "QuickActionsGridWindow.xaml"));

        var match = System.Text.RegularExpressions.Regex.Match(
            xaml, @"x:Name=""CloseButton""[\s\S]{0,400}?Width=""(?<w>[\d.]+)""\s+Height=""(?<h>[\d.]+)""");

        Check.WithCustomMessage("Could not find CloseButton's Width and Height in the XAML.")
            .That(match.Success).IsTrue();
        Check.WithCustomMessage(
                "CloseButton must stay square, or CornerRadiusCircle renders an ellipse on it.")
            .That(match.Groups["w"].Value).IsEqualTo(match.Groups["h"].Value);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(
            typeof(CornerRadiusClampTests).Assembly.Location)!);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!.FullName;
    }
}
```

> **Attributes matter here and there is no choice to make.** `RenderTargetBitmap` and even
> `new Border` require an STA thread — an MTA `[Fact]` throws
> `InvalidOperationException` ("the calling thread must be STA") inside
> `FrameworkElement..ctor`. Use **`[StaTheory]`** on `OnASquare_TheCircleToken_RendersIdenticallyToHalfTheSize`
> and **`[StaFact]`** on `OnANonSquare_TheCircleToken_IsAnEllipseNotAStadium`. Both exist in the
> pinned `Xunit.StaFact` 3.0.13, and `OverlayResourceResolutionTests.cs:74` already uses
> `[StaTheory]`. `TheOneCircleConsumer_IsSquare` is a plain `[Fact]` — it only parses a file.

- [ ] **Step 2: Run to verify it passes (it pins existing behaviour)**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~CornerRadiusClampTests" --nologo
```

Expected: PASS, 8 tests. `TheOneCircleConsumer_IsSquare` passes now and keeps passing after Task 8 changes that Border's radius.

- [ ] **Step 3: Prove the pin actually pins something**

Temporarily change `Tokens.CornerRadiusCircle`'s value in all three themes from `9999.0` to `8.0`, re-run, and confirm the square theory **fails**. Then revert. A guard that has never failed is not known to work.

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.App.Tests/CornerRadiusClampTests.cs
git commit -m "test(theme): pin WPF's corner-radius clamp that CornerRadiusCircle relies on"
```

---

### Task 5: `ContainerRatio`

**Files:**
- Create: `src/EyeCtrl.Controls/Theme/ContainerRatio.cs`
- Create: `tests/EyeCtrl.Controls.Tests/Theme/ContainerRatioTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Controls.Tests.Theme;

using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The ratios are not theme tokens: they describe a glyph's size relative to the container
/// that bounds it, which no palette changes. They are pinned here because every value was
/// taken from a site that already used it (design spec §8.5) — if one drifts, the site it
/// came from has silently changed shape.
/// </summary>
public class ContainerRatioTests
{
    [Fact]
    public void Ratios_HoldTheValuesTheirSitesObserved()
    {
        Check.That(ContainerRatio.Badge).IsEqualTo(0.60);      // DragTrail 16/26 = 0.615
        Check.That(ContainerRatio.Dominant).IsEqualTo(0.45);   // QuickActions close 20/44 = 0.455
        Check.That(ContainerRatio.Large).IsEqualTo(0.40);      // Toolbar's existing 0.4; emoji 28/70
        Check.That(ContainerRatio.Medium).IsEqualTo(0.32);     // category 22/70; skin-tone 36/105
        Check.That(ContainerRatio.Small).IsEqualTo(0.23);      // tile 28/120; key 16/70; note 14/60
    }

    [Fact]
    public void Ratios_DescendAndStayWithinSaneBounds()
    {
        Check.That(ContainerRatio.Badge).IsStrictlyGreaterThan(ContainerRatio.Dominant);
        Check.That(ContainerRatio.Dominant).IsStrictlyGreaterThan(ContainerRatio.Large);
        Check.That(ContainerRatio.Large).IsStrictlyGreaterThan(ContainerRatio.Medium);
        Check.That(ContainerRatio.Medium).IsStrictlyGreaterThan(ContainerRatio.Small);
        Check.That(ContainerRatio.Small).IsStrictlyGreaterThan(0.0);
        Check.That(ContainerRatio.Badge).IsStrictlyLessThan(1.0);
    }

    [Fact]
    public void AppliedToTheirSites_TheRatiosReproduceTodaysSizes()
    {
        // Each cell is "container size x ratio ~= the literal it replaces". These are the
        // deltas the spec's §8.5 table commits to; if one drifts the visual change is larger
        // than the design allowed for.
        Check.That(26 * ContainerRatio.Badge).IsCloseTo(16.0, 0.5);     // 15.6
        Check.That(44 * ContainerRatio.Dominant).IsCloseTo(20.0, 0.5);  // 19.8
        Check.That(70 * ContainerRatio.Large).IsCloseTo(28.0, 0.5);     // 28.0
        Check.That(105 * ContainerRatio.Medium).IsCloseTo(36.0, 2.5);   // 33.6, the -2.4 outlier
        Check.That(70 * ContainerRatio.Small).IsCloseTo(16.0, 0.5);     // 16.1
        Check.That(120 * ContainerRatio.Small).IsCloseTo(28.0, 0.5);    // 27.6
        Check.That(60 * ContainerRatio.Small).IsCloseTo(14.0, 0.5);     // 13.8
        Check.That(100 * ContainerRatio.Small).IsCloseTo(22.0, 1.5);    // 23.0
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~ContainerRatioTests" --nologo
```

Expected: compile error — `The name 'ContainerRatio' does not exist`.

- [ ] **Step 3: Create the class**

```csharp
namespace EyeCtrl.Controls.Theme;

/// <summary>
/// A glyph or label's font size as a fraction of the container that bounds it.
///
/// Roughly a sixth of the app's font sizes are not steps on the type ramp: they are icons
/// and short labels sized to fit a box, and several of those boxes resize at runtime
/// (<c>KeyboardWindow</c>'s keys scale with a setting). Snapping those onto a text ramp
/// would resize icons relative to their containers. <c>ToolbarWindow</c> already modelled
/// the right approach with a local <c>ButtonSymbolSizeRatio = 0.4</c>; this generalises it.
///
/// **Named for containers, not glyphs**, because it also covers keyboard letters and
/// prediction words, for which "glyph" would be wrong.
///
/// Deliberately NOT theme tokens: a ratio describes a geometric relationship, and no palette
/// changes it. If one ever needs to vary per theme it can move to <see cref="ITheme"/> the
/// same way <see cref="ITheme.BorderThicknessStandard"/> did.
///
/// Every value below was observed at a site that already used it — see the design spec
/// §8.5 table. They are ratios, not free parameters.
/// </summary>
public static class ContainerRatio
{
    /// <summary>
    /// Glyph in a small circular badge, under about 32px, where a smaller ratio would be
    /// illegible. Observed: <c>DragTrailOverlay</c>'s grab/release icons at 16/26 = 0.615.
    /// </summary>
    public const double Badge = 0.60;

    /// <summary>
    /// The glyph *is* the control. Observed: <c>QuickActionsGridWindow</c>'s 44px close
    /// button at 20/44 = 0.455.
    /// </summary>
    public const double Dominant = 0.45;

    /// <summary>
    /// A prominent icon filling most of its cell. Observed: <c>ToolbarWindow</c>'s existing
    /// 0.4 verbatim, and the emoji cell at 28/70 = 0.400.
    /// </summary>
    public const double Large = 0.40;

    /// <summary>
    /// A mid-weight icon. Observed: emoji category 22/70 = 0.314, skin-tone 36/105 = 0.343,
    /// the banner's dwell-ring glyphs 22/64 = 0.344, the settings tab icon 30/100 = 0.300.
    /// </summary>
    public const double Medium = 0.32;

    /// <summary>
    /// An icon or short label above or beside other content. The most-observed ratio, at six
    /// sites: tile 28/120, keyboard key 16/70, prediction 16/70, layout-switch 16/70, note
    /// button 14/60, arc button 22/100 — all 0.220 to 0.233.
    /// </summary>
    public const double Small = 0.23;
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~ContainerRatioTests" --nologo
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Controls/Theme/ContainerRatio.cs tests/EyeCtrl.Controls.Tests/Theme/ContainerRatioTests.cs
git commit -m "feat(theme): add ContainerRatio for container-proportional glyph sizing"
```

---

### Task 6: The whitelist drift guard

**Read spec §10.4.** This is deliberately **not** shaped like `RawColourLiteralTests`. That guard enumerates *bad* forms, and commit `9e252e1` records the two holes that follow: `FromA?rgb` never matches `FromRgb`, and the pattern needs a literal first argument so a computed one escapes. The second is structural — any blacklist has a form its author did not think of. This guard allows only token references and flags everything else, so there is no forgotten form.

**Files:**
- Create: `tests/EyeCtrl.App.Tests/SourceScanner.cs`
- Create: `tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs`
- Modify: `tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs`

- [ ] **Step 1: Extract the shared scanner, and delete the duplicate in Task 4's test**

Task 4 shipped a private `RepoRoot()` in `CornerRadiusClampTests` because this helper did not exist
yet. **Delete `CornerRadiusClampTests.RepoRoot` now** and have
`TheOneCircleConsumer_IsSquare` call `SourceScanner.RepoRoot().FullName` instead — two copies of a
repo-root walker is the same drift this extraction exists to prevent.

Duplicating a comment stripper whose behaviour is subtly documented — the `(?<!:)//` rule that keeps `pack://` intact — is how two guards drift apart.

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Shared file-walking and comment-stripping for the repo-wide source guards
/// (<see cref="RawColourLiteralTests"/> and <see cref="RawGeometryLiteralTests"/>).
///
/// Extracted rather than copied: the comment stripper's behaviour is subtle — see
/// <see cref="StripComments"/> — and two independent copies would drift.
/// </summary>
internal static class SourceScanner
{
    /// <summary>Projects whose UI values must come from EyeCtrl.Controls.Theme.</summary>
    public static readonly string[] ScannedProjects =
    [
        "src/EyeCtrl.App",
        "src/EyeCtrl.Controls",
    ];

    /// <summary>Walks up from the test assembly to the directory holding EyeCtrl.slnx.</summary>
    /// <returns>The repository root.</returns>
    public static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(SourceScanner).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        return dir ?? throw new InvalidOperationException("Could not locate EyeCtrl.slnx");
    }

    /// <summary>
    /// Every .cs and .xaml file in the scanned projects, excluding obj/ and bin/, as
    /// (repo-relative path with forward slashes, comment-stripped text) pairs.
    /// </summary>
    /// <returns>One entry per scanned file.</returns>
    public static IEnumerable<(string Relative, string Text)> ScannedFiles()
    {
        var root = RepoRoot().FullName;
        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                yield return (Path.GetRelativePath(root, file).Replace('\\', '/'),
                              StripComments(File.ReadAllText(file)));
            }
        }
    }

    /// <summary>
    /// Comments are not live code — a comment explaining which value was *replaced* must not
    /// fail a guard. Strips XAML comments, C# block comments, and C# line comments;
    /// <c>//</c> preceded by <c>:</c> is left alone so <c>pack://</c> and <c>http://</c>
    /// inside string literals do not truncate the rest of the line.
    /// </summary>
    /// <param name="text">Raw file contents.</param>
    /// <returns>The text with comments replaced by spaces.</returns>
    public static string StripComments(string text) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline),
                @"/\*.*?\*/", " ", RegexOptions.Singleline),
            @"(?<!:)//[^\n]*", " ");
}
```

- [ ] **Step 2: Point `RawColourLiteralTests` at the shared scanner**

In `RawColourLiteralTests.cs`, delete its private `ScannedProjects`, `StripComments` and `RepoRoot` members, and replace the two test bodies' file walking with `SourceScanner.ScannedFiles()`. `NoUIFile_ContainsARawColourOutsideThePalette` becomes:

```csharp
    [Fact]
    public void NoUIFile_ContainsARawColourOutsideThePalette()
    {
        var offenders = new List<string>();

        foreach (var (relative, text) in SourceScanner.ScannedFiles())
        {
            if (Exempt.ContainsKey(relative)) continue;

            var found = RawColour.Matches(text)
                .Where(m => !IsFieldAccess(text, m))
                .Select(m => m.Value)
                .Distinct()
                .ToList();

            if (found.Count > 0)
                offenders.Add($"{relative}: {string.Join(", ", found)}");
        }

        Check.WithCustomMessage(
                "Raw colours found outside the palette. Migrate them onto "
                + "EyeCtrl.Controls.Theme.Tokens, or add the file to Exempt with a specific "
                + "reason:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }
```

and in `EveryExemption_NamesAFileThatExistsAndActuallyHasARawColour`, replace `RepoRoot().FullName` with `SourceScanner.RepoRoot().FullName` and `StripComments(...)` with `SourceScanner.StripComments(...)`.

- [ ] **Step 3: Write the guard**

```csharp
namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Repo-wide guard: no corner radius, font family, font size or font weight may be written
/// as a literal in UI source. Every one must come from
/// <see cref="EyeCtrl.Controls.Theme.Tokens"/> or <see cref="ContainerRatio"/>.
///
/// **This is a whitelist, and that is the whole point.** <see cref="RawColourLiteralTests"/>
/// enumerates *bad* forms, and commit 9e252e1 records the two holes that follow from it:
/// its pattern never matches <c>FromRgb</c> (the capital R), and it requires a literal first
/// argument, so a computed one escapes even once the casing is fixed. The second hole is
/// structural, not a typo — any blacklist has a form its author did not think of.
///
/// So this guard inverts the test: it finds every occurrence of the four property names and
/// allows only those that reference the token surface. A computed argument is an offender
/// like any other, because forms are never enumerated.
///
/// The allowed member names are obtained by **reflection** over Tokens and ContainerRatio,
/// never hard-coded: a whitelist's own failure mode is a stale allow-list, and reflecting
/// means this guard cannot disagree with the surface it guards.
///
/// No <see cref="RegexOptions.IgnorePatternWhitespace"/> — RawColourLiteralTests documents
/// how that mode makes whitespace inside a pattern silently insignificant.
/// </summary>
public class RawGeometryLiteralTests
{
    /// <summary>
    /// Files allowed to contain literals, each with the reason. A path listed here is exempt
    /// in full, so prefer migrating over listing.
    ///
    /// **During the migration this list also holds the not-yet-migrated files.** Each entry
    /// tagged PENDING is removed by the task that migrates that file;
    /// <see cref="EveryExemption_NamesAFileThatStillHasALiteral"/> then fails if one lingers
    /// after its literals are gone. By the end of Task 12 only the permanent entries remain.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/EyeCtrl.Controls/Theme/ITheme.cs"] =
            "declares the tokens — the interface is the definition site",
        ["src/EyeCtrl.Controls/Theme/ThemeBase.cs"] =
            "derives the WPF-typed forms from the primitives",
        ["src/EyeCtrl.Controls/Theme/Tokens.cs"] =
            "the static facade over ITheme — every token name appears here by design",
        ["src/EyeCtrl.Controls/Theme/MidnightTheme.cs"] =
            "declares the Midnight values (spec §5) — gated by tools/palette_validate.py",
        ["src/EyeCtrl.Controls/Theme/EmberTheme.cs"] =
            "declares the Ember values — gated by tools/palette_validate.py",
        ["src/EyeCtrl.Controls/Theme/ContrastTheme.cs"] =
            "declares the Contrast values — gated by tools/palette_validate.py",
        ["src/EyeCtrl.Controls/Theme/ContainerRatio.cs"] =
            "declares the ratios; its doc comments cite the observed values",

        // ── PENDING migration. Remove each as its task lands. ───────────────────
        // Task 7 (Controls radii + ramp):
        ["src/EyeCtrl.Controls/DwellButton.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellToggle.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellComboBox.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellColorPicker.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellLabel.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellSlider.cs"] = "PENDING Task 7",
        ["src/EyeCtrl.Controls/DwellArcButton.cs"] = "PENDING Task 12",
        // Task 8 (App XAML):
        ["src/EyeCtrl.App/Overlays/TargetHighlightWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/DwellFeedbackOverlay.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/DriftNoticeWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/SplashWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/EyeCareExerciseWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Overlays/CalibrationWindow.xaml"] = "PENDING Task 8",
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml"] = "PENDING Task 8",
        // Task 9 (App code-behind):
        ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/EmojiGridManager.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"] = "PENDING Task 9",
        ["src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs"] = "PENDING Task 12",
    };

    /// <summary>
    /// A geometry or type assignment **and its value**, captured together.
    ///
    /// Capturing the value is the whole design. An earlier draft of this guard matched the
    /// property name alone and then scanned 200 characters forward for any token reference,
    /// which cleared a literal whenever an unrelated token happened to sit nearby. Measured
    /// against the real tree it flagged **0 of 6** literals in QuickActionsGridWindow.xaml —
    /// including the <c>CornerRadius="22"</c> circle site, cleared by a neighbouring
    /// <c>{StaticResource BrushSurfaceMedium}</c> — and 3 of 7 in TrackStatusWindow.xaml. Dense
    /// object initialisers are the dominant form in EyeCtrl.App/Overlays, so the window
    /// approach caught almost nothing while looking rigorous.
    ///
    /// <c>(?![>=])</c> stops <c>=&gt;</c> and <c>==</c> parsing as assignment, so an
    /// expression-bodied member such as <c>public static FontFamily FontStandard =&gt; ...</c>
    /// is not read as a value. The unquoted value class excludes <c>,</c> <c>;</c> <c>)</c> but
    /// deliberately **not** newlines, so the wrapped form
    /// <c>CornerRadius = new CornerRadius(\n Tokens.X, Tokens.X, 0, 0)</c> still captures its
    /// first argument.
    ///
    /// <c>BorderThickness</c> is absent on purpose: it was migrated by the parent spec's §4.4
    /// and is guarded by TokenDictionaryTests.
    ///
    /// **Known limitation, recorded rather than hidden:** a value supplied other than by
    /// assignment — a <c>&lt;Setter Property="FontSize" Value="12"/&gt;</c>, or a positional
    /// constructor argument — is not matched. No such site exists in the two scanned projects
    /// today; if one is introduced, widen this pattern.
    /// </summary>
    private static readonly Regex Assignment = new(
        @"\b(?<prop>CornerRadius|FontFamily|FontSize|FontWeight)\s*=(?![>=])\s*(?:""(?<v>[^""]*)""|(?<v>[^,;\)]+))",
        RegexOptions.Compiled);

    /// <summary>
    /// Every allowed reference form, built from the actual token surface. Reflection, not a
    /// hard-coded list: a stale allow-list is a whitelist's own failure mode.
    /// </summary>
    private static IReadOnlyCollection<string> AllowedReferences()
    {
        // Every Tokens property is an allowed reference in both its C# and XAML form. That
        // already covers the derived keys (SmallCornerRadius, FontStandard, ...) because they
        // are Tokens properties — no second list to keep in step.
        var names = new List<string>();
        foreach (var p in typeof(Tokens).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (p.Name == nameof(Tokens.Current)) continue;
            names.Add($"Tokens.{p.Name}");
            names.Add($"{{StaticResource {p.Name}}}");
        }

        // ContainerRatio members are const, so they are fields rather than properties.
        foreach (var f in typeof(ContainerRatio).GetFields(BindingFlags.Public | BindingFlags.Static))
            names.Add($"ContainerRatio.{f.Name}");

        // Exactly one glyph size has to reach XAML through {x:Static}: the tile icon inside
        // QuickActionsGridWindow's ItemTemplate, because a DataTemplate is instantiated per item
        // and cannot multiply by a ratio inline (Task 12). Every other glyph site assigns from
        // code-behind and needs no entry here.
        //
        // Scoped to OverlayWindow SUBCLASSES, not the whole assembly. Reflecting over every type
        // would silently enrol any future public static double in EyeCtrl.App — measured, that
        // already admits three unrelated NoteLayout properties — and a guard whose trust surface
        // grows on its own is the wrong shape. Window classes are where {x:Static} consumers can
        // legitimately live, so that is the boundary.
        //
        // Still reflection rather than a hard-coded member name: a rename then produces a *false
        // positive* on the migrated site, which fails loudly, instead of a silent hole.
        foreach (var type in typeof(OverlayWindow).Assembly.GetTypes())
        {
            if (!typeof(OverlayWindow).IsAssignableFrom(type)) continue;
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
                if (prop.PropertyType == typeof(double))
                    names.Add($"{type.Name}.{prop.Name}");
        }

        return names;
    }

    /// <summary>
    /// True if the captured value references the token surface. **Only the value is examined,
    /// never its surroundings** — that is what stops a neighbouring token from clearing a
    /// literal.
    /// </summary>
    /// <param name="value">The captured right-hand side of the assignment.</param>
    /// <param name="allowed">Allowed reference forms, from <see cref="AllowedReferences"/>.</param>
    /// <returns>True if the value is token-backed.</returns>
    private static bool IsTokenBacked(string value, IReadOnlyCollection<string> allowed) =>
        allowed.Any(a => value.Contains(a, StringComparison.Ordinal));

    [Fact]
    public void NoUIFile_WritesAGeometryOrTypeValueOutsideTheTokens()
    {
        var allowed = AllowedReferences();
        var offenders = new List<string>();

        foreach (var (relative, text) in SourceScanner.ScannedFiles())
        {
            if (Exempt.ContainsKey(relative)) continue;

            var bad = Assignment.Matches(text)
                .Where(m => !IsTokenBacked(m.Groups["v"].Value, allowed))
                .Select(m => $"{m.Groups["prop"].Value}={m.Groups["v"].Value.Trim()}")
                .Distinct()
                .ToList();

            if (bad.Count > 0)
                offenders.Add($"{relative}: {string.Join(", ", bad)}");
        }

        Check.WithCustomMessage(
                "Geometry or type values written outside the tokens. Migrate them onto "
                + "EyeCtrl.Controls.Theme.Tokens or ContainerRatio, or add the file to Exempt "
                + "with a specific reason:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    [Fact]
    public void EveryExemption_NamesAFileThatStillHasALiteral()
    {
        // Stops the allow-list rotting: an exemption for a file with nothing left to exempt
        // is protecting nothing and would hide a future literal. This is also what forces the
        // PENDING entries to be removed as the migration tasks land.
        var allowed = AllowedReferences();
        var byPath = SourceScanner.ScannedFiles().ToDictionary(f => f.Relative, f => f.Text,
            StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();

        foreach (var (relative, _) in Exempt)
        {
            if (!byPath.TryGetValue(relative, out var text))
            {
                stale.Add($"{relative}: file does not exist or is not scanned");
                continue;
            }

            var any = Assignment.Matches(text)
                .Any(m => !IsTokenBacked(m.Groups["v"].Value, allowed));
            if (!any)
                stale.Add($"{relative}: fully migrated — drop the exemption");
        }

        Check.WithCustomMessage("Stale exemptions:\n  " + string.Join("\n  ", stale))
            .That(stale).IsEmpty();
    }
}
```

- [ ] **Step 4: Run both guards**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~RawGeometryLiteralTests|FullyQualifiedName~RawColourLiteralTests" --nologo
```

Expected: PASS. If `NoUIFile_WritesAGeometryOrTypeValueOutsideTheTokens` fails, a file with literals
is missing from `Exempt` — add it with a `PENDING Task N` reason matching the task that will migrate
it. If `EveryExemption_...` fails, a listed file has nothing left to exempt — delete that entry.

> **The report counts distinct values, not sites**, because of the `.Distinct()` call. A file with
> seven literals over three distinct values reports three entries — `TrackStatusWindow.xaml` does
> exactly that (`CornerRadius=8`, `FontSize=10`, `FontWeight=Bold` covering 7 sites), and
> `EyeCareExerciseWindow.xaml` reports 8 covering 16. **No file or site is hidden by this** — every
> non-token-backed value fails the test; only the message text collapses. Do not read a lower entry
> count as an under-catch.

- [ ] **Step 5: Mutation-prove the guard**

A guard that has never failed is not known to work — and a probe that tests the wrong shape is
worse, because it certifies a broken guard. **The first version of this probe put each bad form in
its own method with no neighbouring token reference, which the window-scanning guard caught 8/8
while missing 0/6 real literals in `QuickActionsGridWindow.xaml`.** So the probe must reproduce the
dense object-initialiser shape that dominates `EyeCtrl.App/Overlays`.

Following `9e252e1`'s method, create `src/EyeCtrl.App/Overlays/GuardProbe.cs`:

```csharp
namespace EyeCtrl.App.Overlays;

// System.Windows.Media is REQUIRED, not optional: EyeCtrl.App sets
// <UseWindowsForms>true</UseWindowsForms>, so an unqualified FontFamily binds to
// System.Drawing.FontFamily and the file fails with CS0029. EyeCtrl.Controls.Theme is
// required for the Tokens reads in the ...BesideAToken cases.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;

internal static class GuardProbe
{
    // ── Isolated forms: each bad value with nothing else around it. ─────────────
    internal static Border BareLiteral() => new() { CornerRadius = new CornerRadius(7) };

    internal static Border ComputedArgument(double x) => new() { CornerRadius = new CornerRadius(x * 2) };

    internal static TextBlock RawFamily() => new() { FontFamily = new FontFamily("Arial") };

    internal static TextBlock LiteralSize() => new() { FontSize = 13 };

    internal static TextBlock LiteralWeight() => new() { FontWeight = FontWeights.Bold };

    // ── The shape that actually matters: bad values sitting NEXT TO good token
    // references, in the dense initialiser form used throughout the overlays. A
    // guard that scans for a nearby token instead of examining the value itself
    // passes every case above and fails every case below. ──────────────────────
    internal static TextBlock LiteralSizeBesideAToken() => new()
    {
        FontSize = 13,
        Foreground = Tokens.BrushTextPrimary,
        FontWeight = Tokens.FontWeightMedium,
    };

    internal static Border LiteralRadiusBesideAToken() => new()
    {
        CornerRadius = new CornerRadius(22),
        Background = Tokens.BrushSurfaceMedium,
        BorderBrush = Tokens.BrushBorderSubtle,
    };

    internal static TextBlock ComputedSizeBesideAToken(double n) => new()
    {
        FontSize = n * 0.4,
        Foreground = Tokens.BrushTextPrimary,
    };

    internal static TextBlock RawFamilyBesideAToken() => new()
    {
        FontFamily = new FontFamily("Arial"),
        FontSize = Tokens.FontSizeBody,
    };
}
```

Run the guard and confirm it **fails**, that the message **names `GuardProbe.cs`**, and that it
reports **8 distinct values covering all 9 bad sites** — `FontSize=13` appears in both
`LiteralSize()` and `LiteralSizeBesideAToken()` and collapses to one entry, because the report
de-duplicates by value. Check specifically that:

- the four `...BesideAToken` cases are caught. These are the ones the window-scanning draft
  missed, and they are the shape real overlay code takes. **If any of them is cleared, the guard is
  still broken — stop and fix it before migrating anything.**
- `ComputedArgument` and `ComputedSizeBesideAToken` are caught. A computed value is the hole a
  blacklist cannot close, and the reason this guard is a whitelist (spec §10.4).
- `FontSize = Tokens.FontSizeBody` inside `RawFamilyBesideAToken` is **not** reported — a good
  value next to a bad one must still pass, or the guard cries wolf.

Then **delete the probe file** and re-run to confirm green.

**Sanity-check the guard against the real tree before moving on.** With the `Exempt` list as
written, the guard should be green; temporarily empty `Exempt` and confirm it now reports a large
number of files including `QuickActionsGridWindow.xaml` with **all six** of its values, then restore
the list. If emptying `Exempt` reports few or no findings, the value capture is not working.

- [ ] **Step 6: Commit**

```bash
git add tests/EyeCtrl.App.Tests/SourceScanner.cs tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs tests/EyeCtrl.App.Tests/RawColourLiteralTests.cs
git commit -m "test(theme): add a whitelist geometry/type guard, closing the blacklist holes"
```

---

### Task 7: Radii and ramp in `EyeCtrl.Controls`

Six files, all C#. Every edit is literal → token. **Radii and font values are done together per file** so a file leaves the `Exempt` list in one task rather than two.

**Files (all Modify):**
- `src/EyeCtrl.Controls/DwellButton.cs:72` radius 8, `:83` FontSize 24
- `src/EyeCtrl.Controls/DwellToggle.cs:71` radius 8, `:60` FontSize 22, and its `FontWeight`
- `src/EyeCtrl.Controls/DwellComboBox.cs:140` radius 6, `:112` FontSize 22 + `FontWeight`
- `src/EyeCtrl.Controls/DwellColorPicker.cs:122` radius 6
- `src/EyeCtrl.Controls/DwellLabel.cs:32` FontSize 22
- `src/EyeCtrl.Controls/DwellSlider.cs:87` FontSize 24 + `FontWeight`

- [ ] **Step 1: Apply the substitutions**

| File:line | From | To |
|---|---|---|
| `DwellButton.cs:72` | `Root.CornerRadius = new CornerRadius(8);` | `Root.CornerRadius = Tokens.StandardCornerRadius;` |
| `DwellButton.cs:83` | `FontSize = 24,` | `FontSize = Tokens.FontSizeTitle,` |
| `DwellButton.cs` | `FontWeight = FontWeights.Bold,` | `FontWeight = Tokens.FontWeightStrong,` |
| `DwellToggle.cs:71` | `Root.CornerRadius = new CornerRadius(8);` | `Root.CornerRadius = Tokens.StandardCornerRadius;` |
| `DwellToggle.cs:60` | `FontSize = 22,` | `FontSize = Tokens.FontSizeTitle,` |
| `DwellToggle.cs` | `FontWeight = FontWeights.SemiBold,` | `FontWeight = Tokens.FontWeightMedium,` |
| `DwellComboBox.cs:140` | `CornerRadius = new CornerRadius(6),` | `CornerRadius = Tokens.StandardCornerRadius,` |
| `DwellComboBox.cs:112` | `FontSize = 22,` | `FontSize = Tokens.FontSizeTitle,` |
| `DwellComboBox.cs` | `FontWeight = FontWeights.SemiBold,` | `FontWeight = Tokens.FontWeightMedium,` |
| `DwellColorPicker.cs:122` | `btn.Root.CornerRadius = new CornerRadius(6);` | **delete the line** |
| `DwellLabel.cs:32` | `FontSize = 22,` | `FontSize = Tokens.FontSizeTitle,` |
| `DwellSlider.cs:87` | `FontSize = 24,` | `FontSize = Tokens.FontSizeTitle,` |
| `DwellSlider.cs` | `FontWeight = FontWeights.SemiBold,` | `FontWeight = Tokens.FontWeightMedium,` |

`DwellColorPicker.cs:122` is deleted rather than converted: it existed only to override `DwellButton`'s radius down to 6, and both are now `Standard`. Add a comment where it was:

```csharp
            // No radius override: swatches now share DwellButton's CornerRadiusStandard.
```

`Tokens` is already imported in each of these files (they use `Tokens.Brush*`). Verify with a build.

- [ ] **Step 2: Remove the six `Exempt` entries**

In `RawGeometryLiteralTests.cs`, delete the six `PENDING Task 7` lines. Leave `DwellArcButton.cs` (Task 12).

- [ ] **Step 3: Build and test**

```bash
dotnet build EyeCtrl.slnx --nologo
```

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: build 0 errors; tests PASS. `EveryExemption_NamesAFileThatStillHasALiteral` is what confirms you removed the right entries.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Controls tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs
git commit -m "refactor(controls): read radii, sizes and weights from tokens"
```

---

### Task 8: Radii and type in App XAML

**Files (all Modify), with every substitution:**

| File:line | From | To |
|---|---|---|
| `Overlays/TargetHighlightWindow.xaml:10` | `CornerRadius="2"` | `CornerRadius="{StaticResource SmallCornerRadius}"` |
| `Overlays/DwellFeedbackOverlay.xaml:7` | `CornerRadius="4"` | `CornerRadius="{StaticResource SmallCornerRadius}"` |
| `Overlays/ToolbarWindow.xaml:9` | `CornerRadius="4"` | `CornerRadius="{StaticResource SmallCornerRadius}"` |
| `Overlays/DriftNoticeWindow.xaml:15` | `CornerRadius="6"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Overlays/DriftNoticeWindow.xaml:18` | `FontSize="18"` | `FontSize="{StaticResource FontSizeSubtitle}"` |
| `Overlays/KeyboardWindow.xaml:9` | `CornerRadius="8"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Overlays/QuickActionsGridWindow.xaml:11` | `CornerRadius="8"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Overlays/QuickActionsGridWindow.xaml:23` | `CornerRadius="22"` | `CornerRadius="{StaticResource CircleCornerRadius}"` |
| `Overlays/QuickActionsGridWindow.xaml:26` | `FontSize="20"` | **Task 12** (container ratio) — leave |
| `Overlays/QuickActionsGridWindow.xaml:47` | `CornerRadius="6"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Overlays/QuickActionsGridWindow.xaml:54` | `FontSize="28"` | **Task 12** — leave |
| `Overlays/QuickActionsGridWindow.xaml:57` | `FontSize="14"` | `FontSize="{StaticResource FontSizeBodyLarge}"` |
| `Overlays/TaskSwitcherWindow.xaml:22` | `CornerRadius="8"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Overlays/TaskSwitcherWindow.xaml:25` | `FontSize="12"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Overlays/TaskSwitcherWindow.xaml:35` | `FontSize="14"` | `FontSize="{StaticResource FontSizeBodyLarge}"` |
| `Overlays/TrackStatusWindow.xaml:8` | `CornerRadius="8"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Overlays/TrackStatusWindow.xaml:10,18,19,22,23` | `FontSize="10"` ×5 | `FontSize="{StaticResource FontSizeBody}"` ×5 |
| `Overlays/TrackStatusWindow.xaml:10` | `FontWeight="Bold"` | `FontWeight="{StaticResource FontWeightStrong}"` |
| `Overlays/SplashWindow.xaml:8` | `CornerRadius="12"` | `CornerRadius="{StaticResource LargeCornerRadius}"` |
| `Overlays/SplashWindow.xaml:14` | `FontSize="36"` | `FontSize="{StaticResource FontSizeDisplay}"` |
| `Overlays/SplashWindow.xaml:14` | `FontWeight="SemiBold"` | `FontWeight="{StaticResource FontWeightMedium}"` |
| `Overlays/EyeCareExerciseWindow.xaml:39` | `CornerRadius="14"` | `CornerRadius="{StaticResource LargeCornerRadius}"` |
| `Overlays/EyeCareExerciseWindow.xaml:22` | `FontSize="18"` | `FontSize="{StaticResource FontSizeSubtitle}"` |
| `Overlays/EyeCareExerciseWindow.xaml:22` | `FontWeight="SemiBold"` | `FontWeight="{StaticResource FontWeightMedium}"` |
| `Overlays/EyeCareExerciseWindow.xaml:24,44` | `FontSize="12"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Overlays/EyeCareExerciseWindow.xaml:42` | `FontSize="13"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Overlays/EyeCareExerciseWindow.xaml:47,51,55` | `FontSize="22"` | `FontSize="{StaticResource FontSizeTitle}"` |
| `Overlays/EyeCareExerciseWindow.xaml:48,52,56,59` | `FontSize="9"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Overlays/EyeCareExerciseWindow.xaml` | `FontWeight="Bold"` (on `SummaryAccuracy` etc.) | `FontWeight="{StaticResource FontWeightStrong}"` |
| `Overlays/EyeCareBannerWindow.xaml:22` | `FontSize="16"` | `FontSize="{StaticResource FontSizeSubtitle}"` |
| `Overlays/EyeCareBannerWindow.xaml:22` | `FontWeight="SemiBold"` | `FontWeight="{StaticResource FontWeightMedium}"` |
| `Overlays/EyeCareBannerWindow.xaml:24` | `FontSize="11"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Overlays/EyeCareBannerWindow.xaml:30,37` | `FontSize="22"` | **Task 12** — leave |
| `Overlays/EyeCareBannerWindow.xaml:32,39` | `FontSize="9"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Overlays/CalibrationWindow.xaml:29` | `FontSize="16"` | `FontSize="{StaticResource FontSizeSubtitle}"` |
| `Overlays/CalibrationWindow.xaml:30` | `FontFamily="Segoe UI"` | **delete the attribute** — Task 10 wires it app-wide |
| `Overlays/CalibrationWindow.xaml:36` | `FontSize="32"` | `FontSize="{StaticResource FontSizeDisplay}"` |
| `Overlays/CalibrationWindow.xaml:40` | `FontSize="18"` | `FontSize="{StaticResource FontSizeSubtitle}"` |
| `Overlays/CalibrationWindow.xaml:43` | `FontSize="14"` | `FontSize="{StaticResource FontSizeBodyLarge}"` |
| `Overlays/CalibrationWindow.xaml:46,61` | `FontSize="11"` | `FontSize="{StaticResource FontSizeBody}"` |
| `Views/SettingsWindow.xaml:20` | `CornerRadius="8"` | `CornerRadius="{StaticResource StandardCornerRadius}"` |
| `Views/SettingsWindow.xaml:23` | `FontSize="34"` | `FontSize="{StaticResource FontSizeDisplay}"` |
| `Views/SettingsWindow.xaml:23` | `FontWeight="Bold"` | `FontWeight="{StaticResource FontWeightStrong}"` |
| `Views/SettingsWindow.xaml:58` | `CornerRadius="12"` | `CornerRadius="{StaticResource LargeCornerRadius}"` |
| `Views/SettingsWindow.xaml:64` | `FontSize="24"` | `FontSize="{StaticResource FontSizeTitle}"` |
| `Views/SettingsWindow.xaml:65` | `FontWeight="SemiBold"` | `FontWeight="{StaticResource FontWeightMedium}"` |

> **Weight sites are easy to miss because the tables above are organised by radius and size.**
> Spec §8.2 counts **17** `FontWeight` sites. Two of them —
> `EyeCareExerciseWindow.xaml:22` and `SettingsWindow.xaml:65` — were absent from an earlier
> draft of this table and would have shipped unmigrated, because the ramp rows next to them
> make the file look done. After finishing this task, confirm the count:
>
> ```bash
> grep -rn 'FontWeight="[A-Za-z]' src/EyeCtrl.App src/EyeCtrl.Controls
> ```
>
> Expected: no output.
>
> Line numbers drift as you edit. Re-grep per file rather than trusting them: `grep -n 'CornerRadius\|FontSize\|FontWeight\|FontFamily' <file>`.

- [ ] **Step 1: Apply every substitution above**

- [ ] **Step 2: Remove the `Exempt` entries for files with nothing left**

`QuickActionsGridWindow.xaml` and `EyeCareBannerWindow.xaml` keep their entries (Task 12 owns their remaining glyph sizes) — change their reason to `"PENDING Task 12"`. Delete the rest of the Task 8 entries.

- [ ] **Step 3: Build and test**

```bash
dotnet build EyeCtrl.slnx --nologo
```

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: build 0 errors, tests PASS. `OverlayResourceResolutionTests` is what catches a misspelled resource key — a typo here throws at XAML load, not at compile time, so **do not skip the test run**.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs
git commit -m "refactor(overlays): read radii and type from tokens in XAML"
```

---

### Task 9: Radii and type in App code-behind, and the eight dead consts

**Files (all Modify):**

| File:line | From | To |
|---|---|---|
| `Overlays/ToolbarWindow.xaml.cs:18` | `private const int BorderCornerRadius = 4;` | **delete** |
| `Overlays/ToolbarWindow.xaml.cs:81,124` | `CornerRadius = new CornerRadius(BorderCornerRadius),` | `CornerRadius = Tokens.SmallCornerRadius,` |
| `Overlays/KeyboardWindow.xaml.cs:195,496` | `CornerRadius = new CornerRadius(6),` | `CornerRadius = Tokens.StandardCornerRadius,` |
| `Overlays/KeyboardWindow.xaml.cs:545` | `FontSize = 12,` | `FontSize = Tokens.FontSizeBody,` |
| `Overlays/NoteWindow.xaml.cs:69` | `CornerRadius = new CornerRadius(6),` | `CornerRadius = Tokens.StandardCornerRadius,` |
| `Overlays/NoteWindow.xaml.cs:83` | `CornerRadius = new CornerRadius(6, 6, 0, 0)` | see code below |
| `Overlays/NoteWindow.xaml.cs:87` | `FontSize = 10,` | `FontSize = Tokens.FontSizeBody,` |
| `Overlays/NoteWindow.xaml.cs:124,139` | `FontSize = 12,` | `FontSize = Tokens.FontSizeBody,` |
| `Overlays/EmojiGridManager.cs:151,318,466` | `CornerRadius = new CornerRadius(6),` | `CornerRadius = Tokens.StandardCornerRadius,` |
| `Overlays/EmojiGridManager.cs:430` | `CornerRadius = new CornerRadius(8),` | `CornerRadius = Tokens.StandardCornerRadius,` |
| `Views/SettingsWindow.xaml.cs:284,408,1458` | `CornerRadius = new CornerRadius(8),` | `CornerRadius = Tokens.StandardCornerRadius,` |
| `Views/SettingsWindow.xaml.cs:299` | `FontSize = 22,` | `FontSize = Tokens.FontSizeTitle,` |
| `Views/SettingsWindow.xaml.cs:382,1465` | `FontSize = 24,` | `FontSize = Tokens.FontSizeTitle,` |
| `Views/SettingsWindow.xaml.cs` | `FontWeight = FontWeights.SemiBold` / `.Bold` | `Tokens.FontWeightMedium` / `Tokens.FontWeightStrong` |
| `Views/SettingsWindow.xaml.cs:35,37,38,42` | the 4 dead font consts | **delete** |
| `Views/SettingsWindow.xaml.cs:36,39,40,41` | the 4 dead layout consts | **delete** |

- [ ] **Step 1: The asymmetric note title bar**

Spec §7.2: the only asymmetric radius in the codebase, and because it is C# it needs no new resource and no exemption. At `NoteWindow.xaml.cs:83`:

```csharp
            // Top-only radius: the title bar sits flush against the note body below it.
            // Derived from the same token, so it cannot drift from the body's corners.
            CornerRadius = new CornerRadius(
                Tokens.CornerRadiusStandard, Tokens.CornerRadiusStandard, 0, 0)
```

- [ ] **Step 2: Delete all eight dead consts**

Spec §8.7. Each occurs exactly once — its own declaration — so nothing reads them and deleting them moves nothing. Verify before deleting:

```bash
for c in TabIconFontSize TabSymbolFontSize PaginationCounterFontSize PaginationSymbolFontSize TabContainerHeight PaginationCounterMinWidth PaginationButtonWidth PaginationButtonHeight; do echo "$c: $(grep -c "$c" src/EyeCtrl.App/Views/SettingsWindow.xaml.cs)"; done
```

Expected: `1` for every one. If any reads `2` or more, **stop** — it is live; convert it instead of deleting it.

- [ ] **Step 3: Apply the remaining substitutions from the table**

- [ ] **Step 4: Remove the `Exempt` entries**

Delete the Task 9 entries except `EmojiGridManager.cs` and `KeyboardWindow.xaml.cs`, whose glyph sizes belong to Task 12 — change those two to `"PENDING Task 12"`.

- [ ] **Step 5: Build and test**

```bash
dotnet build EyeCtrl.slnx --nologo
```

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs
git commit -m "refactor(overlays): read radii and type from tokens in code-behind; drop 8 dead consts"
```

---

### Task 10: Application-wide font family

**Read spec §8.8.** All 25 window classes in `EyeCtrl.App` derive from `OverlayWindow`, so one constructor assignment reaches the whole UI.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/OverlayWindow.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`
- Create: `tests/EyeCtrl.App.Tests/OverlayFontTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace EyeCtrl.App.Tests;

using System.Linq;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Guards the one assignment that gives the whole application its font (design spec §8.8).
/// Every window class in EyeCtrl.App derives from OverlayWindow, so the base constructor is
/// the single point of control — no OverrideMetadata, which is process-wide and single-shot.
/// </summary>
public class OverlayFontTests
{
    [StaFact]
    public void OverlayWindow_TakesItsFontFamilyFromTheToken()
    {
        // Constructed, never shown: OverlayWindow's Win32 interop runs in
        // OnSourceInitialized, which needs an HWND and therefore a Show().
        var window = new OverlayWindow();
        Check.That(window.FontFamily.Source).IsEqualTo(Tokens.FontFamilyStandard);
    }

    [StaFact]
    public void TheAssignedFont_DiffersFromWPFsDefault()
    {
        // The failure mode of a font token is being a silent no-op. If the token happened to
        // equal the inherited default, this suite would pass while proving nothing.
        var window = new OverlayWindow();
        Check.WithCustomMessage(
                "The font token matches WPF's inherited default, so the assignment in "
                + "OverlayWindow's constructor is unobservable and this guard is vacuous.")
            .That(window.FontFamily.Source)
            .IsNotEqualTo(SystemFonts.MessageFontFamily.Source);
    }

    [StaFact]
    public void TheFontChain_ResolvesToARealGlyphTypeface()
    {
        // Closes the gap spec §4.2 measured but nothing guarded: a typo in the chain string
        // does not throw. WPF composites a fallback matching no single installed face, so the
        // app would render the *undefined* degradation path while the token looked deliberate.
        // A resolvable chain is the assertion; WHICH member resolves is deliberately not,
        // because falling back from Segoe UI Variable Text to Segoe UI is the chain working.
        var typeface = new Typeface(Tokens.FontStandard, FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

        Check.WithCustomMessage(
                $"The font chain '{Tokens.FontFamilyStandard}' resolves to no glyph typeface. "
                + "Check every family name in it for typos — an unresolvable chain silently "
                + "renders WPF's composite fallback (design spec §4.2).")
            .That(typeface.TryGetGlyphTypeface(out _)).IsTrue();
    }

    [Fact]
    public void NoWindowClass_BypassesOverlayWindow()
    {
        // §8.8's mechanism assumes universal inheritance. A Window subclass that skips
        // OverlayWindow would silently keep the system font.
        var strays = typeof(OverlayWindow).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t)
                     && t != typeof(Window)
                     && !typeof(OverlayWindow).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .ToList();

        Check.WithCustomMessage(
                "These Window subclasses do not derive from OverlayWindow, so they miss the "
                + "app-wide font: " + string.Join(", ", strays))
            .That(strays).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~OverlayFontTests" --nologo
```

Expected: `OverlayWindow_TakesItsFontFamilyFromTheToken` FAILS — the window still has the inherited default.

- [ ] **Step 3: Assign the font in `OverlayWindow`'s constructor**

`OverlayWindow` currently has no explicit constructor. Add one immediately after the field declarations:

```csharp
    /// <summary>
    /// Gives every overlay the themed font family. All 25 window classes in this assembly
    /// derive from OverlayWindow, so this single assignment reaches the whole UI (design
    /// spec §8.8).
    ///
    /// A base constructor rather than TextElement.FontFamilyProperty.OverrideMetadata: that
    /// is process-wide and can only be called once, which collides across tests in one
    /// runner. And it runs before the derived class's InitializeComponent, so a deliberate
    /// FontFamily in a window's XAML still wins.
    ///
    /// Read from Tokens rather than a resource lookup because Tokens is static and needs no
    /// WPF Application — see Tokens' own doc comment.
    /// </summary>
    public OverlayWindow()
    {
        FontFamily = Tokens.FontStandard;
    }
```

Add `using EyeCtrl.Controls.Theme;` to `OverlayWindow.cs`.

- [ ] **Step 4: Fix the startup ordering (spec §16, `m2`)**

`SplashWindow` is constructed at `App.xaml.cs:114`, before `ThemeStartup.Apply` at `:127`, so it
would read Midnight's font rather than the chosen theme's. Harmless while all three themes share one
family, latent otherwise.

**The move is definite: relocate both `InitializeSettingsAndLogging()` (`:120`) and
`ThemeStartup.Apply(settings, Resources);` (`:127`) above the splash block.** `Apply` needs
`settings`, which is why the settings call moves with it; `InitializeSettingsAndLogging` touches only
settings, Serilog and the exception handlers, and has no splash dependency. `InitializeDpi(splash)`
is the only consumer that genuinely needs the splash to exist first, and it stays where it is.
`ThemeStartupTests` does not encode the current order, so nothing else has to change.

Add above the relocated call:

```csharp
        // Theme must be active before ANY window is constructed: OverlayWindow's constructor
        // reads Tokens.FontStandard, and a window built earlier would silently keep the
        // default theme's values. The splash used to be built first — see spec §16.
```

If `ThemeStartup.Apply` depends on `settings` being loaded, move only as early as that allows and confirm the splash is after it.

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: PASS. `ThemeStartupTests` also exercises this ordering — if it fails, the move broke an assumption; read the message rather than reverting blindly.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App tests/EyeCtrl.App.Tests/OverlayFontTests.cs
git commit -m "feat(overlays): give every window the themed font family from one assignment"
```

---

### Task 11: Sweep any remaining weight and size literals

By now Tasks 7–10 have converted the enumerated sites. This task closes whatever the guard still reports, so the `Exempt` list reaches only its seven permanent entries plus the Task 12 ones.

- [ ] **Step 1: Ask the guard what is left**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~RawGeometryLiteralTests" --nologo
```

- [ ] **Step 2: For each file the message names, convert per Appendix A**

Use the spec's Appendix A to decide the token for each site: `T` rows take the §8.4 ramp token for their current value (9/10/11/12/13 → `FontSizeBody`, 14 → `FontSizeBodyLarge`, 16/18 → `FontSizeSubtitle`, 22/24 → `FontSizeTitle`, 32/34/36 → `FontSizeDisplay`); `C` rows are Task 12's and stay exempt.

- [ ] **Step 3: Test and commit**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

```bash
git add -A src tests
git commit -m "refactor(overlays): finish the type-ramp migration"
```

---

### Task 12: Container ratios

**Files (all Modify):** `Overlays/QuickActionsGridWindow.xaml`, `Overlays/EyeCareBannerWindow.xaml`, `Overlays/ToolbarWindow.xaml.cs`, `Overlays/KeyboardWindow.xaml.cs`, `Overlays/EmojiGridManager.cs`, `Overlays/NoteWindow.xaml.cs`, `Overlays/DragTrailOverlay.xaml.cs`, `Views/SettingsWindow.xaml.cs`, `src/EyeCtrl.Controls/DwellArcButton.cs`

The 16 sites and their containers are in spec §8.5's table. XAML sites cannot express a ratio, so **move them to code-behind or bind them**; the two XAML cases below are set in code-behind instead.

- [ ] **Step 1: Convert the code-behind sites**

| File:line | From | To |
|---|---|---|
| `ToolbarWindow.xaml.cs:19` | `private const double ButtonSymbolSizeRatio = 0.4;` | **delete** |
| `ToolbarWindow.xaml.cs:70,113` | `FontSize = _buttonSize * ButtonSymbolSizeRatio,` | `FontSize = _buttonSize * ContainerRatio.Large,` |
| `KeyboardWindow.xaml.cs:183` | `FontSize = 16,` | `FontSize = _keySize * ContainerRatio.Small,` |
| `KeyboardWindow.xaml.cs:441` | `FontSize = 16,` | `FontSize = _keySize * ContainerRatio.Small,` |
| `EmojiGridManager.cs:140` | `FontSize = 28,` | `FontSize = keySize * ContainerRatio.Large,` |
| `EmojiGridManager.cs:322` | `FontSize = 22,` | `FontSize = keySize * ContainerRatio.Medium,` |
| `EmojiGridManager.cs:419` | `FontSize = 36,` | `FontSize = keySize * 1.5 * ContainerRatio.Medium,` |
| `EmojiGridManager.cs:455` | `FontSize = 16,` | `FontSize = keySize * ContainerRatio.Small,` |
| `NoteWindow.xaml.cs:257` | `FontSize = 14,` | `FontSize = ButtonDiameter * ContainerRatio.Small,` |
| `SettingsWindow.xaml.cs:273` | `FontSize = 30,` | `FontSize = 100 * ContainerRatio.Medium,` |
| `SettingsWindow.xaml.cs:415` | `FontSize = 30,` | `FontSize = 80 * ContainerRatio.Large,` |

> The two `SettingsWindow` containers are **literals** at `:282` (`Height = 100`) and `:405-406` (`Width = 100`, `Height = 80`) — the consts that appear to describe them were dead and Task 9 deleted them. Add a local const in each method rather than repeating the magic number, e.g. `const double TabHeight = 100;` used by both the `Border` and the `FontSize`.

The `KeyboardWindow` and `EmojiGridManager` conversions also **fix a real bug**: `_keySize` is `BaseKeySize * keySizeMultiplier`, so today enlarging the keys produces bigger keys carrying the same small letters.

Add `using EyeCtrl.Controls.Theme;` where missing.

- [ ] **Step 2: `DwellArcButton`'s defaulted parameter**

A default parameter cannot reference another argument, so make it nullable. In `src/EyeCtrl.Controls/DwellArcButton.cs`:

```csharp
    // Was: double fontSize = DefaultSymbolFontSize
    // A defaulted parameter cannot reference `radius`, so resolve it inside the method.
    // DefaultSymbolFontSize is deleted — 22 was 0.22 of the 100px diameter every caller uses.
    public static DwellArcButton Create(Point center, double radius, string? symbol,
        Brush? fill = null, Brush? stroke = null, Brush? arcBrush = null, Brush? symbolBrush = null,
        double arcThickness = DefaultArcThickness, double arcInset = DefaultArcInset,
        double? fontSize = null)
```

**`dia` already exists** — `var dia = radius * 2;` is declared at `DwellArcButton.cs:47`. Do not
re-declare it. Immediately after it, add:

```csharp
        var symbolSize = fontSize ?? dia * ContainerRatio.Small;
```

Then change `FontSize = fontSize,` at `:115` to `FontSize = symbolSize,`, and delete
`public const double DefaultSymbolFontSize = 22;` at `:15`.

- [ ] **Step 3: The four XAML glyph sites**

Four sites set a glyph size in XAML: `QuickActionsGridWindow.xaml:26` (`FontSize="20"`, the 44px close
button) and `:54` (`FontSize="28"`, the 120px tile icon), plus `EyeCareBannerWindow.xaml:30,37`
(`FontSize="22"`, in 64px dwell rings). XAML cannot express a multiplication, so each needs either a
code-behind assignment or `{x:Static}`.

**Only one of the four needs `{x:Static}`. Use code-behind for the other three.**

`{x:Static}` is unavoidable for the tile icon, which lives in an `ItemTemplate`: a `DataTemplate` is
instantiated once per item, so there is no single element to assign to. The other three —
`QuickActionsGridWindow.xaml:26` and `EyeCareBannerWindow.xaml:30,37` — are ordinary named elements,
and a plain assignment keeps them out of the guard's allow-list entirely. **Fewer allowed forms is a
stronger guard**, and the allow-list is the guard's trust surface.

**The template case** — in `src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml.cs`, add:

```csharp
    private const double CloseButtonSize = 44;
    private const double TileSize = 120;

    /// <summary>
    /// Tile icon size: a ratio of the 120px tile that bounds it (spec §8.5). Public and static
    /// because it is consumed by {x:Static} from inside the ItemTemplate, which cannot reach an
    /// instance member. It is the ONLY glyph size that needs this — see RawGeometryLiteralTests's
    /// allow-list, which admits public static doubles on OverlayWindow subclasses for exactly
    /// this one case.
    /// </summary>
    public static double TileIconSize => TileSize * ContainerRatio.Small;
```

and in `QuickActionsGridWindow.xaml` replace `FontSize="28"` at `:54` with
`FontSize="{x:Static overlays:QuickActionsGridWindow.TileIconSize}"`. The `overlays:` prefix is
already declared on the root element.

**The three code-behind cases.** Give each `TextBlock` an `x:Name`, delete its `FontSize` attribute,
and assign after `InitializeComponent()`:

- `QuickActionsGridWindow.xaml:26` → `x:Name="CloseGlyph"`, then in the constructor:

  ```csharp
      CloseGlyph.FontSize = CloseButtonSize * ContainerRatio.Dominant;
  ```

- `EyeCareBannerWindow.xaml:30` → `x:Name="SnoozeGlyph"` and `:37` → `x:Name="StartGlyph"`, then in
  that window's constructor:

  ```csharp
      // Ring glyphs are a ratio of the 64px dwell ring that bounds them (spec §8.5).
      const double RingSize = 64;
      SnoozeGlyph.FontSize = RingSize * ContainerRatio.Medium;
      StartGlyph.FontSize = RingSize * ContainerRatio.Medium;
  ```

Leave the `Width="44" Height="44"` literals at `QuickActionsGridWindow.xaml:22` alone — they are
layout, which §2 excludes, and `CornerRadiusClampTests.TheOneCircleConsumer_IsSquare` already couples
them to the circle token by parsing the XAML.

- [ ] **Step 4: `DragTrailOverlay` — the ratio *and* the offset**

Spec §8.5 records that this site's `-8` offset is half the font size written as a literal, so any size change silently de-centres the glyph.

```csharp
    /// <summary>The grab/release glyph size, a ratio of the 26px badge that bounds it.</summary>
    private const double IconSize = ButtonDiameter * ContainerRatio.Badge;

    private static TextBlock CreateIcon(string glyph, Brush foreground)
    {
        return new TextBlock
        {
            Text = glyph,
            FontSize = IconSize,
            Foreground = foreground,
            IsHitTestVisible = false
        };
    }
```

where `ButtonDiameter` is a new `private const double ButtonDiameter = 26;` used by `CreateButtonBg` too. Then replace **both** magic offsets — `:130-131`'s `-10` and `PositionButton`'s `-8`:

```csharp
        Canvas.SetLeft(_grabIcon, cursorLogical.X - (IconSize / 2));
        Canvas.SetTop(_grabIcon, cursorLogical.Y - (IconSize / 2));
```

```csharp
    private static void PositionButton(Ellipse bg, TextBlock icon, Point logicalPos)
    {
        Canvas.SetLeft(bg, logicalPos.X - (ButtonDiameter / 2));
        Canvas.SetTop(bg, logicalPos.Y - (ButtonDiameter / 2));
        // Half the FONT SIZE, not half DesiredSize: DesiredSize.Height is 1.33x the font
        // size, so box-centring would shift the glyph ~2.6px from where it sits today.
        // DesiredSize is also (0,0) here — this is static and the TextBlock is unmeasured.
        Canvas.SetLeft(icon, logicalPos.X - (IconSize / 2));
        Canvas.SetTop(icon, logicalPos.Y - (IconSize / 2));
    }
```

- [ ] **Step 5: Remove the last PENDING `Exempt` entries**

- [ ] **Step 6: Build and test**

```bash
dotnet build EyeCtrl.slnx --nologo
```

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "feat(overlays): size glyphs from their containers, fixing the key-multiplier bug"
```

---

### Task 13: The one layout change — `TrackStatusWindow.Height`

**Read spec §8.6.** This is the single scoped exception to the layout exclusion, and §2 names it so it cannot expand.

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml:7`
- Create: `tests/EyeCtrl.App.Tests/TrackStatusFitTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/TrackStatusFitTests.cs`:

```csharp
namespace EyeCtrl.App.Tests;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// TrackStatusWindow is the only window whose ramp-affected content overflows its fixed size,
/// and the 12px ramp floor is what pushed it over (design spec §8.6): measured content height
/// 95.88px against the 94px its Padding="8" leaves inside a 110px window.
///
/// This couples the window's height to the arithmetic, so the exception cannot silently rot
/// if the ramp floor or the row structure changes.
/// </summary>
public class TrackStatusFitTests
{
    /// <summary>Measured line height is exactly 1.33x the font size for this family (§4.2).</summary>
    private const double LineHeightRatio = 1.33;

    private const double CanvasHeight = 40;   // the eye-dot track box
    private const double Padding = 8;         // Border Padding="8", top and bottom
    private const double HeaderMargin = 4;    // "Track Status" Margin="0,0,0,4"
    private const double DistanceRowMargin = 4;

    [Fact]
    public void TheWindowIsTallEnoughForTheRampFloor()
    {
        var line = Tokens.FontSizeBody * LineHeightRatio;
        var content = (line + HeaderMargin)          // "Track Status"
                    + CanvasHeight                   // eye-dot canvas
                    + (line + DistanceRowMargin)     // Distance row
                    + line;                          // Quality row

        var xaml = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TrackStatusWindow.xaml"));
        var height = double.Parse(
            Regex.Match(xaml, @"Height=""(?<h>[\d.]+)""").Groups["h"].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        var available = height - (2 * Padding);

        Check.WithCustomMessage(
                $"TrackStatusWindow needs {content:F2}px of content space but Height={height} "
                + $"leaves only {available:F2}. At the 12px ramp floor the bottom row clips, "
                + "cutting the descender from \"Quality\" and its value. Design spec §8.6 sets "
                + "Height=116; if the ramp floor or the row structure changed, redo that "
                + "arithmetic rather than widening the window again.")
            .That(available).IsGreaterOrEqualThan(content);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test EyeCtrl.slnx --filter "FullyQualifiedName~TrackStatusFitTests" --nologo
```

Expected: FAIL — needs 95.88px, has 94.00.

- [ ] **Step 3: Change the height**

In `src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml:7`, change `Height="110"` to `Height="116"` and add a comment above the element:

```xml
        <!-- Height 116, not 110: the 12px type-ramp floor needs 95.88px of content space and
             Padding="8" leaves only 94 at 110 (design spec §8.6). This is the one scoped
             layout exception in that work; TrackStatusFitTests couples it to the arithmetic. -->
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: PASS, with 4.12px slack.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml tests/EyeCtrl.App.Tests/TrackStatusFitTests.cs
git commit -m "fix(overlays): raise TrackStatusWindow to 116 so the 12px ramp floor fits"
```

---

### Task 14: Update the design documents

**Files (all Modify):**
- `docs/design/overlay-unification-spec.md` §3.3, §3.4, §4.2, §4.3, §8
- `docs/design/overlay-unification-style-guide.html` §2, §3, and the deferral paragraph at `:419-425`

- [ ] **Step 1: §3.3 — correct the radius audit**

Replace the 9-row table with the verified inventory: 30 literal sites across seven values (`2`, `4`, `6`, `8`, `12`, `14`, `22`), and note the two asymmetric/nesting facts the original audit lacked. Point at the new spec for the full table.

- [ ] **Step 2: §3.4 — correct the typography audit**

Replace with: 68 `FontSize` occurrences = 60 live literal sites over 16 values + 4 dead consts + the ratio mechanism; 17 `FontWeight` sites; 1 `FontFamily` literal. Replace "everything inherits the WPF default (Segoe UI)" with the measured finding: WPF's inherited default is the system message font, and an unresolvable bare name has an **undefined** degradation path — three measurements disagree and none identifies a single face — which is why the token declares a chain. **Do not write "falls back to Arial"**; that claim was disproved.

- [ ] **Step 3: §4.2 and §4.3 — supersede**

Replace both with a pointer to `docs/superpowers/specs/2026-08-06-overlay-geometry-typography-tokens-design.md`, and record that §4.2's three proposed tokens were insufficient (they covered 4/8/12 against seven values in use) and §4.3's family-only proposal left 16 sizes fragmented.

- [ ] **Step 4: §8 — the deferral becomes a delivery**

Rewrite "Deferred, with reason" as delivered, and correct the predicted token name: §8 anticipated `CornerRadiusPill`, but the measurement shows WPF clamps to an inscribed *ellipse*, so a non-square element would silently get an ellipse. The token is `CornerRadiusCircle`, documented square-only and pinned by `CornerRadiusClampTests`.

- [ ] **Step 5: The style guide**

In `docs/design/overlay-unification-style-guide.html`: §2 Typography gains the five-step ramp, the two weights and the family chain; §3 gains the four radius tokens; and the deferral paragraph at `:419-425` is replaced with a delivery note pointing at the new spec.

- [ ] **Step 6: Commit**

```bash
git add docs/design
git commit -m "docs(design): fold the geometry and typography tokens into the parent spec"
```

---

### Task 15: Final verification

- [ ] **Step 1: Full build**

```bash
dotnet build EyeCtrl.slnx --nologo
```

Expected: 0 errors.

- [ ] **Step 2: Validator**

```bash
python tools/palette_validate.py
```

Expected: `ALL GATES PASS`, exit 0, ten gates, three themes.

- [ ] **Step 3: Headless suite**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI" --nologo
```

Expected: PASS. **Read the summary line and grep for `Fehler`** — the output is German and grepping for "Failed" reports green on a red suite.

- [ ] **Step 4: Confirm the guard's exemption list is down to its permanent entries**

```bash
grep -c PENDING tests/EyeCtrl.App.Tests/RawGeometryLiteralTests.cs
```

Expected: `0`. Any remaining `PENDING` means a file was never migrated and is still exempt.

- [ ] **Step 5: Confirm no literal survived**

```bash
grep -rn 'CornerRadius="[0-9]\|new CornerRadius([0-9]' src/EyeCtrl.App src/EyeCtrl.Controls
```

```bash
grep -rn 'FontSize="[0-9]\|FontSize = [0-9]' src/EyeCtrl.App src/EyeCtrl.Controls
```

```bash
grep -rn 'FontWeight="[A-Za-z]\|FontWeight = FontWeights\.' src/EyeCtrl.App src/EyeCtrl.Controls
```

```bash
grep -rn 'FontFamily="[A-Za-z]\|new FontFamily("' src/EyeCtrl.App src/EyeCtrl.Controls
```

Expected: no output from any of the four. An earlier draft checked only radius and size, which is
how two `FontWeight` sites went missing from the task tables — the greps have to cover every
property the guard covers. `EyeCtrl.Supervisor` is out of scope and is not scanned.

- [ ] **Step 6: Scope check with GitNexus**

```bash
npx gitnexus analyze
```

`AGENTS.md` documents `node .gitnexus/run.cjs analyze` as the preferred form, but **that file does
not exist on a fresh checkout of this branch** — use the fallback above, which `AGENTS.md` and
`CLAUDE.md` both name. If npm 11 crashes on it, `npm i -g gitnexus` first (issue #1939).

Then run `detect_changes({scope: "compare", base_ref: "main"})` and confirm the affected symbols are the theme layer, the overlay windows and the dwell controls — nothing in the gaze pipeline, adaptation, or prediction.

- [ ] **Step 7: Ask before any visual check**

The remaining confirmations need eyes on the running app: the `Segoe UI Variable Text` specimens and the `EyeCareBannerWindow` label clearance. **Ask for an explicit go-ahead before launching anything**, and keep any such run short — the user drives this machine solely by eye-tracking.

---

## Task-to-spec coverage

| Spec section | Task |
|---|---|
| §6.1 12 primitives | 1 |
| §6.2 derivations | 1 (`FontStandard`), 3 (radii) |
| §6.3 XAML surface | 3 |
| §6.4 applier | 3 |
| §6.6 the freeze | 3 |
| §7.1 radius map | 7, 8, 9 |
| §7.2 asymmetric note bar | 9 |
| §7.3 nesting inversion | 7 |
| §8.1 family | 10 |
| §8.2 weights | 7, 8, 9, 11 |
| §8.4 text ramp | 7, 8, 9, 11 |
| §8.5 container ratios | 5, 12 |
| §8.6 clip analysis | 13 |
| §8.7 dead consts | 9 |
| §8.8 font wiring | 10 |
| §9 validator | 2 |
| §10.1–10.2 extended/generalised tests | 1, 2, 3 |
| §10.3 clamp pin | 4 |
| §10.4 drift guard | 6 |
| §10.5 font wiring test | 10 |
| §4.2 font chain resolves | 10 |
| §10.6 verification gates | 15 |
| §12 documentation | 14 |
| §16 `m2` startup ordering | 10 |
