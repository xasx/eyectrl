namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// The Track Status quality display is split in two: <see cref="TrackStatusWindow.QualityBrush"/>
/// picks the colour from the enum key, <see cref="TrackStatusWindow.QualityWord"/> supplies the
/// English word. One test per quality pins both halves, so swapping two brushes or two words
/// cannot pass unnoticed.
/// </summary>
public class TrackStatusQualityMapTests
{
    [Fact]
    public void Good_ShowsSuccessBrushAndGoodWord()
    {
        Check.That(TrackStatusWindow.QualityBrush(DistanceQuality.Good)).IsSameReferenceAs(Tokens.BrushAccentSuccess);
        Check.That(TrackStatusWindow.QualityWord(DistanceQuality.Good)).IsEqualTo("Good");
    }

    [Fact]
    public void Fair_ShowsWarningBrushAndFairWord()
    {
        Check.That(TrackStatusWindow.QualityBrush(DistanceQuality.Fair)).IsSameReferenceAs(Tokens.BrushAccentWarning);
        Check.That(TrackStatusWindow.QualityWord(DistanceQuality.Fair)).IsEqualTo("Fair");
    }

    [Fact]
    public void Poor_ShowsDangerBrushAndPoorWord()
    {
        Check.That(TrackStatusWindow.QualityBrush(DistanceQuality.Poor)).IsSameReferenceAs(Tokens.BrushAccentDanger);
        Check.That(TrackStatusWindow.QualityWord(DistanceQuality.Poor)).IsEqualTo("Poor");
    }

    [Fact]
    public void Unknown_ShowsDimBrushAndUnknownWord()
    {
        Check.That(TrackStatusWindow.QualityBrush(DistanceQuality.Unknown)).IsSameReferenceAs(Tokens.BrushTextDim);
        Check.That(TrackStatusWindow.QualityWord(DistanceQuality.Unknown)).IsEqualTo("Unknown");
    }
}
