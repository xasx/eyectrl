namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

/// <summary>
/// Pure, UI-free outbound dedupe. First time an id is seen → RegisterMessage; subsequent
/// changed rects → UpdateMessage; unchanged → null (skip). Reset() forces re-Register on reconnect.
/// </summary>
public sealed class OutboundRectTracker
{
    private readonly Dictionary<string, SdkRect> _sent = new();

    public WireMessage? OnRect(string id, SdkRect rect, long hwnd)
    {
        if (!_sent.TryGetValue(id, out var prev))
        {
            _sent[id] = rect;
            return new RegisterMessage { Id = id, Rect = rect, Hwnd = hwnd };
        }
        if (prev.Equals(rect)) return null;
        _sent[id] = rect;
        return new UpdateMessage { Id = id, Rect = rect };
    }

    public WireMessage OnUnregister(string id)
    {
        _sent.Remove(id);
        return new UnregisterMessage { Id = id };
    }

    public void Reset() => _sent.Clear();
}
