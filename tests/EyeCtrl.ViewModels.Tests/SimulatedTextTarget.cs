namespace EyeCtrl.ViewModels.Tests;

using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// A stand-in for the app that has focus: it applies EXACTLY what the keyboard sends (the three
/// messages the real <c>KeyboardInteractionDispatcher</c> consumes) to a text buffer with a caret,
/// and remembers which characters the keyboard itself placed. The keyboard is a blind injector with
/// a local mirror, so this is the only oracle that can say whether the mirror still describes the
/// text under the caret.
///
/// <para>
/// Each element is one grapheme (an app's Backspace deletes one grapheme), flagged
/// <c>Placed</c> while the keyboard put it there in the CURRENT editing session. Every event that
/// ends a session — Enter, a focus change, anything that moves the caret or edits behind the
/// keyboard's back — flags every element foreign (<see cref="MarkAllForeign"/>) and bumps
/// <see cref="Boundaries"/>, so the oracle knows when the mirror has to be empty again.
/// </para>
/// </summary>
internal sealed class SimulatedTextTarget
{
    private readonly List<(string Text, bool Placed)> _elements = new();

    public int Caret { get; private set; }
    public int Count => _elements.Count;

    /// <summary>Counts the events that ended an editing session.</summary>
    public int Boundaries { get; private set; }

    /// <summary>The graphemes the last one-shot deletion (a Backspace, Ctrl+Backspace or pick) removed.</summary>
    public int LastDeletedCount { get; private set; }

    /// <summary>What the last pick deleted, as text — compared with the keyboard's CurrentInput.</summary>
    public string LastPickDeletedText { get; private set; } = "";

    /// <summary>The number of KeyActivated/PredictionSelected/Special messages seen since the last reset.</summary>
    public int MessagesSeen { get; private set; }

    public List<KeyActivatedMessage> KeyMessages { get; } = new();

    /// <summary>Violations of the pick contract: a pick may only delete characters the keyboard placed.</summary>
    public List<string> Violations { get; } = new();

    public SimulatedTextTarget(IMessenger messenger)
    {
        messenger.Register<SimulatedTextTarget, KeyActivatedMessage>(this, (t, m) => t.OnKey(m));
        messenger.Register<SimulatedTextTarget, PredictionSelectedMessage>(this, (t, m) => t.OnPick(m));
        messenger.Register<SimulatedTextTarget, SpecialKeyMessage>(this, (t, m) => t.OnSpecial(m));
    }

    /// <summary>Text the app already had before the keyboard touched it.</summary>
    public void SeedForeign(string text)
    {
        _elements.Clear();
        Insert(text, placed: false, atCaret: false);
        Caret = _elements.Count;
    }

    /// <summary>The app moved on behind the keyboard's back (focus change): foreign text, caret at its end.</summary>
    public void FocusMovedToForeignText(string text)
    {
        SeedForeign(text);
        Boundaries++;
    }

    public string Text => string.Concat(_elements.Select(e => e.Text));

    /// <summary>The unbroken run of keyboard-placed text immediately before the caret.</summary>
    public string PlacedTail()
    {
        var start = Caret;
        while (start > 0 && _elements[start - 1].Placed) start--;
        return string.Concat(_elements.Skip(start).Take(Caret - start).Select(e => e.Text));
    }

    /// <summary>The text element just before the placed tail, or null when the tail reaches the start.</summary>
    public string? ElementBeforePlacedTail()
    {
        var start = Caret;
        while (start > 0 && _elements[start - 1].Placed) start--;
        return start > 0 ? _elements[start - 1].Text : null;
    }

    public string TextBeforeCaret() => string.Concat(_elements.Take(Caret).Select(e => e.Text));

    public void MarkAllForeign()
    {
        for (var i = 0; i < _elements.Count; i++)
            _elements[i] = (_elements[i].Text, false);
        Boundaries++;
    }

