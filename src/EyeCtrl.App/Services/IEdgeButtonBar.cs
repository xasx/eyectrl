namespace EyeCtrl.App.Services;

using System.Collections.Generic;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The edge-button bar seam: the screen-edge buttons that are the only way out of a drag or scroll
/// mode for a user whose sole input is an eye tracker.
/// </summary>
/// <remarks>
/// <b>Why this interface exists.</b> <see cref="IOverlayManager.EdgeButtonBar"/> was typed as the
/// concrete <c>EdgeButtonBarWindow</c>, so any test that reached it — even one testing logic with
/// nothing to do with the bar's appearance — had to supply a real WPF window. That meant an STA
/// thread, a process-wide <c>Application</c> singleton and the app-level resource dictionary the
/// window's theme bindings resolve against.
/// <para>
/// The cost was concrete and it hid a defect. <c>DragCoordinator</c>'s success path could not be
/// exercised headlessly at all: its setup calls <c>ShowButtons</c>, so reaching "a drag is
/// genuinely in progress" required the whole WPF harness. The tests could therefore only cover the
/// path where setup FAILS, and deleting the release-on-dispose left them all green — the mouse
/// button being left pressed on tray Exit was invisible to the suite for exactly this reason.
/// </para>
/// <para>
/// The members here are the complete set the App layer actually consumes, taken from the twelve
/// call sites rather than from the window's public surface. <c>SyncButtons</c>, <c>Show</c> and
/// <c>Window.Hide</c> are deliberately absent: they are how <c>OverlayManager</c> drives the window
/// it owns, not how the rest of the app asks for buttons, and including them would drag WPF back
/// across the seam.
/// </para>
/// </remarks>
public interface IEdgeButtonBar
{
    /// <summary>
    /// The bound view model, or null before binding. Exposed because callers need to ask what is
    /// currently on the bar — <c>OverlayMessageCoordinator</c> checks whether the action menu's
    /// close button is present before hiding, which is what stops a deferred menu-dismiss from
    /// clearing a drag's cancel button the instant the drag begins.
    /// </summary>
    EdgeButtonBarViewModel? EdgeBarVm { get; }

    /// <summary>Replaces the bar's contents with <paramref name="definitions"/> and shows it.</summary>
    void ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor);

    /// <summary>Adds one button, leaving the others in place.</summary>
    void AddButton(EdgeButtonDefinition definition);

    /// <summary>Removes the button with the given id, if present.</summary>
    void RemoveButton(string id);

    /// <summary>
    /// Clears the bar and hides it. Note this is the view-model-level hide, NOT
    /// <c>Window.Hide</c> — the window responds to the view model's visibility change.
    /// </summary>
    void Hide();
}
