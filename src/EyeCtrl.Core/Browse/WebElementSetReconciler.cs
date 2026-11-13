namespace EyeCtrl.Core.Browse;

/// <summary>
/// The change set between two element snapshots, produced purely by
/// <see cref="WebElementSetReconciler.Plan"/>. Every bucket carries the <em>next</em>
/// snapshot's element, except <see cref="WebElementReconcilePlan.Removes"/> which carries
/// the <em>previous</em> snapshot's (the info needed to unregister what is gone). A
/// re-added id appears only in <c>ReAdds</c> — a re-add is its own remove+add pair and
/// implies discarding that target's accumulated dwell — so no id ever appears in two
/// buckets.
/// </summary>
/// <param name="Adds">Elements absent from the previous snapshot: register fresh.</param>
/// <param name="ReAdds">Elements whose rect moved more than the reset threshold (page
/// scroll / relayout): unregister and register fresh, discarding accumulated dwell — an
/// element scrolling under a resting gaze must not complete a dwell it no longer sits on.</param>
/// <param name="Rebases">Elements that stayed within the threshold but did not keep their
/// exact rect: update in place with state-preserving semantics (the
/// DwellDetector.RebaseTarget contract — accumulated dwell and the routed hit-area margin
/// survive a small drift).</param>
/// <param name="Removes">Elements present before and absent now: unregister.</param>
public sealed record WebElementReconcilePlan(
    IReadOnlyList<WebElementInfo> Adds,
    IReadOnlyList<WebElementInfo> ReAdds,
    IReadOnlyList<WebElementInfo> Rebases,
    IReadOnlyList<WebElementInfo> Removes)
{
    /// <summary>A plan with nothing to do (all buckets empty).</summary>
    public static WebElementReconcilePlan None { get; } = new(
        Array.Empty<WebElementInfo>(),
        Array.Empty<WebElementInfo>(),
        Array.Empty<WebElementInfo>(),
        Array.Empty<WebElementInfo>());

    /// <summary>Whether no bucket carries an entry.</summary>
    public bool IsEmpty => Adds.Count == 0 && ReAdds.Count == 0 && Rebases.Count == 0 && Removes.Count == 0;
}

/// <summary>
/// Partitions one elements snapshot against the previous one into adds / re-adds /
/// rebases / removes. Pure: nothing here touches a dwell detector — the caller applies
/// the plan (register, remove+register, RebaseTarget, unregister). Element identity is
/// the id; geometry is the physical rect.
/// <para>The move test compares each axis of the rect's top-left corner against the
/// threshold (a pure resize therefore rebases rather than resets — the region under a
/// resting gaze keeps meaning the same element). A rect that is exactly unchanged
/// produces no entry at all, so re-planning an unchanged snapshot is idempotent.
/// Duplicate ids within one snapshot keep their first occurrence; a negative threshold
/// is treated as 0 (every movement resets).</para>
/// </summary>
public static class WebElementSetReconciler
{
    /// <summary>
    /// Computes the change set from <paramref name="previous"/> to <paramref name="next"/>.
    /// A null previous (first snapshot after navigation) adds everything.
    /// </summary>
    /// <param name="previous">The snapshot the dwell targets currently reflect, or null
    /// when nothing is registered yet.</param>
    /// <param name="next">The freshly parsed snapshot to reconcile against it.</param>
    /// <param name="moveResetThresholdPx">Top-left displacement (either axis, in physical
    /// pixels) above which an element counts as moved rather than drifted.</param>
    /// <returns>The partitioned plan; buckets are mutually exclusive by id.</returns>
    public static WebElementReconcilePlan Plan(
        WebElementSnapshot? previous,
        WebElementSnapshot next,
        double moveResetThresholdPx)
    {
        var threshold = Math.Max(0, moveResetThresholdPx);

        var previousElements = Deduplicated(previous?.Elements);
        var nextElements = Deduplicated(next.Elements);
        var previousById = new Dictionary<string, WebElementInfo>(previousElements.Count, StringComparer.Ordinal);
        foreach (var element in previousElements)
        {
            previousById.Add(element.Id, element);
        }

        var nextIds = new HashSet<string>(nextElements.Count, StringComparer.Ordinal);
        foreach (var element in nextElements)
        {
            nextIds.Add(element.Id);
        }

        var adds = new List<WebElementInfo>();
        var reAdds = new List<WebElementInfo>();
        var rebases = new List<WebElementInfo>();
        foreach (var element in nextElements)
        {
            if (!previousById.TryGetValue(element.Id, out var before))
            {
                adds.Add(element);
                continue;
            }

            var nextRect = element.RectPhysical;
            var previousRect = before.RectPhysical;
            bool moved = Math.Abs(nextRect.X - previousRect.X) > threshold
                || Math.Abs(nextRect.Y - previousRect.Y) > threshold;
            if (moved)
            {
                reAdds.Add(element);
            }
            else if (nextRect != previousRect)
            {
                rebases.Add(element);
            }
            // Exactly unchanged: no entry — re-planning an unchanged snapshot is a no-op.
        }

        var removes = new List<WebElementInfo>();
        foreach (var element in previousElements)
        {
            if (!nextIds.Contains(element.Id))
            {
                removes.Add(element);
            }
        }

        return new WebElementReconcilePlan(adds, reAdds, rebases, removes);
    }

    /// <summary>Deduplicates by id (first occurrence wins, so the snapshot's first report
    /// of an id — its position when the page enumerated it — is the one used) while
    /// preserving the report order for deterministic plans.</summary>
    private static List<WebElementInfo> Deduplicated(IReadOnlyList<WebElementInfo>? elements)
    {
        var result = new List<WebElementInfo>();
        if (elements is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (seen.Add(element.Id))
            {
                result.Add(element);
            }
        }

        return result;
    }
}
