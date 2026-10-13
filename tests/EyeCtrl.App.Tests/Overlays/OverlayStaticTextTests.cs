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
             .IsEqualTo("Select a window — or look away to cancel");

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
    /// T08 fixup R3: the grid's close target is a Border (no automation peer) whose only text is
    /// the glyph, so it announced "✕". The glyph TextBlock carries the real name; the key reuse is
    /// single-surface (overlays).
    /// </summary>
    [StaFact]
    public void QuickActionsGridCloseButton_PublishesARealName()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new QuickActionsGridWindow();

        Check.That(System.Windows.Automation.AutomationProperties.GetName(
                (TextBlock)window.FindName("CloseGlyph")!))
             .IsEqualTo("Close");
    }

    private static string Text(FrameworkElement window, string name) =>
        ((TextBlock)window.FindName(name)!).Text;

    private static string Content(FrameworkElement window, string name) =>
        ((Button)window.FindName(name)!).Content as string ?? string.Empty;
}
