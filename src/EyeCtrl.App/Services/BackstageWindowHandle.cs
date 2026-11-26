namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;

/// <summary>
/// The production <see cref="IUninstallHandoffWindowHandle" /> over the real
/// <see cref="BackstageWindow" /> (Ticket 04 retarget). The window methods keep the view model and
/// the restart-notice subscriptions in step with the hide/show, so the coordinator only drives one
/// seam.
/// </summary>
internal sealed class BackstageWindowHandle : IUninstallHandoffWindowHandle
{
    private readonly BackstageWindow _window;

    /// <summary>Wraps one open Backstage window.</summary>
    public BackstageWindowHandle(BackstageWindow window) =>
        _window = window ?? throw new ArgumentNullException(nameof(window));

    /// <inheritdoc />
    public bool IsVisible => _window.IsVisible;

    /// <inheritdoc />
    public void Hide() => _window.HideForUninstallHandoff();

    /// <inheritdoc />
    public void Show() => _window.ShowAfterUninstallHandoff();
}
