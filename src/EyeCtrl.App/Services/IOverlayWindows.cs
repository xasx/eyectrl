namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

// Seams for the overlay windows, following IEdgeButtonBar's template and for its reason.
//
// IOverlayManager exposed every overlay as its concrete WPF window, so a test reaching any
// coordinator that touched one had to stand up a real window — an STA thread, a process-wide
// Application singleton, and the app-level resource dictionary the windows' theme bindings resolve
// against. That cost is not hypothetical: it is why DragCoordinator's success path had no headless
// coverage, and why deleting its release-on-dispose left that whole file green while the physical
// mouse button stayed pressed after tray Exit.
//
// Each interface carries ONLY what consumers outside OverlayManager actually call. That set was
// determined by compiling against deliberately EMPTY interfaces and reading the 49 resulting
// errors, not by copying each window's public surface — the two differ substantially, and copying
// would have re-exported members only the owner uses.
//
// Excluded on purpose, because including them would pull WPF back across the seam:
//   * window lifetime (Close) and layout internals;
//   * DataContext / SetViewModel / Configure(vm) binding, which is the owner's job;
//   * anything no consumer calls.
//
// One member is deliberately NOT a mirror of what the code did before. Consumers of the toolbar
// read Left, Top, ActualWidth and ActualHeight — raw WPF Window layout — and an interface exposing
// those four would be a Window in all but name: a fake would have to fabricate layout numbers, and
// the geometry tests that matter would then be asserting against the fake's arithmetic instead of
// the window's. IToolbarOverlay.GetPhysicalBounds() replaces all four, so the seam answers "where
// are you, in physical pixels" — a question a fake can answer honestly and a caller can use
// without knowing WPF. The DPI conversion moved into the window, next to the layout values it
// converts.

/// <summary>
/// An overlay that can be instrumented for the Stage-2 ring-quantization measurement: does sub-pixel
/// positioning survive to the window, or is it rounded to whole device pixels?
/// <para>
/// Kept OUT of <see cref="ICursorCircleOverlay"/> deliberately, even though the same window implements
/// both and the App calls these through the manager. The overlay seams above exist so a coordinator can
/// be tested without standing up a real window; this is the opposite kind of thing — a capability only
/// the real window can have, since the whole measurement is a read-back of where the window manager
/// actually put it. Declaring it on the main seam would oblige every future fake to carry a diagnostic
/// it can only stub, and would answer "what does a cursor ring do for the app" with something that is
/// not about the app at all.
/// </para>
/// <para>
/// Queried by pattern-match at the call site, the same shape <c>IGazeProviderDecorator</c> uses: an
/// overlay that does not implement this is simply not instrumented, which is the correct behaviour
/// rather than a missing case.
/// </para>
/// </summary>
public interface IRingQuantizationInstrument
{
    /// <summary>Attaches the probe and the settings carrying its live gate.</summary>
    void AttachQuantizationProbe(RingQuantizationProbe probe, DiagnosticsSettings diagnostics);

    /// <summary>Emits the instrument's verdict and cadence summary. Called at shutdown.</summary>
    void ReportQuantization();
}

/// <summary>The always-visible gaze cursor ring.</summary>
public interface ICursorCircleOverlay
{
    void Show();
    void Hide();
    void UpdatePosition(PhysicalPoint point);

    /// <summary>
    /// Moves the ring to <paramref name="point"/> as one call carrying the glide flag, so the window
    /// never sees a position before it knows it is a pin edge (critique M3): the ring's own lifetime
    /// decides when to glide, never the jump size. <paramref name="glide"/> true marks a glide frame —
    /// the window writes the same two properties but leaves instrument 2 out of it, whose cadence
    /// histogram must keep measuring the ring's steady-state writes, not an animation.
    /// </summary>
    void MoveTo(PhysicalPoint point, bool glide);

    void UpdateAppearance(CursorSettings settings);

