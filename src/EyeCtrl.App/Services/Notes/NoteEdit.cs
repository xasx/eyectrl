namespace EyeCtrl.App.Services.Notes;

/// <summary>
/// One routed input, mapped for the note editor (P1 D2). The dispatcher hands these to
/// <see cref="INoteTextInput.Apply"/>; a <c>null</c> mapping means the input is swallowed.
/// </summary>
public abstract record NoteEdit;

/// <summary>Appends verbatim text: a printable key, a space, an emoji key, or canned text.</summary>
public sealed record NoteAppend(string Text) : NoteEdit;

/// <summary>Starts a new line (the keyboard's Enter key; P1 D2 — Enter no longer saves).</summary>
public sealed record NoteNewline : NoteEdit;

/// <summary>
/// Removes one whole text element (<see cref="System.Globalization.StringInfo"/>), so an emoji
/// never leaves a lone surrogate behind.
/// </summary>
public sealed record NoteBackspace : NoteEdit;

/// <summary>Ctrl+Backspace word deletion (the keyboard's deleteWord key).</summary>
public sealed record NoteDeleteWord : NoteEdit;

/// <summary>
/// A prediction pick: delete <paramref name="DeleteUnits"/> UTF-16 units (the composing input's
/// length the VM counted), snapped back to a text-element boundary, then append
/// <paramref name="Text"/> (the word plus its trailing space). Atomic: one edit, one re-render.
/// </summary>
public sealed record NotePrediction(int DeleteUnits, string Text) : NoteEdit;
