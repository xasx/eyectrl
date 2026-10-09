namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// D11 (T08): the pinned note's ✕ ✎ ↕ buttons are glyph-only discs — a drawn label would announce
/// as its own symbol — so each peer-bearing TextBlock carries the real name, and the caption is
/// the overlays surface's key.
///
/// <para>
/// The window's canvas is drawn from its <c>Loaded</c> handler, and this test raises that event
/// directly: nothing is shown, no HWND is created, and the user's input surface is untouched.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteWindowNameTests
{
    [StaFact]
    public void PinnedNote_DrawsTheKeyedCaption_AndNamesItsGlyphButtons()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("t08-note");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        var texts = TextBlocks((Canvas)window.Content).ToList();

        Check.WithCustomMessage("The note must have drawn its title bar.")
             .That(texts.Select(t => t.Text)).Contains("PINNED NOTE");

        var named = texts
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetName(t)))
            .ToDictionary(t => t.Text, AutomationProperties.GetName);
        Check.WithCustomMessage("The ✕ button must publish 'Close note', not its glyph.")
             .That(named["✕"]).IsEqualTo("Close note");
        Check.WithCustomMessage("The ✎ button must publish 'Edit note', not its glyph.")
             .That(named["✎"]).IsEqualTo("Edit note");
        Check.WithCustomMessage("The ↕ button must publish 'Move note', not its glyph.")
             .That(named["↕"]).IsEqualTo("Move note");
    }

    /// <summary>Every TextBlock in the note's drawn tree — the button glyphs sit on the canvas, the
    /// caption inside the title bar's Border.</summary>
    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        if (root is TextBlock text)
        {
            yield return text;
            yield break;
        }

        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                foreach (var descendant in TextBlocks(child))
                    yield return descendant;
        }
        else if (root is Border { Child: { } borderChild })
        {
            foreach (var descendant in TextBlocks(borderChild))
                yield return descendant;
        }
    }
}
