namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// Renders the caregiver alert. Separate from <see cref="CaregiverAlertService"/> so the sustain/
/// rate-limit/suppression decision is headless-testable with a fake; the WPF <c>CaregiverAlertWindow</c>
/// is the runtime implementation (Task 4). The surface is informational only and gaze-safe by
/// construction (click-through, excluded from screen capture).
/// </summary>
public interface ICaregiverAlertPresenter
{
    /// <summary>Shows the alert banner for the given condition (and plays the attention chime).</summary>
    void ShowCaregiverAlert(CaregiverAlertCondition condition);

    /// <summary>Begins the short fade-out used when the condition naturally clears (D2).</summary>
    void FadeOutCaregiverAlert();

    /// <summary>Takes the banner down at once — suppression (§8.10), feature-off, or teardown.</summary>
    void HideCaregiverAlert();

    /// <summary>
    /// The banner's visible bounds in physical pixels while it is on screen (P2 D3): the note
    /// gate's occluder source. False (and <paramref name="bounds"/> default) when it is not
    /// showing — the fade-out tail keeps reporting true until the window hides, which is the
    /// conservative reading.
    /// </summary>
    bool TryGetVisibleBounds(out PhysicalRect bounds);
}
