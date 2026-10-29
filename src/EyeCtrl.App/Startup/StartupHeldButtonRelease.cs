namespace EyeCtrl.App.Startup;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Services;

/// <summary>
/// C3's startup backstop: if a previous instance was killed without releasing the left button it
/// held (the Supervisor's hang-kill is the common one, but the Supervisor may not be running at
/// all — Task Manager, a native crash), send exactly one in-place button-up and clear the marker.
/// <para>
/// <b>Ordering is part of the contract.</b> The caller runs this in <c>OnStartup</c> after the
/// single-instance guard (a duplicate that is about to exit must not release the running primary's
/// drag button) and BEFORE the gaze engine starts, before the interaction state machine can enter
/// <c>Paused</c>, and before any overlay or note window exists. The synthesized release must never
/// land while paused: that is <c>NoteInputPolicy</c>'s contract (no synthesized input after
/// <c>CurrentState == Paused</c>), and the sticky note windows pair presses with releases, so a
/// stray release is only inert because it acts on no press.
/// </para>
/// </summary>
internal static class StartupHeldButtonRelease
{
    /// <summary>
    /// Runs the backstop and writes at most one log line.
    /// </summary>
    /// <param name="marker">The cross-process marker (a temp-path file in tests).</param>
    /// <param name="input">The input sink; tests pass a fake — no real <c>SendInput</c>.</param>
    /// <param name="leftButtonIsDown">Optional OS probe for the log line (see <see cref="HeldButtonRelease"/>).</param>
    /// <param name="log">Receives the single line, so tests can assert it without Serilog.</param>
    /// <returns>True when a marker was found and a button-up was sent.</returns>
    internal static bool Run(
        IHeldButtonMarker marker,
        IInputSimulator input,
        Func<bool>? leftButtonIsDown,
        Action<string> log)
    {
        try
        {
            var result = new HeldButtonRelease(marker, input, leftButtonIsDown).ReleaseIfMarked();
            if (!result.MarkerWasSet)
                return false;

            // Fixup-01 F5: a clear failure is NOT a failed release — the button-up already went
            // out. Say exactly that, because the only consequence is one later inert button-up.
            if (result.MarkerClearFailure is { } clearFailure)
            {
                log("Held-button backstop: sent one in-place button-up, but clearing the marker failed ("
                    + $"{clearFailure.GetType().Name}: {clearFailure.Message}); a later start may send "
                    + "one inert button-up (GetAsyncKeyState VK_LBUTTON="
                    + $"{result.LeftButtonWasDown}).");
                return true;
            }

            log("Held-button backstop: a previous session left the left mouse button down; sent one "
                + "in-place button-up and cleared the marker (GetAsyncKeyState VK_LBUTTON="
                + $"{result.LeftButtonWasDown}).");
            return true;
        }
        catch (Exception ex)
        {
            // The app must still start: a failed release leaves the marker in place for the next
            // launch (and for the Supervisor's post-kill release), so nothing is lost by carrying on.
            log("Held-button backstop: the release failed ("
                + $"{ex.GetType().Name}: {ex.Message}); the marker is kept for the next attempt.");
            return false;
        }
    }
}
