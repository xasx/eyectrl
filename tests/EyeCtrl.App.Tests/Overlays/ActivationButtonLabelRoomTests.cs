namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.App.Tests.Localization.Fit;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// M1 (B2 review-02 N1): the activation button's label room ends at the CONTENT clip — the rim's
/// inner edge less <see cref="ActivationButtonFace.FillInset"/>, where both content copies are cut —
/// not at the rim. The clip radius is read off the production face's tree, never restated, so the
/// room and the drawn cut cannot drift apart. Nothing here shows a window: the face is laid out in a
/// plain host, and the A76 gate's own <see cref="FitHost"/> is an invisible off-screen source.
/// </summary>
[Collection("WpfApplication")]
public class ActivationButtonLabelRoomTests
{
    private const string MagnifierKey = "overlays.activationButton.label.magnifier";
    private const string MenuKey = "overlays.activationButton.label.menu";

    private readonly ITestOutputHelper _output;

    public ActivationButtonLabelRoomTests(ITestOutputHelper output) => _output = output;

    private static ActivationButtonFace LaidOutFace(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var face = new ActivationButtonFace();
        face.SetContent(key == MagnifierKey ? ActivationButtonContent.Magnifier : ActivationButtonContent.Menu, text);
        var host = new Grid { Width = ActivationButtonFace.Diameter, Height = ActivationButtonFace.Diameter };
        host.Children.Add(face);
        host.Measure(new Size(ActivationButtonFace.Diameter, ActivationButtonFace.Diameter));
        host.Arrange(new Rect(0, 0, ActivationButtonFace.Diameter, ActivationButtonFace.Diameter));
        host.UpdateLayout();
        return face;
    }

    /// <summary>The radius of the clip the label's copy is actually drawn in, read off the tree.</summary>
    private static double ContentClipRadius(ActivationButtonFace face)
    {
        var clipped = (Grid)face.UpperContent.Parent;
        var clip = (EllipseGeometry)clipped.Clip;
        Check.That(clip.RadiusX).IsEqualTo(clip.RadiusY);
        return clip.RadiusX;
    }

    /// <summary>The label's bottom edge below the disc's centre, in DIP (0 above it).</summary>
    private static double LabelBottomBelowCentre(ActivationButtonFace face)
    {
        var label = face.UpperLabel;
        var bottom = label.TranslatePoint(new Point(0, label.ActualHeight), face).Y;
        return Math.Max(0, bottom - (ActivationButtonFace.Diameter / 2));
    }

    private static double Room(double radius, double belowCentre)
        => (2 * Math.Sqrt((radius * radius) - (belowCentre * belowCentre)))
           - (2 * ActivationButtonFace.LabelInkClearance);

    /// <summary>One-line natural width of <paramref name="text"/> in the label's font, rounded UP to
    /// the device pixel at <paramref name="scale"/> (never more lenient than the drawn text).</summary>
    private static double NaturalWidth(TextBlock like, string text, double scale)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = like.FontFamily,
            FontSize = like.FontSize,
            FontWeight = like.FontWeight,
            FontStyle = like.FontStyle,
            FontStretch = like.FontStretch,
            TextWrapping = TextWrapping.NoWrap,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(like));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(probe.DesiredSize.Width * scale) / scale;
    }

    [StaFact]
    public void TheLabelRoom_IsTheContentClipsChord_AtTheLabelsBottom_LessTheClearance()
    {
        // Mutation: the old radius (Diameter/2 − RimThickness = 48, no FillInset) → the room is the
        // rim's chord, ~5 DIP wider than the clip the label is cut by, red.
        var face = LaidOutFace(MagnifierKey, "Magnifier");
        double radius = ContentClipRadius(face);
        Check.That(radius).IsEqualTo(
            (ActivationButtonFace.Diameter / 2) - ActivationButtonFace.RimThickness - ActivationButtonFace.FillInset);

        double below = LabelBottomBelowCentre(face);
        Check.WithCustomMessage("the label must sit below the centre, or the chord is the full diameter")
            .That(below).IsStrictlyGreaterThan(0.0);

        Check.That(face.LabelRoomDip).IsCloseTo(Room(radius, below), 1e-9);
    }

    [StaTheory]
    [InlineData(MagnifierKey, false, 1.0)]
    [InlineData(MagnifierKey, false, 2.0)]
    [InlineData(MenuKey, false, 1.0)]
    [InlineData(MenuKey, false, 2.0)]
    [InlineData(MagnifierKey, true, 1.0)]
    [InlineData(MagnifierKey, true, 2.0)]
    [InlineData(MenuKey, true, 1.0)]
    [InlineData(MenuKey, true, 2.0)]
    public void TheFourShippedWords_FitTheInsetRoom(string key, bool german, double scale)
    {
        // "Magnifier", "Menu", „Lupe", „Menü" (the shipped tables, the German through the App's own
        // loader on this de-DE machine) at 1.0 and the owner's 2.0; the scale only moves the pixel
        // rounding of the measured width. At 2.0 the A76 gate itself must agree.
        string text = german ? ShippedGerman.Load().T(key) : StringTable.EmbeddedEnglish.T(key);
        string expected = (key, german) switch
        {
            (MagnifierKey, false) => "Magnifier",
            (MenuKey, false) => "Menu",
            (MagnifierKey, true) => "Lupe",
            _ => "Menü",
        };
        Check.That(text).IsEqualTo(expected);

        var face = LaidOutFace(key, text);
        double width = NaturalWidth(face.UpperLabel, text, scale);
        double room = face.LabelRoomDip;
        _output.WriteLine($"{text} @ {scale}: {width:0.##} DIP in {room:0.##} DIP (margin {room - width:0.##})");
        Check.WithCustomMessage($"{text} at {scale}: {width} DIP does not fit {room} DIP")
            .That(width).IsLessOrEqualThan(room);

        if (scale == 2.0)
        {
            var verdict = AppFitMeasurers.Measurers["A76"](key, text);
            _output.WriteLine(verdict.ToString());
            Check.WithCustomMessage(verdict.ToString()).That(verdict.Fits).IsTrue();
        }
    }

    [StaFact]
    public void ALabelThatFitsOnlyTheRimsChord_FailsTheA76Gate()
    {
        // A word grown just past the content clip's room but still inside the rim's: the A76 gate
        // must refuse it, because its outer ink would be cut by the inset clip. Mutation: the old
        // radius in LabelRoomDip → the gate measures the rim's chord and passes it, red.
        var face = LaidOutFace(MagnifierKey, "Magnifier");
        double below = LabelBottomBelowCentre(face);
        double clipRoom = Room(ContentClipRadius(face), below);
        double rimRoom = Room((ActivationButtonFace.Diameter / 2) - ActivationButtonFace.RimThickness, below);

        string text = "Magnifier";
        for (int i = 0; i < 40 && FitText.NaturalWidth(face.UpperLabel, text) <= clipRoom; i++)
            text += "i";
        double width = FitText.NaturalWidth(face.UpperLabel, text);
        _output.WriteLine($"\"{text}\": {width:0.##} DIP; clip room {clipRoom:0.##}, rim room {rimRoom:0.##}");
        Check.WithCustomMessage("the probe word must land between the two rooms, or the test proves nothing")
            .That(width > clipRoom && width <= rimRoom).IsTrue();

        var verdict = AppFitMeasurers.Measurers["A76"](MagnifierKey, text);
        _output.WriteLine(verdict.ToString());
        Check.WithCustomMessage(verdict.ToString()).That(verdict.Fits).IsFalse();
    }
}
