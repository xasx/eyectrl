namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

public abstract class MessengerViewModelBase : ObservableRecipient, IDisposable
{
    protected bool IsDisposed { get; private set; }

    protected MessengerViewModelBase(IMessenger messenger) : base(messenger)
    {
        IsActive = true;
    }

    protected virtual void OnDispose() { }

    public void Dispose()
    {
        if (IsDisposed) return;
        OnDispose();
        IsActive = false;
        IsDisposed = true;
    }
}
