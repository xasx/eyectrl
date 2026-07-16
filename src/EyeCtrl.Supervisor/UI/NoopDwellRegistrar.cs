namespace EyeCtrl.Supervisor.UI;

using System;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// A no-op <see cref="IDwellRegistrar"/> for the supervisor's standalone WPF surfaces.
/// </summary>
/// <remarks>
/// The supervisor has no eye-tracker / overlay render loop, so genuine gaze-dwell
/// detection is unavailable here. <see cref="EyeCtrl.Controls.DwellButton"/> and
/// <see cref="EyeCtrl.Controls.DwellToggle"/> still fire on a mouse-click fallback
/// (<c>Root.MouseLeftButtonDown</c>), which is exactly how they are activated in the
/// FALLEN_BACK scenario: the fallback AT (already running) drives the OS cursor, or the
/// user clicks directly. Using the dwell controls (rather than plain Buttons) keeps the
/// supervisor's UI homogeneous and leaves the door open to a future supervisor-side
/// dwell provider without changing the XAML. <see cref="GetProgress"/> always reports 0,
/// so no dwell feedback animation runs; activation is instantaneous on click.
/// </remarks>
internal sealed class NoopDwellRegistrar : IDwellRegistrar
{
    /// <inheritdoc />
    public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated)
    {
        // No gaze loop to feed; the control's own click fallback drives activation.
    }

    /// <inheritdoc />
    public void Unregister(string id, DwellCategory category)
    {
    }

    /// <inheritdoc />
    public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center)
    {
    }

    /// <inheritdoc />
    public double GetProgress(string id, DwellCategory category) => 0.0;
}
