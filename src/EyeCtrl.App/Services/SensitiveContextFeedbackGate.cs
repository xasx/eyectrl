namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// The real <see cref="IFeedbackGate"/> (design §8.10): suppresses user-visible/loggable adaptation feedback
/// — the on-screen cue, the drift notice, and the verbose log (which falls back to a coarse marker) — when a
/// sensitive (password) context is active <b>and</b> the user has left
/// <see cref="AdaptationSettings.SuppressInSensitiveContexts"/> on (the default). Adaptation itself keeps
/// running silently regardless. Registered in place of 1b-⑤b's no-op <c>AllowAllFeedbackGate</c>; the three
/// feedback consumers (<c>AdaptationCueService</c>, <c>AdaptationLog</c>, <c>DriftNoticeService</c>) read
/// <see cref="IsFeedbackSuppressed"/> unchanged.
/// </summary>
public sealed class SensitiveContextFeedbackGate : IFeedbackGate
{
    private readonly AdaptationSettings _adaptation;
    private readonly ISensitiveContextDetector _detector;

    /// <summary>Initializes the gate over the suppression toggle and the sensitive-context detector.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.SuppressInSensitiveContexts"/> is the backstop toggle.</param>
    /// <param name="detector">The sensitive-context detector supplying <see cref="ISensitiveContextDetector.IsSensitiveActive"/>.</param>
    public SensitiveContextFeedbackGate(AdaptationSettings adaptation, ISensitiveContextDetector detector)
    {
        _adaptation = adaptation;
        _detector = detector;
    }

    /// <inheritdoc />
    /// <remarks>
    /// True only when the setting is on <b>and</b> a sensitive context is active. The detector read is O(1) (a
    /// cached <c>Volatile</c> bool) — nothing runs on the gaze hot path (§9.1). Evaluated fresh on each read
    /// (on the coordinator's dispatcher tick), so a mid-session focus change or settings toggle is reflected
    /// immediately.
    /// </remarks>
    public bool IsFeedbackSuppressed => _adaptation.SuppressInSensitiveContexts && _detector.IsSensitiveActive;
}
