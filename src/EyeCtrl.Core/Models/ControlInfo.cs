namespace EyeCtrl.Core.Models;

/// <summary>Resolved interactive UI control for Auto Selection targeting.</summary>
public record ControlInfo(PhysicalRect BoundingRect)
{
    /// <summary>Center of the control's bounding rectangle — the click target.</summary>
    public PhysicalPoint CenterPoint => BoundingRect.Center;
}
