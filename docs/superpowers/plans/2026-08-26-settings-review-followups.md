# Settings Review Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the three review-flagged defects left out of the descriptions pass: the toolbar-size slider offering values persistence silently rejects, the unguarded grace-period slider's doc lie, and the Layout tab header's "overlays" overclaim — and pin every non-Dwell slider span against its Core guard.

**Architecture:** One behavior fix (slider minimum), one new test file mirroring the existing Dwell range-vs-guard test for the other four tabs, and two string-literal corrections (XML doc, tab header). No delegate, ordering, or description changes.

**Tech Stack:** .NET 10 WPF (`EyeCtrl.App`, `EyeCtrl.Core`), xUnit v3 + NFluent 3.1.0 (global usings — no using lines needed for them).

**Spec context:** the Observations section of `docs/superpowers/specs/2026-08-26-settings-row-descriptions-design.md` and plan-review findings 8/NIT 9's neighborhood. This branch is STACKED on `feat/settings-descriptions` — its commits are the base, not rivals.

## Global Constraints

- Work in the worktree `C:\Users\andre\source\repos\eyectrl\.worktrees\settings-review-followups` (branch `fix/settings-review-followups`, based on `feat/settings-descriptions`). All paths below are relative to it.
- NFluent 3.1.0 inclusive comparisons are `IsGreaterOrEqualThan` / `IsLessOrEqualThan` (NOT `IsGreaterThanOrEqualTo` — that does not compile).
- Prose copy is British ("centre"); code identifiers and existing labels stay as they are.
- The only behavior change allowed is the `t_size` slider minimum (32 → 40). Maxima, steps, decimals, delegates, descriptions, and row ordering must not change anywhere.
- Test command: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` (baseline on this branch: 610/610 green). Never run two suites at once on this box; never run `Category=UI` (spawns app windows; the user controls the machine by gaze alone).
- Conventional commits, no push.
- AGENTS.md ritual: `impact({target:"Build", file_path:"...", direction:"upstream", repo:"eyectrl"})` before editing a page source's `Build()`; `detect_changes` once at the end.

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs` | t_size slider minimum | Modify |
| `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs` | span guards for Toolbar/Selection/Keyboard/Calibration sliders | Create |
| `src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs:14` | grace-period doc truth | Modify |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:317` | Layout tab header scope | Modify |

---

### Task 1: t_size slider floor + span-guard tests for the other four tabs

**Files:**
- Modify: `src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs:21-23` (t_size min)
- Create: `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs`

**Interfaces:**
- Consumes: `ToolbarPageSource(SettingsPageContext)`, `SelectionPageSource`, `KeyboardPageSource`, `CalibrationPageSource`, `TestSettingsPageContext.Build()`, `SliderSpec.Min/.Max`, NFluent `IsGreaterOrEqualThan`/`IsLessOrEqualThan`/`Contains`/`HasSize`.
- Produces: `SettingsSliderSpanGuardTests` with four facts; `t_size` span 40–120 that Task 2 and the final review rely on.

- [ ] **Step 1: Run impact on `ToolbarPageSource.Build`** (`impact({target:"Build", file_path:"ToolbarPageSource.cs", direction:"upstream", repo:"eyectrl"})`) — expected LOW (one caller, `SettingsWindow.BuildToolbarPages`).

- [ ] **Step 2: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Same contract as Dwell_SliderSpans_StayInsideTheValidatedRanges in
/// DwellSelectionKeyboardToolbarPageSourceTests, widened to every other tab: a settings slider
/// must never offer a value its Core guard (OnXxxChanging or Validate) rejects. Two failure
/// modes make this load-bearing: setter guards throw on the UI thread (crash); Validate-only
/// guards like ToolbarSettings.Size throw inside SettingsService.Save, which SaveDebounced
/// swallows — the value applies live and is silently lost on restart. Guard bounds are
/// restated from the Core classes so moving either side fails loudly here.
/// s_grace (Grace Period) is deliberately not span-pinned: ManualSelectionSettings guards
/// neither the setter nor Validate for it — there is no guard to stay inside.
/// </summary>
public class SettingsSliderSpanGuardTests
{
    [Fact]
    public void Toolbar_SliderSpan_StaysInsideTheValidatedRange()
    {
        var sliders = Sliders(new ToolbarPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(1);
        SpanInside(sliders, "t_size", 40, 200);
    }

    [Fact]
    public void Selection_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new SelectionPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(5);
        Check.That(sliders.Keys).Contains(new[]
            { "s_rad", "s_mag", "s_grace", "s_highlightThickness", "s_snapRadius" });
        SpanInside(sliders, "s_rad", 50, 500);
        SpanInside(sliders, "s_mag", 1.5, 5.0);
        SpanInside(sliders, "s_highlightThickness", 1, 10);
        SpanInside(sliders, "s_snapRadius", 16, 96);
        // s_grace: no Core guard exists — nothing to pin (see the class doc).
    }

    [Fact]
    public void Keyboard_SliderSpan_StaysInsideTheValidatedRange()
    {
        var sliders = Sliders(new KeyboardPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(1);
        SpanInside(sliders, "k_size", 0.5, 2.0);
    }

    [Fact]
    public void Calibration_SliderSpans_StayInsideTheValidatedRanges()
    {
        var sliders = Sliders(new CalibrationPageSource(TestSettingsPageContext.Build()));
        Check.That(sliders).HasSize(4);
        SpanInside(sliders, "cal_points", 3, 16);
        SpanInside(sliders, "cal_samples", 5, 100);
        SpanInside(sliders, "cal_hold", 200, 3000);
        SpanInside(sliders, "cal_settle", 0, 500);
    }

    private static Dictionary<string, SliderSpec> Sliders(ISettingsPageSource source) =>
        source.Build().SelectMany(p => p.Rows).OfType<SliderSpec>().ToDictionary(s => s.Id);

    private static void SpanInside(Dictionary<string, SliderSpec> sliders, string id, double min, double max)
    {
        Check.That(sliders[id].Min).IsGreaterOrEqualThan(min);
        Check.That(sliders[id].Max).IsLessOrEqualThan(max);
    }
}
```

