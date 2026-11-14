namespace EyeCtrl.App.Overlays;

using System.Windows;

/// <summary>
/// The splash card's fixed geometry (spec §4) — theme-independent layout constants shared by the
/// window XAML (via x:Static) and the fit gates, so the two cannot drift. All values in DIP.
/// Public because XAML's StaticExtension resolves public members only. The card is fixed-size on
/// purpose: the no-tracker sentence's row is always reserved so showing it never reflows.
/// </summary>
public static class SplashLayout
{
    public const double CardWidth = 440;
    public const double CardHeight = 336;
    public const double SidePadding = 36;
    public const double InnerWidth = CardWidth - 2 * SidePadding;   // 368
    public const double PaddingTop = 28;
    public const double PaddingBottom = 28;
    public const double MarkSize = 150;
    public const double GapMarkToTitle = 12;
    public const double GapTitleToSentence = 8;                     // lives BELOW the title, outside the 48 DIP row
    public const double SentenceRowHeight = 48;                     // two lines
    public const double TileCornerRadiusDip = 37.5;                 // half the mark's 75 DIP centre — the icon tile's radius (spec §4.1)

    public static Thickness CardPadding => new(SidePadding, PaddingTop, SidePadding, PaddingBottom);

    /// <summary>The icon tile's corner radius as a <see cref="CornerRadius"/> for the XAML (a raw
    /// literal there fails RawGeometryLiteralTests; x:Static cannot hand a raw double to the
    /// CornerRadius property — same wrapper rule as the GridLength rows below).</summary>
    public static CornerRadius TileCornerRadius => new(TileCornerRadiusDip);

    /// <summary>
    /// The mark's row as a <see cref="GridLength"/> for the window XAML: RowDefinition.Height is a
    /// GridLength and x:Static cannot hand it a raw double (System.Xaml only converts strings), so
    /// the row sizes ride these wrappers — still sourced from the constants above, never re-typed.
    /// </summary>
    public static GridLength MarkRowLength => new(MarkSize);

    /// <summary>The reserved sentence row as a <see cref="GridLength"/> (see <see cref="MarkRowLength"/>).</summary>
    public static GridLength SentenceRowLength => new(SentenceRowHeight);
}
