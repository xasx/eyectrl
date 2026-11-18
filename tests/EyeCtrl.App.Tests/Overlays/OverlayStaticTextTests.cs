namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// T08 fixup R1: the XAML-only captions of the Step-3 windows, pinned as English literals through
/// the real loaded markup. Before this, swapping a caption's key (mutants M4, M5, M6, M7, M14,
/// M16) kept every test green — the keys were only checked to exist, never to be at the right
/// site. The windows are constructed, never shown: no HWND, no screen, no input.
/// </summary>
[Collection("WpfApplication")]
public class OverlayStaticTextTests
{
    [StaFact]
    public void CalibrationWindow_ShowsTheKeyedEnglishCaptions()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CalibrationWindow();

        Check.That(Text(window, "CompletionTitle")).IsEqualTo("Calibration complete");
        Check.That(Content(window, "RetryButton")).IsEqualTo("Retry");
        Check.That(Content(window, "AcceptButton")).IsEqualTo("Accept anyway");
        Check.That(Text(window, "CompletionHint")).IsEqualTo("Auto-dismissing in 3s...");
        Check.That(Text(window, "CancelHint")).IsEqualTo("Press Esc to cancel");
        Check.That(Text(window, "CancelDwellLabel")).IsEqualTo("✕ Cancel");
    }

    [StaFact]
    public void EyeCareBannerWindow_ShowsTheKeyedEnglishCaptions()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new EyeCareBannerWindow();

        Check.That(Text(window, "SubText")).IsEqualTo("You've been fixating for a while");
        Check.That(Text(window, "SnoozeLabel")).IsEqualTo("snooze");
        Check.That(Text(window, "StartLabel")).IsEqualTo("start");
    }

    [StaFact]
    public void EyeCareExerciseWindow_ShowsTheKeyedEnglishCaptions_AndCompletesTheName()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new EyeCareExerciseWindow();

        Check.That(Content(window, "SkipButton")).IsEqualTo("Skip ✕");
        Check.That(Text(window, "SummaryTitle")).IsEqualTo("✓ Nice work");
        Check.That(Text(window, "AccuracyLabel")).IsEqualTo("accuracy");
        Check.That(Text(window, "SteadinessLabel")).IsEqualTo("steadiness");
        Check.That(Text(window, "ReactionMsLabel")).IsEqualTo("reaction ms");

        // "{0} complete" with the exercise's own name; the panel's 3 s timer never needs a pump.
        _ = window.ShowSummaryAsync(
            new EyeCareExerciseOutcome("Smooth Pursuit", 92, 4.3, null, 45, "Smooth tracking!"));
        Check.That(Text(window, "SummaryName")).IsEqualTo("Smooth Pursuit complete");
    }

    [StaFact]
    public void TaskSwitcherAndScrollWindows_ShowTheKeyedEnglishHintsAndTitles()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var taskSwitcher = new TaskSwitcherWindow();
        Check.That(Text(taskSwitcher, "HintText"))
             .IsEqualTo("Select a window — or ✕ to cancel");

        var scroll = new ScrollOverlayWindow();
        Check.That(Text(scroll, "EscapeHint")).IsEqualTo("Press Esc to stop scrolling");
        Check.That(scroll.Title).IsEqualTo("Scroll Overlay");

        var backdrop = new GlassBackdropWindow();
        Check.That(Text(backdrop, "EscapeHint")).IsEqualTo("Press Esc to exit");
    }

    /// <summary>
    /// T08 fixup R3: the pause window is chromeless and its title is never drawn — it is only the
    /// UIA name, so it gets a real name instead of the identifier "PauseIndicator".
    /// </summary>
    [StaFact]
    public void PauseIndicatorTitle_IsARealName_NotAnIdentifier()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new PauseIndicatorWindow();

        Check.That(window.Title).IsEqualTo("EyeCtrl paused");
    }

    /// <summary>
    /// T08 fixup R3, re-targeted by the close-arc ticket: the grid's close target is now the shared
    /// DwellArcButton, whose drawn icon has no automation peer — its opaque automation anchor
    /// carries the name. It must still announce "Close", not the raw glyph.
    /// </summary>
    [StaFact]
    public void QuickActionsGridCloseDisc_PublishesARealName()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new QuickActionsGridWindow();

        var anchor = window.CloseDisc?.AutomationAnchor;
        Check.WithCustomMessage("the close disc has no automation anchor to name").That(anchor).IsNotNull();
        Check.That(System.Windows.Automation.AutomationProperties.GetName(anchor!))
             .IsEqualTo("Close");
    }

    /// <summary>
    /// The switcher's close disc carries the same automation anchor (close-arc ticket): an
    /// icon-only DwellArcButton publishes its name through the anchor, so it announces "Close"
    /// instead of nothing at all.
    /// </summary>
    [StaFact]
    public void TaskSwitcherCloseDisc_PublishesARealName()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();

        var anchor = window.CloseDisc?.AutomationAnchor;
        Check.WithCustomMessage("the close disc has no automation anchor to name").That(anchor).IsNotNull();
        Check.That(System.Windows.Automation.AutomationProperties.GetName(anchor!))
             .IsEqualTo("Close");
    }

    /// <summary>
    /// A4: the switcher surface must carry the toolbar's automation-anchor convention — a
    /// window AutomationId (the AppFixture FlaUI anchoring pattern), a real Title (it is the
    /// pure UIA name on a chromeless window: ShowInTaskbar=False + the unconditional
    /// WS_EX_TOOLWINDOW of SetOverlayInteraction keep it invisible outside UIA), and a
    /// Tiles AutomationId. No per-tile automation properties: the title TextBlock already
    /// announces Title via WPF's default peer.
    /// </summary>
    [StaFact]
    public void TaskSwitcherWindow_PublishesAutomationAnchors()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();

        Check.That(System.Windows.Automation.AutomationProperties.GetAutomationId(window))
             .IsEqualTo("TaskSwitcher_Window");
        Check.That(window.Title).IsEqualTo("Task switcher");
        Check.That(System.Windows.Automation.AutomationProperties.GetAutomationId(
                (System.Windows.Controls.ItemsControl)window.FindName("TilesPanel")!))
             .IsEqualTo("TaskSwitcher_Tiles");
    }

    private static string Text(FrameworkElement window, string name) =>
        ((TextBlock)window.FindName(name)!).Text;

    private static string Content(FrameworkElement window, string name) =>
        ((Button)window.FindName(name)!).Content as string ?? string.Empty;
}
