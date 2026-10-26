namespace EyeCtrl.Core.Status;

/// <summary>
/// Read-only view of the one source of truth for "do you have control?" (Wave B ticket B1).
/// <para>
/// The live implementation also publishes every change as a
/// <c>EyeCtrl.ViewModels.Messages.ControlStatusChangedMessage</c>. This interface exists for
/// consumers that need the CURRENT value at construction and then the message for changes — a
/// messenger has no replay, so a surface that subscribes after startup must read this first.
/// </para>
/// </summary>
public interface IControlStatus
{
    /// <summary>The current status. Safe to read from any thread.</summary>
    ControlStatus Current { get; }

    /// <summary>
    /// Raised on every status change, on the UI thread in the running app. The payload is the whole
    /// status, so a consumer never has to combine it with <see cref="Current"/> (which may already
    /// be newer if the handler runs late).
    /// </summary>
    event Action<ControlStatus>? Changed;
}
