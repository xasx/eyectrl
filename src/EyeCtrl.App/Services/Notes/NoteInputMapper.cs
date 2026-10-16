namespace EyeCtrl.App.Services.Notes;

using System.Globalization;
using EyeCtrl.Core.Keyboard;

/// <summary>
/// Maps one dispatcher input to a <see cref="NoteEdit"/>, or to <c>null</c> (swallow) — pure,
/// no WPF, no manager state (P1 D2). Only the SYSTEM modifiers (Ctrl, Alt, Win) ever cause a
/// swallow; the text modifiers (Shift, CapsLock) are ignored on control keys, exactly as the
/// keyboard's own bookkeeping treats them: CapsLock rides every <c>ModifiersSnapshot</c>, and
/// an armed Shift must not turn the note's Enter into a swallow.
/// </summary>
public static class NoteInputMapper
{
    /// <summary>The control keys the keyboard sends as strings, mirrored from the dispatcher's own set.</summary>
    public const string BackspaceKey = "\b";
    public const string NewlineKey = "\n";
    public const string TabKey = "\t";
    public const string SpaceKey = " ";

    /// <summary>
    /// A <see cref="KeyActivatedMessage"/> payload. Any key with a system modifier other than the
    /// deleteWord chord is swallowed; <c>"\t"</c> is always swallowed (a tab in a note is nothing
    /// the caret-less v1 editor can express).
    /// </summary>
    public static NoteEdit? FromKey(string key, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not null && SystemModifierCount(modifiers) > 0)
        {
            // deleteWord (KeyboardViewModel.HandleDeleteWord): "\b" with Ctrl added to the armed
            // set — Ctrl is the ONLY system modifier in that message. A user chord like Alt+Back
            // or Ctrl+Alt+Back is a shortcut attempt and dies here.
            if (key == BackspaceKey && SystemModifierCount(modifiers) == 1 && modifiers.Contains(ModifierKey.Ctrl))
                return new NoteDeleteWord();
            return null;
        }

        return key switch
        {
            BackspaceKey => new NoteBackspace(),
            NewlineKey => new NoteNewline(),
            TabKey => null,
            // " " and every printable string — emoji keys included — are plain appends.
            _ => new NoteAppend(key),
        };
    }

    /// <summary>
    /// A <see cref="PredictionSelectedMessage"/> payload. Never swallowed: the delete count is the
    /// VM's own <c>CurrentInput.Length</c> at pick time, so the note must lose exactly that much.
    /// </summary>
    public static NoteEdit FromPrediction(int deleteCount, string word) =>
        new NotePrediction(Math.Max(0, deleteCount), word + " ");

    /// <summary>
    /// A <see cref="SpecialKeyMessage"/> payload. Only the three text-control keys apply; the rest
    /// of the keyboard's special-key row (and every modified variant) is swallowed.
    /// </summary>
    public static NoteEdit? FromSpecialKey(string keyName, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (modifiers is not null && SystemModifierCount(modifiers) > 0)
            return null;

        // The names are the keyboard's, matched like KeyNameMapper: case-insensitive, aliases
        // included. The real path delivers lowercase ("key:backspace" is lower-cased before the
        // message); the aliases keep a capitalized test or chord path honest too.
        return keyName.ToLowerInvariant() switch
        {
            "backspace" or "back" => new NoteBackspace(),
            "enter" or "return" => new NoteNewline(),
            "space" => new NoteAppend(SpaceKey),
            _ => null,
        };
    }

    /// <summary>Canned text rides the note verbatim (P1 D1); it can carry newlines.</summary>
    public static NoteEdit FromCannedText(string payload) => new NoteAppend(payload);

    private static int SystemModifierCount(IReadOnlySet<ModifierKey> modifiers)
    {
        int count = 0;
        if (modifiers.Contains(ModifierKey.Ctrl)) count++;
        if (modifiers.Contains(ModifierKey.Alt)) count++;
        if (modifiers.Contains(ModifierKey.Win)) count++;
        return count;
    }
}
