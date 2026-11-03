namespace EyeCtrl.Core.Services;

using EyeCtrl.Core.Interfaces;

/// <summary>
/// What one <see cref="HeldButtonRelease.ReleaseIfMarked"/> attempt found and did.
/// </summary>
/// <param name="MarkerWasSet">Whether a marker existed — i.e. whether a button-up was sent.</param>
/// <param name="LeftButtonWasDown">What the optional OS probe reported before the release, or null
/// when no probe was supplied. Diagnostic only: the marker, never the probe, decides whether to
/// send, because the owner may be using a second gaze mouse.</param>
/// <param name="MarkerClearFailure">The exception that stopped the marker being cleared AFTER a
/// successful button-up, or null when the clear succeeded. The button is released either way; the
/// stale marker only costs one later inert button-up, but the log must not call that a failed
/// release (fixup-01 F5).</param>
/// <param name="LeftButtonStillDown">What the same probe reported AFTER the release settled, or
/// null when no probe was supplied. When it reads <c>true</c> the marker is KEPT: the button-up may
/// have been dropped by Windows (C8 F3's UIPI case), or the button is genuinely held by another
/// pointer device (a second gaze mouse, TD Control) — the two cannot be told apart, and keeping the
/// marker costs at most one inert button-up later.</param>
public readonly record struct HeldButtonReleaseResult(
    bool MarkerWasSet, bool? LeftButtonWasDown, Exception? MarkerClearFailure,
    bool? LeftButtonStillDown = null)
{
    /// <summary>No marker existed: nothing was sent.</summary>
    public static HeldButtonReleaseResult NoMarker => new(false, null, null);

    /// <summary>True when a marker was found, a button-up was sent, the marker was cleared and the
    /// post-release probe did not report the button still down.</summary>
    public bool ReleasedAndCleared =>
        MarkerWasSet && MarkerClearFailure is null && LeftButtonStillDown != true;

    /// <summary>True when the button-up was sent but the post-release probe still found the button
    /// down, so the marker was deliberately kept for the next release path.</summary>
    public bool MarkerKeptBecauseButtonDown => MarkerWasSet && LeftButtonStillDown == true;
}

/// <summary>
/// The one place that turns a held-button marker into a single left-button-up: shared by the
/// Supervisor (right after a force-kill) and the App (at startup, as the backstop for a kill the
/// Supervisor never saw — Task Manager, a native crash).
/// <para>
/// <b>Never sends when no marker exists.</b> The owner may be holding a physical mouse, or using a
/// second gaze mouse; an unconditional button-up here would release a button EyeCtrl never pressed.
/// </para>
/// <para>
/// The release goes through <see cref="IInputSimulator.ReleaseLeftButton"/>, which sends
/// <c>MOUSEEVENTF_LEFTUP</c> at the CURRENT cursor position — never <see cref="IInputSimulator.MouseUp"/>,
/// whose absolute move would teleport the pointer. The marker is cleared only after the send AND
/// only when the live OS button state is not still down (C8 F3): while an elevated window holds the
/// foreground, Windows silently discards the injected button-up, and clearing the marker then would
/// leave the button held with no record of it.
/// </para>
/// </summary>
public sealed class HeldButtonRelease
{
    /// <summary>The settle time between the button-up and the live state re-read, in milliseconds.
    /// <c>GetAsyncKeyState</c> right after <c>SendInput</c> shares F1's timing question: the call may
    /// return before the input thread has applied the event, and a stale "down" would keep the
    /// marker for no reason. A real UIPI block stays down for seconds, so the settle costs nothing
    /// when it matters.</summary>
    public const int PostReleaseSettleMilliseconds = 40;

    private readonly IHeldButtonMarker _marker;
    private readonly IInputSimulator _input;
    private readonly Func<bool>? _leftButtonIsDown;

    /// <summary>Creates the release operation over its two seams.</summary>
    /// <param name="marker">The cross-process held-button record.</param>
    /// <param name="input">The input sink; tests pass a recording fake — no real <c>SendInput</c>.</param>
    /// <param name="leftButtonIsDown">Optional OS probe (<c>GetAsyncKeyState(VK_LBUTTON)</c>). Read
    /// before the release for the log line, and again after it to decide whether the marker may be
    /// cleared; null skips the probe and preserves the pre-F3 behaviour (clear after the send).</param>
    public HeldButtonRelease(
        IHeldButtonMarker marker, IInputSimulator input, Func<bool>? leftButtonIsDown = null)
    {
        _marker = marker;
        _input = input;
        _leftButtonIsDown = leftButtonIsDown;
    }

    /// <summary>
    /// Sends exactly one in-place left-button-up when a marker exists, then clears the marker only
    /// if the button no longer reads down. Returns without touching input when no marker exists.
    /// </summary>
    /// <returns>Whether a marker was found (and thus a release sent), the probe readings, the
    /// clear's exception when the clear failed, and whether the marker was kept because the button
    /// still read down.</returns>
    /// <exception cref="Exception">A throwing sink or probe propagates and KEEPS the marker: the
    /// release did not verifiably happen, so the next path must retry it. A throwing CLEAR does not
    /// propagate (the button is already up); it is reported through
    /// <see cref="HeldButtonReleaseResult.MarkerClearFailure"/>.</exception>
    public HeldButtonReleaseResult ReleaseIfMarked()
    {
        if (!_marker.IsSet)
            return HeldButtonReleaseResult.NoMarker;

        // Read the OS state BEFORE the release so the log describes what was undone.
        bool? wasDown = _leftButtonIsDown?.Invoke();

        _input.ReleaseLeftButton();

        // C8 F3: SendInput can return success and deliver nothing (UIPI drops it silently). The
        // marker is the only cross-process record that EyeCtrl owes a button-up, so keep it while
        // the OS still says the button is down. Read after a short settle: a stale read shares
        // F1's timing premise and would keep the marker for no reason. A probe that throws also
        // keeps it — "unknown" is not "up".
        bool? stillDown = null;
        if (_leftButtonIsDown is { } probe)
        {
            Thread.Sleep(PostReleaseSettleMilliseconds);
            stillDown = probe();
        }

        if (stillDown == true)
            return new HeldButtonReleaseResult(true, wasDown, null, LeftButtonStillDown: true);

        // The send happened and the button reads up: a failed clear must not be reported as a
        // failed release, and must not escape (the caller's next step is only bookkeeping). It is
        // returned instead, so the caller can log exactly what happened (fixup-01 F5).
        try
        {
            _marker.Clear();
        }
        catch (Exception ex)
        {
            return new HeldButtonReleaseResult(true, wasDown, ex, LeftButtonStillDown: stillDown);
        }

        return new HeldButtonReleaseResult(true, wasDown, null, LeftButtonStillDown: stillDown);
    }
}
