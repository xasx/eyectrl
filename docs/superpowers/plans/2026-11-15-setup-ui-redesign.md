# Setup UI Redesign (Direction B) Implementation Plan — v5

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**v5 note:** the r5 review confirmed v4 execution-ready with two fixes applied here — Task 2's restored button fit test calls `MeasureButton(item.Key, item.Text)` (an `Item` doesn't convert to the string parameter), and the choice band's TITLE column is `Star` (an Auto column measures the title with unlimited width, so wrapping never applies and the setupTitle fit test would measure a layout that doesn't ship; confirm-screen centring still works — the centred Grid reports its content width as its desired size while the title is measured against the band's finite width). Plus two nits: the idle-gate note points at Task 9, and `BandGridOf` is PascalCase.

**Goal:** Rebuild `EyeCtrl.Setup`'s progress/result window and uninstall choice window into direction B of `docs/design/setup-ui-redesign-spec.md` (r4) — full-bleed bands with visible stage lists, three-state results with the confirming text in the body, outlined buttons behind an explicit no-hover template — without touching any gaze-safety mechanism (FC1–FC3, FC5, FC6).

**Architecture:** Both windows become code-behind compositions (no XAML): a full-bleed **band** (two-column Grid: app mark + process title that can wrap, demoted on results), a **6 DIP state edge**, and a **body** holding two sibling stacks — the stage plan (progress) and the result composition (state icon + confirming text, D8) — toggled by `ShowResult`. Buttons get an explicit `FrameworkElementFactory` template so the Aero2 hover can never replace fills under the gaze. `SetupDisplayResult` collapses to a `State` enum (single source of truth; `IsFailure` removed). Stage lists come from a display-side per-verb plan table; any emitted stage outside the plan is appended (D9, r3 F3). Shared brush helpers live in one `SetupBrushes` class.

**Tech Stack:** WPF code-behind (net10.0-windows), xUnit v3 + NFluent (`EyeCtrl.Setup.Tests`), `FrameworkElementFactory`, `BitmapDecoder`, `EyeCtrl.Controls.Theme.Tokens`, `EyeCtrl.Sdk.Wpf.Dwell`.

