namespace EyeCtrl.Contracts;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(UpdateMessage), "update")]
[JsonDerivedType(typeof(UnregisterMessage), "unregister")]
[JsonDerivedType(typeof(ActivatedMessage), "activated")]
public abstract record WireMessage;

/// <summary>Client→server: a control became dwellable. Rect is physical px; Hwnd is the owning top-level window.</summary>
public record RegisterMessage : WireMessage
{
    public required string Id { get; init; }
    public SdkRect Rect { get; init; }
    public long Hwnd { get; init; }
}

/// <summary>Client→server: an already-registered control's rect changed (no dwell reset).</summary>
public record UpdateMessage : WireMessage
{
    public required string Id { get; init; }
    public SdkRect Rect { get; init; }
}

/// <summary>Client→server: a control is no longer dwellable.</summary>
public record UnregisterMessage : WireMessage
{
    public required string Id { get; init; }
}

/// <summary>Server→client: dwell completed on this control; the SDK should invoke it.</summary>
public record ActivatedMessage : WireMessage
{
    public required string Id { get; init; }
}
