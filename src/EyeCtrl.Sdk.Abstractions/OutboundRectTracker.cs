namespace EyeCtrl.Sdk.Abstractions;

using EyeCtrl.Contracts;

/// <summary>
/// Pure, UI-free outbound dedupe. First time an id is seen → RegisterMessage; subsequent
/// changed rects → UpdateMessage; unchanged → null (skip). Reset() forces re-Register on reconnect.
/// </summary>
public sealed class OutboundRectTracker
{
    private readonly Dictionary<string, SdkRect> _sent = new();

    /// <summary>
    /// Records the latest rect for <paramref name="id"/> and returns the message to send: a
    /// <see cref="RegisterMessage"/> on first sighting, an <see cref="UpdateMessage"/> when the rect
    /// changed, or <c>null</c> when unchanged.
    /// </summary>
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

    /// <summary>Forgets <paramref name="id"/> and returns the <see cref="UnregisterMessage"/> to send.</summary>
    public WireMessage OnUnregister(string id)
    {
        _sent.Remove(id);
        return new UnregisterMessage { Id = id };
    }

    /// <summary>Clears all remembered rects so the next <see cref="OnRect"/> per id re-registers (used after reconnect).</summary>
    public void Reset() => _sent.Clear();
}