**v4 note:** v3 was reviewed (verdict "needs fixes": 3 Blockers + 2 High + 6 Medium/Low + nits). Applied: the duplicate `NewStateGlyph`/result-section is gone (one final `BuildBodyHost`); Task 3 writes `_title.Foreground`; `ShowsResultBody` stops its logical-tree walk at the window (a never-shown `Window` defaults to `Collapsed` — the v3 walk was always false); the FC7 test polls through `shown.Dispatcher.Invoke` (the v3 `DispatcherTimer` ticked on the wrong thread); glyphs carry `Width/Height = 24` under their `LayoutTransform` (the v3 combination drew 64 but occupied ~171 DIP of layout); the choice band is a two-column Grid too; M4 uses `LayoutInformation.GetLayoutSlot` (the v4-suggested position check cannot see WPF's negative-offset centring); the question renders primary at Normal weight; the per-theme tint expectation is inline arithmetic per theme; every test file's using directives are listed; the choice-button fit fact has its real body back.

## Settled in v3/v4 (review recommendations — owner-vetoable)

1. **Band title layout:** two-column `Grid` (Auto mark column + Star title column) in BOTH windows so the title can wrap (spec's auto-grow promise). Confirm-screen centring = `HorizontalAlignment.Center` on the Grid itself.
2. **Glyph scaling:** `Width/Height = 24` (geometry units) + `LayoutTransform = new ScaleTransform(box / 24.0)` — the glyph draws at the box size and occupies exactly the box in layout (the v3 Width+transform combination double-scaled the footprint, r4 finding 5). The neutral dash is a **64 × 10 rounded `Border`** (mockup-faithful), not a path.
3. **Screen 1's question renders primary at Normal weight** (mockup-faithful; only `.prose-2` lines are secondary).
4. **Brush helpers live once:** `internal static class SetupBrushes { Frozen, Opaque, OpaqueColor, Blend, DangerTint }` in `Display/SetupBrushes.cs`.
5. **`ShowConfirm` and FC7's `SetBandTitle` keep the taskbar title in sync** with the band title.

## Global Constraints

- **Base:** create the worktree from the **current** `origin/main` (it moves between rounds — re-check `git log --oneline -1 origin/main` at execution; the r5 review read `8e490592`). The spec, mockup and this plan are untracked in the main checkout — **Task 0** copies them in and commits them.
- **FC1–FC3 byte-identical:** arming (1 s + resting gate), cancel paths, confirm-screen columns (danger col 0, safe col 4), and the bottom pin `keep.Bottom ≈ H − 32 − 48` (`UninstallChoiceWindowTests:583`).
- **FC4:** the progress window stays display-only (`IsHitTestVisible=false`, ordinary window, no dwell targets — Task 5 pins "no `Button`, no locally-set `Dwell.ModeProperty`" in the progress tree).
- **FC5:** self-close 10 s success / 60 s failure; `Cancelled` is **not** a failure (10 s).
- **FC6:** choice buttons stay `DwellMode.Invoke`; only the front screen is dwellable.
- **FC7:** on a `VerifyFailed` result **no preferred-table text is anywhere in the visual tree** — `ShowResult` clears the progress texts and the band title re-resolves from the trusted table (r3 F1).
- **FC8:** no XAML files; the template is built with `FrameworkElementFactory`. App runtime code untouched; Core `strings.json` fit metadata + `StringTableSchema` + `tools/strings_validate.py` + `AppFitMeasurers.cs` (App.Tests) are in scope.
- **FC9 floors:** buttons 420×160, gap 120, button text ≥ 28, prose ≥ 36. r4 ships: button text 32, prose 40, stage/dim 36, band+result titles 44.
- **Source-scan constraint:** `UninstallChoiceWindow.cs` must keep the exact line
  `IsVisibleChanged += (_, e) => OnWindowVisibilityChanged((bool)e.NewValue);` (whitespace-insensitive scan).
- **NoVisibleTestWindow:** no test shows a window — `EnsureHandle()`, dispatcher polling through the window's own dispatcher, and the `RunSta` helpers already in the suite.
- **Named pipes:** every new dwell pipe gets a GUID-suffixed name.
- **Never `git checkout --`.** After each task's commit the tree is clean; `git restore <file>` is the safe revert for the mutation round.
- **Test runs:** always `--blame-hang-timeout 300s`; plain `dotnet test`, no env var; one project per invocation (MSB1008). If the box's `wait-idle-tests.ps1` idle gate convention applies (owner confirms), wrap the Task 9 runs in it.

---

### Task 0: Worktree + artifacts

**Files:**
- Create: the worktree `…/eyectrl/.worktrees/setup-ui-b` on branch `feat/setup-ui-b`
- Copy: `docs/design/setup-ui-redesign-spec.md`, `docs/design/setup-ui-redesign-mockup.html`, `docs/superpowers/plans/2026-11-15-setup-ui-redesign.md` (untracked in the main checkout)

- [ ] **Step 1: Create the worktree** (superpowers:using-git-worktrees): branch `feat/setup-ui-b` from the current `origin/main`.

- [ ] **Step 2: Copy the three artifacts** from the main checkout into the same relative paths in the worktree.

- [ ] **Step 3: Commit**

```bash
git add docs/design/setup-ui-redesign-spec.md docs/design/setup-ui-redesign-mockup.html docs/superpowers/plans/2026-11-15-setup-ui-redesign.md
git commit -m "docs(setup): r4 spec, mockup and implementation plan for direction B"
```

---

### Task 1: Metrics, and the shared brush helpers

**Files:**
- Create: `src/EyeCtrl.Setup/Display/SetupBrushes.cs`
- Modify: `src/EyeCtrl.Setup/Display/SetupWindowMetrics.cs`
- Modify: `tests/EyeCtrl.Setup.Tests/Display/SetupFitTests.cs` (metrics test only)

**Interfaces:**
- Produces: `SetupBrushes.Frozen(Color)`, `SetupBrushes.Opaque(Color)` (frozen `SolidColorBrush`), `SetupBrushes.OpaqueColor(Color)` (opaque `Color`), `SetupBrushes.Blend(over, ink, ratio)` (`Color`), `SetupBrushes.DangerTint()` (frozen brush) — used by Tasks 5, 6, 7 and 8; `TitleFontSize = 44`, `StageFontSize = 36`, `DimFontSize = 36`, `ResultIconSize = 64`, `BandIconSize = 52`, `BodyWidthProgress = 1168`, `BodyWidthChoice = 1388`, `TitleFitWidth = 1094`, `TitleFitHeight = 468`; `ChoiceFontSize` 28→**32**, `ProseFontSize` 36→**40**.

- [ ] **Step 1: Write the failing metrics test**

In `SetupFitTests.TheMetrics_PutTheChoiceRowOnTheOwnersWorkArea_WithoutACentreTarget`, replace the last two assertions with:

```csharp
Check.That(SetupWindowMetrics.ChoiceFontSize).IsEqualTo(32);
Check.That(SetupWindowMetrics.ProseFontSize).IsEqualTo(40);
Check.That(SetupWindowMetrics.TitleFontSize).IsEqualTo(44);
Check.That(SetupWindowMetrics.StageFontSize).IsEqualTo(36);
Check.That(SetupWindowMetrics.DimFontSize).IsEqualTo(36);
Check.That(SetupWindowMetrics.BodyWidthProgress).IsEqualTo(1168);
Check.That(SetupWindowMetrics.BodyWidthChoice).IsEqualTo(1388);
Check.That(SetupWindowMetrics.TitleFitWidth).IsEqualTo(1094);
Check.That(SetupWindowMetrics.TitleFitHeight).IsEqualTo(468);
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "TheMetrics_PutTheChoiceRowOnTheOwnersWorkArea"`
Expected: FAIL (`TitleFontSize` not defined).

- [ ] **Step 3: Implement**

New file `src/EyeCtrl.Setup/Display/SetupBrushes.cs`:

```csharp
namespace EyeCtrl.Setup.Display;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;

/// <summary>
/// The opaque, frozen brushes Setup's screens draw with (r3 F11): every theme fill carries 0xFA
/// alpha, and N1's opaque-surface rule (SetupProgressWindow.cs:186-192) applies to bands, edges
/// and tints alike. One definition per compilation unit — Tasks 5, 6, 7 and 8 consume these.
/// </summary>
internal static class SetupBrushes
{
    /// <summary>A frozen solid brush in the given colour, alpha included.</summary>
    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The N1 opaque variant of a theme colour, frozen.</summary>
    public static SolidColorBrush Opaque(Color color) =>
        Frozen(Color.FromRgb(color.R, color.G, color.B));

    /// <summary>The N1 opaque variant as a colour (for contrast arithmetic and pins).</summary>
    public static Color OpaqueColor(Color color) => Color.FromRgb(color.R, color.G, color.B);

    /// <summary>A linear colour blend: <paramref name="ratio"/> of <paramref name="ink"/> over
    /// <paramref name="over"/> (the confirm band's 0.18 danger tint).</summary>
    public static Color Blend(Color over, Color ink, double ratio) => Color.FromRgb(
        (byte)(ink.R * ratio + over.R * (1 - ratio)),
        (byte)(ink.G * ratio + over.G * (1 - ratio)),
        (byte)(ink.B * ratio + over.B * (1 - ratio)));

    /// <summary>SD7/r3 F11: the confirm band's danger tint — AccentDanger at 0.18 over the
    /// OPAQUE SurfaceMedium, frozen.</summary>
    public static SolidColorBrush DangerTint() =>
        Frozen(Blend(OpaqueColor(Tokens.SurfaceMedium), Tokens.AccentDanger, 0.18));
}
```

In `SetupWindowMetrics.cs` change `ChoiceFontSize` to `32`, `ProseFontSize` to `40`, and add:

```csharp
/// <summary>The band/choice title and result-body text (spec r4 SD3).</summary>
public const double TitleFontSize = 44;
/// <summary>The stage-row label (the 36 DIP floor; SD3).</summary>
public const double StageFontSize = 36;
/// <summary>The dim hint line — stays at the 36 DIP floor (SD3).</summary>
public const double DimFontSize = 36;
/// <summary>The state icon on a result body (D8 composition).</summary>
public const double ResultIconSize = 64;
/// <summary>The app mark inside a band.</summary>
public const double BandIconSize = 52;
/// <summary>The progress body's content width: window 1344 less 2 × 88 padding.</summary>
public const double BodyWidthProgress = 1168;
/// <summary>The choice PROSE width: window 1564 less the band's 2 × 88 inset.</summary>
public const double BodyWidthChoice = 1388;
/// <summary>The narrowest title box: progress band 1344 − padding 176 − mark 52 − gap 22.</summary>
public const double TitleFitWidth = 1094;
/// <summary>The title's fit height on a result body: 806.4 − edge 6 − band 132 − padding 64/48
/// − icon 64 − gap 24 (mockup paddings; derived, not guessed).</summary>
public const double TitleFitHeight = 468;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "TheMetrics_PutTheChoiceRowOnTheOwnersWorkArea"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Setup/Display/SetupBrushes.cs src/EyeCtrl.Setup/Display/SetupWindowMetrics.cs tests/EyeCtrl.Setup.Tests/Display/SetupFitTests.cs
git commit -m "feat(setup): r4 metrics + shared opaque/frozen brush helpers (SetupBrushes)"
```

---

### Task 2: Fit metadata — five classes, the prose/title/stage factories, full measurement

**Files:**
- Modify: `src/EyeCtrl.Core/Localization/strings.json` (fit fields only)
- Modify: `src/EyeCtrl.Core/Localization/StringTableSchema.cs` (class list + comment)
- Modify: `tools/strings_validate.py` (class list)
- Modify: `tests/EyeCtrl.App.Tests/Localization/Fit/AppFitMeasurers.cs:51` (`SetupMeasuredClasses`)
- Modify: `src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs` (`CreateProse` signature; add `CreateTitle`/`CreateStageLabel`)
- Modify: `tests/EyeCtrl.Setup.Tests/Display/SetupFitTests.cs` (catalog counts, per-class facts, over-long coverage, measurers)

**Interfaces:**
- Produces: `UninstallChoiceWindow.CreateProse(string text, bool strong = false, bool secondary = false, bool dim = false, TextAlignment alignment = TextAlignment.Left, double size = SetupWindowMetrics.ProseFontSize, double maxWidth = SetupWindowMetrics.BodyWidthProgress)`; `UninstallChoiceWindow.CreateTitle(string)`; `UninstallChoiceWindow.CreateStageLabel(string)`. Fit classes: `setup420x160` (5 keys, 32), `setupTitle` (24, 44 bold), `setupStage` (6, 36), `setupDim` (1, 36), `setupProse` (4, 40). Tasks 5, 6 and 8 consume the factories and constants; **the windows build their text through these factories, never inline** (r3 finding: the fit tests must measure what ships).

**Re-class mapping** (edit the `"fit"` value of each English entry in `strings.json`; German entries carry no fit field — the German draft resolves the class by key):

- → `setupTitle` (24): `rollback.progress.title`; `rollback.result.{allFailed,degraded,failed,notApplied,ok,verifyFailed}`; `uninstall.cancelled`; `uninstall.choice.title`; `uninstall.confirmDelete.title`; `uninstall.done.{deleteIncomplete,deleted,kept}`; `uninstall.failed`; `uninstall.needsHelp`; `uninstall.working`; `update.progress.title`; `update.result.{allFailed,degraded,failed,notApplied,ok,rolledBack,verifyFailed}`
- → `setupStage` (6): `rollback.progress.{start,swap}`; `update.progress.{start,stop,swap,verify}`
- → `setupDim` (1): `uninstall.choice.noChoice`
- stay `setupProse` (4): `uninstall.choice.detail`; `uninstall.choice.question`; `uninstall.confirmDelete.detail`; `update.progress.detail`
- stay `setup420x160` (5): `uninstall.choice.keep`, `uninstall.choice.cancel`, `uninstall.choice.delete`, `uninstall.confirmDelete.no`, `uninstall.confirmDelete.yes`

- [ ] **Step 1: Re-class strings.json** per the mapping (31 edits).

- [ ] **Step 2: Extend the schema class list**

In `StringTableSchema.cs` add `"setupTitle", "setupStage", "setupDim"` next to `"setup420x160", "setupProse"` and update the setup comment to: *"setupProse is a 40 DIP prose line in its 1168 × 384 DIP box; setupTitle is the 44 DIP bold band/result title in its 1094 × 468 DIP box; setupStage and setupDim are 36 DIP single rows in 1168 × 96."*

- [ ] **Step 3: Extend the validator**

In `tools/strings_validate.py`, add `"setupTitle", "setupStage", "setupDim"` to the same list that holds `"setup420x160", "setupProse"`.

- [ ] **Step 4: Declare in AppFitMeasurers**

`SetupMeasuredClasses` becomes `["setup420x160", "setupProse", "setupTitle", "setupStage", "setupDim"]`.

- [ ] **Step 5: New signature + the two factories in `UninstallChoiceWindow`**

`CreateProse` gains the parameters (default `alignment` is Left — every B prose line is left-aligned except screen 2's, which passes `Center`; screen 1's question passes `strong: true` per the mockup's primary question):

```csharp
/// <summary>A prose line: 40 DIP (or an explicit size), left-aligned by default, wrapping in its
/// body box. Both windows consume it (r3 F8: the old centre-only signature could not serve B).
/// The plain tier renders Normal — the weight ladder below belongs to button labels, not prose.</summary>
internal static TextBlock CreateProse(
    string text,
    bool strong = false,
    bool secondary = false,
    bool dim = false,
    TextAlignment alignment = TextAlignment.Left,
    double size = SetupWindowMetrics.ProseFontSize,
    double maxWidth = SetupWindowMetrics.BodyWidthProgress)
{
    return new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = alignment,
        MaxWidth = maxWidth,
        FontFamily = Tokens.FontStandard,
        FontSize = size,
        FontWeight = strong ? Tokens.FontWeightStrong
            : dim || secondary ? Tokens.FontWeightMedium
            : FontWeights.Normal,
        Foreground = dim ? Tokens.BrushTextDim
            : secondary ? Tokens.BrushTextSecondary
            : Tokens.BrushTextPrimary,
        Margin = new Thickness(0, 0, 0, 12),
    };
}
```

`CreateTitle` and `CreateStageLabel` exactly as the measurers consume them:

```csharp
/// <summary>The band/choice title and result-body text: 44 bold, TextPrimary, wrapping.</summary>
internal static TextBlock CreateTitle(string text) => new()
{
    Text = text,
    FontSize = SetupWindowMetrics.TitleFontSize,
    FontWeight = Tokens.FontWeightStrong,
    Foreground = Tokens.BrushTextPrimary,
    TextWrapping = TextWrapping.Wrap,
};

/// <summary>A stage-row label: 36 semibold, pending-dim base colour (the window paints states).</summary>
internal static TextBlock CreateStageLabel(string text) => new()
{
    Text = text,
    FontSize = SetupWindowMetrics.StageFontSize,
    FontWeight = Tokens.FontWeightMedium,
    Foreground = Tokens.BrushTextDim,
};
```

- [ ] **Step 6: Rewrite the fit tests — counts, per-class facts, over-long coverage**

Replace `TheTwoSetupClasses_HaveTheirKeys_AndNoGermanIsMissing` and the prose/over-long facts with:

```csharp
[Fact]
public void TheFiveSetupClasses_HaveTheirKeys_AndNoGermanIsMissing()
{
    Check.That(FitCatalog.ItemsOf("setup420x160")).HasSize(5);
    Check.That(FitCatalog.ItemsOf("setupTitle")).HasSize(24);
    Check.That(FitCatalog.ItemsOf("setupStage")).HasSize(6);
    Check.That(FitCatalog.ItemsOf("setupDim")).HasSize(1);
    Check.That(FitCatalog.ItemsOf("setupProse")).HasSize(4);
    Check.That(GermanDraft.MissingKeys()).IsEmpty();
    Check.WithCustomMessage("The German draft logged load warnings:\n  " + string.Join("\n  ", GermanDraft.LoadWarnings))
        .That(GermanDraft.LoadWarnings).IsEmpty();
}

[Fact]
public void EveryEnglishAndGermanChoiceButton_FitsItsBox()
{
    RunSta(() =>
    {
        using var pipe = Dwell.UseTestPipeForTests("eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));

        foreach (var item in FitCatalog.ItemsOf("setup420x160"))
        {
            var english = MeasureButton(item.Key, item.Text);
            Check.WithCustomMessage($"{item}: {english.Detail}")
                .That(english.Fits).IsTrue();
        }

        foreach (var item in GermanDraft.ItemsOf("setup420x160"))
        {
            var german = MeasureButton(item.Key, item.Text);
            Check.WithCustomMessage($"{item} \"{item.Text}\": {german.Detail}")
                .That(german.Fits).IsTrue();
        }
    });
}

[Fact]
public void EveryEnglishAndGermanProseLine_FitsItsBox() => ForBothTables("setupProse", MeasureProse);

[Fact]
public void EveryEnglishAndGermanTitle_FitsItsBox() => ForBothTables("setupTitle", MeasureTitle);

[Fact]
public void EveryEnglishAndGermanStageRow_FitsItsBox() => ForBothTables("setupStage", MeasureStageRow);

[Fact]
public void EveryEnglishAndGermanDimHint_FitsItsBox() => ForBothTables("setupDim", MeasureDim);

private void ForBothTables(string fitClass, Func<FitCatalog.Item, FitVerdict> measure)
{
    RunSta(() =>
    {
        foreach (var item in FitCatalog.ItemsOf(fitClass))
            Check.WithCustomMessage($"{item}: {measure(item).Detail}")
                .That(measure(item).Fits).IsTrue();

        foreach (var item in GermanDraft.ItemsOf(fitClass))
            Check.WithCustomMessage($"{item} \"{item.Text}\": {measure(item).Detail}")
                .That(measure(item).Fits).IsTrue();
    });
}
```

(The measurers take the `FitCatalog.Item` so `Expand` handles the `{0}`/folder args — `update.result.degraded` is measured with its version argument filled, per r1 F10.)

The measurers — `MeasureProse` at the narrowest shipped prose box, `MeasureTitle` at the derived title box, rows with the label's real slot (r3 F15: 1168 − glyph 30 − gap 24 = 1114, compared by `NaturalWidth`, not `WidestRun`):

```csharp
private static FitVerdict MeasureProse(FitCatalog.Item item)
{
    var text = Expand(item, item.Text);
    var block = UninstallChoiceWindow.CreateProse(text);
    var panel = new StackPanel { Width = SetupWindowMetrics.BodyWidthProgress };
    panel.Children.Add(block);
    using var host = FitHost.Open(panel, width: SetupWindowMetrics.BodyWidthProgress, family: Tokens.FontStandard);
    var (run, runWidth) = FitText.WidestRun(block, text);
    var height = FitText.NaturalHeight(host, block, SetupWindowMetrics.BodyWidthProgress);
    var ok = runWidth <= SetupWindowMetrics.BodyWidthProgress && height <= SetupWindowMetrics.ProseHeight;
    return new FitVerdict(ok,
        $"widest run '{run}' {FitText.Dip(runWidth)} vs {SetupWindowMetrics.BodyWidthProgress}, wrapped height "
        + $"{FitText.Dip(height)} vs {SetupWindowMetrics.ProseHeight}",
        FitText.ResolvedFace(block),
        Math.Min(SetupWindowMetrics.BodyWidthProgress - runWidth, SetupWindowMetrics.ProseHeight - height));
}

private static FitVerdict MeasureTitle(FitCatalog.Item item)
{
    var text = Expand(item, item.Text);
    var block = UninstallChoiceWindow.CreateTitle(text);
    var panel = new StackPanel { Width = SetupWindowMetrics.TitleFitWidth };
    panel.Children.Add(block);
    using var host = FitHost.Open(panel, width: SetupWindowMetrics.TitleFitWidth, family: Tokens.FontStandard);
    var (run, runWidth) = FitText.WidestRun(block, text);
    var height = FitText.NaturalHeight(host, block, SetupWindowMetrics.TitleFitWidth);
    var ok = runWidth <= SetupWindowMetrics.TitleFitWidth && height <= SetupWindowMetrics.TitleFitHeight;
    return new FitVerdict(ok,
        $"widest run '{run}' {FitText.Dip(runWidth)} vs {SetupWindowMetrics.TitleFitWidth}, wrapped height "
        + $"{FitText.Dip(height)} vs {SetupWindowMetrics.TitleFitHeight}",
        FitText.ResolvedFace(block),
        Math.Min(SetupWindowMetrics.TitleFitWidth - runWidth, SetupWindowMetrics.TitleFitHeight - height));
}

private static FitVerdict MeasureStageRow(FitCatalog.Item item)
{
    var block = UninstallChoiceWindow.CreateStageLabel(item.Text);
    return MeasureRow(block, item.Text);
}

private static FitVerdict MeasureDim(FitCatalog.Item item)
{
    var block = UninstallChoiceWindow.CreateProse(
        item.Text, dim: true, size: SetupWindowMetrics.DimFontSize);
    return MeasureRow(block, item.Text);
}

private static FitVerdict MeasureRow(TextBlock block, string text)
{
    // r3 F15: the label sits at BodyWidthProgress − glyph 30 − gap 24 and clips as a whole
    // line — the bound is its full natural width there, not the widest unbreakable word.
    var slot = SetupWindowMetrics.BodyWidthProgress - 30 - 24;
    var panel = new StackPanel { Width = SetupWindowMetrics.BodyWidthProgress };
    panel.Children.Add(block);
    using var host = FitHost.Open(panel, width: SetupWindowMetrics.BodyWidthProgress, family: Tokens.FontStandard);
    var width = FitText.NaturalWidth(block, text);
    var ok = width <= slot;
    return new FitVerdict(ok,
        $"natural width {FitText.Dip(width)} vs the label slot {FitText.Dip(slot)}",
        FitText.ResolvedFace(block), slot - width);
}
```

`MeasureButton` keeps its body (the Content cast survives — Task 7's template presents the same `TextBlock`; the template binds `Padding` so the room maths stays truthful).

Over-long coverage for **all five classes, width and wrapped-height paths** (r3 F19 — the multi-word probe is restored):

```csharp
[Fact]
public void AnOverLongText_FailsEveryClass()
{
    RunSta(() =>
    {
        using var pipe = Dwell.UseTestPipeForTests("eyectrl_dwell_setup_test_" + Guid.NewGuid().ToString("N"));

        var singleWord = new string('W', 300);                                 // width path
        var manyWords = string.Join(" ", Enumerable.Repeat("Wwww", 2000));    // wrapped-height path

        Check.That(MeasureButton("fit.probe", singleWord).Fits).IsFalse();
        Check.That(MeasureProse(ProbeItem("setupProse", singleWord)).Fits).IsFalse();
        Check.That(MeasureProse(ProbeItem("setupProse", manyWords)).Fits).IsFalse();
        Check.That(MeasureTitle(ProbeItem("setupTitle", singleWord)).Fits).IsFalse();
        Check.That(MeasureTitle(ProbeItem("setupTitle", manyWords)).Fits).IsFalse();
        Check.That(MeasureStageRow(ProbeItem("setupStage", singleWord)).Fits).IsFalse();
        Check.That(MeasureDim(ProbeItem("setupDim", singleWord)).Fits).IsFalse();
    });
}

private static FitCatalog.Item ProbeItem(string fitClass, string text) =>
    new(fitClass + ".probe", "", text, Array.Empty<string>());
```

- [ ] **Step 7: Run the fit suite**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "SetupFitTests" --blame-hang-timeout 300s`
Expected: PASS. A FAIL means a box is too small for the real German text — widen the fit box constant, never shrink the text.

- [ ] **Step 8: Run the validator + App fit gate**

Run: `python -I tools/strings_validate.py` and `dotnet test tests/EyeCtrl.App.Tests --filter "Localization" --blame-hang-timeout 300s`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.Core/Localization/strings.json src/EyeCtrl.Core/Localization/StringTableSchema.cs tools/strings_validate.py tests/EyeCtrl.App.Tests/Localization/Fit/AppFitMeasurers.cs src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs tests/EyeCtrl.Setup.Tests/Display/SetupFitTests.cs
git commit -m "feat(setup): five fit classes — setupTitle/setupStage/setupDim split, prose/title/stage factories"
```

---

### Task 3: SetupDisplayResult — State enum as the single source of truth

**Files:**
- Modify: `src/EyeCtrl.Setup/Display/SetupDisplayText.cs`
- Modify: `tests/EyeCtrl.Setup.Tests/Display/SetupDisplayTextTests.cs` (retarget, full code below)
- Modify: `tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowContrastTests.cs:75` (construction only; the full rewrite lands in Task 5)
- Modify: `src/EyeCtrl.Setup/Display/SetupProgressWindow.cs` (`ShowResult` — TWO reads retargeted: the close delay at `:150` and the `:147` title foreground, r4 finding 1)

**Interfaces:**
- Produces: `SetupDisplayState { Success, Failure, Neutral }`; `SetupDisplayResult(string Text, SetupDisplayState State)`; `SetupDisplayText.StateFor(SetupOutcome)` (public — the tests assert against it); `CloseDelayFor(SetupDisplayState)`; `CloseDelay(SetupOutcome)` **kept** (derives via `StateFor` — `SetupDisplayTextTests:153-158` keeps passing).

- [ ] **Step 1: Grep every consumer**

Run: `grep -rn "IsFailure\|CloseDelayFor\|CloseDelay(" src/EyeCtrl.Setup tests/EyeCtrl.Setup.Tests --include="*.cs"`
Expected consumers: `SetupDisplayText.cs` (the record, `IsFailure`, `CloseDelayFor`, `CloseDelay`), `SetupDisplayTextTests.cs` (`:141-158`, `:174`, `:219`), `SetupProgressWindow.cs` (`ShowResult`: the close delay at `:150` AND the `:147` title foreground), `SetupProgressWindowContrastTests.cs:75`. Add any stragglers to this task.

- [ ] **Step 2: Rewrite the record and mapping**

In `SetupDisplayText.cs`:

```csharp
/// <summary>The visual state a result screen renders (r4 SD4): Success (bare check, AccentSuccess),
/// Failure incl. Degraded/Blocked (alert, AccentDanger — Q12), Neutral (Cancelled only — dim dash;
/// HandedOff never reaches a display, SetupWorkflow.cs:623-627).</summary>
public enum SetupDisplayState { Success, Failure, Neutral }

/// <summary>One result screen's text and state. State is the single source of truth — the old
/// IsFailure flag is removed (r3 T5): a defaulted flag next to State gave two answers, and a
/// Success default would have painted a check onto a VerifyFailed screen.</summary>
public readonly record struct SetupDisplayResult(string Text, SetupDisplayState State);
```

`Result(...)` keeps its text logic and ends:

```csharp
return new SetupDisplayResult(text, StateFor(result.Outcome));
```

with the two public mappings:

```csharp
/// <summary>The state an outcome renders as: Ok/Repaired/Uninstalled succeed, Cancelled is
/// neutral, everything else — Degraded and Blocked included — is a failure (Q12).</summary>
public static SetupDisplayState StateFor(SetupOutcome outcome) => outcome switch
{
    SetupOutcome.Ok or SetupOutcome.Repaired or SetupOutcome.Uninstalled => SetupDisplayState.Success,
    SetupOutcome.Cancelled => SetupDisplayState.Neutral,
    _ => SetupDisplayState.Failure,
};

/// <summary>The self-close delay for a state: Failure 60 s (Q12), else 10 s.</summary>
public static TimeSpan CloseDelayFor(SetupDisplayState state) =>
    state == SetupDisplayState.Failure ? FailureCloseDelay : SuccessCloseDelay;

/// <summary>The self-close delay for an outcome (kept for the outcome-based callers).</summary>
public static TimeSpan CloseDelay(SetupOutcome outcome) => CloseDelayFor(StateFor(outcome));
```

`IsFailure(SetupOutcome)` is deleted (its last callers were the old record shape and the close delay).

- [ ] **Step 3: Retarget `SetupDisplayTextTests`**

Replace `Degraded_IsAFailure_AndRolledBackToo`:

```csharp
[Fact]
public void States_FollowQ12_CancelledIsNeutral()
{
    Check.That(SetupDisplayText.StateFor(SetupOutcome.Degraded)).IsEqualTo(SetupDisplayState.Failure);
    Check.That(SetupDisplayText.StateFor(SetupOutcome.RolledBack)).IsEqualTo(SetupDisplayState.Failure);
    Check.That(SetupDisplayText.StateFor(SetupOutcome.Blocked)).IsEqualTo(SetupDisplayState.Failure);
    Check.That(SetupDisplayText.StateFor(SetupOutcome.VerifyFailed)).IsEqualTo(SetupDisplayState.Failure);
    Check.That(SetupDisplayText.StateFor(SetupOutcome.Cancelled)).IsEqualTo(SetupDisplayState.Neutral);
    Check.That(SetupDisplayText.StateFor(SetupOutcome.Ok)).IsEqualTo(SetupDisplayState.Success);
    Check.That(SetupDisplayText.StateFor(SetupOutcome.Uninstalled)).IsEqualTo(SetupDisplayState.Success);
}
```

Replace `CloseDelays_AreTenSecondsForSuccess_AndSixtyForFailure`:

```csharp
[Fact]
public void CloseDelays_AreTenSecondsForSuccessAndNeutral_SixtyForFailure()
{
    Check.That(SetupDisplayText.CloseDelayFor(SetupDisplayState.Success)).IsEqualTo(TimeSpan.FromSeconds(10));
    Check.That(SetupDisplayText.CloseDelayFor(SetupDisplayState.Neutral)).IsEqualTo(TimeSpan.FromSeconds(10));
    Check.That(SetupDisplayText.CloseDelayFor(SetupDisplayState.Failure)).IsEqualTo(TimeSpan.FromSeconds(60));
    // The outcome-based overload derives through StateFor — Cancelled keeps its 10 s (FC5).
    Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Ok)).IsEqualTo(TimeSpan.FromSeconds(10));
    Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Uninstalled)).IsEqualTo(TimeSpan.FromSeconds(10));
    Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Cancelled)).IsEqualTo(TimeSpan.FromSeconds(10));
    Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Degraded)).IsEqualTo(TimeSpan.FromSeconds(60));
    Check.That(SetupDisplayText.CloseDelay(SetupOutcome.RolledBack)).IsEqualTo(TimeSpan.FromSeconds(60));
    Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Failed)).IsEqualTo(TimeSpan.FromSeconds(60));
}
```

In `DegradedText_NamesTheRunningVersion_AndIsNotTheSuccessText`, replace `Check.That(mapped.IsFailure).IsTrue();` with:

```csharp
Check.That(mapped.State).IsEqualTo(SetupDisplayState.Failure);
```

In `AssertMaps`, replace the last line with:

```csharp
Check.That(mapped.State).IsEqualTo(SetupDisplayText.StateFor(outcome));
```

- [ ] **Step 4: Retarget `SetupProgressWindow` — BOTH reads**

In `ShowResult`, replace the close-delay read:

```csharp
ScheduleClose(SetupDisplayText.CloseDelayFor(result.State));
```

and the `:147` band-title foreground — **the field is `_title`** (r4 finding 2: there is no local `title` in `ShowResult`):

```csharp
_title.Foreground = result.State == SetupDisplayState.Failure
    ? Tokens.BrushAccentDanger
    : Tokens.BrushTextPrimary;
```

(The AccentDanger pin in `SetupProgressWindowContrastTests:77` keeps holding until Task 5 rewrites that test.)

- [ ] **Step 5: Retarget the remaining construction site**

`SetupProgressWindowContrastTests.cs:75` (interim — Task 5 replaces the whole test):

```csharp
window.ShowResult(new SetupDisplayResult("failure", SetupDisplayState.Failure));
```

- [ ] **Step 6: Run the Setup suite**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --blame-hang-timeout 300s`
Expected: PASS (any failure names a missed consumer — fold it into this task).

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Setup/Display/SetupDisplayText.cs src/EyeCtrl.Setup/Display/SetupProgressWindow.cs tests/EyeCtrl.Setup.Tests/Display/SetupDisplayTextTests.cs tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowContrastTests.cs
git commit -m "feat(setup): three-state result model — State enum is the single source of truth"
```

---

### Task 4: The app mark decodes its 256-px frame

**Files:**
- Modify: `src/EyeCtrl.Setup/Display/SetupProgressWindow.cs` (`LoadAppIcon`)

**Interfaces:**
- Produces: `SetupProgressWindow.LoadAppIcon()` returning the largest ico frame as a frozen `BitmapSource` (r3 F2: `ImageSource` has no `PixelWidth`) — consumed by Task 5's band and Task 8's choice band.

- [ ] **Step 1: Write the failing test** (in `SetupProgressWindowTests`)

```csharp
[Fact]
public void TheAppMark_DecodesTheLargestIcoFrame_NotFrameZero()
{
    // r1 F11: BitmapImage decodes frame 0 of eyectrl.ico, which is 16x16 — blurred at 52-60 DIP
    // on the 200 % display. The band mark must decode the 256-px frame.
    RunSta(() =>
    {
        var icon = SetupProgressWindow.LoadAppIcon();
        Check.That(icon).IsNotNull();
        Check.That(icon!.PixelWidth).IsEqualTo(256);
        Check.That(icon.PixelHeight).IsEqualTo(256);
    });
}
```

- [ ] **Step 2: Run it — Expected: FAIL** (the current `LoadAppIcon` returns frame 0).

- [ ] **Step 3: Implement**

Replace `LoadAppIcon` with:

```csharp
/// <summary>EyeCtrl's icon for the band mark and the taskbar button. The .ico's frame 0 is
/// 16×16 (r1 F11), so the LARGEST frame is selected explicitly; a missing icon must never stop
/// the screen from appearing. The pack scheme is touched first so a lone-test run (no Window
/// ctor yet) does not swallow a UriFormatException into null (r3 F17); the stream is disposed —
/// OnLoad has already copied the frames.</summary>
internal static BitmapSource? LoadAppIcon()
{
    try
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var stream = Application.GetResourceStream(new Uri(IconResource))?.Stream;
        if (stream is null) return null;
        using (stream)
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            frame.Freeze();
            return frame;
        }
    }
    catch (Exception)
    {
        return null;
    }
}
```

(Add `using System.Windows.Media.Imaging;`. `Window.Icon` and `Image.Source` accept `BitmapSource`.)

- [ ] **Step 4: Run it — Expected: PASS** (also standalone: `dotnet test tests/EyeCtrl.Setup.Tests --filter "TheAppMark_DecodesTheLargestIcoFrame_NotFrameZero"`).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Setup/Display/SetupProgressWindow.cs tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowTests.cs
git commit -m "feat(setup): the band mark decodes the ico's 256-px frame (r1 F11)"
```

---

### Task 5: SetupProgressWindow — the B layout (band, edge, stage plan, sibling result body)

**Files:**
- Modify: `src/EyeCtrl.Setup/Display/SetupProgressWindow.cs` (full content rebuild)
- Modify: `tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowContrastTests.cs` (rewrite for the new tree)
- Modify: `tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowTests.cs` (`:122` retarget, body-visibility test, plan-seam test, FC4 tree check)
- Using directives to add: `tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowTests.cs` gains `using EyeCtrl.Setup.Windows;` (`PhysicalFileSystem`, Task 6) and `using EyeCtrl.Sdk.Wpf;` (`Dwell`, the FC4 probe); `SetupProgressWindowContrastTests.cs` gains `using System.Windows;` (`FrameworkElement`) and `using EyeCtrl.Tests.TestSupport;` (`FitText`).

**Interfaces:**
- Consumes: `SetupWindowMetrics.*` (Task 1), `SetupDisplayState` (Task 3), `SetupBrushes` (Task 1), `LoadAppIcon` (Task 4), `UninstallChoiceWindow.CreateProse/CreateTitle/CreateStageLabel` (Task 2), `SetupDisplayText.Stage/ProgressTitle/Result` (existing).
- Produces: accessors `TitleText` (kept — `Window.Title`, read at `:34`/`:183`), `BandTitleText`, `ResultText`, `DetailText`, `StageText`, `ShowsResultBody`, `ResultStateGlyph` (`Path` — the contrast test reads it directly); `SetBandTitle(string)`; `PlanFor(SetupVerb)` (internal static). Task 6's FC7 test consumes all of them.

- [ ] **Step 1: Write the failing tests**

In `SetupProgressWindowTests`, retarget `TheResultReplacesTheProgress_WithItsOwnText` (`:122`) and add three tests:

```csharp
// inside TheResultReplacesTheProgress_WithItsOwnText, after ShowResult:
Check.That(window.ResultText).IsEqualTo(English.T("uninstall.done.kept"));
Check.That(window.BandTitleText).IsEqualTo(English.T("uninstall.working"));
Check.That(window.TitleText).IsEqualTo(English.T("uninstall.working"));
Check.That(window.DetailText).IsEqualTo(string.Empty);
Check.That(window.StageText).IsEqualTo(string.Empty);
```

```csharp
[Fact]
public void ShowResult_TogglesToTheResultBody_AndClearsTheProgressTexts()
{
    // r3 finding 2's mutant: a result body nested inside the collapsed progress stack would
    // never be visible. The two stacks are siblings, and the progress texts are cleared so the
    // visual tree holds no preferred-table text (FC7).
    RunSta(() =>
    {
        var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
        try
        {
            window.Stage(SetupStage.Verify);
            Check.That(window.ShowsResultBody).IsFalse();

            var result = new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.Ok, Version = "1.0.0" };
            window.ShowResult(SetupDisplayText.Result(English, result, AppData));

            Check.WithCustomMessage("The result body was not shown.")
                .That(window.ShowsResultBody).IsTrue();
            Check.That(window.DetailText).IsEqualTo(string.Empty);
            Check.That(window.StageText).IsEqualTo(string.Empty);
        }
        finally
        {
            window.Close();
        }
    });
}

[Fact]
public void TheStagePlan_IsDataDriven_PerVerb_AndTheTreeStaysDisplayOnly()
{
    // D9/r3 F3: the plan is a display-side per-verb table. Uninstall plans exactly one stage
    // today (Stop); repair and revert fabricate nothing (they have no window at all — the
    // factory test pins that). FC4/r3 T4: the progress tree gains no Button and no locally-set
    // Dwell mode (r4 finding 6: the SDK's default mode IS Invoke, so a GetMode comparison
    // cannot find anything — probe the local value instead).
    Check.That(SetupProgressWindow.PlanFor(SetupVerb.Update).Count).IsEqualTo(4);
    Check.That(SetupProgressWindow.PlanFor(SetupVerb.Rollback).Count).IsEqualTo(4);
    Check.That(SetupProgressWindow.PlanFor(SetupVerb.Uninstall).Count).IsEqualTo(1);
    Check.That(SetupProgressWindow.PlanFor(SetupVerb.Uninstall)[0]).IsEqualTo(SetupStage.Stop);

    RunSta(() =>
    {
        var window = new SetupProgressWindow(English, SetupVerb.Update, close: () => { });
        try
        {
            Check.That(FitText.Descendants<System.Windows.Controls.Button>((FrameworkElement)window.Content))
                .IsEmpty();
            Check.That(FitText.Descendants<FrameworkElement>((FrameworkElement)window.Content)
                .Where(e => e.ReadLocalValue(Dwell.ModeProperty) != DependencyProperty.UnsetValue
                            || Dwell.GetEnabled(e))).IsEmpty();
        }
        finally
        {
            window.Close();
        }
    });
}
```

- [ ] **Step 2: Run them — Expected: FAIL** (`ResultText`, `BandTitleText`, `ShowsResultBody`, `PlanFor` do not exist).

- [ ] **Step 3: Rebuild the window content**

Replace the ctor's content-building block and add the members. The two result/progress stacks are **siblings in one host** (r3 finding 2 — a result body nested inside the collapsed progress stack would never be visible). **One `BuildBodyHost`, one `NewStateGlyph` — no duplicate blocks** (r4 finding 1). The glyphs carry `Width/Height = 24` (geometry units) + `LayoutTransform(box / 24)` so they DRAW at the box size and OCCUPY exactly the box in layout — the v3 `Width = box + transform` combination occupied box × scale (r4 finding 5):

```csharp
_edge = BuildEdge();
_band = BuildBand(out _bandTitle);
_bodyHost = BuildBodyHost();

var root = new Grid();
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // state edge
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // band
root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
Grid.SetRow(_edge, 0);
Grid.SetRow(_band, 1);
Grid.SetRow(_bodyHost, 2);
root.Children.Add(_edge);
root.Children.Add(_band);
root.Children.Add(_bodyHost);
Content = root;
_edge.Visibility = Visibility.Collapsed;
```

Fields and the plan seam (the five builder-assigned members are **not** readonly — CS0191, r3 finding 3; `_edge`/`_band`/`_bodyHost`/`_bandTitle` are assigned in the ctor itself and stay readonly):

```csharp
private readonly StringTable _strings;
private readonly SetupVerb _verb;
private readonly Border _edge = null!;
private readonly Border _band = null!;
private readonly Border _bodyHost = null!;
private readonly TextBlock _bandTitle = null!;
private StackPanel _progressBody = null!;
private StackPanel _resultBody = null!;
private TextBlock _detail = null!;
private TextBlock _resultText = null!;
private System.Windows.Shapes.Path _resultIcon = null!;
private System.Windows.Controls.Border _neutralDash = null!;
private readonly List<StageRow> _stageRows = [];

private sealed class StageRow
{
    public SetupStage Stage = default;
    public System.Windows.Shapes.Path Glyph = null!;
    public TextBlock Label = null!;
    public bool Done;
    public bool Active;
}

/// <summary>D9/r3 F3: the display-side per-verb plan. Uninstall plans one stage today (Stop —
/// the elevated job stops and deletes in one pass); a new SetupStage member slots into this
/// table. Any emitted stage outside the plan is appended by Stage().</summary>
internal static IReadOnlyList<SetupStage> PlanFor(SetupVerb verb) => verb switch
{
    SetupVerb.Uninstall => [SetupStage.Stop],
    _ => [SetupStage.Verify, SetupStage.Stop, SetupStage.Swap, SetupStage.Start],
};
```

The builders (full code — the band is a **two-column Grid** so the title can wrap, r3 F14; the body padding follows the mockup's 64 top):

```csharp
private Border BuildEdge() => new()
{
    Height = 6,
    Visibility = Visibility.Collapsed,
    Background = SetupBrushes.Frozen(Tokens.SurfaceDark),
};

private Border BuildBand(out TextBlock title)
{
    var mark = new System.Windows.Controls.Image
    {
        Width = SetupWindowMetrics.BandIconSize,
        Height = SetupWindowMetrics.BandIconSize,
        Source = LoadAppIcon(),
        VerticalAlignment = VerticalAlignment.Center,
    };
    title = UninstallChoiceWindow.CreateTitle(SetupDisplayText.ProgressTitle(_strings, _verb));
    title.VerticalAlignment = VerticalAlignment.Center;
    title.Margin = new Thickness(22, 0, 0, 0);

    // r3 F14: a horizontal StackPanel offers the title unlimited width, so TextWrapping never
    // applies. The two-column Grid gives the mark an Auto slot and the title the rest.
    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    Grid.SetColumn(mark, 0);
    Grid.SetColumn(title, 1);
    grid.Children.Add(mark);
    grid.Children.Add(title);

    return new Border
    {
        Background = SetupBrushes.Opaque(Tokens.SurfaceMedium),
        Padding = new Thickness(88, 22, 88, 22),
        MinHeight = 132,
        Child = grid,
    };
}

private Border BuildBodyHost()
{
    var host = new Border
    {
        Padding = new Thickness(88, 64, 88, 48),
        Background = SetupBrushes.Opaque(Tokens.SurfaceDark),
    };
    var stack = new StackPanel();

    // The progress body: detail line + one row per planned stage (D9).
    _progressBody = new StackPanel();
    _detail = UninstallChoiceWindow.CreateProse(
        SetupDisplayText.ProgressDetail(_strings, _verb), secondary: true);
    _detail.Margin = new Thickness(0, 0, 0, 32);
    _progressBody.Children.Add(_detail);
    foreach (var stage in PlanFor(_verb))
    {
        var row = new StageRow { Stage = stage };
        row.Glyph = NewPendingGlyph();
        row.Label = UninstallChoiceWindow.CreateStageLabel(
            SetupDisplayText.Stage(_strings, _verb, stage));
        row.Label.Margin = new Thickness(24, 0, 0, 0);
        row.Label.VerticalAlignment = VerticalAlignment.Center;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 15) };
        line.Children.Add(row.Glyph);
        line.Children.Add(row.Label);
        _stageRows.Add(row);
        _progressBody.Children.Add(line);
    }

    // The result body (D8): neutral dash / state icon + confirming text, collapsed until
    // ShowResult. SIBLING of the progress body — never nested inside it (r3 finding 2).
    _resultBody = new StackPanel();
    _resultBody.Visibility = Visibility.Collapsed;
    _neutralDash = new System.Windows.Controls.Border
    {
        Width = 64,
        Height = 10,
        CornerRadius = new CornerRadius(5),
        Background = SetupBrushes.Frozen(Tokens.TextDim),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 0, 0, 24),
        Visibility = Visibility.Collapsed,
    };
    _resultIcon = NewStateGlyph();
    _resultText = UninstallChoiceWindow.CreateTitle(string.Empty);
    _resultBody.Children.Add(_neutralDash);
    _resultBody.Children.Add(_resultIcon);
    _resultBody.Children.Add(_resultText);

    stack.Children.Add(_progressBody);
    stack.Children.Add(_resultBody);
    host.Child = stack;
    return host;
}
```

The glyph builders and state painters (full code):

```csharp
/// <summary>A pending stage row: outline diamond (r1 F14 — non-round), drawn at creation
/// (r3 finding: pending glyphs that are never painted show nothing). Width/Height are the
/// GEOMETRY units (24); the LayoutTransform lifts the drawn glyph to the 30-DIP box and the
/// layout footprint stays 30 (r4 finding 5 — Width = box + transform double-scaled it).</summary>
private System.Windows.Shapes.Path NewPendingGlyph() => new()
{
    Data = Geometry.Parse("M12 3.6 L20.4 12 L12 20.4 L3.6 12 Z"),
    Stroke = Tokens.BrushBorderSubtle,
    StrokeThickness = 1.7,
    StrokeLineJoin = PenLineJoin.Round,
    Width = 24,
    Height = 24,
    Stretch = Stretch.None,
    LayoutTransform = new ScaleTransform(30 / 24.0, 30 / 24.0),
    VerticalAlignment = VerticalAlignment.Center,
};

