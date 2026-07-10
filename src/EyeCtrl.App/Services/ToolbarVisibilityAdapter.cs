namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;

/// <summary>
/// Real <see cref="IToolbarVisibility"/> over <see cref="IOverlayManager"/>. Marshals every
/// call onto the WPF dispatcher (the controller calls from the gaze thread).
/// </summary>
public sealed class ToolbarVisibilityAdapter : IToolbarVisibility
{
    private readonly IOverlayManager _overlays;
    private readonly Dispatcher _dispatcher;

    public ToolbarVisibilityAdapter(IOverlayManager overlays, Dispatcher dispatcher)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
    }

    public void ShowToolbar() => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Show();
        _overlays.PauseIndicator.Hide();
    });

    public void HideToolbar(bool paused) => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Hide();
        if (paused)
        {
            _overlays.PauseIndicator.Show();
        }
        else
        {
            _overlays.PauseIndicator.Hide();
        }
    });

    public void ForceShow() => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Show();
        _overlays.PauseIndicator.Hide();
    });

    public void PositionIndicator(string edge) => _dispatcher.BeginInvoke(() =>
        _overlays.PauseIndicator.SetEdge(edge));
}
