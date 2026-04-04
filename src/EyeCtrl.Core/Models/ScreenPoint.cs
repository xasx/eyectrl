namespace EyeCtrl.Core.Models;

/// <summary>
/// DEPRECATED: Use <see cref="PhysicalPoint"/> instead for type safety.
/// This type will be removed after migration is complete.
/// ScreenPoint is a separate legacy type that is being replaced by PhysicalPoint.
/// It is not a type alias - it is an obsolete type that should not be used in new code.
/// </summary>
[Obsolete("Use PhysicalPoint instead for type safety. ScreenPoint will be removed after migration.")]
public record ScreenPoint(double X, double Y);
