namespace EyeCtrl.Core;

/// <summary>Background material type for the overlay window.</summary>
/// <remarks>
/// One blur member: Mica and Acrylic drew the same material since 2026-10-14 (the legacy acrylic
/// accent blur — the DWM system backdrops only ever showed their flat inactive fallback on this
/// never-activated overlay), so they merged into <see cref="Glass"/>. Files holding the old names
/// are rewritten by <c>SettingsService</c>'s pre-deserialize migration before this enum ever
/// sees them.
/// <para>
/// The values are EXPLICIT for on-disk numeric compatibility: the string-enum converter accepts
/// integers, the old member order was Acrylic = 0, Mica = 1, SolidDarken = 2, and this owner
/// hand-edits settings.json — so 0 must stay Glass and 2 must stay Solid darken, or a
/// hand-written number would silently change the material. (The old Mica's 1 is undefined here
/// on purpose; the migration rewrites it at the JSON node.) Nothing else depends on the values:
/// the combo codes read <c>Enum.GetNames</c> (ordered by underlying value — 0, then 2) and every
/// parse is by name.
/// </para>
/// </remarks>
public enum BackdropType
{
    /// <summary>The live blur — the legacy acrylic accent at the fixed glass tint.</summary>
    Glass = 0,
    /// <summary>Solid dark semi-transparent overlay without blur effects.</summary>
    SolidDarken = 2
}
