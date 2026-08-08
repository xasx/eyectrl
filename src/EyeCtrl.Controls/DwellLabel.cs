namespace EyeCtrl.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;

/// <summary>
/// A non-interactive label helper used by composite dwell controls to display a
/// descriptive heading above their interactive elements.
/// </summary>
/// <remarks>
/// <see cref="DwellLabel"/> is not itself a <see cref="DwellControl"/> — it has no
/// dwell target. Composites call <see cref="Attach"/> / <see cref="Detach"/> directly
/// to manage its lifetime alongside their other visual elements.
/// </remarks>
public class DwellLabel
{
    /// <summary>Gets the <see cref="TextBlock"/> that represents this label in the WPF visual tree.</summary>
    public TextBlock Root { get; }

    /// <summary>
    /// Creates a <see cref="DwellLabel"/> with the specified text.
    /// </summary>
    /// <param name="text">The label text to display.</param>
    public DwellLabel(string text)
    {
        Root = new TextBlock
        {
            Text = text,
            Foreground = Tokens.BrushTextSecondary,
            FontSize = Tokens.FontSizeTitle,
            Margin = new Thickness(0, 8, 0, 2)
        };
    }

    /// <summary>Adds the label's <see cref="Root"/> to <paramref name="parent"/>.</summary>
    /// <param name="parent">The panel to add the label to.</param>
    public void Attach(Panel parent) => parent.Children.Add(Root);

    /// <summary>Removes the label's <see cref="Root"/> from <paramref name="parent"/>.</summary>
    /// <param name="parent">The panel that currently contains the label.</param>
    public void Detach(Panel parent) => parent.Children.Remove(Root);
}
