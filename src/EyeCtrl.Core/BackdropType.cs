namespace EyeCtrl.Core;

/// <summary>Background material type for the overlay window.</summary>
/// <remarks>
/// One blur member: Mica and Acrylic drew the same material since 2026-10-14 (the legacy acrylic
/// accent blur — the DWM system backdrops only ever showed their flat inactive fallback on this
/// never-activated overlay), so they merged into <see cref="Glass"/>. Files holding the old names
/// are rewritten by <c>SettingsService</c>'s pre-deserialize migration before this enum ever
/// sees them.
/// </remarks>
public enum BackdropType
{
    /// <summary>The live blur — the legacy acrylic accent at the fixed glass tint.</summary>
    Glass,
    /// <summary>Solid dark semi-transparent overlay without blur effects.</summary>
    SolidDarken
}
