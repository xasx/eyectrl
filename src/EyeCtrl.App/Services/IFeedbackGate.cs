namespace EyeCtrl.App.Services;

/// <summary>
/// The single seam that suppresses user-visible adaptation feedback (the cue now; the drift report and
/// verbose log in 1b-⑤c) in sensitive contexts (design §8.10). 1b-⑥ replaces the registered
/// implementation with one backed by a UIA <c>ISensitiveContextDetector</c> and
/// <c>AdaptationSettings.SuppressInSensitiveContexts</c>; adaptation itself keeps running silently.
/// </summary>
public interface IFeedbackGate
{
    /// <summary>True when visible/loggable feedback must be suppressed (e.g. a password field is focused).</summary>
    bool IsFeedbackSuppressed { get; }
}

/// <summary>
/// The default no-op gate: feedback is never suppressed. Used until 1b-⑥ supplies the sensitive-context
/// implementation. Behaviourally identical to having no gate.
/// </summary>
public sealed class AllowAllFeedbackGate : IFeedbackGate
{
    /// <inheritdoc />
    public bool IsFeedbackSuppressed => false;
}
