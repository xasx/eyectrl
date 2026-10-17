namespace EyeCtrl.EyeCare;

/// <summary>
/// Sent by <see cref="EyeCareService"/> once with <c>Active = true</c> before an exercise run and
/// <c>false</c> in a <c>finally</c> after its summary (P2 D1): the flag spans the whole break —
/// the full-screen, click-through exercise window stays shown between the run finishing and the
/// summary showing, and <c>CancelActive</c> lands in the same <c>finally</c>. The note gate keeps
/// this flag and suspends note dwell while it holds: the window is click-through, so
/// <c>WindowFromPoint</c> reports the note beneath it and nothing else would stop a hidden ✕ or ✎
/// from firing while the owner follows the exercise dot. The record lives in this project (not the
/// ViewModels) because the sender is this project and the leaf must not reference the UI layer;
/// the App consumes it.
/// </summary>
public record EyeCareExerciseActiveMessage(bool Active);
