namespace EyeCtrl.App.Tests.Settings;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09 PR 2: the combo box's wrapped rows keep the 40 DIP dwell floor, and English does not move.
///
/// <para>
/// The pseudo-locale wrapped three Settings combos (Cursor filter, ring style, dictionary
/// language) onto a second row 112 DIP below the first — 12–16 DIP between two stacked 100 DIP
/// hit circles, against the 40 DIP floor every other adjacent pair keeps. English never wraps on
/// the user's display, so the fix (<c>DwellComboBox.OptionMargin</c>: 14 above, 22 below) is a
/// <b>language-driven re-layout inside Settings; English targets unchanged</b>: a single-row combo
/// stays centred 71 DIP into its 150 DIP slot, exactly where the old 0/8 margin put it. Both pins
/// are on ARRANGED geometry of the real row (<see cref="SettingsWindow.CreateControl"/>), hosted
/// at 2.0.
/// </para>
/// </summary>
public class ComboRowPitchTests
{
    private const double RadiusDip = SettingsWindow.ControlBtnRadius;

    /// <summary>The 40 DIP floor: the swatch grid's pitch less one circle, as the reachability gate derives it.</summary>
    private static double FloorDip =>
        DwellColorPicker.SwatchHeight + (2 * DwellColorPicker.SwatchVerticalMargin) - (2 * SettingsWindow.ColorSwatchRadius);

    /// <summary>Builds one combo row through production and returns each option's centre relative to the row.</summary>
    private static (double RowHeight, List<Point> Centres) Row(string[] options, double columnWidth)
    {
        var spec = new ComboBoxSpec("c_probe", "Probe setting", options, null, () => options[0], _ => { });
        var row = (IDwellComposite)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), RadiusDip, 800, columnWidth);
        var panel = new StackPanel { Width = columnWidth };
        row.Attach(panel);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);

        var rowRoot = (FrameworkElement)panel.Children[0];
        var centres = row.Children
            .Select(c => c.Root.TransformToAncestor(rowRoot).TransformBounds(new Rect(c.Root.RenderSize)))
            .Select(b => new Point(b.Left + b.Width / 2, b.Top + b.Height / 2))
            .ToList();
        return (rowRoot.ActualHeight, centres);
    }

    [StaTheory]
    [InlineData(1424.0)]   // two-column, the reference display's content column
    [InlineData(416.0)]    // stacked, the narrowest supported column
    public void ASingleRowCombo_StaysCentred71DipIntoIts150DipSlot(double columnWidth)
    {
        var (rowHeight, centres) = Row(["One", "Two"], columnWidth);
        Check.WithCustomMessage("The probe combo wrapped; this pin is about a single row.")
            .That(centres.Select(c => c.Y).Distinct().Count()).IsEqualTo(1);

        // Two-column: the row IS the 150 slot. Stacked: the slot sits under the name block, so the
        // centre is measured from the slot's top — the row's bottom less 150.
        var slotTop = rowHeight - SettingsWindow.LabelledRowMinHeight;
        if (columnWidth >= SettingsWindow.TwoColumnMinimumWidth)
            Check.WithCustomMessage($"The row is {rowHeight} tall; a single-row combo must not grow its 150 DIP row.")
                .That(rowHeight).IsEqualTo(SettingsWindow.LabelledRowMinHeight);
        Check.WithCustomMessage("A single-row combo's dwell centre moved: English targets must not move.")
            .That(centres[0].Y - slotTop).IsCloseTo(71.0, 1e-6);
    }

    [StaFact]
    public void AWrappedCombo_KeepsTheFloorBetweenItsRows()
    {
        var options = new[] { "Smoothed (Legacy)", "One Euro filter", "Weighted sandwich", "Achromatic reticle", "Bitonal segments" };
        var (_, centres) = Row(options, 416.0);
        var rows = centres.Select(c => c.Y).Distinct().OrderBy(y => y).ToList();
        Check.WithCustomMessage("The probe combo did not wrap at 416 DIP: the floor would be checked on nothing.")
            .That(rows.Count).IsStrictlyGreaterThan(1);

        var tight = new List<string>();
        for (var i = 0; i < centres.Count; i++)
        for (var j = i + 1; j < centres.Count; j++)
        {
            var distance = (centres[i] - centres[j]).Length;
            var clearance = distance - 2 * RadiusDip;
            if (clearance < FloorDip - 0.01) tight.Add($"{i}/{j}: {clearance:0.##} DIP");
        }

        Check.WithCustomMessage($"Wrapped combo options closer than the {FloorDip} DIP floor: " + string.Join(", ", tight))
            .That(tight).IsEmpty();
    }
}
