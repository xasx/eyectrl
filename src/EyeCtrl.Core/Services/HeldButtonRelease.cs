namespace EyeCtrl.Core.Services;

using EyeCtrl.Core.Interfaces;

/// <summary>
/// What one <see cref="HeldButtonRelease.ReleaseIfMarked"/> attempt found and did.
/// </summary>
/// <param name="MarkerWasSet">Whether a marker existed — i.e. whether a button-up was sent.</param>
/// <param name="LeftButtonWasDown">What the optional OS probe reported before the release, or null
/// when no probe was supplied. Diagnostic only: the marker, never the probe, decides whether to
/// send, because the owner may be using a second gaze mouse.</param>
public readonly record struct HeldButtonReleaseResult(bool MarkerWasSet, bool? LeftButtonWasDown)
{
    /// <summary>No marker existed: nothing was sent.</summary>
    public static HeldButtonReleaseResult NoMarker => new(false, null);
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
/// whose absolute move would teleport the pointer. The marker is cleared only after the send, so a
/// throwing sink leaves it in place for the next release path to retry.
/// </para>
/// </summary>
public sealed class HeldButtonRelease
{
    private readonly IHeldButtonMarker _marker;
    private readonly IInputSimulator _input;
    private readonly Func<bool>? _leftButtonIsDown;

    /// <summary>Creates the release operation over its two seams.</summary>
    /// <param name="marker">The cross-process held-button record.</param>
    /// <param name="input">The input sink; tests pass a recording fake — no real <c>SendInput</c>.</param>
    /// <param name="leftButtonIsDown">Optional OS probe (<c>GetAsyncKeyState(VK_LBUTTON)</c>) read
    /// BEFORE the release purely for the log line; null skips it.</param>
    public HeldButtonRelease(
        IHeldButtonMarker marker, IInputSimulator input, Func<bool>? leftButtonIsDown = null)
    {
        _marker = marker;
        _input = input;
        _leftButtonIsDown = leftButtonIsDown;
    }

    /// <summary>
    /// Sends exactly one in-place left-button-up when a marker exists, then clears the marker.
    /// Returns without touching input when no marker exists.
    /// </summary>
    /// <returns>Whether a marker was found (and thus a release sent), and the probe's reading.</returns>
    /// <exception cref="Exception">A throwing sink or marker propagates; the marker is kept when the
    /// release did not happen, and a successful send followed by a failed clear only means the next
    /// release path sends another (inert) button-up.</exception>
    public HeldButtonReleaseResult ReleaseIfMarked()
    {
        if (!_marker.IsSet)
            return HeldButtonReleaseResult.NoMarker;

        // Read the OS state BEFORE the release so the log describes what was undone. Optional and
        // diagnostic only: the marker decides, never this probe (a second gaze mouse may be down).
        bool? wasDown = _leftButtonIsDown?.Invoke();

        _input.ReleaseLeftButton();
        _marker.Clear();
        return new HeldButtonReleaseResult(true, wasDown);
    }
}
