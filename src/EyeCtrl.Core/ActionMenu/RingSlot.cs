namespace EyeCtrl.Core.ActionMenu;

using EyeCtrl.Core.Models;

/// <summary>
/// One slot of a built ring layer: identity, payload, and the angle it is drawn at.
///
/// <para>
/// <see cref="AngleDegrees"/> is measured exactly as the runtime places items:
/// <c>angleStep × Index − 90</c> — index 0 starts at the top of the ring and angles grow
/// clockwise (screen Y grows downward). A host turns this into a position itself
/// (runtime: physical pixels via its DPI helper; preview: DIPs), so the slot stays doubles-only.
/// </para>
/// </summary>
public sealed class RingSlot
{
    internal RingSlot(
        string id,
        InteractionAction? action,
        ActionIcon icon,
        string label,
        ActionMenuItemKind kind,
        int index,
        double angleDegrees)
    {
        Id = id;
        Action = action;
        Icon = icon;
        Label = label;
        Kind = kind;
        Index = index;
        AngleDegrees = angleDegrees;
    }

    /// <summary>
    /// The dwell-target id: the action's enum name, or <c>More</c> / <c>BackToPrimary</c> /
    /// <c>Back</c> for the navigation affordances. Matches the ids the runtime registers today.
    /// </summary>
    public string Id { get; }

    /// <summary>The action the slot performs; null for the navigation affordances.</summary>
    public InteractionAction? Action { get; }

    /// <summary>Which drawn icon the slot shows (see <see cref="ActionIconMap"/>).</summary>
    public ActionIcon Icon { get; }

    /// <summary>The ring name (see <see cref="RingLabelMap"/>).</summary>
    public string Label { get; }

    /// <summary>What a dwell on the slot does.</summary>
    public ActionMenuItemKind Kind { get; }

    /// <summary>Position in the layer, 0 first.</summary>
    public int Index { get; }

    /// <summary>Draw angle from the ring centre in degrees; −90° is the top.</summary>
    public double AngleDegrees { get; }
}
