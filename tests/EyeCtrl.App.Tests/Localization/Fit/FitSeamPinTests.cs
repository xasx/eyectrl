namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.IO;

/// <summary>
/// T09 review-01 R2/R3: the fit gates measure production through SEAMS — <c>Prepare</c> for the
/// caregiver alert, <c>BuildModalButton</c> for the modal buttons, <c>BuildTabFace</c> for the
/// sidebar tabs — because the user's own path cannot run headless (<c>ShowCaregiverAlert</c> calls
/// <c>Show()</c>; <c>SettingsWindow</c> needs eight dependencies). A seam is only worth measuring if
/// the user's path goes through it, so these pins read the path's body and fail when it bypasses
/// the seam (review mutants A5, A6 and A14's shape), the same call-site technique the repo uses for
/// <c>DwellArcButton.Create</c>. Comments are stripped first, so a mention in prose cannot satisfy a
/// pin.
/// </summary>
public class FitSeamPinTests
{
    private static string Source(params string[] parts) =>
        SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            [SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", .. parts])));

    /// <summary>The body of the member whose declaration starts with <paramref name="signature"/>.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"`{signature}` is no longer declared; repoint this pin, do not delete it.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.WithCustomMessage($"The `{signature}` body is unterminated.").That(end).IsStrictlyGreaterThan(start);
        return source[start..end];
    }

    [Fact]
    public void ShowCaregiverAlert_SizesAndAnchorsThroughPrepare()
    {
        var body = Body(Source("Overlays", "CaregiverAlertWindow.xaml.cs"), "public void ShowCaregiverAlert(");
        Check.WithCustomMessage(
                "ShowCaregiverAlert no longer calls Prepare(…): the class-C growth and the bottom-edge "
                + "anchor the C-alert gate measures would not happen on screen.")
            .That(body).Contains("Prepare(");
        Check.WithCustomMessage("ShowCaregiverAlert sets the message itself, bypassing Prepare's sizing.")
            .That(body).Not.Contains("Message.Text");
        Check.WithCustomMessage("ShowCaregiverAlert anchors itself, bypassing Prepare.")
            .That(body).Not.Contains("Anchor(");
    }

    [Fact]
    public void TheAlertsConstructor_ReAnchorsOnSizeChanged()
    {
        var body = Body(Source("Overlays", "CaregiverAlertWindow.xaml.cs"), "public CaregiverAlertWindow(");
        Check.WithCustomMessage("The SizeChanged backstop no longer re-anchors the alert's bottom edge.")
            .That(body).Contains("SizeChanged +=");
        Check.That(body).Contains("Anchor(");
    }

    [Fact]
    public void MakeModalButton_BuildsTheFaceTheGateMeasures()
    {
        var body = Body(Source("Views", "SettingsWindow.xaml.cs"), "private Border MakeModalButton(");
        Check.WithCustomMessage(
                "MakeModalButton no longer goes through BuildModalButton, so the modal170/modal460 "
                + "boxes the gate measures are not the ones on screen.")
            .That(body).Contains("BuildModalButton(");
        Check.WithCustomMessage("MakeModalButton builds a chrome button of its own, beside the measured face.")
            .That(body).Not.Contains("MakeChromeButton(");
    }

    [Fact]
    public void BuildTabButtons_BuildsEveryTabThroughTheMeasuredFace()
    {
        var body = Body(Source("Views", "SettingsWindow.xaml.cs"), "private void BuildTabButtons(");
        Check.WithCustomMessage(
                "BuildTabButtons no longer builds its tabs through BuildTabFace, so the tab156 gate "
                + "measures a tab production does not draw.")
            .That(body).Contains("BuildTabFace(");
        Check.WithCustomMessage("BuildTabButtons builds a tab Border of its own beside the measured face.")
            .That(body).Not.Contains("new Border");
    }
}
