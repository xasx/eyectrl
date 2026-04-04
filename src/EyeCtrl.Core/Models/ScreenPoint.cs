namespace EyeCtrl.Core.Models;

/// <summary>
/// DEPRECATED: Use <see cref="PhysicalPoint"/> instead for type safety.
/// This type will be removed after migration is complete.
/// ScreenPoint is a temporary alias during the migration to PhysicalPoint.
/// </summary>
[Obsolete("Use PhysicalPoint instead for type safety. ScreenPoint will be removed after migration.")]
public record ScreenPoint(double X, double Y);