    private void OnKey(KeyActivatedMessage m)
    {
        MessagesSeen++;
        KeyMessages.Add(m);
        var mods = m.Modifiers ?? new HashSet<ModifierKey>();
        var ctrl = mods.Contains(ModifierKey.Ctrl);
        var alt = mods.Contains(ModifierKey.Alt);
        var win = mods.Contains(ModifierKey.Win);

        if (m.Key == "\b")
        {
            if (ctrl && !alt && !win)
                DeleteWordBeforeCaret();
            else if (alt || win)
                MarkAllForeign();      // Alt+Backspace is Undo in many apps: the text is unknown now
            else
                DeleteBeforeCaret(1);
            return;
        }

        if (m.Key == "\n")
        {
            Insert("\n", placed: true, atCaret: true);
            MarkAllForeign();
            return;
        }

        if (m.Key == "\t")
        {
            MarkAllForeign();
            return;
        }

        if (ctrl || alt || win)
        {
            // A shortcut, not text.
            var key = m.Key.ToLowerInvariant();
            if (ctrl && !alt && !win && key == "c")
                return;                // copy changes nothing
            if (ctrl && !alt && !win && key == "v")
                Insert("PASTE", placed: false, atCaret: true);
            else if (ctrl && !alt && !win && key == "x")
                DeleteBeforeCaret(1);
            else if (ctrl && !alt && !win && key == "z")
                DeleteBeforeCaret(2);
            MarkAllForeign();
            return;
        }

        Insert(m.Key, placed: true, atCaret: true);
    }

    private void OnPick(PredictionSelectedMessage m)
    {
        MessagesSeen++;
        var deleteCount = Math.Min(m.DeleteCount, Caret);
        var from = Caret - deleteCount;
        var deleted = _elements.Skip(from).Take(deleteCount).ToList();
        LastPickDeletedText = string.Concat(deleted.Select(e => e.Text));
        if (m.DeleteCount > Caret)
            Violations.Add($"pick asked to delete {m.DeleteCount} graphemes but only {Caret} precede the caret");
        if (deleted.Any(e => !e.Placed))
            Violations.Add($"pick deleted a character the keyboard never placed: \"{LastPickDeletedText}\"");
        DeleteBeforeCaret(deleteCount);
        Insert(m.Word + " ", placed: true, atCaret: true);
    }

    private void OnSpecial(SpecialKeyMessage m)
    {
        MessagesSeen++;
        switch (m.KeyName.ToLowerInvariant())
        {
            case "arrowleft": Caret = Math.Max(0, Caret - 1); break;
            case "arrowright": Caret = Math.Min(_elements.Count, Caret + 1); break;
            case "home": Caret = 0; break;
            case "end": Caret = _elements.Count; break;
            case "delete":
                if (Caret < _elements.Count) _elements.RemoveAt(Caret);
                break;
            case "printscreen":
                return;                // copies the screen; the text and the caret do not change
        }
        MarkAllForeign();
    }

    private void DeleteBeforeCaret(int count)
    {
        count = Math.Min(count, Caret);
        _elements.RemoveRange(Caret - count, count);
        Caret -= count;
        LastDeletedCount = count;
    }

    /// <summary>Ctrl+Backspace the way <c>TrimLastWord</c> defines it: trailing separators, then one run of letters/digits.</summary>
    private void DeleteWordBeforeCaret()
    {
        var end = Caret;
        while (end > 0 && !IsWordElement(_elements[end - 1].Text)) end--;
        var start = end;
        while (start > 0 && IsWordElement(_elements[start - 1].Text)) start--;
        DeleteBeforeCaret(Caret - start);
    }

    private static bool IsWordElement(string element) => element.All(char.IsLetterOrDigit);

    private void Insert(string text, bool placed, bool atCaret)
    {
        var index = atCaret ? Caret : _elements.Count;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
            _elements.Insert(index++, ((string)e.Current, placed));
        if (atCaret) Caret = index;
    }
}
