namespace EyeCtrl.App.Services;

/// <summary>
/// The cue's presentation decision for one assessment: whether the badge should be shown and, if so,
/// how intense it should be (design §10 — intensity scales with the struggle level).
/// </summary>
/// <param name="Visible">True when the badge should be shown this tick.</param>
/// <param name="Intensity">Struggle intensity in [0, 1] driving the badge's peak opacity; 0 when hidden.</param>
public readonly record struct CueState(bool Visible, double Intensity);