Guard sources, for the record: `t_size` 40–200 (`ToolbarSettings.Validate`); `s_rad` 50–500 and `s_mag` 1.5–5.0 (`ManualSelectionSettings` OnChanging + Validate); `s_highlightThickness` 1–10 and `s_snapRadius` 16–96 (`AutoSelectionSettings` OnChanging + Validate); `k_size` 0.5–2.0 (`KeyboardSettings.Validate`); all four Calibration spans (`CalibrationSettings` OnChanging + Validate).

- [ ] **Step 3: Run the new test to verify it fails**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SettingsSliderSpanGuardTests"`
Expected: FAIL — Toolbar fact: `t_size` Min is 32 (floor 40). The other three facts pass.

- [ ] **Step 4: Fix the t_size minimum**

In `ToolbarPageSource.cs`, the `t_size` construction (min 32 lands off the 40 floor; 40 is on-step with step 4):

```csharp
new SliderSpec("t_size", "Button Size", 40, 120, 4, 0,
    () => t.Size, v => { t.Size = v; _ctx.ApplyAndSave(); })
    { Description = "Size of each button; bigger ones are easier to aim at." },
```

The max stays 120 — the validator's 200 is an outer bound the UI deliberately doesn't offer; only the below-floor minimum is a defect.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS, 614 total (610 + 4 new facts).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.App/Views/Settings/Sources/ToolbarPageSource.cs tests/EyeCtrl.App.Tests/Settings/Pages/SettingsSliderSpanGuardTests.cs
git commit -m "fix(settings): keep the toolbar size slider inside the validated range

The 32-120 span offered 32 and 36, which ToolbarSettings.Validate
rejects; the save throws inside SaveDebounced and is swallowed, so those
values applied live and were silently lost on restart. Minimum raised to
the validated floor (40); a new span-guard test pins every non-Dwell
slider against its Core guard."
```

---

### Task 2: grace-period doc truth + Layout tab header scope

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs:14` (XML doc only)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:317` (string literal only)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: truthful doc and header copy; no code consumes either string programmatically (verified: no test pins the header text).

- [ ] **Step 1: Run impact on `SettingsWindow`** (`impact({target:"SettingsWindow", direction:"upstream", repo:"eyectrl"})`) — expected none/LOW; the edit is a string literal in the `_tabDefs` initializer. Also `impact({target:"ManualSelectionSettings", direction:"upstream", repo:"eyectrl"})` — the edit is an XML doc comment.

- [ ] **Step 2: Correct the grace-period doc**

`ManualSelectionSettings.cs` line 14, replace:

```csharp
    /// <summary>Grace period in milliseconds after entering manual selection before dwell activates. Valid range: 100–2000.</summary>
```

with:

```csharp
    /// <summary>Grace period in milliseconds after entering manual selection before dwell activates. Not range-guarded: neither OnManualSelectionGracePeriodMsChanging nor Validate restricts it, and the settings UI offers 0–2000.</summary>
```

(Zero is a meaningful value — no grace — so no guard is added; the doc stops claiming one.)

- [ ] **Step 3: Scope the Layout tab header**

`SettingsWindow.xaml.cs` line 317, replace:

```csharp
                ("Layout",   "⊞", "Layout",
                    "Whether overlays open at your gaze or at the screen centre.", BuildLayoutPages),
```

with:

```csharp
                ("Layout",   "⊞", "Layout",
                    "Where the action menu and magnifier open: at your gaze, or at the screen centre.", BuildLayoutPages),
```

(`ViewPosition` is read only by the action menu and manual selection, `GazeEngineAdapter.cs:129/171` — the keyboard, toolbar and settings window never consult it. The row description already says this; the header now matches it instead of overclaiming.)

- [ ] **Step 4: Verify no test pins the old strings, build, run the suite**

Run: `grep -rn "Whether overlays open" tests src --include="*.cs"` → no matches.
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` — Expected: PASS, 614 total.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "fix(settings): stop the grace-period doc and layout header overclaiming

The grace-period doc cited a 100-2000 range that no guard enforces; the
Layout tab header said 'overlays' though ViewPosition moves only the
action menu and the manual-selection magnifier."
```

---

## Self-Review (completed)

- **Coverage:** the three review-flagged items — t_size span defect (Task 1), s_grace doc lie (Task 2), Layout header overclaim (Task 2) — plus the reviewer's NIT 8 suggestion to extend range-vs-guard beyond Dwell (Task 1). The settings-load resilience item is deliberately out: behavior change needing its own design.
- **Placeholders:** none; both file edits and the full test are verbatim.
- **Type consistency:** `IsGreaterOrEqualThan`/`IsLessOrEqualThan`, `SliderSpec.Min/.Max`, `TestSettingsPageContext.Build()` all verified against this codebase (the NFluent names by compile error and repo usage, `tests/EyeCtrl.App.Tests/HaloContrastTests.cs:81` and `HeartbeatRestartPayloadTests.cs:157`).
