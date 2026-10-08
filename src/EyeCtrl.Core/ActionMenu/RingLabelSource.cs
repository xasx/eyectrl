namespace EyeCtrl.Core.ActionMenu;

using EyeCtrl.Core.Models;

/// <summary>
/// The text a <see cref="RingPlan"/> composes into its slots, supplied by the surface that shows
/// the ring. A library plan cannot know which surface's keys it is drawing for: the Settings
/// preview passes <c>settings</c> keys and the live ring passes <c>overlays</c> keys, so the
/// action names and the two navigation affordances all arrive from the host.
/// </summary>
/// <param name="ActionLabel">Names one action slot. Never asked to name Back or More.</param>
/// <param name="BackLabel">Names both Back slots — exit menu and return to the first layer.</param>
/// <param name="MoreLabel">Names the More slot.</param>
public sealed record RingLabelSource(
    Func<InteractionAction, string> ActionLabel,
    string BackLabel,
    string MoreLabel)
{
    /// <summary>
    /// The English default — <see cref="RingLabelMap"/>'s vocabulary plus the literal "More".
    /// Every current host passes this until its own keys arrive.
    /// </summary>
    public static RingLabelSource English { get; } = new(
        action => RingLabelMap.For(action),
        RingLabelMap.For(null),
        "More");
}
