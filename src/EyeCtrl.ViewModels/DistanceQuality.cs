namespace EyeCtrl.ViewModels;

/// <summary>
/// Head-distance quality for the Track Status overlay.
/// </summary>
/// <remarks>
/// This enum is the comparison key: the overlay picks its colour from it, while the displayed
/// word comes from a separate mapping in the window. The two used to be one string, so
/// translating the word would have silently turned every quality grey.
/// </remarks>
public enum DistanceQuality
{
    /// <summary>No usable distance reading (the default, and the tracker's no-signal state).</summary>
    Unknown,

    /// <summary>Within the PCEye 5 optimal range.</summary>
    Good,

    /// <summary>Usable, but outside the optimal range.</summary>
    Fair,

    /// <summary>Too close or too far to track reliably.</summary>
    Poor,
}
