namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// D2/F3a of the password-detector diagnostics: the live focus-event counters behind the detector's
/// one-per-session exit line, exposed so a running session can show them on the keyboard's D4 show/hide line
/// (a sitting's log is read while EyeCtrl is still running, before that exit line exists). Read-only and
/// content-free: four counts.
/// </summary>
public interface IFocusEventDiagnostics
{
    /// <summary>A snapshot of the session's focus-event counters; safe to read from any thread.</summary>
    FocusEventCounts GetFocusEventCounts();
}

/// <summary>
/// The focus-event counters: total events, events whose read said password, read failures, and own-process
/// events (whose flag is deliberately never read). <see cref="Unknown"/> (-1) means the detector does not
/// expose them and renders as <c>-</c>, never as a real zero.
/// </summary>
public readonly record struct FocusEventCounts(int Total, int Password, int Failures, int Own)
{
    /// <summary>No counts available (the detector is not the UI-Automation one).</summary>
    public static FocusEventCounts Unknown => new(-1, -1, -1, -1);
}
