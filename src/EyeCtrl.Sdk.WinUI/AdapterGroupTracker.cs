namespace EyeCtrl.Sdk.WinUI;

using System.Collections.Concurrent;

/// <summary>
/// Tracks which adapter ids are currently registered on a per-UI-thread group, and invokes
/// a single callback when the last id is removed. The caller wires the callback to whatever
/// cleanup the group needs (stop the refresh timer, evict from the dispatcher-keyed registry).
/// </summary>
internal sealed class AdapterGroupTracker
{
    private readonly Action _onLastRemoved;
    private readonly ConcurrentDictionary<string, byte> _ids = new();

    /// <summary>True when no ids are currently registered.</summary>
    public bool IsEmpty => _ids.IsEmpty;

    /// <summary>
    /// Creates a tracker. <paramref name="onLastRemoved"/> is invoked at most once per
    /// add-then-remove cycle, when the tracked set transitions from non-empty to empty.
    /// </summary>
    public AdapterGroupTracker(Action onLastRemoved) => _onLastRemoved = onLastRemoved;

    /// <summary>Registers an adapter id. Idempotent for the same id.</summary>
    public void Add(string id) => _ids[id] = 0;

    /// <summary>Unregisters an adapter id. If this empties the set, invokes the callback.</summary>
    public void Remove(string id)
    {
        if (_ids.TryRemove(id, out _) && _ids.IsEmpty) _onLastRemoved();
    }
}
