namespace EyeCtrl.Core.ActionMenu;

using EyeCtrl.Core.Localization;
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
    /// The live ring's source: resolves <see cref="RingLabelMap"/>'s <c>overlays</c> keys and the
    /// <c>more</c> key through the process table, so the ring follows the overlays surface's
    /// language. Computed per access rather than cached, so a table activated later in the process
    /// is picked up and no test can freeze a stale one.
    /// </summary>
    public static RingLabelSource Overlays => new(
        action => StringTable.Current.T(RingLabelMap.For(action)),
        StringTable.Current.T(RingLabelMap.For(null)),
        StringTable.Current.T("overlays.ring.more"));

    /// <summary>
    /// The embedded-English source — <see cref="RingLabelMap"/>'s vocabulary resolved against the
    /// compiled English table. The fallback default for a host that has not chosen a source yet
    /// (the modes menu before composition, the Settings preview until T07 passes <c>settings</c>
    /// keys), and the deterministic vocabulary the Core tests assert.
    /// </summary>
    public static RingLabelSource English => new(
        action => StringTable.EmbeddedEnglish.T(RingLabelMap.For(action)),
        StringTable.EmbeddedEnglish.T(RingLabelMap.For(null)),
        StringTable.EmbeddedEnglish.T("overlays.ring.more"));
}