    /// <summary>
    /// Raises the ring to its bold look while a fixation is held. Until then the ring is quiet
    /// (reduced emphasis) in FreeLook; the pin is what makes stillness the "you're holding" signal.
    /// </summary>
    void SetPinned(bool pinned);
}

/// <summary>The activation button's dwell feedback, drawn where the dwell is counted.</summary>
public interface IActivationButtonOverlay
{
    void Show();

    /// <summary>
    /// Places the symmetric halo centred on <paramref name="buttonCenter"/> — the offer's B — and
    /// sizes it for <paramref name="hitRadiusPixels"/> (physical px; the window divides the live DPI
    /// scale out itself). There is no offset parameter on purpose: the drawn centre must be the
    /// counted centre, and the halo's outer radius never exceeds the hit radius.
    /// </summary>
    void ShowAt(PhysicalPoint buttonCenter, double hitRadiusPixels);

    /// <summary>Hides with the fade-out animation.</summary>
    void Hide();

    /// <summary>Hides without animating — used when the interaction state changes underneath.</summary>
    void HideImmediate();

    void UpdateDwellProgress(double progress);
}

/// <summary>The manual-selection magnifier.</summary>
public interface IManualSelectionOverlay
{
    void Show(PhysicalPoint center, int radius, double magnification,
        PhysicalPoint? gazePoint = null, bool recapture = true);

    void UpdateCenter(PhysicalPoint center, int radius, double magnification,
        PhysicalPoint? gazePoint = null, bool recapture = true);

    void Hide();

    /// <summary>Maps a point on the magnified view back to the real screen coordinate.</summary>
    PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen);

    /// <summary>
    /// The centre, in physical pixels, of the capture currently displayed. The capture rectangle is
    /// clamped to the screen, so near an edge this is not the centre that was requested; every
    /// mapping between the magnified view and real screen coordinates reads this value.
    /// </summary>
    PhysicalPoint EffectiveCaptureCenter { get; }
}

/// <summary>The radial action menu.</summary>
public interface IActionMenuOverlay
{
    // Twelve parameters, kept as-is rather than "improved" into an options object. Reshaping the
    // call while narrowing the type would mix a mechanical change with a behavioural one, and this
    // seam's job is to remove the WPF dependency, not to redesign the menu's API.
    void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
        int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
        PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
        int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false);

    void Hide();
}

/// <summary>The utility toolbar.</summary>
public interface IToolbarOverlay
{
    bool IsVisible { get; }

    /// <summary>Whether the toolbar has published its dwell targets yet.</summary>
    bool TargetsInitialized { get; }

    void Show();
    void Hide();
    void Initialize(double buttonSize, int dwellTimeMs);
    void SetMode(bool isDocked, string edge);
    void SetSelectionModeState(SelectionMode mode);
    void SetPausedState(bool isPaused);
    void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds);

    /// <summary>Rebinds the buttons to the live view model after a mode or size change.</summary>
    void BindToViewModel();

    // ── Slide transition ──
    // The auto-hide adapter drives these, so they have to be on the seam for it to be testable
    // without a window. They are intent-level ("slide in", "am I sliding") rather than WPF
    // mechanics, so exposing them costs the seam nothing.

    /// <summary>True while a slide animation is in flight.</summary>
    bool IsSliding { get; }

    /// <summary>True when visible AND not in the middle of sliding away.</summary>
    bool IsOnScreen { get; }

    /// <summary>The toolbar's on-screen thickness in DIP (its window height) — the extent the
    /// keyboard yields by when both sit at the same edge (KeyboardCoordinator's occupancy
    /// mapping). Intent-level like the slide members: the seam speaks DIP because both
    /// surfaces position in DIP, so callers must not feed it the physical-px rect.</summary>
    double ExtentDip { get; }

    /// <summary>Shows the toolbar already in its resting position, with no animation.</summary>
    void ShowAtRest();

    /// <summary>Shows the toolbar off-edge and slides it into its resting position.</summary>
    void ShowThenSlideIn();

    /// <summary>Slides the toolbar off-edge, then hides it and invokes <paramref name="onHidden"/>.</summary>
    void SlideOutThenHide(Action onHidden);

    /// <summary>
    /// The window's bounds in physical pixels.
    /// </summary>
    /// <remarks>
    /// Replaces four raw WPF layout reads (Left/Top/ActualWidth/ActualHeight) that callers were
    /// converting through DpiHelper themselves. Moving the conversion into the window keeps the
    /// seam free of WPF and gives the auto-hide controller a value it can use directly — the same
    /// rect that decides whether gaze is on the toolbar, which is what keeps it from hiding under
    /// the user's gaze.
    /// </remarks>
    PhysicalRect GetPhysicalBounds();
}

