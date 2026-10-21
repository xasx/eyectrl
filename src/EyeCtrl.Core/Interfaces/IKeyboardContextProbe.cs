namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// The two OS facts E1 reads about the app the keyboard types into. Both are for the LOG only,
/// and both are content-free by contract: a process name, and a comparison of text that never
/// leaves memory (<see cref="Keyboard.KeyboardMirrorChecker"/> logs only the verdict and lengths).
/// </summary>
public interface IKeyboardContextProbe
{
    /// <summary>
    /// The foreground window's PROCESS name (e.g. <c>WINWORD.exe</c>) — never its title, which can
    /// carry a document name or a page's subject. Null when it cannot be read. Cheap; safe on the UI thread.
    /// </summary>
    string? GetForegroundProcessName();

    /// <summary>
    /// Up to <paramref name="maxChars"/> characters ending at the caret of the focused control, via
    /// UI Automation TextPattern; null when the control does not support it or anything fails.
    /// BLOCKING and possibly slow (UI Automation is an RPC): the caller runs it off the UI thread
    /// under a time box and never waits for it past that.
    /// </summary>
    string? ReadFocusedTextTail(int maxChars);
}
