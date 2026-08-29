# Unexposed Settings UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every App-owned orphaned setting a row in the settings window — three new tabs (Eye Care, Adaptation, Advanced) plus rows on the Calibration/Cursor/Selection tabs — per the approved spec `docs/superpowers/specs/2026-08-29-unexposed-settings-ui-design.md` (rev 3, `cd999d8a`).

**Architecture:** Three new `ISettingsPageSource` classes built from declarative `SettingsRowSpec`s (the window's spec pipeline renders them), a Core change that validates the five gaze-indicator properties with a clamp-on-load guard, and gate/test extensions. No new row-spec types; no feature-behaviour changes.

**Tech Stack:** .NET 10 WPF (`net10.0-windows`), CommunityToolkit.Mvvm source generators, xUnit v3 + NFluent 3.1.0.

**Worktree:** all paths are relative to `C:\Users\andre\source\repos\eyectrl\.worktrees\settings-orphan-ui` (branch `feat/settings-orphan-ui`). `cd` there first; every command below assumes it as the working directory.

## Global Constraints

- Assertions are **NFluent**: `Check.That(x).IsEqualTo(y)`, `IsGreaterOrEqualThan` / `IsLessOrEqualThan` (NOT `OrEqualTo`), `IsInstanceOf<T>()`, `HasSize(n)`, `ContainsExactly`, `HasElementThatMatches`. Global usings `Xunit` + `NFluent` come from `tests/Directory.Build.props` — do not add `using Xunit;`/`using NFluent;` to test files.
- **One `dotnet test` project per invocation, never two at once** (named pipes are machine-global). Never filter `Category=UI`. If an App test class fails in isolation but the project passes, run the whole project before diagnosing.
- File-scoped namespaces; Allman braces; `_camelCase` private fields.
- **Descriptions**: British prose ("Colour"), American labels ("Color" in row Labels), one sentence each. Every "Applies" claim is already traced in the spec — copy the claim verbatim into the description where the cell is not "live"; do not soften or invent.
- **Slider Min/Max must sit inside the property's `Validate()` range** — the span guard asserts containment (`Min ≥` floor, `Max ≤` ceiling; `c_thick` already ships 1–6 against a 1–10 guard). This plan uses exact equality throughout, which is the house preference, not the gate.
- **Reset buttons copy properties from a fresh instance; they never replace a settings-group object** (services hold references to those objects).
- Every new page source: `internal sealed class`, public `(SettingsPageContext ctx)` constructor, `PrefersAutoPagination => true`. The fit-test harness constructs sources reflectively and fails the build otherwise.
- AGENTS.md gates: run `impact` (GitNexus MCP) before editing an existing symbol, `detect_changes` before each commit if code (not docs-only) changed.
- Commit style: `feat(settings): ...` / `test(settings): ...`, present tense, no push unless asked.

---

### Task 1: Core — validate + clamp-on-load the five gaze-indicator properties

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs` (Validate + one doc comment)
- Modify: `src/EyeCtrl.Core/Settings/SettingsService.cs` (`Load`, ~line 130–145)
- Test: `tests/EyeCtrl.Core.Tests/Settings/SettingsPropertyValidationTests.cs`, `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ManualSelectionSettings.Validate()` now covers the five `GazeIndicator*` ranges (20–200, 2–40, 0.5–10, 0.1–1.0, 0–1000); `SettingsService.Load()` clamps out-of-range persisted values instead of returning whole-file defaults. Tasks 6 and 7 rely on these exact ranges.

- [ ] **Step 1: Write the failing validation test**

Append to `tests/EyeCtrl.Core.Tests/Settings/SettingsPropertyValidationTests.cs` (match the file's existing namespace/usings — namespace is `EyeCtrl.Core.Tests.Settings`):

```csharp
public class GazeIndicatorValidationTests
{
    [Fact]
    public void GazeIndicator_Defaults_ProduceNoValidationErrors()
    {
        var errors = new ManualSelectionSettings().Validate();
        Check.That(errors.Where(e =>
            e.Contains("GazeIndicator", StringComparison.Ordinal))).IsEmpty();
    }

    [Theory]
    [InlineData(nameof(ManualSelectionSettings.GazeIndicatorOuterDiameter), 250.0, 20.0, 200.0)]
    [InlineData(nameof(ManualSelectionSettings.GazeIndicatorCenterDotDiameter), 60.0, 2.0, 40.0)]
    [InlineData(nameof(ManualSelectionSettings.GazeIndicatorStrokeThickness), 12.0, 0.5, 10.0)]
    [InlineData(nameof(ManualSelectionSettings.GazeIndicatorOpacity), 0.05, 0.1, 1.0)]
    [InlineData(nameof(ManualSelectionSettings.GazeIndicatorResetAnimationMs), 5000, 0, 1000)]
    public void GazeIndicator_OutOfRange_IsRejected(string property, double bad, double min, double max)
    {
        var s = new ManualSelectionSettings();
        var prop = s.GetType().GetProperty(property)!;
        // InlineData carries doubles; the int property (ResetAnimationMs) needs Convert,
        // or SetValue throws ArgumentException instead of setting the value.
        prop.SetValue(s, Convert.ChangeType(bad, prop.PropertyType));
        Check.That(s.Validate()).HasElementThatMatches(e => e.Contains(property, StringComparison.Ordinal));
    }
}
```

(Reflection-set keeps one theory body for mixed double/int properties; `ValidateRange` messages embed the property name — same convention as the other settings classes.)

- [ ] **Step 2: Run it to verify it fails**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeIndicatorValidationTests"`
Expected: FAIL — the out-of-range theory cases produce no errors (defaults case may pass already).

- [ ] **Step 3: Add the Validate entries + fix the stale doc comment**

In `ManualSelectionSettings.cs`, the `Validate()` body currently ends:

```csharp
            ValidationHelper.ValidateRange(MagnificationLevel, 1.5, 5.0, nameof(MagnificationLevel))
        );
```

Add a trailing comma after the `MagnificationLevel` line, then **replace the `);` line** with the five entries below (the block's own `));` closes `CollectErrors`):

```csharp
ValidationHelper.ValidateRange(GazeIndicatorOuterDiameter, 20, 200, nameof(GazeIndicatorOuterDiameter)),
ValidationHelper.ValidateRange(GazeIndicatorCenterDotDiameter, 2, 40, nameof(GazeIndicatorCenterDotDiameter)),
ValidationHelper.ValidateRange(GazeIndicatorStrokeThickness, 0.5, 10, nameof(GazeIndicatorStrokeThickness)),
ValidationHelper.ValidateRange(GazeIndicatorOpacity, 0.1, 1.0, nameof(GazeIndicatorOpacity)),
ValidationHelper.ValidateRange(GazeIndicatorResetAnimationMs, 0, 1000, nameof(GazeIndicatorResetAnimationMs)));
```

Also change the doc comment above `_gazeIndicatorOpacity` from `Valid range: 0–1.` to `Valid range: 0.1–1.0.`

- [ ] **Step 4: Run the validation test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Write the failing clamp test**

Append to `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs` (it currently has only `EyeCtrl.Core.Models` + `EyeCtrl.Core.Settings` usings — add `using System.Text.Json.Nodes;`):

```csharp
[Fact]
public void Load_ClampsOutOfRangeGazeIndicator_InsteadOfResettingEverything()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
    try
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 600;
        var service = new SettingsService(tempPath);
        service.Save(settings);

        // Hand-edit the file the way a user would: one out-of-range indicator value.
        var root = JsonNode.Parse(File.ReadAllText(tempPath))!.AsObject();
        root["manualSelection"]!["gazeIndicatorOuterDiameter"] = 250;
        File.WriteAllText(tempPath, root.ToJsonString());

        var loaded = service.Load();
        Check.That(loaded.ManualSelection.GazeIndicatorOuterDiameter).IsEqualTo(200);
        Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(600);
    }
    finally { File.Delete(tempPath); }
}
```

- [ ] **Step 6: Run it to verify it fails**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~Load_ClampsOutOfRangeGazeIndicator"`
Expected: FAIL at the **first** assertion — `IsEqualTo(200)` with actual **60**: without the clamp, `Load()` hits the validation-error path and returns whole-file defaults (the default outer diameter is 60). Discriminator if something else is wrong: on the second assertion `ActivationButtonDwellMs` should also read back **700** (its default); if it reads 600 the JSON key edit didn't land and the test is failing for the wrong reason — check the key spelling before touching `SettingsService`.

- [ ] **Step 7: Add the clamp to `Load()`**

In `SettingsService.cs`, inside `Load()`, immediately after the SelectionMode-seed migration block and **before** `var validationErrors = settings.Validate();`, insert:

```csharp
// The gaze-indifier visuals were unvalidated before they got ranges (2026-08-29); a
// hand-edited file holding an out-of-range value must clamp, not trip the whole-file
// default below — the file-wipe path punishes exactly the hand-editors the UI serves.
ClampGazeIndicator(settings.ManualSelection);
```

(typo guard: the comment word is "gaze-indicator".) And add the private method to the class:

```csharp
private void ClampGazeIndicator(ManualSelectionSettings ms)
{
    var (outer, dot, stroke, opacity, anim) = (
        ms.GazeIndicatorOuterDiameter, ms.GazeIndicatorCenterDotDiameter,
        ms.GazeIndicatorStrokeThickness, ms.GazeIndicatorOpacity,
        ms.GazeIndicatorResetAnimationMs);
    ms.GazeIndicatorOuterDiameter = Math.Clamp(outer, 20, 200);
    ms.GazeIndicatorCenterDotDiameter = Math.Clamp(dot, 2, 40);
    ms.GazeIndicatorStrokeThickness = Math.Clamp(stroke, 0.5, 10);
    ms.GazeIndicatorOpacity = Math.Clamp(opacity, 0.1, 1.0);
    ms.GazeIndicatorResetAnimationMs = Math.Clamp(anim, 0, 1000);
    if (ms.GazeIndicatorOuterDiameter != outer || ms.GazeIndicatorCenterDotDiameter != dot
        || ms.GazeIndicatorStrokeThickness != stroke || ms.GazeIndicatorOpacity != opacity
        || ms.GazeIndicatorResetAnimationMs != anim)
    {
        _logger?.LogInformation("Clamped out-of-range gaze-indicator settings to their valid ranges.");
    }
}
```

- [ ] **Step 8: Run both tests, then the full Core suite**

`dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~Load_ClampsOutOfRangeGazeIndicator"` → PASS.
`dotnet test tests/EyeCtrl.Core.Tests` → all pass (no other test persisted out-of-range indicator values).

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs src/EyeCtrl.Core/Settings/SettingsService.cs tests/EyeCtrl.Core.Tests/Settings/SettingsPropertyValidationTests.cs tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs
git commit -m "feat(settings): validate the gaze-indicator visuals with clamp-on-load"
```

---

### Task 2: EyeCarePageSource

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/Sources/EyeCarePageSource.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/EyeCarePageSourceTests.cs` (new)
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs` (source array), `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs` (new Fact)

**Interfaces:**
- Consumes: `SettingsPageContext` (`_ctx.Settings`, `_ctx.ApplyAndSave()`, `_ctx.ShowModal(string, Action)`, `_ctx.RebuildContent()`); `EyeCtrlSettings.EyeCare`.
- Produces: `EyeCarePageSource` with ids `ec_enabled, ec_interval, ec_break, ec_exercise, ec_snooze_banner, ec_snooze, ec_fatigue, ec_fatigue_thresh, ec_reset` — relied on by Task 8's `_sources["EyeCare"]` registration and the completeness-gate array.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/EyeCarePageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

public class EyeCarePageSourceTests
{
    [Fact]
    public void Build_HasNineRows_LastIsReset()
    {
        var pages = new EyeCarePageSource(TestSettingsPageContext.Build()).Build();
        var rows = pages.SelectMany(p => p.Rows).ToList();
        Check.That(rows).HasSize(9);
        var reset = rows.OfType<ButtonSpec>().Single();
        Check.That(reset.Id).IsEqualTo("ec_reset");
        Check.That(reset.Kind).IsEqualTo(ButtonKind.Reset);
        Check.That(rows.Last()).IsSameReferenceAs(reset);
    }

    [Fact]
    public void Build_SliderRanges_MatchValidateRanges()
    {
        var rows = new EyeCarePageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
        Check.That(rows).HasSize(6);
        Check.That(rows["ec_interval"].Min).IsEqualTo(1);
        Check.That(rows["ec_interval"].Max).IsEqualTo(120);
        Check.That(rows["ec_break"].Min).IsEqualTo(5);
        Check.That(rows["ec_break"].Max).IsEqualTo(600);
        Check.That(rows["ec_exercise"].Min).IsEqualTo(10);
        Check.That(rows["ec_exercise"].Max).IsEqualTo(180);
        Check.That(rows["ec_snooze_banner"].Min).IsEqualTo(5);
        Check.That(rows["ec_snooze_banner"].Max).IsEqualTo(120);
        Check.That(rows["ec_snooze"].Min).IsEqualTo(1);
        Check.That(rows["ec_snooze"].Max).IsEqualTo(60);
        Check.That(rows["ec_fatigue_thresh"].Min).IsEqualTo(0.0);
        Check.That(rows["ec_fatigue_thresh"].Max).IsEqualTo(1.0);
    }

    [Fact]
    public void Setters_WriteLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        var source = new EyeCarePageSource(TestSettingsPageContext.Build(settings));
        var toggles = source.Build().SelectMany(p => p.Rows).OfType<ToggleSpec>().ToDictionary(t => t.Id);
        toggles["ec_enabled"].Set(true);
        Check.That(settings.EyeCare.Enabled).IsTrue();
    }

    [Fact]
    public void Reset_CopiesAllEightFromDefaults()
    {
        var settings = new EyeCtrlSettings();
        settings.EyeCare.Enabled = true;
        settings.EyeCare.IntervalMinutes = 45;
        settings.EyeCare.NaturalBreakSeconds = 120;
        settings.EyeCare.ExerciseDurationSeconds = 90;
        settings.EyeCare.BannerAutoSnoozeSeconds = 30;
        settings.EyeCare.SnoozeMinutes = 10;
        settings.EyeCare.FatigueMonitorEnabled = true;
        settings.EyeCare.FatigueThreshold = 0.9;
        var source = new EyeCarePageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        var reset = source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single();
        reset.OnActivate();
        var d = new EyeCareSettings();
        Check.That(settings.EyeCare.Enabled).IsEqualTo(d.Enabled);
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(d.IntervalMinutes);
        Check.That(settings.EyeCare.NaturalBreakSeconds).IsEqualTo(d.NaturalBreakSeconds);
        Check.That(settings.EyeCare.ExerciseDurationSeconds).IsEqualTo(d.ExerciseDurationSeconds);
        Check.That(settings.EyeCare.BannerAutoSnoozeSeconds).IsEqualTo(d.BannerAutoSnoozeSeconds);
        Check.That(settings.EyeCare.SnoozeMinutes).IsEqualTo(d.SnoozeMinutes);
        Check.That(settings.EyeCare.FatigueMonitorEnabled).IsEqualTo(d.FatigueMonitorEnabled);
        Check.That(settings.EyeCare.FatigueThreshold).IsEqualTo(d.FatigueThreshold);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

`dotnet build EyeCtrl.slnx` (new file referenced by tests must compile first) — expected compile error `EyeCarePageSource not found`. Then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~EyeCarePageSourceTests"` → FAIL.

- [ ] **Step 3: Write the source**

Create `src/EyeCtrl.App/Views/Settings/Sources/EyeCarePageSource.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class EyeCarePageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public EyeCarePageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings.EyeCare;
        return
        [
            new PageSpec([
                new ToggleSpec("ec_enabled", "Enable Eye Care",
                    () => s.Enabled, v => { s.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Periodic break reminders and gaze exercises while you use EyeCtrl." },
                new SliderSpec("ec_interval", "Break Interval (min)", 1, 120, 1, 0,
                    () => s.IntervalMinutes, v => { s.IntervalMinutes = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How often a break is offered when you haven't rested." },
                new SliderSpec("ec_break", "Natural Break Length (s)", 5, 600, 5, 0,
                    () => s.NaturalBreakSeconds, v => { s.NaturalBreakSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Looking away this long counts as a natural break." },
                new SliderSpec("ec_exercise", "Exercise Duration (s)", 10, 180, 5, 0,
                    () => s.ExerciseDurationSeconds, v => { s.ExerciseDurationSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long each guided gaze exercise runs." },
                new SliderSpec("ec_snooze_banner", "Banner Auto-snooze (s)", 5, 120, 5, 0,
                    () => s.BannerAutoSnoozeSeconds, v => { s.BannerAutoSnoozeSeconds = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long an ignored banner waits before hiding itself. Applies after a restart." },
                new SliderSpec("ec_snooze", "Snooze Length (min)", 1, 60, 1, 0,
                    () => s.SnoozeMinutes, v => { s.SnoozeMinutes = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long breaks are postponed when you snooze a banner." },
                new ToggleSpec("ec_fatigue", "Fatigue Escalation",
                    () => s.FatigueMonitorEnabled, v => { s.FatigueMonitorEnabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Let rising fatigue bring breaks on early. Experimental." },
                new SliderSpec("ec_fatigue_thresh", "Fatigue Threshold", 0.0, 1.0, 0.05, 2,
                    () => s.FatigueThreshold, v => { s.FatigueThreshold = v; _ctx.ApplyAndSave(); })
                    { Description = "The fatigue score at which an early break fires." },
                new ButtonSpec("ec_reset", "Reset Eye Care Settings",
                    () => _ctx.ShowModal("Reset Eye Care settings to defaults?", () =>
                    {
                        var d = new Core.Settings.EyeCareSettings();
                        s.Enabled = d.Enabled;
                        s.IntervalMinutes = d.IntervalMinutes;
                        s.NaturalBreakSeconds = d.NaturalBreakSeconds;
                        s.ExerciseDurationSeconds = d.ExerciseDurationSeconds;
                        s.BannerAutoSnoozeSeconds = d.BannerAutoSnoozeSeconds;
                        s.SnoozeMinutes = d.SnoozeMinutes;
                        s.FatigueMonitorEnabled = d.FatigueMonitorEnabled;
                        s.FatigueThreshold = d.FatigueThreshold;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
```

- [ ] **Step 4: Register in the gates**

In `SettingsRowDescriptionCompletenessTests.cs`, add `new EyeCarePageSource(ctx),` after `new GeneralPageSource(ctx),` in the `sources` array. In `SettingsSliderSpanGuardTests.cs`, add:

```csharp
[Fact]
public void EyeCare_SliderSpans_StayInsideTheValidatedRanges()
{
    var sliders = Sliders(new EyeCarePageSource(TestSettingsPageContext.Build()));
    Check.That(sliders).HasSize(6);
    SpanInside(sliders, "ec_interval", 1, 120);
    SpanInside(sliders, "ec_break", 5, 600);
    SpanInside(sliders, "ec_exercise", 10, 180);
    SpanInside(sliders, "ec_snooze_banner", 5, 120);
    SpanInside(sliders, "ec_snooze", 1, 60);
    SpanInside(sliders, "ec_fatigue_thresh", 0.0, 1.0);
}
```

- [ ] **Step 5: Run the App suite**

`dotnet test tests/EyeCtrl.App.Tests` → PASS (new tests + both gates green; the completeness gate would fail if any `ec_*` row lacked a Description).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/EyeCarePageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/EyeCarePageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(settings): Eye Care tab page source"
```

---

### Task 3: AdaptationPageSource

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs` (new)
- Modify: completeness-gate array + span-guard (same two files as Task 2)

**Interfaces:**
- Consumes: `SettingsPageContext`; `EyeCtrlSettings.Adaptation`; `Core.Settings.AdaptationPosture` (members `Conservative`, `Balanced`, `Responsive`).
- Produces: `AdaptationPageSource` with ids `ad_enabled, ad_posture, ad_dwell, ad_tolerance, ad_smoothing, ad_geometry, ad_cue, ad_sensitive, ad_frac_dwell_short, ad_frac_dwell_long, ad_frac_tol_loose, ad_frac_smooth, ad_frac_geom, ad_reset`. **No `ad_caregiver` row** — `CaregiverAlerts` has no consumer (spec §Decisions).

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

public class AdaptationPageSourceTests
{
    [Fact]
    public void Build_HasFourteenRows_NoCaregiverRow()
    {
        var rows = new AdaptationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).ToList();
        Check.That(rows).HasSize(14);
        Check.That(rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("ad_reset");
        Check.That(rows.Any(r => r.Id.Contains("caregiver", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void Build_PostureCombo_OffersTheThreeEnumValues()
    {
        var combo = new AdaptationPageSource(TestSettingsPageContext.Build()).Build()
            .SelectMany(p => p.Rows).OfType<ComboBoxSpec>().Single();
        Check.That(combo.Id).IsEqualTo("ad_posture");
        Check.That(combo.Display).ContainsExactly("Conservative", "Balanced", "Responsive");
    }

    [Fact]
    public void Setters_WriteLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        var source = new AdaptationPageSource(TestSettingsPageContext.Build(settings));
        var rows = source.Build().SelectMany(p => p.Rows).ToList();
        var toggles = rows.OfType<ToggleSpec>().ToDictionary(t => t.Id);
        var sliders = rows.OfType<SliderSpec>().ToDictionary(s => s.Id);
        toggles["ad_enabled"].Set(true);
        Check.That(settings.Adaptation.Enabled).IsTrue();
        sliders["ad_frac_dwell_short"].Set(0.25);
        Check.That(settings.Adaptation.DwellShorterMaxFraction).IsEqualTo(0.25);
    }

    [Fact]
    public void Reset_CopiesAllThirteenFromDefaults()
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        settings.Adaptation.Posture = AdaptationPosture.Responsive;
        settings.Adaptation.AdaptDwell = false;
        settings.Adaptation.AdaptTolerance = false;
        settings.Adaptation.AdaptSmoothing = false;
        settings.Adaptation.AdaptGeometry = false;
        settings.Adaptation.ShowCue = false;
        settings.Adaptation.SuppressInSensitiveContexts = false;
        settings.Adaptation.DwellShorterMaxFraction = 0.2;
        settings.Adaptation.DwellLongerMaxFraction = 0.3;
        settings.Adaptation.ToleranceLooserMaxFraction = 0.4;
        settings.Adaptation.SmoothingMoreMaxFraction = 0.6;
        settings.Adaptation.GeometryBiggerMaxFraction = 0.7;
        var source = new AdaptationPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single().OnActivate();
        var d = new AdaptationSettings();
        Check.That(settings.Adaptation.Enabled).IsEqualTo(d.Enabled);
        Check.That(settings.Adaptation.Posture).IsEqualTo(d.Posture);
        Check.That(settings.Adaptation.AdaptDwell).IsEqualTo(d.AdaptDwell);
        Check.That(settings.Adaptation.AdaptTolerance).IsEqualTo(d.AdaptTolerance);
        Check.That(settings.Adaptation.AdaptSmoothing).IsEqualTo(d.AdaptSmoothing);
        Check.That(settings.Adaptation.AdaptGeometry).IsEqualTo(d.AdaptGeometry);
        Check.That(settings.Adaptation.ShowCue).IsEqualTo(d.ShowCue);
        Check.That(settings.Adaptation.SuppressInSensitiveContexts).IsEqualTo(d.SuppressInSensitiveContexts);
        Check.That(settings.Adaptation.DwellShorterMaxFraction).IsEqualTo(d.DwellShorterMaxFraction);
        Check.That(settings.Adaptation.DwellLongerMaxFraction).IsEqualTo(d.DwellLongerMaxFraction);
        Check.That(settings.Adaptation.ToleranceLooserMaxFraction).IsEqualTo(d.ToleranceLooserMaxFraction);
        Check.That(settings.Adaptation.SmoothingMoreMaxFraction).IsEqualTo(d.SmoothingMoreMaxFraction);
        Check.That(settings.Adaptation.GeometryBiggerMaxFraction).IsEqualTo(d.GeometryBiggerMaxFraction);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

`dotnet build EyeCtrl.slnx` (compile error expected), then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdaptationPageSourceTests"` → FAIL.

- [ ] **Step 3: Write the source**

Create `src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class AdaptationPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public AdaptationPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var s = _ctx.Settings.Adaptation;
        return
        [
            new PageSpec([
                new ToggleSpec("ad_enabled", "Enable Adaptation",
                    () => s.Enabled, v => { s.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Ease dwell and targeting automatically while you struggle." },
                new ComboBoxSpec("ad_posture", "Posture",
                    Enum.GetNames<Core.Settings.AdaptationPosture>(), null,
                    () => s.Posture.ToString(),
                    v => { s.Posture = Enum.Parse<Core.Settings.AdaptationPosture>(v); _ctx.ApplyAndSave(); })
                    { Description = "How readily adaptation responds to struggle." },
                new ToggleSpec("ad_dwell", "Adapt Dwell Time",
                    () => s.AdaptDwell, v => { s.AdaptDwell = v; _ctx.ApplyAndSave(); })
                    { Description = "Shorten or lengthen dwell times as you struggle or cruise." },
                new ToggleSpec("ad_tolerance", "Adapt Targeting Tolerance",
                    () => s.AdaptTolerance, v => { s.AdaptTolerance = v; _ctx.ApplyAndSave(); })
                    { Description = "Loosen or tighten how steadily you must rest on a target." },
                new ToggleSpec("ad_smoothing", "Adapt Smoothing",
                    () => s.AdaptSmoothing, v => { s.AdaptSmoothing = v; _ctx.ApplyAndSave(); })
                    { Description = "Adapt how strongly raw gaze is smoothed." },
                new ToggleSpec("ad_geometry", "Adapt Target Size",
                    () => s.AdaptGeometry, v => { s.AdaptGeometry = v; _ctx.ApplyAndSave(); })
                    { Description = "Grow or shrink targets when targeting is hard." },
                new ToggleSpec("ad_cue", "Show Adaptation Cue",
                    () => s.ShowCue, v => { s.ShowCue = v; _ctx.ApplyAndSave(); })
                    { Description = "Show a small cue while a setting is being adapted." },
                new ToggleSpec("ad_sensitive", "Suppress in Sensitive Contexts",
                    () => s.SuppressInSensitiveContexts, v => { s.SuppressInSensitiveContexts = v; _ctx.ApplyAndSave(); })
                    { Description = "Keep the cue and toasts hidden while you type passwords." },
                new SliderSpec("ad_frac_dwell_short", "Dwell — Shorter Limit", 0.0, 1.0, 0.05, 2,
                    () => s.DwellShorterMaxFraction, v => { s.DwellShorterMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a dwell may be shortened, as a share of its baseline." },
                new SliderSpec("ad_frac_dwell_long", "Dwell — Longer Limit", 0.0, 1.0, 0.05, 2,
                    () => s.DwellLongerMaxFraction, v => { s.DwellLongerMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a dwell may be lengthened, as a share of its baseline." },
                new SliderSpec("ad_frac_tol_loose", "Tolerance — Looser Limit", 0.0, 1.0, 0.05, 2,
                    () => s.ToleranceLooserMaxFraction, v => { s.ToleranceLooserMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most tolerance may be loosened, as a share of its baseline." },
                new SliderSpec("ad_frac_smooth", "Smoothing — More Limit", 0.0, 1.0, 0.05, 2,
                    () => s.SmoothingMoreMaxFraction, v => { s.SmoothingMoreMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most smoothing may be increased, as a share of its baseline." },
                new SliderSpec("ad_frac_geom", "Target Size — Bigger Limit", 0.0, 1.0, 0.05, 2,
                    () => s.GeometryBiggerMaxFraction, v => { s.GeometryBiggerMaxFraction = v; _ctx.ApplyAndSave(); })
                    { Description = "The most a target may be grown, as a share of its baseline." },
                new ButtonSpec("ad_reset", "Reset Adaptation Settings",
                    () => _ctx.ShowModal("Reset Adaptation settings to defaults?", () =>
                    {
                        var d = new Core.Settings.AdaptationSettings();
                        s.Enabled = d.Enabled;
                        s.Posture = d.Posture;
                        s.AdaptDwell = d.AdaptDwell;
                        s.AdaptTolerance = d.AdaptTolerance;
                        s.AdaptSmoothing = d.AdaptSmoothing;
                        s.AdaptGeometry = d.AdaptGeometry;
                        s.ShowCue = d.ShowCue;
                        s.SuppressInSensitiveContexts = d.SuppressInSensitiveContexts;
                        s.DwellShorterMaxFraction = d.DwellShorterMaxFraction;
                        s.DwellLongerMaxFraction = d.DwellLongerMaxFraction;
                        s.ToleranceLooserMaxFraction = d.ToleranceLooserMaxFraction;
                        s.SmoothingMoreMaxFraction = d.SmoothingMoreMaxFraction;
                        s.GeometryBiggerMaxFraction = d.GeometryBiggerMaxFraction;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
```

- [ ] **Step 4: Register in the gates**

Completeness array: add `new AdaptationPageSource(ctx),` after the EyeCare entry. Span-guard:

```csharp
[Fact]
public void Adaptation_SliderSpans_StayInsideTheValidatedRanges()
{
    var sliders = Sliders(new AdaptationPageSource(TestSettingsPageContext.Build()));
    Check.That(sliders).HasSize(5);
    SpanInside(sliders, "ad_frac_dwell_short", 0.0, 1.0);
    SpanInside(sliders, "ad_frac_dwell_long", 0.0, 1.0);
    SpanInside(sliders, "ad_frac_tol_loose", 0.0, 1.0);
    SpanInside(sliders, "ad_frac_smooth", 0.0, 1.0);
    SpanInside(sliders, "ad_frac_geom", 0.0, 1.0);
}
```

- [ ] **Step 5: Run, commit**

`dotnet test tests/EyeCtrl.App.Tests` → PASS.

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/AdaptationPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdaptationPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(settings): Adaptation tab page source"
```

---

### Task 4: AdvancedPageSource

**Files:**
- Create: `src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs`
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/AdvancedPageSourceTests.cs` (new)
- Modify: completeness-gate array + span-guard (same two files as Tasks 2–3)

**Interfaces:**
- Consumes: `SettingsPageContext`; `EyeCtrlSettings.Backdrop/Drag/Scroll/ExternalDwell`.
- Produces: `AdvancedPageSource` with ids `av_backdrop_enabled, av_backdrop_type, av_backdrop_opacity, av_drag_settle, av_drag_radius, av_drag_color, av_drag_opacity, av_scroll_sens, av_ext_enabled, av_ext_dwell, av_ext_cooldown, av_reset` + three `LabelSpec` separator rows (empty ids). **No Supervision rows** (spec §Problem: the App writer cannot persist that section).

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/AdvancedPageSourceTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

public class AdvancedPageSourceTests
{
    private static List<SettingsRowSpec> Rows() => new AdvancedPageSource(TestSettingsPageContext.Build())
        .Build().SelectMany(p => p.Rows).ToList();

    [Fact]
    public void Build_HasFifteenRows_ThreeSeparators_ResetLast()
    {
        var rows = Rows();
        Check.That(rows).HasSize(15);
        Check.That(rows.OfType<LabelSpec>()).HasSize(3);
        Check.That(rows.OfType<ButtonSpec>().Single().Id).IsEqualTo("av_reset");
        Check.That(rows.Last()).IsInstanceOf<ButtonSpec>();
    }

    [Fact]
    public void Build_SectionOrder_BackdropDragScrollExternalDwell()
    {
        var ids = Rows().Where(r => r.Id.Length > 0).Select(r => r.Id).ToList();
        Check.That(ids.IndexOf("av_backdrop_enabled")).IsStrictlyLessThan(ids.IndexOf("av_drag_settle"));
        Check.That(ids.IndexOf("av_drag_settle")).IsStrictlyLessThan(ids.IndexOf("av_scroll_sens"));
        Check.That(ids.IndexOf("av_scroll_sens")).IsStrictlyLessThan(ids.IndexOf("av_ext_enabled"));
    }

    [Fact]
    public void Setters_WriteLiveSettings()
    {
        var settings = new EyeCtrlSettings();
        var source = new AdvancedPageSource(TestSettingsPageContext.Build(settings));
        var sliders = source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
        sliders["av_scroll_sens"].Set(1.5);
        Check.That(settings.Scroll.Sensitivity).IsEqualTo(1.5);
    }

    [Fact]
    public void Reset_CopiesTheFourGroups_LeavesOthersAlone()
    {
        var settings = new EyeCtrlSettings();
        settings.Backdrop.Opacity = 0.8;
        settings.Drag.SettleDwellMs = 1500;
        settings.Scroll.Sensitivity = 1.8;
        settings.ExternalDwell.CooldownMs = 900;
        settings.EyeCare.IntervalMinutes = 45; // must NOT be touched
        var source = new AdvancedPageSource(
            TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
        source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single().OnActivate();
        var b = new BackdropSettings();
        var d = new DragSettings();
        var sc = new ScrollSettings();
        var e = new ExternalDwellSettings();
        Check.That(settings.Backdrop.Opacity).IsEqualTo(b.Opacity);
        Check.That(settings.Drag.SettleDwellMs).IsEqualTo(d.SettleDwellMs);
        Check.That(settings.Scroll.Sensitivity).IsEqualTo(sc.Sensitivity);
        Check.That(settings.ExternalDwell.CooldownMs).IsEqualTo(e.CooldownMs);
        Check.That(settings.EyeCare.IntervalMinutes).IsEqualTo(45);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Build → compile error, then `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~AdvancedPageSourceTests"` → FAIL.

- [ ] **Step 3: Write the source**

Create `src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs`:

```csharp
namespace EyeCtrl.App.Views.Settings;

internal sealed class AdvancedPageSource : ISettingsPageSource
{
    private readonly SettingsPageContext _ctx;
    public AdvancedPageSource(SettingsPageContext ctx) => _ctx = ctx;

    public bool PrefersAutoPagination => true;

    public IReadOnlyList<PageSpec> Build()
    {
        var st = _ctx.Settings;
        var backdrop = st.Backdrop;
        var drag = st.Drag;
        var scroll = st.Scroll;
        var ext = st.ExternalDwell;
        return
        [
            new PageSpec([
                // Backdrop
                new ToggleSpec("av_backdrop_enabled", "Enable Backdrop",
                    () => backdrop.Enabled, v => { backdrop.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Dim what's behind the keyboard and other overlays." },
                new ComboBoxSpec("av_backdrop_type", "Backdrop Type",
                    ["Acrylic", "Mica", "Solid Darken"], Enum.GetNames<Core.BackdropType>(),
                    () => backdrop.Type.ToString(),
                    v => { backdrop.Type = Enum.Parse<Core.BackdropType>(v); _ctx.ApplyAndSave(); })
                    { Description = "The dimming material; Acrylic and Mica blur, Solid Darken only tints." },
                new SliderSpec("av_backdrop_opacity", "Backdrop Opacity", 0.1, 0.9, 0.05, 2,
                    () => backdrop.Opacity, v => { backdrop.Opacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How dark the backdrop is — only Solid Darken uses this; applies from the next open." },
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // Drag
                new SliderSpec("av_drag_settle", "Drag Settle Dwell (ms)", 200, 2000, 50, 0,
                    () => drag.SettleDwellMs, v => { drag.SettleDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How long gaze must hold still before a drag starts. Applies after a restart." },
                new SliderSpec("av_drag_radius", "Drag Settle Radius (px)", 10, 200, 5, 0,
                    () => drag.SettleRadiusPixels, v => { drag.SettleRadiusPixels = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "How far gaze may drift while settling into a drag. Applies after a restart." },
                new ColorPickerSpec("av_drag_color", "Drag Trail Color",
                    () => drag.TrailColor, v => { drag.TrailColor = v; _ctx.ApplyAndSave(); },
                    SettingsWindow.ColorSwatchRadius)
                    { Description = "Colour of the trail drawn while dragging." },
                new SliderSpec("av_drag_opacity", "Drag Trail Opacity", 0.1, 1.0, 0.05, 2,
                    () => drag.TrailOpacity, v => { drag.TrailOpacity = v; _ctx.ApplyAndSave(); })
                    { Description = "How visible the drag trail is." },
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // Scroll
                new SliderSpec("av_scroll_sens", "Scroll Sensitivity", 0.5, 2.0, 0.1, 1,
                    () => scroll.Sensitivity, v => { scroll.Sensitivity = v; _ctx.ApplyAndSave(); })
                    { Description = "How fast gaze scrolling moves through documents." },
                new LabelSpec(() => new string('\u2500', 50), Wrap: false),
                // External Dwell
                new ToggleSpec("av_ext_enabled", "Enable External Dwell",
                    () => ext.Enabled, v => { ext.Enabled = v; _ctx.ApplyAndSave(); })
                    { Description = "Let other apps trigger dwell through EyeCtrl's pipe." },
                new SliderSpec("av_ext_dwell", "External Dwell (ms)", 200, 3000, 50, 0,
                    () => ext.DefaultDwellMs, v => { ext.DefaultDwellMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Dwell length applied when an external app registers a target." },
                new SliderSpec("av_ext_cooldown", "External Cooldown (ms)", 0, 5000, 100, 0,
                    () => ext.CooldownMs, v => { ext.CooldownMs = (int)v; _ctx.ApplyAndSave(); })
                    { Description = "Minimum wait between external dwell activations." },
                new ButtonSpec("av_reset", "Reset Advanced Settings",
                    () => _ctx.ShowModal("Reset Advanced settings to defaults?", () =>
                    {
                        var b = new Core.Settings.BackdropSettings();
                        var d = new Core.Settings.DragSettings();
                        var sc = new Core.Settings.ScrollSettings();
                        var e = new Core.Settings.ExternalDwellSettings();
                        st.Backdrop.Enabled = b.Enabled;
                        st.Backdrop.Type = b.Type;
                        st.Backdrop.Opacity = b.Opacity;
                        st.Drag.SettleDwellMs = d.SettleDwellMs;
                        st.Drag.SettleRadiusPixels = d.SettleRadiusPixels;
                        st.Drag.TrailColor = d.TrailColor;
                        st.Drag.TrailOpacity = d.TrailOpacity;
                        st.Scroll.Sensitivity = sc.Sensitivity;
                        st.ExternalDwell.Enabled = e.Enabled;
                        st.ExternalDwell.DefaultDwellMs = e.DefaultDwellMs;
                        st.ExternalDwell.CooldownMs = e.CooldownMs;
                        _ctx.ApplyAndSave();
                        _ctx.RebuildContent();
                    }),
                    ButtonKind.Reset, Width: 300, Height: 100),
            ]),
        ];
    }
}
```

(No `using EyeCtrl.App.Views;` needed: the source sits in `EyeCtrl.App.Views.Settings`, a
child namespace, so `SettingsWindow` resolves via namespace nesting — same as the other
sources that reference `SettingsWindow.ColorSwatchRadius`.)

- [ ] **Step 4: Register in the gates**

Completeness array: add `new AdvancedPageSource(ctx),` after the Adaptation entry. Span-guard:

```csharp
[Fact]
public void Advanced_SliderSpans_StayInsideTheValidatedRanges()
{
    var sliders = Sliders(new AdvancedPageSource(TestSettingsPageContext.Build()));
    Check.That(sliders).HasSize(7);
    SpanInside(sliders, "av_backdrop_opacity", 0.1, 0.9);
    SpanInside(sliders, "av_drag_settle", 200, 2000);
    SpanInside(sliders, "av_drag_radius", 10, 200);
    SpanInside(sliders, "av_drag_opacity", 0.1, 1.0);
    SpanInside(sliders, "av_scroll_sens", 0.5, 2.0);
    SpanInside(sliders, "av_ext_dwell", 200, 3000);
    SpanInside(sliders, "av_ext_cooldown", 0, 5000);
}
```

- [ ] **Step 5: Run, commit**

`dotnet test tests/EyeCtrl.App.Tests` → PASS.

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/AdvancedPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/AdvancedPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsRowDescriptionCompletenessTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(settings): Advanced tab page source"
```

---

### Task 5: Cursor tab additions

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs` (5 new rows immediately **before `c_reset`** in the SECOND PageSpec — page 1 currently holds `c_opacity`, `c_thick`, `c_reset`; page 0 ends at `c_color`; `c_reset` must remain the final row so `SettingsPageSourceCallbackTests`'s `Build()[1]` indexing stays valid; `c_reset` copies 5→10)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs` (extend)
- Modify: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs` (Cursor Fact: HasSize(3)→7 + 4 SpanInside)

**Interfaces:**
- Consumes: `EyeCtrlSettings.Cursor` (`MaxGapInterpolationMs`, `PositionIndicatorStyle`, `CircleSize`, `OneEuroMinCutoff`, `OneEuroBeta`).
- Produces: ids `c_gap, c_pos_style, c_size, c_euro_cutoff, c_euro_beta` on the existing Cursor tab.

- [ ] **Step 1: Write the failing tests**

Append to `CursorPageSourceTests.cs`:

```csharp
[Fact]
public void Build_HasAdvancedRows_ResetStillLast()
{
    var rows = new CursorPageSource(TestSettingsPageContext.Build()).Build()
        .SelectMany(p => p.Rows).ToList();
    Check.That(rows.OfType<SliderSpec>().Count(s => s.Id.StartsWith("c_", StringComparison.Ordinal)))
        .IsEqualTo(7);
    Check.That(rows.Last()).IsInstanceOf<ButtonSpec>();
    var combo = rows.OfType<ComboBoxSpec>().Single(c => c.Id == "c_pos_style");
    Check.That(combo.Display).ContainsExactly("Crosshair", "Pin");
}

[Fact]
public void Build_GapAndEuroSliders_MatchValidateRanges()
{
    var sliders = new CursorPageSource(TestSettingsPageContext.Build()).Build()
        .SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);
    Check.That(sliders["c_gap"].Min).IsEqualTo(0);
    Check.That(sliders["c_gap"].Max).IsEqualTo(500);
    Check.That(sliders["c_size"].Min).IsEqualTo(10);
    Check.That(sliders["c_size"].Max).IsEqualTo(500);
    Check.That(sliders["c_euro_cutoff"].Min).IsEqualTo(0.1);
    Check.That(sliders["c_euro_cutoff"].Max).IsEqualTo(20.0);
    Check.That(sliders["c_euro_beta"].Min).IsEqualTo(0.0);
    Check.That(sliders["c_euro_beta"].Max).IsEqualTo(5.0);
}

[Fact]
public void Reset_CopiesAllTenProperties()
{
    var settings = new EyeCtrlSettings();
    settings.Cursor.MaxGapInterpolationMs = 400;
    settings.Cursor.PositionIndicatorStyle = PositionIndicatorStyle.Pin;
    settings.Cursor.CircleSize = 400;
    settings.Cursor.OneEuroMinCutoff = 12;
    settings.Cursor.OneEuroBeta = 3;
    var source = new CursorPageSource(
        TestSettingsPageContext.Build(settings, showModal: (_, act) => act()));
    source.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>().Single(b => b.Id == "c_reset").OnActivate();
    var d = new CursorSettings();
    Check.That(settings.Cursor.MaxGapInterpolationMs).IsEqualTo(d.MaxGapInterpolationMs);
    Check.That(settings.Cursor.PositionIndicatorStyle).IsEqualTo(d.PositionIndicatorStyle);
    Check.That(settings.Cursor.CircleSize).IsEqualTo(d.CircleSize);
    Check.That(settings.Cursor.OneEuroMinCutoff).IsEqualTo(d.OneEuroMinCutoff);
    Check.That(settings.Cursor.OneEuroBeta).IsEqualTo(d.OneEuroBeta);
}
```

(`PositionIndicatorStyle` needs `using EyeCtrl.Core.Models;` — check the enum's namespace when the compile fails and match it.)

- [ ] **Step 2: Run to verify they fail**

`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~CursorPageSourceTests"` → FAIL (count 7 vs actual 3; rows missing).

- [ ] **Step 3: Add the rows and extend c_reset**

In `CursorPageSource.cs`, insert the five rows immediately **before the `c_reset` ButtonSpec** in the second PageSpec (i.e. after `c_thick`):

```csharp
new SliderSpec("c_gap", "Gap Interpolation (ms)", 0, 500, 25, 0,
    () => c.MaxGapInterpolationMs, v => { c.MaxGapInterpolationMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "Bridge short tracker dropouts up to this long." },
new ComboBoxSpec("c_pos_style", "Position Indicator",
    Enum.GetNames<Core.Models.PositionIndicatorStyle>(), null,
    () => c.PositionIndicatorStyle.ToString(),
    v => { c.PositionIndicatorStyle = Enum.Parse<Core.Models.PositionIndicatorStyle>(v); _ctx.ApplyAndSave(); })
    { Description = "How the position indicator is drawn; applies from the next action-menu open." },
new SliderSpec("c_size", "Circle Size (px)", 10, 500, 10, 0,
    () => c.CircleSize, v => { c.CircleSize = v; _ctx.ApplyAndSave(); })
    { Description = "Diameter of the cursor circle." },
new SliderSpec("c_euro_cutoff", "One Euro Min Cutoff", 0.1, 20.0, 0.1, 1,
    () => c.OneEuroMinCutoff, v => { c.OneEuroMinCutoff = v; _ctx.ApplyAndSave(); })
    { Description = "Lower is smoother and slower; higher follows fast motion." },
new SliderSpec("c_euro_beta", "One Euro Beta", 0.0, 5.0, 0.1, 1,
    () => c.OneEuroBeta, v => { c.OneEuroBeta = v; _ctx.ApplyAndSave(); })
    { Description = "How quickly speed is allowed to reduce smoothing." },
```

(Match the source's existing local variable name for the cursor settings — it is `c`, as the existing `c_reset` lambda's `c.FilterMode` shows. `PositionIndicatorStyle` lives in `EyeCtrl.Core.Models` (`src/EyeCtrl.Core/Models/PositionIndicatorStyle.cs`) — the qualification below is correct.)

In the `c_reset` modal action, after the five existing copies add:

```csharp
c.MaxGapInterpolationMs = d.MaxGapInterpolationMs;
c.PositionIndicatorStyle = d.PositionIndicatorStyle;
c.CircleSize = d.CircleSize;
c.OneEuroMinCutoff = d.OneEuroMinCutoff;
c.OneEuroBeta = d.OneEuroBeta;
```

- [ ] **Step 4: Extend the span-guard Cursor Fact**

```csharp
Check.That(sliders).HasSize(7);
SpanInside(sliders, "c_gap", 0, 500);
SpanInside(sliders, "c_size", 10, 500);
SpanInside(sliders, "c_euro_cutoff", 0.1, 20.0);
SpanInside(sliders, "c_euro_beta", 0.0, 5.0);
```

(keep the existing `c_smooth`/`c_opacity`/`c_thick` SpanInside lines; only `HasSize` changes from 3 to 7).

- [ ] **Step 5: Run, commit**

`dotnet test tests/EyeCtrl.App.Tests` → PASS (completeness gate now requires the five new Descriptions — they are in Step 3).

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/CursorPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/CursorPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(settings): expose gap interpolation, indicator style and One Euro tuning on Cursor"
```

---

### Task 6: Selection tab additions

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/SelectionPageSource.cs` (5 rows after `s_snapRadius`)
- Test: extend whichever test file covers `SelectionPageSource` (in `DwellSelectionKeyboardToolbarPageSourceTests.cs`)
- Modify: span-guard Selection Fact (HasSize(5)→10 + 5 SpanInside)

**Interfaces:**
- Consumes: `EyeCtrlSettings.ManualSelection` (`GazeIndicatorOuterDiameter`, `GazeIndicatorCenterDotDiameter`, `GazeIndicatorStrokeThickness`, `GazeIndicatorOpacity`, `GazeIndicatorResetAnimationMs`) — ranges from Task 1.
- Produces: ids `s_gi_outer, s_gi_dot, s_gi_stroke, s_gi_opacity, s_gi_anim`.

- [ ] **Step 1: Write the failing test**

Add to the single class `DwellSelectionKeyboardToolbarPageSourceTests` in that file (it hosts `Dwell_*`, `Selection_*`, `Keyboard_*`, `Toolbar_*` Facts together — no per-source classes):

```csharp
[Fact]
public void Selection_Build_HasFiveGazeIndicatorRows_RestartDescribed_ExceptOuter()
{
    var sliders = new SelectionPageSource(TestSettingsPageContext.Build()).Build()
        .SelectMany(p => p.Rows).OfType<SliderSpec>()
        .Where(s => s.Id.StartsWith("s_gi_", StringComparison.Ordinal)).ToList();
    Check.That(sliders.Select(s => s.Id)).ContainsExactly(
        "s_gi_outer", "s_gi_dot", "s_gi_stroke", "s_gi_opacity", "s_gi_anim");
    // Four of the five are snapshot-once at startup; the outer diameter is re-read live on
    // every reset animation (OverlayMessageCoordinator), so its claim differs on purpose.
    Check.That(sliders.Where(s => s.Id != "s_gi_outer")
        .All(s => s.Description!.Contains("restart", StringComparison.Ordinal))).IsTrue();
    Check.That(sliders.Single(s => s.Id == "s_gi_outer").Description)
        .Contains("next selection", StringComparison.Ordinal);
    Check.That(sliders.Single(s => s.Id == "s_gi_outer").Get()).IsEqualTo(60);
}
```

- [ ] **Step 2: Run to verify it fails**

`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~DwellSelectionKeyboardToolbarPageSourceTests"` → FAIL.

- [ ] **Step 3: Add the rows**

In `SelectionPageSource.cs`, after the `s_snapRadius` row, still inside the same PageSpec:

```csharp
new SliderSpec("s_gi_outer", "Gaze Ring Diameter (px)", 20, 200, 5, 0,
    () => s.ManualSelection.GazeIndicatorOuterDiameter, v => { s.ManualSelection.GazeIndicatorOuterDiameter = v; _ctx.ApplyAndSave(); })
    { Description = "Diameter of the ring shown at your gaze; applies from the next selection." },
new SliderSpec("s_gi_dot", "Centre Dot (px)", 2, 40, 1, 0,
    () => s.ManualSelection.GazeIndicatorCenterDotDiameter, v => { s.ManualSelection.GazeIndicatorCenterDotDiameter = v; _ctx.ApplyAndSave(); })
    { Description = "Diameter of the dot at the centre of the gaze ring; applies after a restart." },
new SliderSpec("s_gi_stroke", "Ring Stroke (px)", 0.5, 10, 0.5, 1,
    () => s.ManualSelection.GazeIndicatorStrokeThickness, v => { s.ManualSelection.GazeIndicatorStrokeThickness = v; _ctx.ApplyAndSave(); })
    { Description = "Thickness of the gaze ring's outline; applies after a restart." },
new SliderSpec("s_gi_opacity", "Ring Opacity", 0.1, 1.0, 0.05, 2,
    () => s.ManualSelection.GazeIndicatorOpacity, v => { s.ManualSelection.GazeIndicatorOpacity = v; _ctx.ApplyAndSave(); })
    { Description = "How visible the gaze ring is; applies after a restart." },
new SliderSpec("s_gi_anim", "Reset Animation (ms)", 0, 1000, 50, 0,
    () => s.ManualSelection.GazeIndicatorResetAnimationMs, v => { s.ManualSelection.GazeIndicatorResetAnimationMs = (int)v; _ctx.ApplyAndSave(); })
    { Description = "How long the gaze ring takes to fade after a selection; applies after a restart." },
```

(The file's `Build()` local is `s` — the whole `EyeCtrlSettings` (`s.AutoSelection.HighlightColor` in the existing rows) — so ManualSelection is reached as `s.ManualSelection`. `GazeIndicatorOuterDiameter`/`CenterDotDiameter`/`StrokeThickness`/`Opacity` are `double` (no cast); `ResetAnimationMs` is `int` — cast as shown.)- [ ] **Step 4: Extend the span-guard Selection Fact**

```csharp
Check.That(sliders).HasSize(10);
SpanInside(sliders, "s_gi_outer", 20, 200);
SpanInside(sliders, "s_gi_dot", 2, 40);
SpanInside(sliders, "s_gi_stroke", 0.5, 10);
SpanInside(sliders, "s_gi_opacity", 0.1, 1.0);
SpanInside(sliders, "s_gi_anim", 0, 1000);
```

(keep the existing five SpanInside lines).

- [ ] **Step 5: Run, commit**

`dotnet test tests/EyeCtrl.App.Tests` → PASS.

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/SelectionPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/DwellSelectionKeyboardToolbarPageSourceTests.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "feat(settings): gaze-indicator visual rows on Selection"
```

---

### Task 7: Calibration tab addition

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs` (one row after `cal_settle`)
- Test: `tests/EyeCtrl.App.Tests/Settings/Pages/CalibrationPageSourceTests.cs` (extend)

**Interfaces:**
- Consumes: `EyeCtrlSettings.Calibration.TargetColor` (hex string, `SettingsDefaults.Accent` default).
- Produces: id `cal_color`.

- [ ] **Step 1: Write the failing test**

Append to `CalibrationPageSourceTests.cs`:

```csharp
[Fact]
public void Build_HasTargetColorPicker_AfterSettle()
{
    var rows = new CalibrationPageSource(TestSettingsPageContext.Build()).Build()
        .SelectMany(p => p.Rows).ToList();
    var cp = rows.OfType<ColorPickerSpec>().Single();
    Check.That(cp.Id).IsEqualTo("cal_color");
    Check.That(rows.IndexOf(cp)).IsEqualTo(4); // after the four sliders on page 1
    Check.That(cp.Get()).IsEqualTo(SettingsDefaults.Accent);
}
```

(`SettingsDefaults` — add whatever using the file needs; it is `EyeCtrl.Core.Settings`.)

- [ ] **Step 2: Run to verify it fails**

`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~CalibrationPageSourceTests"` → FAIL.

- [ ] **Step 3: Add the row**

In `CalibrationPageSource.cs`, after the `cal_settle` slider (still in the first PageSpec):

```csharp
new ColorPickerSpec("cal_color", "Target Color",
    () => cal.TargetColor, v => { cal.TargetColor = v; _ctx.ApplyAndSave(); },
    SettingsWindow.ColorSwatchRadius)
    { Description = "Colour of the calibration target. Applies after a restart." },
```

(No `using` additions needed: `SettingsWindow` resolves from the child namespace `EyeCtrl.App.Views.Settings` via namespace nesting — `SelectionPageSource` already uses `SettingsWindow.ControlBtnRadius` with no such using.)

- [ ] **Step 4: Run, commit**

`dotnet test tests/EyeCtrl.App.Tests` → PASS.

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/CalibrationPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/CalibrationPageSourceTests.cs
git commit -m "feat(settings): calibration target colour picker"
```

---

### Task 8: Integration — sidebar, wiring, audit comment, full verification

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (fields ~line 190–200, `_tabDefs` ~line 309–335, construction ~line 370–380, `_sources` ~line 384–394, audit comment ~line 239–247)
- Modify: `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs` (expectedTabs array)

**Interfaces:**
- Consumes: the three sources from Tasks 2–4 (constructor `(SettingsPageContext)`).
- Produces: rendered tabs `EyeCare`, `Adaptation`, `Advanced` in `_tabDefs` order … Calibration, Eye Care, Adaptation, General, Quick Actions, Advanced.

- [ ] **Step 1: Extend expectedTabs (failing first)**

In `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs`, add to the `expectedTabs` array. The current last entry `"Settings_Tab_QuickActions"` carries **no trailing comma** — add one, then the three literals:

```csharp
"Settings_Tab_QuickActions",
"Settings_Tab_EyeCare",
"Settings_Tab_Adaptation",
"Settings_Tab_Advanced"
```

(The comparison sorts both sides Ordinal, so position in the array does not matter. Note this file is a FlaUI UI test — see Step 3's gating note.)

- [ ] **Step 2: Add the tab defs, fields, construction, registration, audit comment**

In `SettingsWindow.xaml.cs`:

Fields (after `private readonly QuickActionsPageSource _qaSource;`):

```csharp
private readonly EyeCarePageSource _eyeCareSource;
private readonly AdaptationPageSource _adaptationSource;
private readonly AdvancedPageSource _advancedSource;
```

Construction (after `_qaSource = new QuickActionsPageSource(_ctx);`):

```csharp
_eyeCareSource = new EyeCarePageSource(_ctx);
_adaptationSource = new AdaptationPageSource(_ctx);
_advancedSource = new AdvancedPageSource(_ctx);
```

Registration (after `_sources["QuickActions"] = _qaSource;`):

```csharp
_sources["EyeCare"] = _eyeCareSource;
_sources["Adaptation"] = _adaptationSource;
_sources["Advanced"] = _advancedSource;
```

`_tabDefs` — insert between the `("Calibration", …)` and `("General", …)` entries:

```csharp
("EyeCare", "◑", "Eye Care",
    "Break reminders, gaze exercises and fatigue escalation."),
("Adaptation", "⇄", "Adaptation",
    "Auto-tuning of dwell and targeting when you struggle."),
```

and after the `("QuickActions", …)` entry (last). **`QuickActions` currently carries no trailing comma** — add a comma after its description line, then append the Advanced entry, which itself takes no trailing comma (it becomes the final entry):

```csharp
("Advanced", "⚒", "Advanced",
    "Expert tuning: backdrop, drag, scroll and external dwell.")
```

Replace the whole audit comment block (the `// INTENTIONALLY NOT EXPOSED …` lines) with:

```csharp
// INTENTIONALLY NOT EXPOSED in the settings UI (audit this list when adding settings):
//  - ManualSelection.Enabled (driven by SelectionMode; the Selection tab owns the mode)
//  - Keyboard ML/model settings (CustomModels etc. — managed by the model registry; Models tab)
//  - Adaptation.CaregiverAlerts (no consumer until Phase 3)
//  - Supervision — its UI is SupervisorSetupWindow in the supervisor process; the App
//    cannot persist that section (SettingsWriter.Supervisor owns it)
// Everything else in EyeCtrlSettings.EnumerateSubSettings() should have a tab/control here.
```

- [ ] **Step 3: Build and run the full App suite**

`dotnet build EyeCtrl.slnx` → clean.
`dotnet test tests/EyeCtrl.App.Tests` → PASS.

Two auto-discovering suites pick up this task's changes and should stay green with no edit:
`SettingsAutoPaginationTests.EveryRenderedTab_HasASourcesEntry` asserts every `_tabDefs` id has a matching `_sources["id"] =` line — both land in this task's commit, which is why they must not be split. `SettingsContentFitTests.TheLongestRealToggleLabel_FitsTheContentColumn_OnANarrowDisplay` regex-scrapes every `ToggleSpec("id", "Label"` in the Sources tree; the longest new toggle label ("Suppress in Sensitive Contexts", 30 chars) is well under the 48-char strings already in that set.

**`SettingsTests` is an opt-in UI test** — `[Trait("Category","UI")]` + `AppFixture.SkipIfDisabled()`, which `Assert.skip`s unless `EYECTRL_UI_TESTS=1`. It launches the real app and drives the cursor. **Do NOT set that variable; leave it skipped** — the `expectedTabs` edit is verified headlessly by `SettingsTabDriftTests`, which parses both `_tabDefs` and the UI test's expectation array. Likewise, never launch the app to eyeball the three new tab icons without the user's explicit OK; if `◑ ⇄ ⚒` render as tofu when the user next opens the window, swap the character then (the label-fit test only checks non-empty icons).

- [ ] **Step 4: Run the remaining non-UI suites**

One at a time:

```bash
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
dotnet test tests/EyeCtrl.Platform.Tests
```

All pass (nothing outside App/Core changed, but the full sweep is the completion gate).

- [ ] **Step 5: detect_changes, commit**

Run GitNexus `detect_changes` (worktree path). Expected changed symbols: the three new `Build()` methods + classes, `CursorPageSource.Build`, `SelectionPageSource.Build`, `CalibrationPageSource.Build`, `SettingsWindow` ctor members, `ManualSelectionSettings.Validate`, `SettingsService.Load`. Anything else — stop and investigate before committing.

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs
git commit -m "feat(settings): Eye Care, Adaptation and Advanced tabs in the sidebar"
```

---

## Plan Self-Review (already applied)

- **Spec coverage**: §2→Task 2, §3→Task 3, §4→Task 4, §5→Tasks 5–7, §6→Task 1, §1+Testing→Task 8 (+ gate extensions inside each task). Non-goals hold: no Supervision rows, no `ad_caregiver`, no new spec types.
- **Counts**: 8+13+11+1+5+5 = 43 describable rows; span-guard Facts assert 6/5/7 new-source sliders and 7/10 extended Cursor/Selection counts.
- **Type consistency**: `SliderSpec(Id, Label, Min, Max, Step, Decimals, Get, Set)`, `ComboBoxSpec(Id, Label, Display, Code, Get, Set)`, `ColorPickerSpec(Id, Label, Get, Set, RadiusLogical)` — matches `SettingsRowSpec.cs`. `Math.Clamp` doubles for double props, `(int)v` casts for int props per house style. Locals verified: `CursorPageSource` uses `c`, `SelectionPageSource` uses `s` (settings root); `PositionIndicatorStyle` is `EyeCtrl.Core.Models`.
