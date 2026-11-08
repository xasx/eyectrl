namespace EyeCtrl.App.Services;

using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// One <c>Activation outcome</c> Information line per ACCEPTED activation, written when its session
/// ends (R3, the owner's Q5 answer: no recording first — the logs count the unintended opens). With
/// R1's <c>Offer shown/expired/fired</c> lines this yields, per day: rests started, cancelled, fired,
/// and of those fired how many ended in an action versus being closed unused.
/// <para>
/// <see cref="NoteAccepted"/> is called by <see cref="GazeEngineAdapter"/> exactly where it writes
/// its <c>Offer accepted</c> line, with the offer's id, its style word (<c>rest</c>/<c>button</c>)
/// and the live selection mode. The session resolves at the FIRST of: an action (the same
/// <see cref="ActionRequestedMessage"/> traffic that writes today's <c>Action:</c> lines, or a
/// drag/scroll start — <see cref="InteractionState.Dragging"/>/<see cref="InteractionState.ScrollMode"/>
/// entry, which raise no ActionRequested), a return to FreeLook (closed unused: Back, ✕, timeout,
/// looking away — the unintended-open count), Pause, or an unexpected state (<c>other</c>, the state
/// named). Exactly one line per accept; an EXPIRED (cancelled) offer never reaches
/// <see cref="NoteAccepted"/>, so it writes no outcome line — its <c>Offer expired unused</c> line
/// from R1 already counts it.
/// </para>
/// <para>
/// Everything arrives on the UI thread: the accept inside the adapter's
/// <see cref="ActivationCompletedMessage"/> handler, and both message types through the same
/// dispatcher marshalling the adapter applies to its Core→UI traffic. No synchronization is needed,
/// and none is used.
/// </para>
/// </summary>
internal sealed class ActivationOutcomeLog : IDisposable
{
    private readonly IMessenger _messenger;
    private readonly Action<string> _info;
    private readonly Func<long> _msClock;
    private Session? _open;

    /// <summary>The accepted activation whose end has not been logged yet.</summary>
    private sealed record Session(long OfferId, string Style, string Mode, long AcceptedAtMs);

    public ActivationOutcomeLog(IMessenger messenger, Action<string> info, Func<long>? msClock = null)
    {
        _messenger = messenger;
        _info = info;
        _msClock = msClock ?? (() => Environment.TickCount64);
        _messenger.Register<ActivationOutcomeLog, StateChangedMessage>(
            this, static (tracker, message) => tracker.OnStateChanged(message.NewState));
        _messenger.Register<ActivationOutcomeLog, ActionRequestedMessage>(
            this, static (tracker, _) => tracker.Resolve("action", null));
    }

    /// <summary>Notes the accepted offer. Called once per accept; a still-open session (a wiring
    /// anomaly — the state machine cannot accept twice without ending the first) is closed as
    /// <c>other state=superseded</c> first, so the one-line-per-accept invariant holds either way.</summary>
    public void NoteAccepted(long offerId, string style, SelectionMode mode)
    {
        if (_open is not null)
            Resolve("other", "superseded");
        _open = new Session(offerId, style, mode.ToString(), _msClock());
    }

    private void OnStateChanged(InteractionState state)
    {
        if (_open is null)
            return;
        switch (state)
        {
            case InteractionState.FreeLook:
                // The session's surfaces are gone with no action recorded on the way out: Back, ✕,
                // the timeout, or looking away. This is the unintended-open count.
                Resolve("closed", null);
                break;
            case InteractionState.Paused:
                Resolve("paused", null);
                break;
            case InteractionState.Dragging:
            case InteractionState.ScrollMode:
                // A drag or scroll START is the session's action too: OnActionSelected moves into
                // the mode without raising ActionRequested, and the FreeLook that later ends the
                // mode must not re-report the (already resolved) session.
                Resolve("action", null);
                break;
            case InteractionState.Fixation:
            case InteractionState.ActionMenuVisible:
            case InteractionState.ManualSelectionActive:
            case InteractionState.ManualSelectionActionMenuVisible:
                // The session's own interior states — everything between the accept and its end.
                break;
            default:
                // RepeatMode (and anything a future state adds): not a way an activation session
                // ends today, so it is named rather than guessed at.
                Resolve("other", state.ToString());
                break;
        }
    }

    private void Resolve(string result, string? state)
    {
        if (_open is not { } session)
            return;
        _open = null;
        _info(OutcomeLine(
            session.OfferId, session.Style, session.Mode, result, _msClock() - session.AcceptedAtMs, state));
    }

    /// <summary>The exact line <c>tools/activation_log_metrics.py</c> parses; the invariant-culture
    /// twin of <see cref="RingArmLog"/>'s formats.</summary>
    internal static string OutcomeLine(
        long offerId, string style, string mode, string result, double heldMs, string? state = null) =>
        string.Format(CultureInfo.InvariantCulture,
            "Activation outcome #{0} style={1} mode={2} result={3}{4} heldMs={5:0}",
            offerId, style, mode, result, state is null ? "" : " state=" + state, heldMs);

    public void Dispose() => _messenger.UnregisterAll(this);
}
