namespace EyeCtrl.App.Services;

/// <summary>
/// Renders the adaptation cue. Kept separate from <see cref="AdaptationCueService"/> so the service's
/// show/hide decisions are headless-testable with a fake presenter; the WPF <c>CueOverlayWindow</c> is
/// the runtime implementation.
/// </summary>
public interface ICuePresenter
{
    /// <summary>Flashes the cue badge at the given intensity ([0, 1]) and (re)starts its auto-fade (~2 s).</summary>
    /// <param name="intensity">Struggle intensity driving the badge's peak opacity.</param>
    void Show(double intensity);

    /// <summary>
    /// Immediately hides the cue badge, cancelling any in-progress fade. Called when the cue decision flips
    /// to hidden while the badge is still up — e.g. feedback suppression turning on mid-struggle (§8.10) — so
    /// the badge does not keep flashing through its 2 s auto-fade window while a sensitive context is active.
    /// </summary>
    void Hide();
}
