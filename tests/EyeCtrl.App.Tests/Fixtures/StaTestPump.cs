// tests/EyeCtrl.App.Tests/Fixtures/StaTestPump.cs
namespace EyeCtrl.App.Tests.Fixtures;

using System.Windows.Threading;

/// <summary>
/// Pumps the current dispatcher until a condition holds or the budget runs out — how STA tests
/// let WPF animation clocks advance (storyboard Completed events fire on dispatcher ticks).
/// </summary>
internal static class StaTestPump
{
    public static bool PumpUntil(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new DispatcherOperationCallback(f => ((DispatcherFrame)f).Continue = false), frame);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
        return condition();
    }
}