/// <summary>The result state glyph: 24 geometry units under a LayoutTransform to the 64-DIP
/// box (r4 finding 5 — draws at 64 and occupies 64, not 171). Exposed for the contrast test.</summary>
private System.Windows.Shapes.Path NewStateGlyph() => new()
{
    StrokeThickness = 1.7,
    StrokeLineJoin = PenLineJoin.Round,
    StrokeStartLineCap = PenLineCap.Round,
    StrokeEndLineCap = PenLineCap.Round,
    Width = 24,
    Height = 24,
    Stretch = Stretch.None,
    LayoutTransform = new ScaleTransform(
        SetupWindowMetrics.ResultIconSize / 24.0, SetupWindowMetrics.ResultIconSize / 24.0),
    HorizontalAlignment = HorizontalAlignment.Left,
    Margin = new Thickness(0, 0, 0, 24),
};

private void MarkActive(StageRow row)
{
    // r3 finding: the previously active row must leave the active state, or StageText sticks
    // to the first stage forever.
    foreach (var other in _stageRows.Where(r => r != row && r.Active))
    {
        other.Active = false;
        other.Done = true;
        other.Glyph.Data = Geometry.Parse("M4 12.5 L9.5 18 L20 6.5");
        other.Glyph.Fill = null;
        other.Glyph.Stroke = Tokens.BrushAccentSuccess;
        other.Label.Foreground = Tokens.BrushTextSecondary;
    }

    row.Active = true;
    row.Glyph.Data = Geometry.Parse("M12 3.6 L20.4 12 L12 20.4 L3.6 12 Z");
    row.Glyph.Fill = Tokens.BrushAccent;
    row.Glyph.Stroke = Tokens.BrushAccent;
    row.Label.Foreground = Tokens.BrushTextPrimary;
}

