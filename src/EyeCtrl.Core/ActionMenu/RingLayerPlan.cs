namespace EyeCtrl.Core.ActionMenu;

/// <summary>
/// One built layer of the ring: its ordered slots and the angular pitch between neighbours.
/// Layers are built through <see cref="RingPlan.BuildLayer"/> so composition and geometry always
/// come from the same plan.
/// </summary>
public sealed class RingLayerPlan
{
    internal RingLayerPlan(RingLayer layer, IReadOnlyList<RingSlot> slots, double angleStepDegrees)
    {
        Layer = layer;
        Slots = slots;
        AngleStepDegrees = angleStepDegrees;
    }

    /// <summary>Which layer this plan describes.</summary>
    public RingLayer Layer { get; }

    /// <summary>The layer's slots in draw order, index 0 first.</summary>
    public IReadOnlyList<RingSlot> Slots { get; }

    /// <summary>
    /// Angular pitch between neighbouring slots: 360° divided by the layer's slot count. Zero for
    /// an empty layer — the runtime skips empty layers before drawing, and a zero step describes
    /// that case without dividing by zero.
    /// </summary>
    public double AngleStepDegrees { get; }
}
