namespace EyeCtrl.App.Controls;

using System.Collections.Generic;
using System.Windows.Controls;

/// <summary>
/// Represents a composite dwell control that owns multiple <see cref="DwellControl"/> children.
/// </summary>
/// <remarks>
/// Composites (e.g. <see cref="DwellSlider"/>, <see cref="DwellComboBox"/>,
/// <see cref="DwellColorPicker"/>) manage their own internal panel layout and register
/// each child individually via <see cref="DwellControl.RegisterDwell"/> /
/// <see cref="DwellControl.UnregisterDwell"/> rather than <see cref="DwellControl.Attach"/> /
/// <see cref="DwellControl.Detach"/>.
/// The overlay's per-frame loop iterates <see cref="Children"/> to call
/// <see cref="DwellControl.UpdatePosition"/> and <see cref="DwellControl.UpdateVisual"/>
/// on each child.
/// </remarks>
public interface IDwellComposite
{
    /// <summary>
    /// Adds this composite's visual elements to <paramref name="parent"/> and registers
    /// all child dwell targets.
    /// </summary>
    /// <param name="parent">The panel to add visual elements to.</param>
    void Attach(Panel parent);

    /// <summary>
    /// Removes this composite's visual elements from <paramref name="parent"/> and
    /// unregisters all child dwell targets.
    /// </summary>
    /// <param name="parent">The panel that currently hosts this composite's elements.</param>
    void Detach(Panel parent);

    /// <summary>
    /// The individual dwell targets that make up this composite.
    /// Iterated by the overlay's per-frame loop for position and visual updates.
    /// </summary>
    IEnumerable<DwellControl> Children { get; }
}
