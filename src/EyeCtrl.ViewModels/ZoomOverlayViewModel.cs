namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

public partial class ZoomOverlayViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>,
    IRecipient<ShowActionMenuMessage>
{
    [ObservableProperty] private ScreenPoint _center = new(0, 0);
    [ObservableProperty] private int _radius;
    [ObservableProperty] private double _magnification;
    [ObservableProperty] private bool _isVisible;

    public ZoomOverlayViewModel(IMessenger messenger)
    {
        messenger.RegisterAll(this);
    }

    public void Receive(StartZoomMessage message)
    {
        Center = message.Center;
        Radius = message.Radius;
        Magnification = message.Magnification;
        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
    }

    public void Receive(ShowActionMenuMessage message)
    {
        // When the zoom action menu appears, align the zoom overlay to the same center.
        if (message.ShowBack && IsVisible)
            Center = message.Center;
    }
}
