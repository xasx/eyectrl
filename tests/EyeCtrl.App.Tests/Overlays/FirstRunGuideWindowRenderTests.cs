// tests/EyeCtrl.App.Tests/Overlays/FirstRunGuideWindowRenderTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.App.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

/// <summary>
/// S2-3: the integrated dispatch + re-render path had NO tests — nothing referenced SetChoices,
/// SetChromeClose, SetDwellKnob, SetThemeGrid or the per-step target registration, which is the
/// gap the S1 no-op escaped through. These tests drive the window builders in the
/// <c>GuideSurface.ShowStep</c> order (ShowCardContent → SetChoices → SetChromeClose →
/// SetPractice → SetDwellKnob → SetThemeGrid) and pin the EXACT chrome target-id set on
/// <c>vm.DetectorForTest.Targets</c> after each step's shape, plus the two re-render paths an
/// in-place press takes (dwell ±, theme chip). The VM clears EVERY chrome target before
/// dispatching, so a re-render that fails to re-register strands the user — the id sets here are
/// the pin that would have caught S1's cousin on the window side.
/// </summary>
[Trait("Category", "UI")]
public class FirstRunGuideWindowRenderTests
{
    // Mirrors GuideSurface.RowChoicesFor (SettingsUiCoordinator) — kept literal so a row-table
    // change on either side shows up as a failing set here.
    private static (string Label, string Id)[] RowChoicesFor(FirstRunGuideStep step) => step switch
    {
        FirstRunGuideStep.Welcome => [("Start", "guide_start"), ("Later", "guide_later")],
        FirstRunGuideStep.MeetDwell => [("Skip", "guide_dwell_skip")],
        FirstRunGuideStep.DwellTime => [("Continue", "guide_dwelltime_continue")],
        FirstRunGuideStep.Calibrate => [("Now", "guide_calibrate_now"), ("Skip", "guide_calibrate_skip")],
        FirstRunGuideStep.ToolbarTask => [("Continue", "guide_toolbar_continue"), ("Skip", "guide_toolbar_skip")],
        FirstRunGuideStep.Theme => [("Continue", "guide_theme_continue")],
        FirstRunGuideStep.Safety => [("Begin", "guide_safety_begin")],
        FirstRunGuideStep.Done => [("Finish", "guide_done_finish")],
        _ => [],
    };

    // Mirrors GuideSurface.ThemeChips.
    private static (string Label, string Id)[] ThemeChips() =>
    [
        ("Midnight", "guide_theme_midnight"),
        ("Ember", "guide_theme_ember"),
        ("Daylight", "guide_theme_daylight"),
        ("Contrast", "guide_theme_contrast"),
    ];

    /// <summary>The GuideSurface.ShowStep order, driven against the real window builders. The
    /// chrome Close label is a stand-in (the surface passes the localized one).</summary>
    private static void RenderStep(FirstRunGuideWindow window, FirstRunGuideStep step)
    {
        var chromeClose = step is not (FirstRunGuideStep.Welcome or FirstRunGuideStep.Done);
        window.ShowCardContent("title", "body", chromeClose);
        window.SetChoices(RowChoicesFor(step));
        window.SetChromeClose(chromeClose, "✕");
        window.SetPractice(step is FirstRunGuideStep.MeetDwell or FirstRunGuideStep.DwellTime);
        window.SetDwellKnob(step == FirstRunGuideStep.DwellTime);
        window.SetThemeGrid(step == FirstRunGuideStep.Theme, ThemeChips());
    }

    private static List<string> ChromeTargetIds(FirstRunGuideViewModel vm) =>
        vm.DetectorForTest.Targets.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

    private static List<string> Sorted(params string[] ids) =>
        ids.OrderBy(id => id, StringComparer.Ordinal).ToList();

