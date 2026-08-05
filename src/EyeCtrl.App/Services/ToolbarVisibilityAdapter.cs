namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels;

/// <summary>
/// Real <see cref="IToolbarVisibility"/> over <see cref="IOverlayManager"/>. Marshals every
/// call onto the WPF dispatcher (the controller calls from the gaze thread).
/// </summary>
public sealed class ToolbarVisibilityAdapter : IToolbarVisibility
{
    private readonly IOverlayManager _overlays;
    private readonly Dispatcher _dispatcher;
    private readonly ToolbarViewModel _toolbarVm;

    public ToolbarVisibilityAdapter(IOverlayManager overlays, Dispatcher dispatcher, ToolbarViewModel toolbarVm)
    {
        _overlays = overlays;
        _dispatcher = dispatcher;
        _toolbarVm = toolbarVm;
    }

    public void ShowToolbar() => _dispatcher.BeginInvoke(() =>
    {
        _overlays.Toolbar.Show();
        _overlays.PauseIndicator.Hide();
        _toolbarVm.SetActive(true);
    });

    public void HideToolbar(bool paused) => _dispatcher.BeginInvoke(() =>
    {
        // Deactivate dwell BEFORE the hide so a sample arriving between Hide and the next
        // dispatcher tick can't accumulate against the (now-hidden) targets.
        _toolbarVm.SetActive(false);
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
        _toolbarVm.SetActive(true);
    });

    public void PositionIndicator(string edge) => _dispatcher.BeginInvoke(() => _overlays.PauseIndicator.SetEdge(edge));

    public void SetActive(bool active) => _dispatcher.BeginInvoke(() => _toolbarVm.SetActive(active));
}
