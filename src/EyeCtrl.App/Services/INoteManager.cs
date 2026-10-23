namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The pinned-note lifecycle. Extends <see cref="INoteTextInput"/> (P1 D1): the keyboard
/// dispatchers read the manager through <see cref="EventCoordinationContext.NoteManager"/> and
/// route every keyboard output into the editing note while <see cref="INoteTextInput.IsEditing"/>
/// holds, so the positional context record gains no new member.
/// </summary>
public interface INoteManager : INoteTextInput
{
    IReadOnlyList<NoteData> ActiveNotes { get; }
    bool IsAtCapacity { get; }

    void CreateNoteAt(PhysicalPoint point);
    void RemoveNote(string id);
    void StartEdit(string id);

    /// <summary>
    /// The ONLY Edit exit in P1 (D4): the note's ✓ Done dwell. P1 always passes
    /// <c>commit: true</c> — a commit saves the trimmed draft, and a brand-new note whose draft
    /// is empty disappears (nothing was ever saved). Discard arrives in P2 with Undo.
    /// </summary>
    void FinishEdit(string id, bool commit);

    void StartMove(string id);
    void FinishMove(string id, PhysicalPoint newPosition);

    void OnGazeMoved(PhysicalPoint gazePoint, long timestamp);

    /// <summary>
    /// The gate's pause poll (P2 D1 row 2): read per sample, alongside the sample's own
    /// <c>IsPaused</c> flag. Polled rather than subscribed — pause is entered and left only by
    /// <c>TogglePause</c> on the UI thread, so a polled read is simpler and just as correct.
    /// Wired once from EventCoordinator.
    /// </summary>
    void SetPausePoll(Func<bool> poll);

    /// <summary>
    /// Suspends note dwell while the predicate returns true (sub-pixel build plan C15, generalised
    /// by P2 D1). Note targets are gaze-only surfaces under no window, and dwell ignores z-order —
    /// so while a suspender is up (modal surfaces, the interaction modes, the modes menu) a note's
    /// controls would otherwise stay live underneath it. Wired once from EventCoordinator, before
    /// the activation-offer coordinator exists — the lambda must read it null-safely.
    /// </summary>
    void SetSuspendPredicate(Func<bool> predicate);

    /// <summary>
    /// Wired once from EventCoordinator (P1 D5), like <see cref="SetSuspendPredicate"/>: the
    /// keyboard visibility probe that drives the summon/release of the eye keyboard, the ⌨
    /// Show-keyboard control, the keyboard-gate on note dwell, and the foreign-hide detection.
    /// </summary>
    void SetKeyboardProbe(IKeyboardProbe probe);

    /// <summary>
    /// The occluder sources (P2 D3), read per sample — no shared gaze-surface provider, no cache
    /// to invalidate. The keyboard and its edge bar reach the gate through <see cref="SetKeyboardProbe"/>'s
    /// rects; this setter takes the App-owned surfaces that neither suspend nor carry a probe: the
    /// lag mark (empty before its bridge is attached), the drift notice's and caregiver alert's
    /// visible bounds (<c>null</c> while not showing), and the track-status card's (fixup 01 M1 —
    /// opaque, click-through, up for as long as TrackStatusSettings.Enabled).
    /// </summary>
    void SetOccluderSources(
        Func<IReadOnlyList<PhysicalRect>>? lagMarkRects = null,
        Func<PhysicalRect?>? driftNoticeBounds = null,
        Func<PhysicalRect?>? caregiverAlertBounds = null,
        Func<PhysicalRect?>? trackStatusBounds = null);

    /// <summary>
    /// The note chrome rects (P2 D7): every note's card body plus its control-cell rects, in
    /// physical pixels — no tail, no window chrome beyond the card. The activation-offer
    /// coordinator publishes these as chrome zones, so no offer is placed over a note.
    /// </summary>
    IReadOnlyList<PhysicalRect> ChromeRects();
}