/// <summary>D9/r3 F3: an emitted stage outside the plan is appended (with its own weight and
/// colour) and marked active — never silently dropped.</summary>
private StageRow AppendStageRow(SetupStage stage, string label)
{
    var row = new StageRow { Stage = stage };
    row.Glyph = NewPendingGlyph();
    row.Label = UninstallChoiceWindow.CreateStageLabel(label);
    row.Label.Margin = new Thickness(24, 0, 0, 0);
    row.Label.VerticalAlignment = VerticalAlignment.Center;
    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 15, 0, 15) };
    line.Children.Add(row.Glyph);
    line.Children.Add(row.Label);
    // Child 0 is the detail line — the rows start at 1 (r3 finding: the index was off by one).
    _progressBody.Children.Insert(1 + _stageRows.Count, line);
    _stageRows.Add(row);
    return row;
}

/// <summary>D8's state colours: edge + result icon carry the colour (D7 — the title stays
/// TextPrimary). Neutral = the dim dash Border instead of the path (r1 F14, mockup).</summary>
private void PaintStateGlyph(SetupDisplayState state)
{
    _resultIcon.Visibility = state == SetupDisplayState.Neutral
        ? Visibility.Collapsed
        : Visibility.Visible;
    _neutralDash.Visibility = state == SetupDisplayState.Neutral
        ? Visibility.Visible
        : Visibility.Collapsed;
    if (state == SetupDisplayState.Neutral)
        return;

    _resultIcon.StrokeThickness = 1.7;
    switch (state)
    {
        case SetupDisplayState.Success:
            _resultIcon.Data = Geometry.Parse("M4 12.5 L9.5 18 L20 6.5");
            _resultIcon.Stroke = Tokens.BrushAccentSuccess;
            _resultIcon.Fill = null;
            break;
        case SetupDisplayState.Failure:
            _resultIcon.Data = Geometry.Parse(
                "M12 3.5 L21.2 19.8 L2.8 19.8 Z M12 9.6 L12 14.2 M12 17.2 L12 17.3");
            _resultIcon.Stroke = Tokens.BrushAccentDanger;
            _resultIcon.Fill = null;
            break;
    }
}

