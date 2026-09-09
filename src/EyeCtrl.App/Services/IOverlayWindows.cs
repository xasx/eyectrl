namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Calibration;
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

/// <summary>The always-visible gaze cursor ring.</summary>
public interface ICursorCircleOverlay
{
    void Show();
    void Hide();
    void UpdatePosition(PhysicalPoint point);
    void UpdateAppearance(CursorSettings settings);
}

/// <summary>The fixation activation button.</summary>
public interface IActivationButtonOverlay
{
    void Show();
    void ShowAt(PhysicalPoint position, double offset);

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
}

/// <summary>The dimming backdrop behind modal overlays.</summary>
public interface IGlassBackdropOverlay
{
    void Show();
    void Hide();
    void ApplyBackdrop(BackdropSettings settings);
    void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0);
    void UpdateHideButtonProgress(double progress);
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

/// <summary>The quick-actions grid.</summary>
public interface IQuickActionsGridOverlay
{
    void Show();
    void Hide();
    void PositionOnEdge(string edge);
    void SyncTilePositions();
}
