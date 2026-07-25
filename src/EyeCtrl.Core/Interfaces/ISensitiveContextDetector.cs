namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// Detects whether the currently focused UI element is a <em>sensitive</em> (password) context, so the
/// struggle-adaptation feedback surface (on-screen cue, drift notice, verbose log) can be suppressed there
/// (design §8.10 / §13.1) — <b>adaptation itself keeps running</b>, only the visible/loggable feedback is
/// gated. Implemented in <c>EyeCtrl.Platform</c> over UI Automation focus-changed events; the feedback gate
/// reads <see cref="IsSensitiveActive"/> in O(1) (nothing runs on the gaze hot path, §9.1). Best-effort: on
/// UI-Automation failure or unavailability it reports <em>not</em> sensitive — a false negative only shows a
/// subtle cue and never exposes the credential, with <c>AdaptationSettings.SuppressInSensitiveContexts</c>
/// (default true) as the backstop.
/// </summary>
public interface ISensitiveContextDetector : IDisposable
{
    /// <summary>
    /// <c>true</c> when the focused element is a sensitive (password) field. A cached value updated on UI
    /// Automation focus changes and safe to read from any thread; it is <c>false</c> before the first focus
    /// event and whenever detection is stopped, inactive, or has failed.
    /// </summary>
    bool IsSensitiveActive { get; }

    /// <summary>
    /// Starts detection by subscribing to UI Automation focus-changed events. Idempotent (a second call is a
    /// no-op); call once at startup. Any UI-Automation failure is swallowed and leaves detection inactive
    /// (never sensitive), so callers need no <c>try</c>/<c>catch</c>.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops detection by unsubscribing the focus hook (which otherwise leaks) and resets
    /// <see cref="IsSensitiveActive"/> to <c>false</c>. Idempotent; also invoked by <see cref="IDisposable.Dispose"/>.
    /// </summary>
    void Stop();
}
