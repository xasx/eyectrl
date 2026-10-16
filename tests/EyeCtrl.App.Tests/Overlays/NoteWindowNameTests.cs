namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;

/// <summary>
/// D11 (T08) + sticky notes P1 D3/D7: the pinned note's glyph-only discs — a drawn label would
/// announce as its own symbol — so each peer-bearing TextBlock carries the real name, and the
/// caption is the overlays surface's key. In Edit the three slots are re-keyed: ✓ Done in slot 0
/// and ⌨ Show keyboard in slot 1 (P1 D7); the editor itself is a TextBlock plus a static end
/// marker, NOT a TextBox (P1 D3), and no path ever makes the window focusable.
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

    [StaFact]
    public void EditState_ShowsDoneAndShowKeyboard_Names_NotTheGlyphs()
    {
        // P1 D7's slot table: 0 ✓ Done, 1 ⌨ Show keyboard, 2 empty. The UIA names are the
        // glyphs' only voice.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("p1-note");
        window.SetState(NoteState.Edit);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        var texts = TextBlocks((Canvas)window.Content).ToList();
        var named = texts
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetName(t)))
            .ToDictionary(t => t.Text, AutomationProperties.GetName);

        Check.WithCustomMessage("The ✓ button must publish 'Done', not its glyph.")
             .That(named["✓"]).IsEqualTo("Done");
        Check.WithCustomMessage("The ⌨ button must publish 'Show keyboard', not its glyph.")
             .That(named["⌨"]).IsEqualTo("Show keyboard");
    }

    [StaFact]
    public void EditState_DrawsTextPlusAnEndMarker_AndNeverATextBox()
    {
        // P1 D3: the editor is not a TextBox. The caret is always at the end in v1, so Edit shows
        // the note text followed by a static end marker, and the OS-focus path is gone entirely.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("p1-marker");
        window.SetState(NoteState.Edit);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        window.SetText("draft text");

        var all = Descendants<System.Windows.UIElement>((Canvas)window.Content).ToList();

        Check.WithCustomMessage("the editor must not be a TextBox (P1 D3)")
            .That(all.OfType<TextBox>()).IsEmpty();
        Check.WithCustomMessage("the draft must be visible as plain text")
            .That(all.OfType<TextBlock>().Any(t => t.Text == "draft text")).IsTrue();
        // The static end marker: a thin accent-filled bar where a caret would blink.
        Check.WithCustomMessage("Edit must draw the static end marker (P1 D3)")
            .That(all.OfType<System.Windows.Shapes.Rectangle>()
                .Any(r => ReferenceEquals(r.Fill, EyeCtrl.Controls.Theme.Tokens.BrushAccent))).IsTrue();
    }

    [Fact]
    public void TheNoteWindowSource_NeverMakesTheWindowFocusable()
    {
        // P1 D3: "The note window never becomes focusable: no SetOverlayInteraction(...,
        // focusable: true) remains for notes." A source pin — the base class owns the one call
        // that exists, and it is focusable: false.
        var file = SourceScanner.ScannedFiles()
            .First(f => f.Relative.Replace('\\', '/').EndsWith("NoteWindow.xaml.cs", System.StringComparison.Ordinal));

        Check.WithCustomMessage(
                "A focusable: true call crept back into NoteWindow. The note window is " +
                "WS_EX_NOACTIVATE for life: typing routes through NoteManager.Apply, and a " +
                "focusable window is how eye-typed text landed in other windows (P1 D3).")
            .That(file.Text.Contains("focusable: true")).IsFalse();
        Check.That(file.Text.Contains("FocusEditor")).IsFalse();
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : class
    {
        if (root is T match) yield return match;
        switch (root)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                    foreach (var descendant in Descendants<T>(child))
                        yield return descendant;
                break;
            case Border { Child: { } borderChild }:
                foreach (var descendant in Descendants<T>(borderChild))
                    yield return descendant;
                break;
            case ContentControl { Content: DependencyObject content }:
                foreach (var descendant in Descendants<T>(content))
                    yield return descendant;
                break;
        }
    }
}
