namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// G-A3 (E3), the show half: one INFO line per magnifier session carrying the picture's provenance
/// (fresh/reused/none), the capture's duration and size, a content signature (luma mean±σ) and
/// whether the picture is the same as the previous capture.
///
/// <para>
/// The line is built by <c>BuildShownLine</c>, which <c>Show</c> logs before <c>base.Show()</c>.
/// Testing the builder directly is the ticket's headless shape: the window must never be shown in a
/// test (an on-screen <c>Show()</c> would seize the owner's display). The sameAsPrevious check is
/// the field discriminator for a STALE picture, so it is pinned in both directions.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ManualSelectionMagnifierLogTests
{
    private static readonly PhysicalPoint ViewCenter = new(960, 540);

    [StaFact]
    public void ShownLine_CarriesFreshPicture_CaptureMs_SizeAndLuma()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            window.Capture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(680, 128), 620, 200, 680);

            window.UpdateCenter(ViewCenter, 170, 2.0);
            var line = window.BuildShownLine(ViewCenter, 170, recapture: true);

            Check.WithCustomMessage($"the shown line is incomplete: {line}")
                .That(line)
                .Contains("Magnifier shown: P=(960,540) view=(960,540) mag=2 viewDip=680 recapture=True")
                .And.Contains("picture=fresh")
                .And.Contains("captured=680x680px")
                .And.Contains("luma=128")
                .And.Contains("sameAsPrevious=False");
            Check.WithCustomMessage($"captureMs is missing or not a number: {line}")
                .That(System.Text.RegularExpressions.Regex.IsMatch(line, "captureMs=\\d+")).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ShownLine_StaysInvariant_UnderTheShippedGermanTable()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            window.Capture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(680, 128), 620, 200, 680);

            // F1: this is a machine-read diagnostic line, never screen text. On a German instance
            // (the shipped table, all surfaces switched on) it must still say mag=1.5, not mag=1,5 -
            // the A4 session readout parses the line invariant.
            ShippedLanguageRecoveryTests.ActivateShippedGerman();
            window.UpdateCenter(ViewCenter, 170, 1.5);
            var line = window.BuildShownLine(ViewCenter, 170, recapture: true);

            Check.WithCustomMessage($"the shown line is culture-bound: {line}")
                .That(line).Contains("mag=1.5");
            Check.That(line).Not.Contains("mag=1,5");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
            window.Close();
        }
    }

    [StaFact]
    public void SameAsPrevious_IsTrueForIdenticalCaptures_AndFalseForADifferentOne()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            window.Capture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(680, 128), 620, 200, 680);
            window.UpdateCenter(ViewCenter, 170, 2.0);
            var first = window.BuildShownLine(ViewCenter, 170, recapture: true);
            Check.That(first).Contains("sameAsPrevious=False"); // no previous capture yet

            // Same pixels, same size: the field signature of a STALE (not refreshed) picture.
            window.UpdateCenter(ViewCenter, 170, 2.0);
            var same = window.BuildShownLine(ViewCenter, 170, recapture: true);
            Check.WithCustomMessage($"identical captures must read sameAsPrevious=True, saw: {same}")
                .That(same).Contains("sameAsPrevious=True");

            window.Capture = (_, _) =>
                new CaptureResult(ManualSelectionCaptureBeforeShowTests.Solid(680, 200), 620, 200, 680);
            window.UpdateCenter(ViewCenter, 170, 2.0);
            var different = window.BuildShownLine(ViewCenter, 170, recapture: true);
            Check.WithCustomMessage($"a changed picture must read sameAsPrevious=False, saw: {different}")
                .That(different).Contains("sameAsPrevious=False").And.Contains("luma=200");
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void AReusedPicture_ReadsReused_NotFresh()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ManualSelectionOverlayWindow();
        try
        {
            // Back from the action menu: the same shot, recapture:false.
            window.ManualSelectionBrush.ImageSource =
                ManualSelectionCaptureBeforeShowTests.Solid(680, 64);

            window.UpdateCenter(ViewCenter, 170, 2.0, recapture: false);
            var line = window.BuildShownLine(ViewCenter, 170, recapture: false);

            Check.WithCustomMessage($"a reused picture must say so: {line}")
                .That(line).Contains("picture=reused").And.Contains("recapture=False");
        }
        finally
        {
            window.Close();
        }
    }
}
