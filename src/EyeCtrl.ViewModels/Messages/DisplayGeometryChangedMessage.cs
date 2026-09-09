namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>
/// The display's addressable space changed — a dock, undock, resolution change, scaling change or
/// RDP reconnect. Carries the complete new geometry so every recipient reconciles against the same
/// snapshot instead of re-reading the system independently and possibly disagreeing.
/// </summary>
/// <remarks>
/// Sent on the dispatcher, after <c>DpiHelper</c> has already been updated, so a recipient that
/// converts logical to physical units during its own refresh gets the new scale.
/// </remarks>
public record DisplayGeometryChangedMessage(DisplayGeometry Geometry);
