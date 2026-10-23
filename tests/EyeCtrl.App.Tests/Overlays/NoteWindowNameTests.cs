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

        // P4 D2: the title bar is gone - no caption text at all.
        Check.WithCustomMessage("The note must not draw a title caption any more (P4 D2).")
             .That(texts.Select(t => t.Text)).Not.Contains("PINNED NOTE");

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
    public void EditState_ShowsDoneShowKeyboardAndDiscard_Names_NotTheGlyphs()
    {
        // P1 D7's slot table, final P2 layout (P2 D5/D8): 0 ✓ Done, 1 ⌨ Show keyboard (while
        // hidden), 2 ↩ Discard — one slot from Done. The UIA names are the glyphs' only voice.
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
        Check.WithCustomMessage("The ↩ button must publish 'Discard changes', not its glyph.")
             .That(named["↩"]).IsEqualTo("Discard changes");
    }

    [StaFact]
    public void PendingUndo_RendersTheChipText_AndNamesItsUndoDisc()
    {
        // P2 D5: the window re-renders in place as a chip. The chip text is the manager's
        // localized string; the ↺ Undo disc is the chip's only control and speaks its UIA name.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("p2-chip");
        window.SetState(NoteState.PendingUndo);
        window.SetChipText("Note removed · Undo");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        var texts = TextBlocks((Canvas)window.Content).ToList();

        Check.WithCustomMessage("the chip must show the text the manager set")
            .That(texts.Select(t => t.Text)).Contains("Note removed · Undo");

        var named = texts
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetName(t)))
            .ToDictionary(t => t.Text, AutomationProperties.GetName);
        Check.WithCustomMessage("The ↺ button must publish 'Undo', not its glyph.")
             .That(named["↺"]).IsEqualTo("Undo");

        // And the chip carries none of the card controls.
        Check.WithCustomMessage("the chip must not show the card's control glyphs")
            .That(texts.Where(t => t.Visibility == Visibility.Visible)
                .Select(t => t.Text).Any(g => g is "✓" or "⌨" or "↩" or "✕" or "✎" or "↕")).IsFalse();
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

    [StaFact]
    public void EditToDisplayToEdit_KeepsTheRightGlyphsVisible_AtEveryStep()
    {
        // Fixup B1: every note is CREATED in Edit (before Show), so a canvas that drew only the
        // current state's buttons could never gain the Display set after the first Done —
        // SetState(Display) un-hid buttons that did not exist while their dwell targets went
        // live. Both sets are drawn once and the state picks the visible one; this transition
        // pins the visible glyphs AND their UIA names at every step.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new NoteWindow("p1-transition");
        window.SetState(NoteState.Edit);
        // The manager drives ⌨ visibility from the probe (hidden → drawn); the window alone
        // defaults it to hidden, so each Edit phase sets it the way an entry does.
        window.SetKeyboardControlVisible(true);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        AssertVisibleButtons(window, expectEditSet: true);
        window.SetState(NoteState.Display);
        AssertVisibleButtons(window, expectEditSet: false);
        window.SetState(NoteState.Edit);
        window.SetKeyboardControlVisible(true);
        AssertVisibleButtons(window, expectEditSet: true);
    }

    private static void AssertVisibleButtons(NoteWindow window, bool expectEditSet)
    {
        var texts = TextBlocks((Canvas)window.Content).ToList();
        var visible = texts.Where(t => t.Visibility == Visibility.Visible).ToList();
        var visibleGlyphs = visible.Select(t => t.Text).ToList();
        var visibleNames = visible
            .Select(AutomationProperties.GetName)
            .Where(n => !string.IsNullOrEmpty(n)).ToList();

        if (expectEditSet)
        {
            Check.WithCustomMessage("Edit must show its own glyphs, got: " + string.Join("|", visibleGlyphs))
                .That(visibleGlyphs).Contains("✓", "⌨", "↩");
            Check.WithCustomMessage("Edit must not show the Display glyphs")
                .That(visibleGlyphs.Any(g => g is "✕" or "✎" or "↕" or "↺")).IsFalse();
            Check.WithCustomMessage("Edit's buttons must speak their UIA names")
                .That(visibleNames).Contains("Done", "Show keyboard", "Discard changes");
        }
        else
        {
            Check.WithCustomMessage("Display must show its own glyphs, got: " + string.Join("|", visibleGlyphs))
                .That(visibleGlyphs).Contains("✕", "✎", "↕");
            Check.WithCustomMessage("Display must not show the Edit glyphs")
                .That(visibleGlyphs.Any(g => g is "✓" or "⌨")).IsFalse();
            Check.WithCustomMessage("Display's buttons must speak their UIA names")
                .That(visibleNames).Contains("Close note", "Edit note", "Move note");
        }
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
