namespace EyeCtrl.Controls.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// Readability guarantee for dwell labels: a label longer than its control's text room WRAPS
/// (two centered lines) instead of silently clipping mid-word. Found in the 2026-08-17 visual
/// round: every Models download button clipped its label ("Phi-3.5-mini (GPU, DirectML)  2,0 GB
/// [Download]" measured 561 DIP against 360 of text room), and two settings reset buttons
/// shaved their tails. Labels that fit are unaffected — wrap only engages on overflow.
/// </summary>
public class DwellButtonTextWrappingTests
{
    [StaFact]
    public void ButtonLabel_WrapsAndCenters_InsteadOfClipping()
    {
        var button = new DwellButton(
            new FakeDwellRegistrar(), "t", "A long label that will not fit one line", () => { },
            Colors.Black, DwellCategory.Control, 50, 500);

        var text = (TextBlock)button.Content!;

        Check.That(text.TextWrapping).IsEqualTo(TextWrapping.Wrap);
        Check.That(text.TextAlignment).IsEqualTo(TextAlignment.Center);
    }

    [StaFact]
    public void StandaloneLabel_WrapsOnlyWhenAsked()
    {
        // wrap: true — status lines must be fully readable (a gaze user cannot scroll or hover
        // to see clipped text).
        Check.That(new DwellLabel("A long status line", wrap: true).Root.TextWrapping)
            .IsEqualTo(TextWrapping.Wrap);

        // Default off: the box-drawing separator rule's pinned rendering IS the clip —
        // a run of U+2500 cut at the column edge draws as a rule spanning the column,
        // wrapped it would stack into a block of strokes (SettingsContentFitTests).
        Check.That(new DwellLabel("──────").Root.TextWrapping)
            .IsEqualTo(TextWrapping.NoWrap);
    }

}
