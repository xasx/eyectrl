namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Windows.Threading;

/// <summary>A real WPF dispatcher on its own STA thread — the Supervisor's UI thread, headless.</summary>
internal sealed class DispatcherThread : IDisposable
{
    private readonly Thread _thread;

    public DispatcherThread()
    {
        using var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "test-supervisor-ui" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        Dispatcher = dispatcher!;
    }

    public Dispatcher Dispatcher { get; }

    public void Dispose()
    {
        Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
