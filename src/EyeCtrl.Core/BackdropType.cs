namespace EyeCtrl.Core;

/// <summary>Background material type for the overlay window.</summary>
public enum BackdropType
{
    /// <summary>Windows 11 Acrylic blur material — semi-transparent with noise texture.</summary>
    Acrylic,
    /// <summary>Windows 11 Mica material — samples the desktop wallpaper for a tinted background.</summary>
    Mica,
    /// <summary>Solid dark semi-transparent overlay without blur effects.</summary>
    SolidDarken
}
