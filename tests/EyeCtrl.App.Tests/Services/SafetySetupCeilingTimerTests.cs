namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using EyeCtrl.App.Services;

/// <summary>
/// The PRODUCTION scheduler behind the crash-recovery ceiling (A2 review 03 L1): every ceiling test
/// in SafetySetupFlowTests injects its own scheduler, and the Settings window uses this default, so
/// a dead timer would leave the "could not be confirmed" card unreachable with every other test
/// green. Driven on a real dispatcher with a pumped frame, a short delay and a bounded wait. No
/// window is constructed.
/// </summary>
public class SafetySetupCeilingTimerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [StaFact]
    public void TheDefaultScheduler_RunsTheActionOnce_AfterTheDelay_OnThisDispatcher()
    {
        var runs = 0;
        var thread = Environment.CurrentManagedThreadId;
        var ranOn = -1;
        var frame = new DispatcherFrame();

        using var ceiling = SafetySetupFlow.DispatcherDelay(Delay, () =>
        {
            runs++;
            ranOn = Environment.CurrentManagedThreadId;
            frame.Continue = false;
        });
        Pump(frame, Bound);

        Check.WithCustomMessage("The production ceiling timer never fired within the bound.")
            .That(runs).IsEqualTo(1);
        Check.That(ranOn).IsEqualTo(thread);

        Pump(new DispatcherFrame(), TimeSpan.FromMilliseconds(300));   // one-shot: no second tick
        Check.That(runs).IsEqualTo(1);
    }

    [StaFact]
    public void TheDefaultScheduler_DisposedBeforeTheDelay_NeverRuns()
    {
        var runs = 0;

        var ceiling = SafetySetupFlow.DispatcherDelay(Delay, () => runs++);
        ceiling.Dispose();
        Pump(new DispatcherFrame(), TimeSpan.FromMilliseconds(300));

        Check.That(runs).IsEqualTo(0);
    }

    /// <summary>Pumps <paramref name="frame"/> until something ends it, or for at most <paramref name="bound"/>.</summary>
    private static void Pump(DispatcherFrame frame, TimeSpan bound)
    {
        var guard = new DispatcherTimer(bound, DispatcherPriority.Normal,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        guard.Start();
        Dispatcher.PushFrame(frame);
        guard.Stop();
    }
}
