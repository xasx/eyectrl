namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Where the paused strip, its state label and its Resume disc go (B2a). Pure: logical (DIP)
/// rectangles in, logical rectangles out, so the placement acceptance runs headless at any DPI —
/// the window and the tests call the same function.
///
/// <para>
/// <b>The rules.</b> The strip runs the whole docked edge. The disc sits MID-edge — never in a
/// corner — inset inward past the toolbar band's rest rectangle (the band has its own Play and the
/// two must never overlap) by <see cref="ClearanceDip"/>. A keep-out (the right-edge out slot) that
/// the disc would touch pushes it further inward until it clears. The label goes on the far side of
/// the disc from the edge (beside it on a side edge), so neither ever covers the band.
/// </para>
/// </summary>
internal static class ResumeStripLayout
{
    /// <summary>The strip's thickness along the docked edge, in DIP (the ticket's floor).</summary>
    internal const double StripThicknessDip = 12;

    /// <summary>The Resume disc's radius in DIP: the r50 every gaze exit uses.</summary>
    internal const double DiscRadiusDip = UIConstants.ButtonRadius;

    /// <summary>
    /// Clearance in DIP between the disc's edge and the band, a keep-out, or the label. Covers the
    /// DwellArcButton's 4 DIP halo with room to spare.
    /// </summary>
    internal const double ClearanceDip = 16;

    /// <summary>The state label's fixed box. Fixed so the fit measurer can bound it.</summary>
    internal const double LabelWidthDip = 200;

    /// <summary>The state label's box height.</summary>
    internal const double LabelHeightDip = 44;

    /// <summary>The step in DIP by which a keep-out pushes the disc inward.</summary>
    private const double KeepOutStepDip = 4;

    /// <summary>How far, in DIP, a keep-out may push the disc before the layout gives up on it.</summary>
    private const double MaxKeepOutPushDip = 600;

    /// <summary>One computed layout, all in screen-logical DIP.</summary>
    /// <param name="Window">The window's bounds: the strip, the disc and the label, with the band area between.</param>
    /// <param name="Strip">The strip along the edge.</param>
    /// <param name="DiscCentre">The disc's centre.</param>
    /// <param name="Label">The label's box.</param>
    internal readonly record struct Result(Rect Window, Rect Strip, Point DiscCentre, Rect Label)
    {
        /// <summary>The disc's bounding square.</summary>
        internal Rect Disc => new(
            DiscCentre.X - DiscRadiusDip, DiscCentre.Y - DiscRadiusDip, DiscRadiusDip * 2, DiscRadiusDip * 2);
    }

