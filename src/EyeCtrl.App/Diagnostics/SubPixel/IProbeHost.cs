namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Windows.Media.Imaging;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The narrow seam over every effect C7 has on the rest of the app (build plan C14). Production is
/// <see cref="ProbeHost"/>; tests substitute a recording fake that logs every call with
/// <c>Dispatcher.CheckAccess()</c>, can throw or re-enter at any named call, and forwards fixation
/// suppression to a real headless <c>GazeEngine</c>.
///
/// <para>
/// The interface deliberately holds no session type: C7 passes what each call needs, and every
/// continuation captures its own session. That is what lets W1 (d) prove a late callback from run 1
/// cannot affect run 2.
/// </para>
/// </summary>
internal interface IProbeHost
{
    /// <summary>Writes <c>ModalInteractionState.ProbeOpen</c>, raising <c>ProbeOpenChanged</c>.</summary>
    /// <param name="open">The new flag value.</param>
    void SetProbeOpen(bool open);

    /// <summary>The real cursor ring's visibility (<c>CursorVM.IsVisible</c>).</summary>
    bool IsRingVisible { get; }

    /// <summary>Hides the real cursor ring.</summary>
    void HideRing();

    /// <summary>Shows the real cursor ring iff <see cref="IsRingVisible"/> is set.</summary>
    void ShowRingIfVisible();

    /// <summary>Claims <c>FixationSuppressor.SubPixelProbe</c> on the real engine.</summary>
    void AcquireProbeSuppression();

    /// <summary>Releases <c>FixationSuppressor.SubPixelProbe</c> on the real engine.</summary>
    void ReleaseProbeSuppression();

    /// <summary>Deactivates the toolbar (the adapter itself posts if it must).</summary>
    void DeactivateToolbar();

    /// <summary>Re-asserts toolbar inactivity synchronously on its VM (pre-show re-check).</summary>
    void AssertToolbarInactive();

    /// <summary>Restores the toolbar's pre-probe visibility (its own reassert path).</summary>
    void ReassertToolbarVisibility();

    /// <summary>The keyboard's current visibility (<c>ModalInteractionState.KeyboardVisible</c>).</summary>
    bool KeyboardVisible { get; }

    /// <summary>
    /// Hides a visible keyboard through the C13 probe seam, leaving its suppression claim as it was.
    /// </summary>
    void HideKeyboard();

    /// <summary>
    /// Restores the keyboard through the C13 probe seam. <c>false</c> is a no-op — it must never
    /// hide a keyboard the probe did not hide (review 05 F2).
    /// </summary>
    /// <param name="wasVisible">Whether the probe hid a visible keyboard.</param>
    void RestoreKeyboard(bool wasVisible);

    /// <summary>Suspends the quick-actions summon.</summary>
    void SuspendSummon();

    /// <summary>Resumes the quick-actions summon.</summary>
    void ResumeSummon();

    /// <summary>Whether any of the three precondition menus is visible (the pre-show re-check).</summary>
    bool AnyMenuVisible { get; }

    /// <summary>
    /// Flushes DirectComposition and captures the primary screen for the backdrop. May return
    /// <c>null</c> when the capture is unavailable; the window then shows a plain backdrop.
    /// </summary>
    BitmapSource? CaptureBackdrop();

    /// <summary>
    /// Builds C5 for the run. Production constructs the real view model; W1's fake can return one
    /// built from a test factory (and can re-enter <c>EndTest</c> around the call).
    /// </summary>
    /// <param name="schedule">The run's trial plan.</param>
    /// <param name="timeProvider">The dispatcher-backed clock and timer source C5 uses.</param>
    /// <param name="isValidSample">C5's per-sample validity source, fed from <c>GazeSampled</c>.</param>
    /// <param name="startMode">The run to start: the full run, or a session-2 resume.</param>
    /// <param name="resumedSession1Verdict">The persisted convicting verdict a resume continues from.</param>
    /// <returns>The run's view model, not yet started.</returns>
    SubPixelProbeViewModel CreateViewModel(
        SubPixelAbSchedule schedule,
        TimeProvider timeProvider,
        Func<bool> isValidSample,
        SubPixelProbeStartMode startMode,
        SubPixelAbSession1Verdict? resumedSession1Verdict);

    /// <summary>Disposes C5 and unregisters it from the messenger. Idempotent.</summary>
    /// <param name="vm">The view model to dispose.</param>
    void DisposeViewModel(SubPixelProbeViewModel vm);

    /// <summary>
    /// Builds C6 for the run, wiring its close path to <paramref name="sessionEnded"/> — the
    /// captured session's end, never a coordinator field (review 05 F4).
    /// </summary>
    /// <param name="backdrop">The captured backdrop, or <c>null</c>.</param>
    /// <param name="vm">The run's view model.</param>
    /// <param name="cursorSettings">The ring look the probe draws with.</param>
    /// <param name="sessionEnded">Invoked once, when the window's close is observed.</param>
    /// <returns>The window, not yet shown.</returns>
    IProbeWindow CreateWindow(
        BitmapSource? backdrop,
        SubPixelProbeViewModel vm,
        CursorSettings cursorSettings,
        Action sessionEnded);
}
