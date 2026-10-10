namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// One Settings sidebar tab, rebuilt element for element and measured at 2.0 — the geometry the
/// <c>tab156</c> class and <see cref="EyeCtrl.App.Tests.SettingsTabLabelFitTests"/> share.
///
/// <para>
/// <see cref="SettingsWindow"/> cannot be constructed here (it takes eight non-optional
/// dependencies), so the tab's tree is rebuilt the way <c>SettingsWindow.BuildTabButtons</c> and
/// the XAML around it build it: the root two-column grid, the sidebar's Auto/Star rows, the close
/// border that sizes row 0, the <c>TabButtonPanel</c>'s Margin 8, the tab border's Margin 4 and the
/// inner horizontal panel's Margin 16,0,16,0. The column width is read from SettingsWindow.xaml,
/// the label type from the live tokens and the icon size from the production expression, so none
/// of them is restated. Since T09 it is laid out in <see cref="FitHost"/> at 2.0.
/// </para>
/// </summary>
internal static class SidebarTabFit
{
    /// <summary>
    /// The overhang accepted past the tab's own highlight: the tab border's Margin is 4 per side,
    /// so 8 is "still within the gutter between two tabs' highlights". "Quick Actions" overhangs
    /// by 5.86 today.
    /// </summary>
    internal const double ToleratedOverhang = 8.0;

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>One measured tab.</summary>
    internal sealed record Result(double LabelRight, double BorderRight, double ColumnWidth, string Face);

    /// <summary>The sidebar column width, from the root grid's ColumnDefinitions in SettingsWindow.xaml.</summary>
    internal static double ColumnWidth() =>
        SidebarColumnWidth(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml")));

    /// <summary>
    /// The first column's width of the root <c>Grid</c> in <paramref name="xaml"/>. A wrong reading
    /// fails GREEN (the Star content column is hundreds wide), so every step throws on a shape it
    /// does not understand.
    /// </summary>
    internal static double SidebarColumnWidth(string xaml)
    {
        var root = XDocument.Parse(xaml).Root
            ?? throw new InvalidOperationException("SettingsWindow.xaml has no root element.");
        var grid = root.Element(Wpf + "Grid")
            ?? throw new InvalidOperationException(
                "SettingsWindow.xaml's root no longer holds a <Grid> directly; redo the column extraction.");
        var columns = grid.Attribute("ColumnDefinitions")
            ?? throw new InvalidOperationException(
                "The root <Grid> no longer carries a ColumnDefinitions attribute; read the columns from "
                + "wherever they moved — never drop this, or the guard measures against nothing.");
        var first = columns.Value.Split(',')[0].Trim();
        if (!double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
            throw new InvalidOperationException(
                $"The sidebar column is \"{first}\", not an absolute width; the fit arithmetic no longer applies.");
        return width;
    }

    /// <summary>Every tab's label key → icon, from the production <c>_tabDefs</c> initializer.</summary>
    internal static IReadOnlyDictionary<string, string> IconsByKey() =>
        SettingsTabDriftTests.RenderedTabDefs().ToDictionary(t => t.LabelKey, t => t.Icon, StringComparer.Ordinal);

    /// <summary>
    /// The icon a key's tab draws. A key outside the tab table (an over-long probe) takes the
    /// widest-drawing icon, so a synthetic text is measured in the tightest slot.
    /// </summary>
    internal static string IconFor(string key)
    {
        var icons = IconsByKey();
        return icons.TryGetValue(key, out var icon)
            ? icon
            : icons.Values.MaxBy(i => Measure(i, "x").LabelRight)!;
    }

    /// <summary>Lays out one tab at 2.0 and returns the label's and the tab border's right edges.</summary>
    internal static Result Measure(string icon, string label)
    {
        const double windowWidth = 1600;
        const double windowHeight = 900;
        var columnWidth = ColumnWidth();
        var iconSize = SettingsWindow.TabContainerHeight * ContainerRatio.Medium;

        var outer = new Grid { Width = windowWidth, Height = windowHeight };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidth) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Grid();
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(sidebar, 0);
        outer.Children.Add(sidebar);

        var close = new Border
        {
            Height = SettingsWindow.TabContainerHeight,
            Width = SettingsWindow.TabContainerHeight,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
        };
        Grid.SetRow(close, 0);
        sidebar.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(panel, 1);
        sidebar.Children.Add(panel);

        var iconText = new TextBlock
        {
            Text = icon,
            FontSize = iconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = Tokens.FontSizeTitle,
            FontWeight = Tokens.FontWeightMedium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border
        {
            Height = SettingsWindow.TabContainerHeight,
            Margin = new Thickness(4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 16, 0),
                Children = { iconText, labelText },
            },
        };
        panel.Children.Add(border);

        // FontFamily is inherited; OverlayWindow sets it on the window, the host sets it here.
        using var host = FitHost.Open(outer, family: Tokens.FontStandard);

        double Right(FrameworkElement e) => e.TransformToAncestor(outer).Transform(new Point(e.ActualWidth, 0)).X;
        return new Result(Right(labelText), Right(border), columnWidth, FitText.ResolvedFace(labelText));
    }
}
