namespace EyeCtrl.App.Tests.Localization.Fit;

using System.Linq;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09's class-C growth for the caregiver alert, and its re-anchor (recheck N3). The window is
/// 720 DIP wide and grows in HEIGHT only, from a 120 DIP minimum; its BOTTOM edge stays 48 DIP
/// above the work area's bottom on every show, so a taller window rises instead of sliding under
/// the taskbar. Before T09 the position was computed once, from the XAML's fixed 120, so a taller
/// window would have grown downward past the margin.
///
/// <para>
/// Driven through the production <see cref="CaregiverAlertWindow.Prepare"/> — the step
/// <c>ShowCaregiverAlert</c> runs before <c>Show</c> — and never shown. The laid-out height is
/// then re-checked at 2.0 in <see cref="FitHost"/>, so the size the window was given is the size
/// its content needs at the user's scale. A message is asserted as an English literal where the
/// test pins English, per the crisis-surface convention.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class CaregiverAlertGrowthTests
{
    /// <summary>A work area that does not start at the origin: a top taskbar.</summary>
    private static readonly Rect TopTaskbarArea = new(0, 48, 1920, 1152);

    /// <summary>About three times the English poor-tracking message: wraps to several lines.</summary>
    private const string LongMessage =
        "Die Blickverfolgung ist seit einer Minute unzuverlässig. Bitte prüfen Sie die Sitzposition "
        + "und den Abstand zum Bildschirm, reinigen Sie gegebenenfalls die Kamera, und kalibrieren Sie "
        + "die Augensteuerung in der Tobii-Software neu, falls das Problem bestehen bleibt. Wenn die "
        + "Betreuungsperson nicht im Raum ist, bleibt dieser Hinweis sichtbar, bis sich die "
        + "Verfolgung wieder stabilisiert hat.";

    [StaFact]
    public void TheEnglishMessages_KeepTodaysWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        foreach (var condition in new[] { CaregiverAlertCondition.SustainedPoorTracking, CaregiverAlertCondition.SustainedStruggle })
        {
            var window = new CaregiverAlertWindow();
            var message = CaregiverAlertWindow.MessageFor(StringTable.EmbeddedEnglish, condition);
            window.Prepare(message, AppFitMeasurers.ReferenceWorkArea);

            Check.WithCustomMessage($"{condition}: English must not grow the alert (\"{message}\").")
                .That(window.Height).IsEqualTo(120.0);
            Check.That(window.Width).IsEqualTo(720.0);
            Check.That(window.Top).IsEqualTo(AppFitMeasurers.ReferenceWorkArea.Bottom - 48 - 120);
            Check.That(window.Left).IsEqualTo((1920 - 720) / 2.0);
        }
    }

    [StaFact]
    public void ALongMessage_GrowsTheWindowUpward_AndTheBottomEdgeStaysAt48()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        foreach (var area in new[] { AppFitMeasurers.ReferenceWorkArea, TopTaskbarArea })
        {
            var window = new CaregiverAlertWindow();
            window.Prepare(LongMessage, area);

            Check.WithCustomMessage("A three-line message must grow the alert past 120.")
                .That(window.Height).IsStrictlyGreaterThan(120.0);
            Check.WithCustomMessage("The width is fixed: class-C growth is height only.")
                .That(window.Width).IsEqualTo(720.0);
            Check.WithCustomMessage($"The bottom edge must stay at {area.Bottom} − 48.")
                .That(window.Top + window.Height).IsCloseTo(area.Bottom - CaregiverAlertWindow.MarginDip, 1e-9);

            var height = window.Height;
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            using var host = FitHost.Open(root, width: window.Width, inheritFrom: window);
            var laidOut = FitText.NaturalHeight(host, root, window.Width);
            Check.WithCustomMessage($"At 2.0 the banner needs {laidOut:0.##} DIP but the window is {height:0.##}: it would clip.")
                .That(laidOut).IsLessOrEqualThan(height + 0.5);
        }
    }

    [StaFact]
    public void TheGermanMessages_KeepTodaysWindow_AndTheAnchor()
    {
        // T11: measured at 2.0 on the user's work area, both German messages are 86.54–105.16 DIP
        // of layout, under the 120 DIP floor, so German does NOT grow the alert. If a later German
        // grows past it, this pin fails and the grown state (bottom edge at work-area bottom − 48,
        // window inside the work area) is re-measured by the growth gate above.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var german = ShippedGerman.Load();
        foreach (var condition in new[] { CaregiverAlertCondition.SustainedPoorTracking, CaregiverAlertCondition.SustainedStruggle })
        {
            var window = new CaregiverAlertWindow();
            var message = CaregiverAlertWindow.MessageFor(german, condition);
            window.Prepare(message, AppFitMeasurers.ReferenceWorkArea);

            Check.WithCustomMessage($"{condition}: German grew the alert (\"{message}\").")
                .That(window.Height).IsEqualTo(120.0);
            Check.That(window.Width).IsEqualTo(720.0);
            Check.WithCustomMessage($"{condition}: the bottom edge left its anchor.")
                .That(window.Top).IsEqualTo(AppFitMeasurers.ReferenceWorkArea.Bottom - 48 - 120);
        }
    }

    [StaFact]
    public void EveryShow_ReSizesAndReAnchors_SoAShorterMessageShrinksBack()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CaregiverAlertWindow();
        window.Prepare(LongMessage, AppFitMeasurers.ReferenceWorkArea);
        var grown = window.Height;

        window.Prepare(CaregiverAlertWindow.MessageFor(StringTable.EmbeddedEnglish, CaregiverAlertCondition.SustainedStruggle),
            AppFitMeasurers.ReferenceWorkArea);

        Check.That(grown).IsStrictlyGreaterThan(120.0);
        Check.WithCustomMessage("A later, shorter message must size the window back to its minimum.")
            .That(window.Height).IsEqualTo(120.0);
        Check.WithCustomMessage("…and re-anchor it: the position is recomputed on every show.")
            .That(window.Top).IsEqualTo(AppFitMeasurers.ReferenceWorkArea.Bottom - 48 - 120);
    }

    [StaFact]
    public void TheSizeChangedBackstop_AnchorsTheBottomEdge()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CaregiverAlertWindow();
        window.Anchor(TopTaskbarArea, 300);
        Check.That(window.Top + 300).IsEqualTo(TopTaskbarArea.Bottom - CaregiverAlertWindow.MarginDip);
        Check.That(window.Left).IsEqualTo(TopTaskbarArea.Left + (TopTaskbarArea.Width - 720) / 2);
    }

    [StaFact]
    public void TheEnglishMessages_ReadTheirLiterals()
    {
        // The two messages the growth tests measure, pinned as English literals (a T(key)
        // expectation would pass on a raw key).
        Check.That(CaregiverAlertWindow.MessageFor(StringTable.EmbeddedEnglish, CaregiverAlertCondition.SustainedStruggle))
            .IsEqualTo("The user has been struggling continuously — consider checking in.");
        Check.That(CaregiverAlertWindow.MessageFor(StringTable.EmbeddedEnglish, CaregiverAlertCondition.SustainedPoorTracking)
            .StartsWith("Eye tracking has been unreliable for the last minute", System.StringComparison.Ordinal)).IsTrue();
    }
}
