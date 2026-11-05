namespace EyeCtrl.App.Browse;

using System.Collections.Generic;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The coordinator's window seam for the gaze browser — the
/// <c>ScrollModeController</c> factory-seam counterpart for the browse surface: the App
/// constructs the concrete <c>BrowseWindow</c> and hands it to the coordinator as this
/// interface, so the coordinator stays constructible headlessly.
/// </summary>
public interface IBrowseWindowSurface
{
    /// <summary>
    /// Shows the window over the work area and wires the dwell surface: the surface's chrome
    /// and page dwell targets are registered by the coordinator after the layout is known.
    /// </summary>
    public void Show(BrowseViewModel vm);

    /// <summary>Closes the window (raising <see cref="SurfaceClosed"/> through the normal
    /// closing path).</summary>
    public void Close();

    /// <summary>Moves OS keyboard focus to the address box (selecting its text), so physical
    /// typing edits the URL.</summary>
    public void FocusAddressBox();

    /// <summary>Brings the window to the front and activates it (re-asserting focusability —
    /// the post-Show <c>SetOverlayInteraction(clickThrough: false, focusable: true)</c>
    /// contract — and the topmost Z-order).</summary>
    public void ActivateToFront();

    /// <summary>
    /// The web source this surface created and DISPLAYS (external review C2: exactly one host
    /// per summon — the coordinator drives this instance, never one of its own). Created by
    /// <see cref="Show"/>, so null before it; the coordinator reads it immediately after.
    /// </summary>
    public IBrowseWebSource? WebSource { get; }

    /// <summary>
    /// The WebView2 client area's physical screen bounds (external review C3a: the clip box
    /// every page element rect is intersected with — a page cannot register a dwell target
    /// outside the web view). False before the window is laid out (no honest bounds exist).
    /// </summary>
    public bool TryGetWebBounds(out PhysicalRect bounds);

    /// <summary>
    /// The whole window's physical screen bounds (external review I4: the note gate's occluder —
    /// an opaque browser window occludes notes across its WHOLE face, not just where its dwell
    /// targets sit). False before the window is laid out.
    /// </summary>
    public bool TryGetWindowBounds(out PhysicalRect bounds);

    /// <summary>
    /// Whether the window (its root HWND) is the one under <paramref name="screenPoint"/> —
    /// the gaze point (external review I3). When another EyeCtrl surface (keyboard, toolbar,
    /// ring) sits over the browser, the browser must not accumulate dwell on whatever lies
    /// beneath it; this is the per-sample gate. False before the window is shown.
    /// </summary>
    public bool IsUnderPoint(PhysicalPoint screenPoint);

    /// <summary>The address bar's Enter: navigate the web view to the typed URL.</summary>
    public event Action<Uri>? NavigateRequested;

    /// <summary>The window closed (user alt+F4, the Close chrome button's window-internal
    /// path, or the coordinator's <see cref="Close"/>).</summary>
    public event Action? SurfaceClosed;

    /// <summary>The window's on-screen geometry changed (external review M6: the chrome rects
    /// and the web bounds are re-read then, instead of going stale until a re-summon).</summary>
    public event Action? LayoutChanged;

    /// <summary>
    /// The WebView2 client area's top-left corner in physical screen pixels — the origin the
    /// T1 parser offsets every CSS-px element rect by. False before the window is laid out
    /// (no honest origin exists yet).
    /// </summary>
    public bool TryGetWebOrigin(out PhysicalPoint origin);

    /// <summary>
    /// The chrome row's dwell rects, keyed by the <c>chrome_</c> ids BrowseViewModel's dwell
    /// targets use. False while the window is not laid out (no honest rects yet).
    /// </summary>
    public bool TryGetChromeRects(out IReadOnlyList<(string Id, PhysicalRect Rect)> rects);

    /// <summary>Surfaces a fatal web-source failure in the address bar (the missing-runtime
    /// notice — the window STAYS OPEN with it: the chrome Close dwell is the exit, review R3).</summary>
    public void ShowAddressError(string message);

    /// <summary>
    /// A chrome dwell target's progress changed (0–1): the window draws its WPF-side cue — the
    /// page's JS cue cannot know the <c>chrome_</c> ids. <c>null</c> id (or 0) clears the cue.
    /// </summary>
    public void ShowChromeProgress(string? id, double progress);
}