    private static void Pump(FirstRunGuideWindow window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    [StaFact]
    public void EveryStepShape_RegistersExactlyItsOwnTargetSet()
    {
        var cases = new (FirstRunGuideStep Step, string[] Expected, bool Practice)[]
        {
            (FirstRunGuideStep.Welcome,
                ["guide_later", "guide_start"], practice: false),
            (FirstRunGuideStep.MeetDwell,
                ["guide_close", "guide_dwell_skip"], practice: true),
            (FirstRunGuideStep.DwellTime,
                ["guide_close", "guide_dwell_minus", "guide_dwell_plus", "guide_dwelltime_continue"],
                practice: true),
            (FirstRunGuideStep.Calibrate,
                ["guide_calibrate_now", "guide_calibrate_skip", "guide_close"], practice: false),
            (FirstRunGuideStep.ToolbarTask,
                ["guide_close", "guide_toolbar_continue", "guide_toolbar_skip"], practice: false),
            (FirstRunGuideStep.Theme,
                ["guide_close", "guide_theme_contrast", "guide_theme_continue", "guide_theme_daylight",
                 "guide_theme_ember", "guide_theme_midnight"], practice: false),
            (FirstRunGuideStep.Safety,
                ["guide_close", "guide_safety_begin"], practice: false),
            (FirstRunGuideStep.Done,
                ["guide_done_finish"], practice: false),   // no chrome Close on the last card
        };

        foreach (var (step, expected, practice) in cases)
        {
            var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
                new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
            var window = new FirstRunGuideWindow(vm);
            InvisibleTestWindow.Prepare(window);
            window.Show();
            try
            {
                RenderStep(window, step);
                Pump(window);

                Check.WithCustomMessage($"{step}: the chrome target set must be exactly the step's own")
                    .That(ChromeTargetIds(vm)).IsEqualTo(Sorted(expected));

                // The practice ring is a SEPARATE detector: its window-level evidence is the ring
                // grid in StepContent, its VM-level gate PracticeVisible (what adds/removes the
                // guide_practice target).
                var ring = window.StepContent.Content is Grid g
                    && g.Children.OfType<Ellipse>().Any(e => e.StrokeThickness > 0);
                Check.WithCustomMessage($"{step}: practice ring present={practice}").That(ring)
                    .IsEqualTo(practice);
                Check.WithCustomMessage($"{step}: PracticeVisible must follow the step")
                    .That(vm.PracticeVisible).IsEqualTo(practice);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [StaFact]
    public void DwellTimeReRender_BringsTheKnobTargetsBack_AfterAClearedPress()
    {
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            RenderStep(window, FirstRunGuideStep.DwellTime);
            Pump(window);

            // A press clears EVERY chrome target before dispatching (the VM's contract); the
            // wiring's re-render starts from SetChoices — which must leave exactly the new row.
            window.SetChoices(RowChoicesFor(FirstRunGuideStep.DwellTime));
            Pump(window);
            Check.That(ChromeTargetIds(vm))
                .IsEqualTo(Sorted("guide_dwelltime_continue"));   // close and ± are gone here

            // RenderChoicesForCurrentStep continues through ShowStep: the Close and the knob's
            // own already-built branches RE-REGISTER (r5 F2). A missing re-register strand is
            // exactly what this pin exists for.
            window.SetChromeClose(true, "✕");
            window.SetDwellKnob(true);
            Pump(window);

            Check.That(ChromeTargetIds(vm)).IsEqualTo(
                Sorted("guide_close", "guide_dwell_minus", "guide_dwell_plus", "guide_dwelltime_continue"));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ThemeReRender_ReAttachesTheSameGrid_AndReRegistersTheChips()
    {
        var vm = new FirstRunGuideViewModel(new WeakReferenceMessenger(), 0,
            new EyeCtrlSettings(), sink: null, dwellRegistry: null) { IsVisible = true };
        var window = new FirstRunGuideWindow(vm);
        InvisibleTestWindow.Prepare(window);
        window.Show();
        try
        {
            RenderStep(window, FirstRunGuideStep.Theme);
            Pump(window);
            var grid = (Grid)window.StepContent.Content;
            var fullThemeSet = Sorted(
                "guide_close", "guide_theme_contrast", "guide_theme_continue", "guide_theme_daylight",
                "guide_theme_ember", "guide_theme_midnight");
            Check.That(ChromeTargetIds(vm)).IsEqualTo(fullThemeSet);

            // A chip press clears every chrome target; the re-render runs ShowStep again —
            // SetPractice(false) empties StepContent (the grid detaches) BEFORE SetThemeGrid
            // re-attaches it (r6 F4).
            window.SetChoices(RowChoicesFor(FirstRunGuideStep.Theme));
            window.SetChromeClose(true, "✕");
            window.SetPractice(false);
            Pump(window);
            Check.That(ChromeTargetIds(vm))
                .IsEqualTo(Sorted("guide_close", "guide_theme_continue"));   // chips cleared
            Check.That(window.StepContent.Content).IsNull();

            window.SetDwellKnob(false);
            window.SetThemeGrid(true, ThemeChips());
            Pump(window);

            Check.That(ChromeTargetIds(vm)).IsEqualTo(fullThemeSet);   // chips re-registered
            Check.WithCustomMessage("the re-render must re-attach the SAME built grid, not rebuild one")
                .That(ReferenceEquals(window.StepContent.Content, grid)).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }
}
