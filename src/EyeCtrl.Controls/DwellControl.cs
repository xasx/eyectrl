namespace EyeCtrl.Controls;

using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Abstract base class for dwell-enabled WPF controls.
/// </summary>
/// <remarks>
/// <para>
/// Each subclass owns a single <see cref="Root"/> <see cref="Border"/> that represents
/// its visual element in the WPF panel. Subclasses implement <see cref="CreateRoot"/> to
/// configure the border, <see cref="OnActivated"/> to respond to a completed dwell, and
/// <see cref="ApplyProgress"/> to update the visual feedback as dwell accumulates.
/// </para>
/// <para>
/// Lifecycle: call <see cref="Attach"/> to add the control to a panel and register it
/// for dwell detection; call <see cref="Detach"/> to remove and unregister it.
/// Composite controls (see <see cref="IDwellComposite"/>) that manage multiple
/// <see cref="DwellControl"/> children call <see cref="RegisterDwell"/> /
/// <see cref="UnregisterDwell"/> directly instead.
/// </para>
/// <para>
/// Position updates: call <see cref="UpdatePosition"/> and <see cref="UpdateVisual"/>
/// from the overlay's per-frame rendering loop to keep dwell detection accurate and
/// visual feedback current.
/// </para>
/// </remarks>
public abstract class DwellControl
{
    /// <summary>Gets the unique identifier used to register this target with the <see cref="Registrar"/>.</summary>
    public string DwellId { get; }

    /// <summary>
    /// Gets the root <see cref="Border"/> element that represents this control in the WPF visual tree.
    /// Subclasses configure this element in <see cref="CreateRoot"/>; composites may further
    /// override its properties after construction.
    /// </summary>
    public Border Root { get; }

    /// <summary>Gets the <see cref="IDwellRegistrar"/> this control is registered with.</summary>
    public IDwellRegistrar Registrar { get; }

    /// <summary>Gets the dwell category this control is registered under.</summary>
    public DwellCategory Category { get; }

    /// <summary>Gets the hit-test radius in physical screen pixels.</summary>
    public double RadiusPhysical { get; }

    /// <summary>Gets the dwell duration in milliseconds required to activate this control.</summary>
    public int DwellMs { get; }

    /// <summary>
    /// Initialises the control and calls <see cref="CreateRoot"/> to build the root border.
    /// </summary>
    /// <param name="registrar">The registrar that will track this target's dwell state.</param>
    /// <param name="dwellId">Unique identifier within the given <paramref name="category"/>.</param>
    /// <param name="category">UI section this control belongs to.</param>
    /// <param name="radiusPhysical">Hit-test radius in physical screen pixels.</param>
    /// <param name="dwellMs">Dwell duration in milliseconds.</param>
    protected DwellControl(
        IDwellRegistrar registrar,
        string dwellId,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        Registrar = registrar;
        DwellId = dwellId;
        Category = category;
        RadiusPhysical = radiusPhysical;
        DwellMs = dwellMs;
        Root = CreateRoot();
    }

    /// <summary>Creates and returns the root <see cref="Border"/> for this control.</summary>
    protected abstract Border CreateRoot();

    /// <summary>Called by the registrar when dwell on this target completes.</summary>
    protected abstract void OnActivated();

    /// <summary>
    /// Updates the visual state of the control to reflect the current dwell progress.
    /// </summary>
    /// <param name="progress">Dwell progress in the range [0.0, 1.0].</param>
    protected abstract void ApplyProgress(double progress);

    /// <summary>
    /// Adds <see cref="Root"/> to <paramref name="parent"/> and registers this target for dwell detection.
    /// </summary>
    /// <param name="parent">The panel to add this control's root element to.</param>
    public void Attach(Panel parent)
    {
        parent.Children.Add(Root);
        RegisterDwell();
    }

    /// <summary>
    /// Removes <see cref="Root"/> from <paramref name="parent"/> and unregisters this target.
    /// </summary>
    /// <param name="parent">The panel that currently contains this control's root element.</param>
    public void Detach(Panel parent)
    {
        parent.Children.Remove(Root);
        UnregisterDwell();
    }

    /// <summary>
    /// Registers this target with the <see cref="Registrar"/> without adding it to a panel.
    /// Use this when the control is a child of an <see cref="IDwellComposite"/> that manages
    /// panel insertion separately.
    /// </summary>
    public void RegisterDwell()
    {
        Registrar.Register(DwellId, Category, RadiusPhysical, DwellMs, OnActivated);
    }

    /// <summary>
    /// Unregisters this target from the <see cref="Registrar"/> without removing it from a panel.
    /// </summary>
    public void UnregisterDwell()
    {
        Registrar.Unregister(DwellId, Category);
    }

    /// <summary>
    /// Reads <see cref="Root"/>'s current screen position and forwards it to the
    /// <see cref="Registrar"/> so dwell hit-testing uses up-to-date coordinates.
    /// Call this every frame from the overlay's rendering loop.
    /// </summary>
    public void UpdatePosition()
    {
        try
        {
            var pos = Root.PointToScreen(
                new Point(Root.ActualWidth / 2, Root.ActualHeight / 2));
            Registrar.UpdatePosition(DwellId, Category, new PhysicalPoint(pos.X, pos.Y));
        }
        catch (InvalidOperationException)
        {
            // Not yet rendered or not in visual tree
        }
    }

    /// <summary>
    /// Queries the current dwell progress from the <see cref="Registrar"/> and calls
    /// <see cref="ApplyProgress"/> to refresh the visual feedback.
    /// Call this every frame from the overlay's rendering loop.
    /// </summary>
    public void UpdateVisual()
    {
        double progress = Registrar.GetProgress(DwellId, Category);
        ApplyProgress(progress);
    }

    /// <summary>
    /// Applies <paramref name="progress"/> directly, bypassing the registrar.
    ///
    /// <see cref="UpdateVisual"/> is the production path and reads progress from the
    /// <see cref="Registrar"/>; this exists for tests — and any future composite — that hold a
    /// child control and need to drive its feedback with a known value.
    /// </summary>
    /// <param name="progress">Dwell progress in [0, 1].</param>
    public void UpdateVisualForTest(double progress) => ApplyProgress(progress);
}
