// tests/EyeCtrl.App.Tests/Overlays/SplashThreadHandleWhenExitedTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Threading;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using NFluent;
using Xunit;

public class SplashThreadHandleWhenExitedTests
{
    private sealed class FakeSplashWindow : ISplashWindow
    {
        public event Action? ExitFinished;
        public void OnSplashShown() { }
        public void BeginExit() { }
        public void ShowNoTrackerSentence() { }
        public void ForceClose() { }
        public void FatalClose() { }
        public bool TryReadDpiScale(out double scaleX, out double scaleY) { scaleX = scaleY = 0; return false; }
        public void RaiseExit() => ExitFinished?.Invoke();
    }

    [StaFact]
    public void WhenExited_Attaches_BeforeExit_AndFiresOnce_OnTheRegisteringDispatcher()
    {
        var window = new FakeSplashWindow();
        var handle = new SplashThreadHandle(Dispatcher.CurrentDispatcher, window);
        var fired = new List<string>();

        handle.WhenExited(() => fired.Add("a"));          // attached: waits
        Check.That(fired).IsEmpty();

        window.RaiseExit();                                // fires on THIS thread (no splash thread here)
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain

        Check.That(fired).HasSize(1);
        handle.WhenExited(() => fired.Add("late"));        // already exited: fires at once
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain
        Check.That(fired).HasSize(2);
        Check.That(fired[0]).IsEqualTo("a");
    }

    [StaFact]
    public void WhenExited_AfterTheSplashDispatcherShutDown_FiresImmediately()
    {
        // A REAL second dispatcher, shut down on its own thread and joined — then the
        // registration on THIS thread sees HasShutdownStarted. Pump before asserting (r2 F1).
        var window = new FakeSplashWindow();
        Dispatcher? splashDispatcher = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            splashDispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Check.That(ready.Wait(5000)).IsTrue();
            var handle = new SplashThreadHandle(splashDispatcher!, window);

            splashDispatcher!.Invoke(() => splashDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
            thread.Join(5000);

            var fired = new List<string>();
            handle.WhenExited(() => fired.Add("x"));       // shutdown: fires on THIS thread at once
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); // drain
            Check.That(fired).HasSize(1);
        }
        finally
        {
            if (splashDispatcher is { HasShutdownStarted: false } d)
                d.Invoke(() => d.BeginInvokeShutdown(DispatcherPriority.Normal));
            thread.Join(1000);
        }
    }
}