private void DemoteBand()
{
    _bandTitle.Foreground = Tokens.BrushTextSecondary;
    _bandTitle.FontWeight = Tokens.FontWeightMedium;
}
```

The public/interior surface (full code):

```csharp
/// <summary>Advances the stage plan: marks the emitted stage active, earlier active rows done,
/// and appends any stage outside the plan (D9/r3 F3).</summary>
internal void Stage(SetupStage stage)
{
    var row = _stageRows.FirstOrDefault(r => r.Stage == stage)
        ?? AppendStageRow(stage, SetupDisplayText.Stage(_strings, _verb, stage));
    MarkActive(row);
}

/// <summary>Re-resolves the band title from the given table (FC7 after D8, r3 F1) — and keeps
/// the taskbar title in sync, so the package cannot park its claim there either.</summary>
internal void SetBandTitle(string title)
{
    _bandTitle.Text = title;
    Title = title;
}

/// <summary>Replaces the progress with the terminal result: the state edge and icon carry the
/// colour (D7), the band is demoted (r3 F2), the progress texts are CLEARED so no preferred-table
/// text survives anywhere in the visual tree (FC7), and the close timer arms (FC5).</summary>
internal void ShowResult(SetupDisplayResult result)
{
    _edge.Background = SetupBrushes.Frozen(StateColor(result.State));
    _edge.Visibility = Visibility.Visible;
    DemoteBand();
    _progressBody.Visibility = Visibility.Collapsed;
    _resultBody.Visibility = Visibility.Visible;
    PaintStateGlyph(result.State);
    _resultText.Text = result.Text;
    _detail.Text = string.Empty;
    foreach (var row in _stageRows)
        row.Label.Text = string.Empty;
    ScheduleClose(SetupDisplayText.CloseDelayFor(result.State));
}

/// <summary>The edge colours per state (r3 T4: the per-theme contrast tests assert them).</summary>
private static System.Windows.Media.Color StateColor(SetupDisplayState state) => state switch
{
    SetupDisplayState.Success => Tokens.AccentSuccess,
    SetupDisplayState.Failure => Tokens.AccentDanger,
    _ => Tokens.TextDim,
};
```

Accessors (full code — `TitleText` is KEPT: `SetupProgressWindowTests:34` and `:183` read it; `ResultStateGlyph` replaces the width-based tree search, r4 finding 5):

```csharp
/// <summary>The taskbar/window title: the process title (FC7: re-resolved on results via
/// SetBandTitle).</summary>
internal string TitleText => Title;

/// <summary>The band title as drawn (the FC7 test reads it).</summary>
internal string BandTitleText => _bandTitle.Text;

/// <summary>The confirming body text (D8 — SetupProgressWindowTests:122 reads it).</summary>
internal string ResultText => _resultText.Text;

internal string DetailText => _detail.Text;

/// <summary>The active stage row's label, or empty once the result body is shown.</summary>
internal string StageText =>
    _resultBody.Visibility == Visibility.Visible
        ? string.Empty
        : _stageRows.FirstOrDefault(r => r.Active)?.Label.Text ?? string.Empty;