    /// <summary>Computes the layout for <paramref name="edge"/>.</summary>
    /// <param name="workArea">The work area in logical DIP — the space the toolbar docks in.</param>
    /// <param name="edge">The docked edge ("Top"/"Bottom"/"Left"/"Right", any case); anything else is Top.</param>
    /// <param name="band">The toolbar band's rest rectangle in logical DIP, or null when unknown.</param>
    /// <param name="keepOuts">Rectangles in logical DIP the disc must stay clear of.</param>
    internal static Result Compute(Rect workArea, string? edge, Rect? band, IReadOnlyList<Rect> keepOuts)
    {
        var key = edge?.ToLowerInvariant() switch
        {
            "bottom" => "bottom",
            "left" => "left",
            "right" => "right",
            _ => "top",
        };

        // How deep the band reaches inward from the edge; the disc starts past it.
        // A band that does not sit against this edge (a floating toolbar, a rect captured before
        // layout) says nothing about it: only a depth inside the edge's first third counts.
        var bandDepth = band is { IsEmpty: false } b ? BandDepth(workArea, key, b) : 0;
        var across = key is "left" or "right" ? workArea.Width : workArea.Height;
        if (bandDepth < 0 || bandDepth > across / 3) bandDepth = 0;
        var inset = Math.Max(StripThicknessDip, bandDepth) + ClearanceDip + DiscRadiusDip;

        var push = 0.0;
        var centre = DiscCentreAt(workArea, key, inset);
        while (push < MaxKeepOutPushDip && TouchesAny(centre, keepOuts))
        {
            push += KeepOutStepDip;
            centre = DiscCentreAt(workArea, key, inset + push);
        }

        var strip = key switch
        {
            "bottom" => new Rect(workArea.Left, workArea.Bottom - StripThicknessDip, workArea.Width, StripThicknessDip),
            "left" => new Rect(workArea.Left, workArea.Top, StripThicknessDip, workArea.Height),
            "right" => new Rect(workArea.Right - StripThicknessDip, workArea.Top, StripThicknessDip, workArea.Height),
            _ => new Rect(workArea.Left, workArea.Top, workArea.Width, StripThicknessDip),
        };

        var labelGap = DiscRadiusDip + ClearanceDip;
        var label = key switch
        {
            // On a horizontal edge the label sits on the inward side of the disc, centred on it.
            "bottom" => new Rect(centre.X - LabelWidthDip / 2, centre.Y - labelGap - LabelHeightDip, LabelWidthDip, LabelHeightDip),
            // On a side edge it sits beside the disc, inward, so it never reaches back over the band.
            "left" => new Rect(centre.X + labelGap, centre.Y - LabelHeightDip / 2, LabelWidthDip, LabelHeightDip),
            "right" => new Rect(centre.X - labelGap - LabelWidthDip, centre.Y - LabelHeightDip / 2, LabelWidthDip, LabelHeightDip),
            _ => new Rect(centre.X - LabelWidthDip / 2, centre.Y + labelGap, LabelWidthDip, LabelHeightDip),
        };

        var disc = new Rect(centre.X - DiscRadiusDip, centre.Y - DiscRadiusDip, DiscRadiusDip * 2, DiscRadiusDip * 2);
        var window = Rect.Union(Rect.Union(strip, disc), label);
        // The window spans the whole edge so the strip is one element; its far side stops just past
        // the label/disc — everything between is transparent and click-through.
        window = key switch
        {
            "bottom" or "top" => new Rect(workArea.Left, window.Top, workArea.Width, window.Height),
            _ => new Rect(window.Left, workArea.Top, window.Width, workArea.Height),
        };

        return new Result(window, strip, centre, label);
    }

    /// <summary>
    /// The right-edge out slot and the back slot below it, as squares in logical DIP: the shared exit
    /// anchor every non-keyboard mode places its exits on (<see cref="ScreenGeometry"/>), r50 each.
    /// </summary>
    internal static IReadOnlyList<Rect> OutSlotKeepOuts(double screenWidthDip, double screenHeightDip)
    {
        var x = screenWidthDip
            - (ScreenGeometry.SharedRightEdgeInsetLogical + UIConstants.ButtonRadius
               + ScreenGeometry.EdgeButtonWindowMarginLogical);
        var y = screenHeightDip / 2;
        var r = UIConstants.ButtonRadius;
        return
        [
            new Rect(x - r, y - UIConstants.EdgeButtonSlotOffset - r, r * 2, r * 2),
            new Rect(x - r, y + UIConstants.EdgeButtonSlotOffset - r, r * 2, r * 2),
        ];
    }

    private static Point DiscCentreAt(Rect workArea, string key, double inset) => key switch
    {
        "bottom" => new Point(workArea.Left + workArea.Width / 2, workArea.Bottom - inset),
        "left" => new Point(workArea.Left + inset, workArea.Top + workArea.Height / 2),
        "right" => new Point(workArea.Right - inset, workArea.Top + workArea.Height / 2),
        _ => new Point(workArea.Left + workArea.Width / 2, workArea.Top + inset),
    };

    private static double BandDepth(Rect workArea, string key, Rect band) => key switch
    {
        "bottom" => workArea.Bottom - band.Top,
        "left" => band.Right - workArea.Left,
        "right" => workArea.Right - band.Left,
        _ => band.Bottom - workArea.Top,
    };

    private static bool TouchesAny(Point centre, IReadOnlyList<Rect> keepOuts)
    {
        var reach = DiscRadiusDip + ClearanceDip;
        foreach (var k in keepOuts)
        {
            var grown = new Rect(k.X - reach, k.Y - reach, k.Width + reach * 2, k.Height + reach * 2);
            if (grown.Contains(centre)) return true;
        }

        return false;
    }
}
