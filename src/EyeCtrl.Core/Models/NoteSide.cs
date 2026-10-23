namespace EyeCtrl.Core.Models;

/// <summary>
/// The four sides of the anchor a note card can sit on (P3 D1). The enum's order IS the
/// placement cycle (<c>Right → Left → Below → Above</c>): the App's <c>NoteLayout</c> candidate
/// list and the "Other side" step both walk it front to back, so reordering the members would
/// silently change which side an "Other side" dwell lands on.
/// </summary>
public enum NoteSide
{
    /// <summary>The card sits to the RIGHT of the anchor; the tail points left.</summary>
    Right,
    /// <summary>The card sits to the LEFT of the anchor; the tail points right.</summary>
    Left,
    /// <summary>The card sits BELOW the anchor; the tail points up.</summary>
    Below,
    /// <summary>The card sits ABOVE the anchor; the tail points down.</summary>
    Above
}
