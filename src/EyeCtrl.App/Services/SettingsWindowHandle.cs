namespace EyeCtrl.App.Services;

using EyeCtrl.App.Views;

/// <summary>
/// The Settings window as the uninstall hand-off drives it (C9 S6e fixup-08). A seam, not a
/// convenience: the hide/restore contract (hide only on Started, come back on „Erweitert" for
/// every non-uninstall end, never for an uninstall) is the ticket's whole acceptance list, and a
/// test may never show a real window. Production wraps the real window; the tests record the
/// calls.
/// </summary>
internal interface ISettingsWindowHandle
{
    /// <summary>Whether the window is on screen. A hidden window is not.</summary>
    bool IsVisible { get; }

    /// <summary>Hides the window without closing it, so its tab and scroll state survive.</summary>
    void Hide();

    /// <summary>Shows the hidden window again and lands it on „Erweitert", the tab the uninstall
    /// row lives on.</summary>
    void ShowOnAdvanced();
}

/// <summary>The production handle over the real <see cref="SettingsWindow" />.</summary>
internal sealed class SettingsWindowHandle : ISettingsWindowHandle
{
    private readonly SettingsWindow _window;

    /// <summary>Wraps one open Settings window.</summary>
    public SettingsWindowHandle(SettingsWindow window) =>
        _window = window ?? throw new ArgumentNullException(nameof(window));

    /// <inheritdoc />
    public bool IsVisible => _window.IsVisible;

    /// <inheritdoc />
    public void Hide() => _window.Hide();

    /// <inheritdoc />
    public void ShowOnAdvanced()
    {
        // Show() first, then the navigation: the tab rebuild must run against a visible window, the
        // same order the language deep link's open-then-navigate plan uses.
        _window.Show();
        _window.NavigateToAdvancedTab();
    }
}
