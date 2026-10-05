namespace EyeCtrl.App.Tests.Views;

using System;
using System.Windows.Threading;
using EyeCtrl.App.Views;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 07 review F5: pin the settings window's presence-refresh wiring — the tick really calls
/// the refresh (W1), and Stop really stops it (W2, so a closed window cannot leak its timer).
/// No window is shown and no input is simulated; safe headless.
/// </summary>
public class SupervisionPresenceRefresherTests
{
    [StaFact]
    public void Refresher_TicksTheRefresh_AndStopStopsIt()
    {
        var count = 0;
        var refresher = new SupervisionPresenceRefresher(() => count++, TimeSpan.FromMilliseconds(20));
        try
        {
            refresher.Start();
            Check.That(refresher.IsRunning).IsTrue();

            Pump(TimeSpan.FromMilliseconds(300));
            Check.That(count).IsStrictlyGreaterThan(0);   // W1: the tick calls the refresh

            refresher.Stop();
            Check.That(refresher.IsRunning).IsFalse();    // W2: Stop really stops
            var afterStop = count;
            Pump(TimeSpan.FromMilliseconds(150));
            Check.That(count).IsEqualTo(afterStop);       // and it stays stopped
        }
        finally
        {
            refresher.Stop();
        }
    }

    [Fact]
    public void DefaultInterval_IsTwoSeconds() =>
        Check.That(SupervisionPresenceRefresher.DefaultInterval).IsEqualTo(TimeSpan.FromSeconds(2));

    /// <summary>Runs the current thread's dispatcher for <paramref name="duration"/> so timer
    /// ticks can fire without showing a window or blocking on input.</summary>
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
