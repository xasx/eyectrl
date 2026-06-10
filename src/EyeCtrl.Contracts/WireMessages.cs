namespace EyeCtrl.Contracts;

using System.Text.Json.Serialization;

/// <summary>Base type for the newline-framed JSON messages exchanged between an SDK head and the EyeCtrl dwell service.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(UpdateMessage), "update")]
[JsonDerivedType(typeof(UnregisterMessage), "unregister")]
[JsonDerivedType(typeof(ActivatedMessage), "activated")]
public abstract record WireMessage;

/// <summary>Client→server: a control became dwellable. Rect is physical px; Hwnd is the owning top-level window.</summary>
public record RegisterMessage : WireMessage
{
    /// <summary>Stable id the head assigned to the dwellable control.</summary>
    public required string Id { get; init; }
    /// <summary>The control's bounds in physical screen pixels.</summary>
    public SdkRect Rect { get; init; }
    /// <summary>Handle of the owning top-level window (used for occlusion gating).</summary>
    public long Hwnd { get; init; }
}

/// <summary>Client→server: an already-registered control's rect changed (no dwell reset).</summary>
public record UpdateMessage : WireMessage
{
    /// <summary>Id of the previously-registered control.</summary>
    public required string Id { get; init; }
    /// <summary>The control's new bounds in physical screen pixels.</summary>
    public SdkRect Rect { get; init; }
}

/// <summary>Client→server: a control is no longer dwellable.</summary>
public record UnregisterMessage : WireMessage
{
    /// <summary>Id of the control that is no longer dwellable.</summary>
    public required string Id { get; init; }
}

/// <summary>Server→client: dwell completed on this control; the SDK should invoke it.</summary>
public record ActivatedMessage : WireMessage
{
    /// <summary>Id of the control whose dwell completed.</summary>
    public required string Id { get; init; }
}
