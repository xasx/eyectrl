namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// The drag-trail overlay seam: the full-screen mark layer a drag draws between the grab point and
/// the cursor, and the release button it parks under the gaze.
/// </summary>
/// <remarks>
/// <b>Why this interface exists.</b> <see cref="Coordinators.DragCoordinator"/> constructed the
/// concrete <c>DragTrailOverlay</c> — a maximized, topmost, layered WPF window — so every test that
/// drove a successful drag put that window on the real desktop. On a CI runner that window's
/// <c>Show()</c> is the only step in the drag's setup that can fail for environmental reasons
/// (handle or desktop pressure while parallel STA tests create windows), and a failure there is
/// exactly the intermittent drag-cancel test flake this seam was added to remove. Locally it means
/// a test suite briefly covers the owner's screen.
/// <para>
/// The members are the complete set <see cref="Coordinators.DragCoordinator"/> consumes —
/// <c>Configure</c>, <c>Show</c>, <c>Update</c> and <c>Close</c> — not the window's public surface,
/// following <see cref="IEdgeButtonBar"/>'s rule. <c>Clear</c> and the window's WPF members are
/// deliberately absent: no coordinator calls them.
/// </para>
/// <para>
/// The default factory still returns the real overlay, so production is unchanged; the seam exists
/// so a test can supply a recording fake instead. See
/// <see cref="Coordinators.DragCoordinator.CreateTrailOverlay"/>.
/// </para>
/// </remarks>
internal interface IDragTrailOverlay
{
    /// <summary>Applies the user-configurable trail colour (a hex string) and opacity.</summary>
    void Configure(string trailColor, double trailOpacity);

    /// <summary>Puts the overlay on screen. The only member that creates an HWND.</summary>
    void Show();

    /// <summary>Redraws the trail, the grab glyph and the release button at these positions.</summary>
    void Update(PhysicalPoint grabOrigin, PhysicalPoint cursorPos, double settleProgress,
        PhysicalPoint releasePos);

    /// <summary>Takes the overlay off screen and releases the window.</summary>
    void Close();
}