/// <summary>True while the result body is the visible one. The logical-tree walk STOPS AT THE
/// WINDOW (r4 finding 3: a never-shown Window's own Visibility defaults to Collapsed — walking
/// into it made this accessor always false on correct code). Mutant M2 (nesting the result body
/// inside the collapsed progress stack) still fails it: the collapsed ancestor is the stack.</summary>
internal bool ShowsResultBody
{
    get
    {
        if (_resultBody.Visibility != Visibility.Visible) return false;
        for (System.Windows.DependencyObject? ancestor = System.Windows.LogicalTreeHelper.GetParent(_resultBody);
             ancestor is not null && !ReferenceEquals(ancestor, this);
             ancestor = System.Windows.LogicalTreeHelper.GetParent(ancestor))
        {
            if (ancestor is System.Windows.UIElement element
                && element.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The result state glyph (the contrast test reads stroke, transform and bounds
/// directly instead of searching the tree by Width — r4 finding 5).</summary>
internal System.Windows.Shapes.Path ResultStateGlyph => _resultIcon;
```

The rest of the class is unchanged: Q13 style block, bounds, `Focusable=false`, `IsHitTestVisible=false`, `Icon = LoadAppIcon()`, `_closeTimer`, `Closed` wiring, `UseLayoutRounding`, `Background = SetupBrushes.Opaque(Tokens.SurfaceDark)`, the Win32 style helpers, and `ScheduleClose`. The old `LoadAppIcon` BitmapImage version and the old `OpaqueSurfaceDark` helper are gone (replaced by Task 4's decoder and `SetupBrushes`).

- [ ] **Step 4: Rewrite the contrast test for the new tree**

Replace `SetupProgressWindowContrastTests.TheResultText_KeepsItsContrast_OnTheOpaqueDarkSurface` with (r3 F13: the detail pin KEPT through `_detail` before `ShowResult`; the band title measured against the BAND background, normal AND demoted; the icon's LayoutTransform pinned so the 24-unit geometry cannot shrink, r4 finding 5):

```csharp
[Fact]
public void TheResultText_KeepsItsContrast_OnTheOpaqueDarkSurface()
{
    RunSta(() =>
    {
        try
        {
            foreach (var theme in ThemeRegistry.All)
            {
                Tokens.Activate(theme);
                var window = new SetupProgressWindow(
                    StringTable.EmbeddedEnglish, SetupVerb.Update, close: () => { });
                try
                {
                    var background = (SolidColorBrush)window.Background;
                    Check.WithCustomMessage($"{theme.Name}: the window background is not frozen.")
                        .That(background.IsFrozen).IsTrue();
                    Check.That(background.Color.A).IsEqualTo((byte)255);

                    var band = BandOf(window);
                    var bandBackground = (SolidColorBrush)band.Background;
                    Check.WithCustomMessage($"{theme.Name}: the band background is not frozen.")
                        .That(bandBackground.IsFrozen).IsTrue();
                    Check.That(bandBackground.Color.A).IsEqualTo((byte)255);

                    var bandTitle = BandTitleOf(window);
                    Check.WithCustomMessage($"{theme.Name}: the band title brush is not TextPrimary.")
                        .That(((SolidColorBrush)bandTitle.Foreground).Color).IsEqualTo(theme.TextPrimary);

                    var detail = DetailOf(window);
                    Check.WithCustomMessage($"{theme.Name}: the detail brush is not TextSecondary.")
                        .That(((SolidColorBrush)detail.Foreground).Color).IsEqualTo(theme.TextSecondary);

                    var surface = background.Color;
                    Check.WithCustomMessage(
                            $"{theme.Name}: title contrast {Contrast(theme.TextPrimary, surface):0.00}:1 < 10.")
                        .That(Contrast(theme.TextPrimary, surface)).IsGreaterOrEqualThan(MinimumTitleContrast);
                    Check.WithCustomMessage(
                            $"{theme.Name}: detail contrast {Contrast(theme.TextSecondary, surface):0.00}:1 < 4.5.")
                        .That(Contrast(theme.TextSecondary, surface)).IsGreaterOrEqualThan(MinimumDetailContrast);

                    var bandSurface = SetupBrushes.OpaqueColor(Tokens.SurfaceMedium);
                    var bandTitleContrast = Contrast(theme.TextPrimary, bandSurface);
                    Check.WithCustomMessage(
                            $"{theme.Name}: the band title contrast on the band is "
                            + $"{bandTitleContrast:0.00}:1 < 10.")
                        .That(bandTitleContrast).IsGreaterOrEqualThan(MinimumTitleContrast);

                    // D8/r3 F2: on results the band title is demoted (TextSecondary — Midnight
                    // measures 5.63 on the band) and the state edge + icon carry the colour; the
                    // confirming text stays TextPrimary (D7).
                    window.SetBandTitle(SetupDisplayText.ProgressTitle(
                        StringTable.EmbeddedEnglish, SetupVerb.Update));
                    window.ShowResult(new SetupDisplayResult(
                        SetupDisplayText.ProgressTitle(StringTable.EmbeddedEnglish, SetupVerb.Update),
                        SetupDisplayState.Failure));

                    Check.WithCustomMessage($"{theme.Name}: the band title was not demoted.")
                        .That(((SolidColorBrush)BandTitleOf(window).Foreground).Color)
                        .IsEqualTo(theme.TextSecondary);
                    var demotedContrast = Contrast(theme.TextSecondary, bandSurface);
                    Check.WithCustomMessage(
                            $"{theme.Name}: the demoted band title contrast is {demotedContrast:0.00}:1 < 4.5.")
                        .That(demotedContrast).IsGreaterOrEqualThan(MinimumDetailContrast);
                    Check.WithCustomMessage($"{theme.Name}: the state edge was not failure-red.")
                        .That(((SolidColorBrush)EdgeOf(window).Background).Color)
                        .IsEqualTo(theme.AccentDanger);
                    Check.WithCustomMessage($"{theme.Name}: the confirming text brush is not TextPrimary (D7).")
                        .That(((SolidColorBrush)ResultTitleOf(window).Foreground).Color)
                        .IsEqualTo(theme.TextPrimary);
                    Check.WithCustomMessage(
                            $"{theme.Name}: the failure-icon contrast is "
                            + $"{Contrast(theme.AccentDanger, surface):0.00}:1 < {MinimumFailureTitleContrast}.")
                        .That(Contrast(theme.AccentDanger, surface))
                        .IsGreaterOrEqualThan(MinimumFailureTitleContrast);
                    Check.WithCustomMessage($"{theme.Name}: the failure icon is not danger-red.")
                        .That(((SolidColorBrush)window.ResultStateGlyph.Stroke).Color)
                        .IsEqualTo(theme.AccentDanger);

                    // r4 finding 5: the glyph's geometry is 24 units under a LayoutTransform to
                    // the 64-DIP box — pin the transform (a missing or halved transform drew the
                    // check at ~17 DIP).
                    Check.That(window.ResultStateGlyph.LayoutTransform)
                        .IsInstanceOf<ScaleTransform>();
                    var transform = (ScaleTransform)window.ResultStateGlyph.LayoutTransform;
                    Check.That(transform.ScaleX).IsEqualTo(SetupWindowMetrics.ResultIconSize / 24.0);
                    Check.That(window.ResultStateGlyph.Width).IsEqualTo(24);
                    Check.That(window.ResultStateGlyph.Data.Bounds.Width).IsStrictlyGreaterThan(14);
                    Check.That(window.ResultStateGlyph.Data.Bounds.Height).IsStrictlyGreaterThan(14);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    });
}

private static Border BandOf(SetupProgressWindow window) => (Border)((Grid)window.Content).Children[1];
private static Border EdgeOf(SetupProgressWindow window) => (Border)((Grid)window.Content).Children[0];

private static TextBlock BandTitleOf(SetupProgressWindow window) =>
    ((Grid)BandOf(window).Child).Children.OfType<TextBlock>().First();

private static TextBlock DetailOf(SetupProgressWindow window) =>
    FitText.Descendants<TextBlock>((FrameworkElement)window.Content)
        .First(t => t.FontSize == SetupWindowMetrics.ProseFontSize);

private static TextBlock ResultTitleOf(SetupProgressWindow window) =>
    FitText.Descendants<TextBlock>((FrameworkElement)window.Content)
        .First(t => t.FontSize == SetupWindowMetrics.TitleFontSize && t != BandTitleOf(window));
```

(`FitText.Descendants` is already used by `SetupFitTests` — the file gains `using EyeCtrl.Tests.TestSupport;`. `MinimumFailureTitleContrast` (4.4) stays as the icon floor per D7; `MinimumTitleContrast` (10) covers the band title against BOTH surfaces; `MinimumDetailContrast` (4.5) covers the detail and the demoted title. The detail is reachable while the progress body is visible — the contrast test asserts it BEFORE `ShowResult`.)

- [ ] **Step 5: Run the two window test files**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "SetupProgressWindow" --blame-hang-timeout 300s`
Expected: PASS — including `AtDpiTwo` (rides the new root Grid), `EveryStage_ShowsTheOneProgressWindowOnce`, `TheSelfCloseTimer_ActuallyFires`, `ClosingTheWindowEarly`, and the factory test.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Setup/Display/SetupProgressWindow.cs tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowContrastTests.cs tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowTests.cs
git commit -m "feat(setup): B progress/result window — band, state edge, stage plan, three-state result"
```

---

### Task 6: FC7 — the band title re-resolves through Finish (tested end to end)

**Files:**
- Modify: `src/EyeCtrl.Setup/Display/WpfSetupDisplay.cs` (`Finish` — one line)
- Create: test in `tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowTests.cs`

**Interfaces:**
- Consumes: Task 5's `BandTitleText`/`ResultText`/`ShowsResultBody` accessors, `ResultStateGlyph`, and `SetBandTitle`; `SetupUi.TableFor` (exists); the `SetupUiTests:131-155` tamper pattern (`CopyShippedGerman` + `ReplaceGerman`, replicated locally).

- [ ] **Step 1: Write the failing test** (in `SetupProgressWindowTests`)

```csharp
[Fact]
public void ARefusedPackage_CannotLeaveItsOwnTextOnTheResultScreen()
{
    // r3 F1: the band title comes from the package's preferred table while the window is up.
    // On a VerifyFailed result, Finish must re-resolve it from the trusted table — a tampered
    // package cannot park its own claim above its refusal. Pattern after SetupUiTests:131-155:
    // BOTH the package and the install root get the shipped German file + manifest (without the
    // manifest every German surface falls back to English, StringTable.cs:568-579).
    var root = Path.Combine(Path.GetTempPath(), "eyectrl_setup_fc7_" + Guid.NewGuid().ToString("N"));
    var package = Path.Combine(root, "package");
    Directory.CreateDirectory(package);
    try
    {
        CopyShippedGerman(package);
        ReplaceGerman(package, "Update wird installiert", "Paket behauptet installiert");

        var paths = new SetupPaths(root, root, root);
        CopyShippedGerman(paths.InstallRoot);   // the trusted table, in German

        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        File.WriteAllText(paths.SettingsPath, """{"general":{"uiLanguage":"german"}}""");

        var ui = SetupUi.Load(paths, new PhysicalFileSystem(), preferredDirectory: package);
        Check.WithCustomMessage("The test needs the tampered table to be the preferred one.")
            .That(ui.Strings.T("update.progress.title")).IsEqualTo("Paket behauptet installiert");
        Check.WithCustomMessage("TableFor must route VerifyFailed to the trusted table.")
            .That(SetupDisplayText.ProgressTitle(ui.TableFor(
                new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.VerifyFailed }),
                SetupVerb.Update))
            .IsEqualTo("Update wird installiert");

        SetupProgressWindow? window = null;
        string? bandTitle = null;
        string? resultText = null;
        string? taskbarTitle = null;
        RunSta(() =>
        {
            using var display = new WpfSetupDisplay(
                SetupVerb.Update, ui, Path.Combine(root, "appdata"),
                createWindow: () => window = new SetupProgressWindow(ui.Strings, SetupVerb.Update, close: () => { }),
                showWindow: w => _ = new WindowInteropHelper(w).EnsureHandle());

            // The window must EXIST before Finish touches it (createWindow runs inside
            // EnsureWindow) — the working pattern calls Stage first (:313-324).
            display.Stage(SetupStage.Verify);
            var shown = window!;

            // Finish runs on a worker (it blocks on the result window's close timer); the test
            // thread polls THROUGH the window's own dispatcher (r4 finding 9: a DispatcherTimer
            // built on the test thread ticks there, and reading `shown` from it throws), then
            // captures and closes via the same dispatcher.
            var worker = Task.Run(() => display.Finish(new SetupResult
            {
                Verb = SetupVerb.Update, Outcome = SetupOutcome.VerifyFailed,
            }));

            var deadline = Stopwatch.StartNew();
            while (!shown.Dispatcher.Invoke(() => shown.ShowsResultBody)
                   && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }

            shown.Dispatcher.Invoke(() =>
            {
                bandTitle = shown.BandTitleText;
                resultText = shown.ResultText;
                taskbarTitle = shown.TitleText;
                shown.Close();
            });
            worker.Wait(2000);
        });

        Check.WithCustomMessage("The band still carried the package's own title on the refusal screen.")
            .That(bandTitle).IsEqualTo("Update wird installiert");
        Check.WithCustomMessage("The refusal text must come from the trusted table.")
            .That(resultText).IsEqualTo(
                SetupDisplayText.Result(ui.InstalledStrings,
                    new SetupResult { Verb = SetupVerb.Update, Outcome = SetupOutcome.VerifyFailed },
                    Path.Combine(root, "appdata")).Text);
        Check.WithCustomMessage("FC7 covers the taskbar title too — it still named the package's claim.")
            .That(taskbarTitle).IsEqualTo("Update wird installiert");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

private static void CopyShippedGerman(string directory)
{
    Directory.CreateDirectory(directory);
    File.Copy(GermanDraft.GermanPath, Path.Combine(directory, StringTable.GermanFileName), overwrite: true);
    File.Copy(
        Path.Combine(Path.GetDirectoryName(GermanDraft.GermanPath)!, StringTable.ManifestFileName),
        Path.Combine(directory, StringTable.ManifestFileName),
        overwrite: true);
}

private static void ReplaceGerman(string directory, string oldText, string newText)
{
    var path = Path.Combine(directory, StringTable.GermanFileName);
    File.WriteAllText(path, File.ReadAllText(path).Replace(oldText, newText));
}
```

(The German expectations come from the shipped table: "Update wird installiert" is `update.progress.title` and the trusted refusal is `update.result.verifyFailed` — the r3 F1 hole was exactly that the BAND could say the former while the BODY said the latter.)

- [ ] **Step 2: Run it — Expected: FAIL** (`Finish` does not re-resolve the band title yet — the band still reads "Paket behauptet installiert").

- [ ] **Step 3: Implement** — in `WpfSetupDisplay.Finish`, inside the `Invoke(...)` before `ShowResult`:

```csharp
_window!.SetBandTitle(SetupDisplayText.ProgressTitle(_ui.TableFor(result), _verb));
```

- [ ] **Step 4: Run it — Expected: PASS.**

- [ ] **Step 5: Mutation check (before the commit — the tree is clean only after it)**

Comment out the `SetBandTitle` line, re-run the test — Expected: FAIL. Restore the line, re-run — Expected: PASS.

- [ ] **Step 6: Commit (test + implementation together)**

```bash
git add src/EyeCtrl.Setup/Display/WpfSetupDisplay.cs tests/EyeCtrl.Setup.Tests/Display/SetupProgressWindowTests.cs
git commit -m "feat(setup): FC7 — the band title re-resolves through Finish (r3 F1), mutation-checked"
```

---

### Task 7: Choice buttons — the explicit template (no hover) and SD8 anatomy

**Files:**
- Modify: `src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs` (`CreateChoiceButton`)
- Modify: `tests/EyeCtrl.Setup.Tests/Display/UninstallChoiceWindowTests.cs` (two new tests; using directives below)

**Interfaces:**
- Produces: `CreateChoiceButton(string label, ButtonKind kind, string automationId, Action action)` — same signature; the visual anatomy changes per SD8. (The danger tint lives in `SetupBrushes.DangerTint()` since Task 1 — Task 8's per-theme test consumes it.)

- [ ] **Step 1: Write the failing tests** (in `UninstallChoiceWindowTests`)

```csharp
[Fact]
public void ChoiceButtons_UseAnExplicitTemplate_WithNoHoverTriggers()
{
    // r2 F2 (High): the Aero2 hover replaces fills under the gaze while the click-through
    // overlay sweeps on top. The template must carry the visuals and must not react to the
    // mouse. The named border/presenter are asserted by reference, not by re-parsing XAML
    // (r3 F18); the presenter's content is checked after ITS template applied (r4 finding 8:
    // ApplyTemplate on the button alone leaves the presenter's content null).
    WithWindow(window =>
    {
        foreach (var button in new[] { window.KeepButton, window.CancelButton, window.DeleteButton })
        {
            button.ApplyTemplate();
            Check.That(button.Template is not null).IsTrue();
            Check.WithCustomMessage("A choice button template carries hover/pressed triggers.")
                .That(button.Template.Triggers.Count).IsEqualTo(0);

            var border = button.Template.FindName("TemplateBorder", button) as Border;
            Check.WithCustomMessage("The template has no named border to carry the visuals.")
                .That(border).IsNotNull();
            var presenter = button.Template.FindName("Presenter", button) as ContentPresenter;
            Check.WithCustomMessage("The template has no content presenter.")
                .That(presenter).IsNotNull();

            presenter!.ApplyTemplate();
            Check.WithCustomMessage("The presenter does not carry the button's content.")
                .That(ReferenceEquals(presenter.Content, button.Content)).IsTrue();
        }
    });
}

[Fact]
public void ChoiceButtonFaces_FollowSD8_OutlinedNotFilled()
{
    WithWindow(window =>
    {
        var primary = (SolidColorBrush)window.KeepButton.Background;
        var quiet = (SolidColorBrush)window.CancelButton.Background;
        var danger = (SolidColorBrush)window.ConfirmDeleteButton.Background;
        Check.That(primary.Color).IsEqualTo(SetupBrushes.OpaqueColor(Tokens.SurfaceLight));
        Check.That(quiet.Color).IsEqualTo(SetupBrushes.OpaqueColor(Tokens.SurfaceMedium));
        Check.That(danger.Color).IsEqualTo(SetupBrushes.OpaqueColor(Tokens.SurfaceMedium));
        Check.WithCustomMessage("The danger button must carry a danger-coloured border.")
            .That(((SolidColorBrush)window.ConfirmDeleteButton.BorderBrush).Color)
            .IsEqualTo(Tokens.AccentDanger);

        // r3 F10: the weights live on the inner TextBlock (the Button only forwards the font).
        Check.That(((TextBlock)window.KeepButton.Content).FontWeight)
            .IsEqualTo(System.Windows.FontWeights.Bold);
        Check.That(((TextBlock)window.CancelButton.Content).FontWeight)
            .IsEqualTo(System.Windows.FontWeights.Normal);
        Check.That(((TextBlock)window.ConfirmDeleteButton.Content).FontWeight)
            .IsEqualTo(Tokens.FontWeightMedium);
    });
}
```

(The button widths are pinned by Task 8's LAYOUT-slot test — `Button.Width` is the requested size and cannot see row clipping, r4 finding 4.)

- [ ] **Step 2: Run them — Expected: FAIL** (default template, accent/danger fills, SemiBold everywhere).

- [ ] **Step 3: Implement `CreateChoiceButton`**

Replace the visual part (keep the signature, the `Click` wiring, the AutomationProperties, and `Dwell.SetMode(button, DwellMode.Invoke)`):

```csharp
var (background, foreground, border, weight) = kind switch
{
    ButtonKind.Primary => (SetupBrushes.Opaque(Tokens.SurfaceLight), Tokens.BrushTextPrimary,
        Tokens.BrushBorderSubtle, Tokens.FontWeightStrong),
    ButtonKind.Danger => (SetupBrushes.Opaque(Tokens.SurfaceMedium), Tokens.BrushAccentDanger,
        Tokens.BrushAccentDanger, Tokens.FontWeightMedium),
    _ => (SetupBrushes.Opaque(Tokens.SurfaceMedium), Tokens.BrushTextPrimary,
        Tokens.BrushBorderSubtle, FontWeights.Normal),
};

var text = new TextBlock
{
    Text = label,
    TextWrapping = TextWrapping.Wrap,
    TextAlignment = TextAlignment.Center,
    FontFamily = Tokens.FontStandard,
    FontSize = SetupWindowMetrics.ChoiceFontSize,
    FontWeight = weight,
    Foreground = foreground,
};

var button = new Button
{
    Content = text,
    Width = SetupWindowMetrics.ChoiceButtonWidth,
    Height = SetupWindowMetrics.ChoiceButtonHeight,
    Padding = new Thickness(16),
    HorizontalContentAlignment = HorizontalAlignment.Center,
    VerticalContentAlignment = VerticalAlignment.Center,
    Background = background,
    Foreground = foreground,
    BorderBrush = border,
    BorderThickness = new Thickness(Tokens.Current.BorderThicknessStandard),
    FontFamily = Tokens.FontStandard,
    FontSize = SetupWindowMetrics.ChoiceFontSize,
    FocusVisualStyle = null,
    UseLayoutRounding = true,
    Cursor = Cursors.Hand,
};

// r2 F2: an explicit template — the Aero2 hover must never replace the fills under the gaze.
// r3 F9: FrameworkElementFactory keeps FC8's no-XAML rule; SetValue carries the
// TemplateBindingExtension (SetBinding needs a BindingBase and would not compile).
var borderFactory = new FrameworkElementFactory(typeof(Border)) { Name = "TemplateBorder" };
borderFactory.SetValue(Border.BackgroundProperty,
    new TemplateBindingExtension(Button.BackgroundProperty));
borderFactory.SetValue(Border.BorderBrushProperty,
    new TemplateBindingExtension(Button.BorderBrushProperty));
borderFactory.SetValue(Border.BorderThicknessProperty,
    new TemplateBindingExtension(Button.BorderThicknessProperty));
borderFactory.SetValue(Border.PaddingProperty,
    new TemplateBindingExtension(Button.PaddingProperty));
borderFactory.SetValue(Border.CornerRadiusProperty,
    new CornerRadius(Tokens.Current.CornerRadiusSmall));
var presenter = new FrameworkElementFactory(typeof(ContentPresenter)) { Name = "Presenter" };
presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
    new TemplateBindingExtension(Button.HorizontalContentAlignmentProperty));
presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,
    new TemplateBindingExtension(Button.VerticalContentAlignmentProperty));
borderFactory.AppendChild(presenter);
button.Template = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
```

(The shared brush helpers come from `SetupBrushes` (Task 1) — no per-window copies, r3 F8. `button.Content` stays the `TextBlock`, so `SetupFitTests.MeasureButton`'s cast, its `button.Padding`-based room maths, and the `ScreenTwo_PutsNoUnderScreenOnesDelete` casts keep working. `CornerRadius = SmallCornerRadius` matches the overlay so the sweep doesn't overhang the corners, r3 F9. `HorizontalContentAlignment` is bound per spec §6 — the Button's own `Center` value flows through the TemplateBinding.)

- [ ] **Step 4: Run the choice window tests**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "UninstallChoiceWindow" --blame-hang-timeout 300s`
Expected: PASS — including `TargetsAre420By160...` and the L2 cell-identity check.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs tests/EyeCtrl.Setup.Tests/Display/UninstallChoiceWindowTests.cs
git commit -m "feat(setup): explicit button template (no hover) + SD8 outlined anatomy"
```

---

### Task 8: Choice window — the B layout (full-bleed band, 32-DIP body, H−80, confirm demotion)

**Files:**
- Modify: `src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs` (root layout, band, `ShowConfirm`, `BuildScreen` prose/title wiring)
- Modify: `tests/EyeCtrl.Setup.Tests/Display/UninstallChoiceWindowTests.cs` (band tests, per-theme gate, layout test update)

**Interfaces:**
- Consumes: Task 4's `LoadAppIcon`, Task 2's `CreateProse`/`CreateTitle`, `SetupBrushes.DangerTint()` (Task 1).
- Produces: `BandOf(window)`/`BandTitleOf(...)`/`ProseBlock(...)` test surfaces; **body side padding 32** so the 1500-DIP row fits exactly (r3 finding: 88-DIP sides clipped Delete below the 420 floor); prose keeps its 88-DIP inset via its container margin/`MaxWidth = BodyWidthChoice`; the **layout-slot** assertion that can actually see row clipping (r4 finding 4).

- [ ] **Step 1: Update the layout tests**

`TargetsAre420By160_WithA120Gap_AndNothingInTheCentre` keeps its pin as-is (`keep.Bottom ≈ H − 32 − 48` — Task 8's compensation preserves it). Add the band tests:

```csharp
[Fact]
public void TheChoiceBand_CarriesTheMarkAndTitle_AndTheRowFitsUnclipped()
{
    WithWindow(window =>
    {
        var band = BandOf(window);
        var title = BandTitleOf(band);
        Check.That(title.Text).IsEqualTo(English.T("uninstall.choice.title"));
        Check.That(title.FontSize).IsEqualTo(SetupWindowMetrics.TitleFontSize);

        // r3 F7: the full-bleed band removes the window padding; the body compensates so the
        // row stays at H - 32 - 48 (the :583 pin).
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(content, width: window.Width, height: window.Height, inheritFrom: window);
        var keep = Box(window.KeepButton, content);
        var delete = Box(window.DeleteButton, content);
        Check.That(keep.Bottom).IsCloseTo(window.Height - 32 - 48, 0.5);

        // r4 finding 4: RenderSize and requested Width stay 420 even when the row clips — WPF
        // centres an oversized row with a NEGATIVE offset, so keep.Left/delete.Right look
        // correct at padding 88 too. The layout slot is the proof: the row's parent (the
        // screen's button Grid) must be granted its full 1500 DIP, and the row must not be
        // layout-clipped.
        var row = (FrameworkElement)window.DeleteButton.Parent;
        var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(row);
        Check.WithCustomMessage($"The button row's layout slot is {slot.Width} — the 88-DIP body padding clipped the 1500-DIP row (M4).")
            .That(slot.Width).IsGreaterOrEqualThan(1500);
        Check.WithCustomMessage("The row is layout-clipped: targets would lose their right edge.")
            .That(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(row)).IsNull();
    });
}

[Fact]
public void TheConfirmScreen_TintsAndCentresTheBand_AndKeepsTheProseRules()
{
    WithWindow(window =>
    {
        Arm(window);
        Click(window.DeleteButton);

        var band = BandOf(window);
        var tint = (SolidColorBrush)band.Background;
        Check.WithCustomMessage("The danger tint must be opaque and frozen (r3 F11).")
            .That(tint.IsFrozen && tint.Color.A == 255).IsTrue();
        var expected = SetupBrushes.OpaqueColor(SetupBrushes.Blend(
            Tokens.SurfaceMedium, Tokens.AccentDanger, 0.18));
        Check.That(tint.Color).IsEqualTo(expected);

        // SD9: the confirm band's content is centred (the two-column Grid itself is centred);
        // SD2: its prose stays centred while screen 1's prose is left-aligned and primary.
        Check.That(BandGridOf(window).HorizontalAlignment)
            .IsEqualTo(HorizontalAlignment.Center);
        Check.That(window.ProseBlock(confirm: true).TextAlignment)
            .IsEqualTo(TextAlignment.Center);
        Check.That(window.ProseBlock(confirm: false).TextAlignment)
            .IsEqualTo(TextAlignment.Left);
        Check.That(window.ProseBlock(confirm: false).FontWeight)
            .IsEqualTo(FontWeights.Normal);                    // primary colour, normal weight (r4 F7)
        Check.That(((SolidColorBrush)window.ProseBlock(confirm: false).Foreground).Color)
            .IsEqualTo(Tokens.TextPrimary);

        // D8/r3 finding: the title lives in the band — the prose stacks must not repeat it.
        Check.That(window.ProseBlock(confirm: false).Text)
            .IsNotEqualTo(English.T("uninstall.choice.title"));

        // r3 F16: the taskbar title follows the confirm screen.
        Check.That(window.Title).IsEqualTo(English.T("uninstall.confirmDelete.title"));
    });
}

private static Grid BandGridOf(UninstallChoiceWindow window) =>
    (Grid)BandOf(window).Child;

[Fact]
public void EveryButtonKind_ClearsTheSweepGate_AndTheTintDerives_InEveryTheme()
{
    // Spec §7/r3 T4: the sweep-vs-fill gate (≥ 3:1) binds EVERY kind in EVERY theme, the danger
    // label keeps ≥ 4.5 on its own ground, and the tint derivation is checked PER THEME against
    // inline arithmetic — not against the production helper with itself (r4 finding 11).
    RunSta(() =>
    {
        try
        {
            foreach (var theme in ThemeRegistry.All)
            {
                Tokens.Activate(theme);
                var grounds = new[]
                {
                    (ButtonKind.Primary, SetupBrushes.OpaqueColor(Tokens.SurfaceLight)),
                    (ButtonKind.Normal, SetupBrushes.OpaqueColor(Tokens.SurfaceMedium)),
                    (ButtonKind.Danger, SetupBrushes.OpaqueColor(Tokens.SurfaceMedium)),
                };
                foreach (var (kind, ground) in grounds)
                {
                    var ratio = Contrast(Tokens.Accent, ground);
                    Check.WithCustomMessage($"{theme.Name}/{kind}: sweep-vs-fill {ratio:0.00}:1 < 3.")
                        .That(ratio).IsGreaterOrEqualThan(3.0);
                }

                var dangerLabel = Contrast(Tokens.AccentDanger, SetupBrushes.OpaqueColor(Tokens.SurfaceMedium));
                Check.WithCustomMessage($"{theme.Name}: danger label {dangerLabel:0.00}:1 < 4.5.")
                    .That(dangerLabel).IsGreaterOrEqualThan(4.5);

                // The expected tint, computed here from the theme's own colours (r4 finding 11).
                var expected = Color.FromRgb(
                    (byte)(Tokens.AccentDanger.R * 0.18 + Tokens.SurfaceMedium.R * 0.82),
                    (byte)(Tokens.AccentDanger.G * 0.18 + Tokens.SurfaceMedium.G * 0.82),
                    (byte)(Tokens.AccentDanger.B * 0.18 + Tokens.SurfaceMedium.B * 0.82));
                var tint = SetupBrushes.DangerTint();
                Check.WithCustomMessage($"{theme.Name}: the tint drifted from the 0.18 blend.")
                    .That(tint.Color).IsEqualTo(expected);
                Check.WithCustomMessage($"{theme.Name}: the tint is not frozen/opaque.")
                    .That(tint.IsFrozen && tint.Color.A == 255).IsTrue();

                // SD7's alert-on-tint leg (r3 F20): the confirm band's alert reads on the tint.
                var alertOnTint = Contrast(Tokens.AccentDanger, tint.Color);
                Check.WithCustomMessage($"{theme.Name}: alert-on-tint {alertOnTint:0.00}:1 < 3.")
                    .That(alertOnTint).IsGreaterOrEqualThan(3.0);
            }
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }
    });
}

private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
{
    static double Channel(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    var first = (0.2126 * Channel(a.R)) + (0.7152 * Channel(a.G)) + (0.0722 * Channel(a.B));
    var second = (0.2126 * Channel(b.R)) + (0.7152 * Channel(b.G)) + (0.0722 * Channel(b.B));
    return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
}

private static Border BandOf(UninstallChoiceWindow window) =>
    (Border)((Grid)window.Content).Children[0];

private static TextBlock BandTitleOf(Border band) =>
    ((Grid)band.Child).Children.OfType<TextBlock>().First();
```

(Requires `using System.Windows.Media;` and `using EyeCtrl.Controls.Theme;` — add both to `UninstallChoiceWindowTests.cs` if missing, r4 finding 8. The per-theme tint check compares the production `DangerTint()` against arithmetic computed HERE from the theme's own colours — per theme, not Midnight-only, r4 finding 11.)

- [ ] **Step 2: Implement the layout**

In `UninstallChoiceWindow`, replace the content-building block with:

```csharp
_band = BuildBand();
_body = BuildBody();
_screen2.Visibility = Visibility.Collapsed;

var root = new Grid();
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
Grid.SetRow(_band, 0);
Grid.SetRow(_body, 1);
root.Children.Add(_band);
root.Children.Add(_body);
Content = root;
```

with:

```csharp
private Border _band = null!;
private Border _body = null!;
private TextBlock _bandTitle = null!;
private Grid _bandGrid = null!;
private System.Windows.Controls.Image? _bandMark;
private System.Windows.Shapes.Path? _bandAlert;
private TextBlock? _prose1;
private TextBlock? _prose2;

private Border BuildBand()
{
    _bandMark = new System.Windows.Controls.Image
    {
        Width = SetupWindowMetrics.BandIconSize,
        Height = SetupWindowMetrics.BandIconSize,
        Source = SetupProgressWindow.LoadAppIcon(),
        VerticalAlignment = VerticalAlignment.Center,
    };
    _bandAlert = new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse("M12 3.5 L21.2 19.8 L2.8 19.8 Z M12 9.6 L12 14.2 M12 17.2 L12 17.3"),
        Stroke = Tokens.BrushAccentDanger,
        StrokeThickness = 1.7,
        Width = 24,
        Height = 24,
        Stretch = Stretch.None,
        LayoutTransform = new ScaleTransform(44 / 24.0, 44 / 24.0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    _bandTitle = UninstallChoiceWindow.CreateTitle(_strings.T("uninstall.choice.title"));
    _bandTitle.VerticalAlignment = VerticalAlignment.Center;
    _bandTitle.Margin = new Thickness(22, 0, 0, 0);

    // r3 F14 in BOTH windows: the two-column Grid lets the title wrap (r4 finding 10 — the
    // choice band was still a StackPanel). Confirm-screen centring centres the Grid itself.
    _bandGrid = new Grid();
    _bandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    _bandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    // r5 finding 2: the TITLE column is Star, not Auto — an Auto column measures the title with
    // unlimited width, so TextWrapping never applies and the setupTitle fit test would measure a
    // layout that doesn't ship. Under the confirm screen's Center alignment the Grid is measured
    // against the band's finite width (wrapping applies) and arranged at its content width.
    _bandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    Grid.SetColumn(_bandMark, 0);
    Grid.SetColumn(_bandAlert, 1);
    Grid.SetColumn(_bandTitle, 2);
    _bandGrid.Children.Add(_bandMark);
    _bandGrid.Children.Add(_bandAlert);
    _bandGrid.Children.Add(_bandTitle);
    return new Border
    {
        Background = SetupBrushes.Opaque(Tokens.SurfaceMedium),
        Padding = new Thickness(88, 22, 88, 22),
        MinHeight = 132,
        Child = _bandGrid,
    };
}

private Border BuildBody()
{
    _screen1 = BuildScreen(confirm: false);
    _screen2 = BuildScreen(confirm: true);
    var host = new Grid();
    host.Children.Add(_screen1);
    host.Children.Add(_screen2);
    return new Border
    {
        Background = SetupBrushes.Opaque(Tokens.SurfaceDark),
        // r3 finding: the SIDE padding stays 32 so the 1500-DIP row fits exactly; the prose
        // keeps its 88-DIP inset via its container below. Bottom 32 + the row's 48 = H-80.
        Padding = new Thickness(32, 0, 32, 32),
        Child = host,
    };
}
```

`_screen1`/`_screen2` lose their `readonly` (they are assigned in `BuildBody`, not the ctor — r3 finding 9). `BuildScreen(confirm:)` keeps its button wiring and `RowDefinitions` but changes:

- the title prose is **removed** from both screens (the band carries it — r3 finding: it would show twice);
- the prose CONTAINER is rebuilt per screen (r3 F10): screen 1's container gets `HorizontalAlignment = Left`, `Margin = new Thickness(56, 24, 0, 0)` (32 body padding + 56 = the band's 88 line) and `MaxWidth = SetupWindowMetrics.BodyWidthChoice`; screen 2's container stays `HorizontalAlignment = Center` (SD9);
- the question line renders `CreateProse(text, maxWidth: SetupWindowMetrics.BodyWidthChoice)` — **primary at Normal weight**, per the mockup (r4 finding 7); the detail/secondary lines keep `secondary: true`; `noChoice` renders `CreateProse(text, dim: true, size: SetupWindowMetrics.DimFontSize, maxWidth: SetupWindowMetrics.BodyWidthChoice)` (36 DIP — the fit class measures 36);
- the first prose line of each screen is stored: screen 1 assigns `_prose1`, screen 2 assigns `_prose2`;
- the buttons row's bottom margin stays `Margin = new Thickness(0, 0, 0, 48)` — with the body's 32 bottom padding this keeps `keep.Bottom = H − 32 − 48` (r3 F7).

`ShowConfirm()` additionally switches the band (r3 F16: the taskbar title follows):

```csharp
private void ShowConfirm()
{
    _showingConfirm = true;
    _screen1.Visibility = Visibility.Collapsed;
    _screen2.Visibility = Visibility.Visible;
    _bandAlert.Visibility = Visibility.Visible;
    _bandMark.Visibility = Visibility.Collapsed;
    _bandTitle.Text = _strings.T("uninstall.confirmDelete.title");
    Title = _bandTitle.Text;
    _band.Background = SetupBrushes.DangerTint();
    _bandGrid.HorizontalAlignment = HorizontalAlignment.Center;
    ArmDwellTargets();
}

/// <summary>The screen's question (screen 1) / detail (screen 2) prose line — the alignment
/// tests read it.</summary>
internal TextBlock ProseBlock(bool confirm) => confirm ? _prose2! : _prose1!;
```

(`SetupBrushes.DangerTint()`/`Frozen`/`Opaque`/`OpaqueColor` come from Task 1 — no per-window copies, r3 F8.)

- [ ] **Step 3: Run the choice window tests**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "UninstallChoiceWindow" --blame-hang-timeout 300s`
Expected: PASS — every FC1/FC2/FC6 test untouched, the bottom pin and L2 identity hold, the band tests pass, no target below 420, the layout slot grants the full 1500.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs tests/EyeCtrl.Setup.Tests/Display/UninstallChoiceWindowTests.cs
git commit -m "feat(setup): B choice window — full-bleed band, danger tint, 32-DIP body, H-80 pin"
```

---

### Task 9: Mutation round + full gate

**Files:** none new (verification only). **Every mutant runs against a CLEAN tree** (each task committed): mutate → run the named test → expect FAIL → `git restore <file>` → re-run → expect PASS. Four mutants, each targeting a test that v1/v2 proved toothless without it (r4 finding: M1 duplicates Task 6's inline mutation — replaced by the Title-sync mutant).

- [ ] **Step 1: Mutant M1 — drop the taskbar-title sync**

In `SetupProgressWindow.SetBandTitle`, delete the `Title = title;` line. Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "ARefusedPackage_CannotLeaveItsOwnTextOnTheResultScreen" --blame-hang-timeout 300s` — Expected: FAIL (the taskbar still names the package's claim). Restore: `git restore src/EyeCtrl.Setup/Display/SetupProgressWindow.cs`; re-run — Expected: PASS. (The FC7 band re-resolution itself is mutation-checked inline in Task 6 Step 5.)

- [ ] **Step 2: Mutant M2 — nest the result body**

In `SetupProgressWindow.BuildBodyHost`, move `_resultBody` inside `_progressBody` (and remove the sibling add). Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "ShowResult_TogglesToTheResultBody"` — Expected: FAIL (`ShowsResultBody` walks the logical tree: the nested body's ancestor is the collapsed progress stack). Restore: `git restore src/EyeCtrl.Setup/Display/SetupProgressWindow.cs`; re-run — Expected: PASS.

- [ ] **Step 3: Mutant M3 — stick the active stage**

In `MarkActive`, delete the previously-active clearing loop. Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "ProgressText_IsTheVerbsOwn_AndEveryStageAdvances"` — Expected: FAIL (StageText sticks to Verify). Restore: `git restore src/EyeCtrl.Setup/Display/SetupProgressWindow.cs`; re-run — Expected: PASS.

- [ ] **Step 4: Mutant M4 — clip the button row**

In `UninstallChoiceWindow.BuildBody`, change the side padding from 32 to 88. Run: `dotnet test tests/EyeCtrl.Setup.Tests --filter "TheChoiceBand_CarriesTheMarkAndTitle_AndTheRowFitsUnclipped"` — Expected: FAIL (the row's layout slot shrinks to 1388 < 1500). Restore: `git restore src/EyeCtrl.Setup/Display/UninstallChoiceWindow.cs`; re-run — Expected: PASS.

- [ ] **Step 5: Build everything**

Run: `dotnet build EyeCtrl.slnx`
Expected: 0 errors.

- [ ] **Step 6: The Setup suite + validator + App fit gate**

Run: `dotnet test tests/EyeCtrl.Setup.Tests --blame-hang-timeout 300s` (plain `dotnet test`, no env var), `python -I tools/strings_validate.py`, and `dotnet test tests/EyeCtrl.App.Tests --filter "Localization" --blame-hang-timeout 300s`
Expected: PASS.

- [ ] **Step 7: The wider non-UI suite**

Run — one project per invocation (MSB1008 rejects multi-project args): `dotnet test tests/EyeCtrl.Core.Tests --blame-hang-timeout 300s`, then `tests/EyeCtrl.ViewModels.Tests`, then `tests/EyeCtrl.Platform.Tests`
Expected: PASS.

- [ ] **Step 8: Spec tick-off + drift check**

Re-read `docs/design/setup-ui-redesign-spec.md` §6/§7 inside the worktree (Task 0 committed it) and tick every claim; where reality diverged, update the spec — it is the record. Run `git status`: the worktree holds only the plan's files.

- [ ] **Step 9: Hand off**

Summarize: what shipped, what the fit suite measured, the open D2 decision (all four review rounds recommend skip — the owner's call), and offer the r5 review round (the owner runs it) or the live gaze check.