/// <summary>The paused-state edge indicator shown while the toolbar is hidden.</summary>
public interface IPauseIndicatorOverlay
{
    void Show();
    void Hide();
    void SetEdge(string edge);

    /// <summary>Re-asserts this window above sibling topmost overlays shown after it
    /// (OverlayWindow.RaiseToTop: SetWindowPos HWND_TOPMOST | SWP_NOACTIVATE).</summary>
    void RaiseToTop();
}

/// <summary>The manual-selection gaze dot.</summary>
public interface IManualSelectionIndicatorOverlay
{
    bool IsVisible { get; }
    void Show();
    void Hide();
    void UpdatePosition(double screenX, double screenY);
    void UpdateOuterDiameter(double diameter);
    void AnimateReset(double targetDiameter, int durationMs);
    void RaiseToTop();

    /// <summary>
    /// Sets the held dwell anchor's mark. The anchor is a fixed screen point (the dot moves, the
    /// anchor does not), so it is drawn in the same window as the dot but offset from it; callers
    /// pass <paramref name="visible"/> false to release the mark.
    /// </summary>
    void UpdateAnchor(double screenX, double screenY, bool visible);
}

/// <summary>The dimming backdrop behind modal overlays.</summary>
public interface IGlassBackdropOverlay
{
    void Show();
    void Hide();
    void ApplyBackdrop(BackdropSettings settings);
    void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0);
    void UpdateHideButtonProgress(double progress);

    /// <summary>Shows the "Press Esc to exit" hint — the caregiver-facing signage for manual selection.</summary>
    void ShowEscapeHint();

    /// <summary>Hides the escape hint.</summary>
    void HideEscapeHint();
}

/// <summary>The calibration surface.</summary>
public interface ICalibrationOverlay
{
    void Show();
    void Hide();
    void UpdateVisualState(CalibrationState state, double progress);
    void DismissCalibration();
}

/// <summary>The resolved-target highlight rectangle.</summary>
public interface ITargetHighlightOverlay
{
    void Configure(string color, double thickness, double opacity);
    void ShowHighlight();
    void HideHighlight();
    void PositionAt(PhysicalRect bounds);
}

/// <summary>The tracker track-status box.</summary>
public interface ITrackStatusOverlay
{
    void Show();
    void Hide();
    void StartTracking();
    void StopTracking();
    void PositionTopRight();
}

/// <summary>The task switcher.</summary>
public interface ITaskSwitcherOverlay
{
    void ShowFullScreen();
    void HideOverlay();
}

/// <summary>The contracting locked-mode aiming cursor.</summary>
public interface IRepeatCursorOverlay
{
    void Update(RepeatCursorViewModel vm);
    void ShowCursor();
    void HideCursor();
}

/// <summary>The modes menu panel (toolbar-triggered input-mode switches).</summary>
public interface IModesMenuOverlay
{
    /// <summary>Places the panel top-centre under the toolbar and reports the row's dwell target.</summary>
    void ShowAtTopCentre();
    void HideMenu();
}

/// <summary>The quick-actions grid.</summary>
public interface IQuickActionsGridOverlay
{
    void Show();
    void Hide();
    void PositionOnEdge(string edge);
    void SyncTilePositions();
}
