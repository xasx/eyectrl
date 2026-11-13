// tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The splash thread + marshalled handle (spec §7): every public call marshals to the splash
/// thread, calls after shutdown are no-ops, only the main thread reads the DPI report, and a
/// late DPI report is discarded (r3 #2). Tests run against the real window prepared-invisible
/// via the ISplashWindow factory seam — no test shows a visible splash (r2 #3, spec §9 rev 3.2).
/// </summary>
[Collection("WpfApplication")]
public class SplashThreadLauncherTests : IDisposable
{
    private SplashThreadHandle? _handle;

    public SplashThreadLauncherTests()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        SplashThreadLauncher.WindowFactoryForTests = () => InvisibleTestWindow.Prepare(new SplashWindow());
    }

    public void Dispose() { _handle?.Dispose(); SplashThreadLauncher.WindowFactoryForTests = null; }

    private SplashThreadHandle StartHandle()
    {
        _handle = SplashThreadLauncher.Start();
        Check.That(_handle).IsNotNull();
        return _handle!;
    }

    [StaFact]
    public void ShowNoTrackerSentence_MarshalsOntoTheSplashThread()
    {
        var handle = StartHandle();
        handle.ShowNoTrackerSentence();
        var shown = false;
        var ok = StaTestPump.PumpUntil(() =>
        {
            handle.Inspect(w => shown = ((SplashWindow)w).SentenceShown);   // synchronous on the splash thread
            return shown;
        }, TimeSpan.FromSeconds(3));
        Check.That(ok).IsTrue();
        Check.That(shown).IsTrue();
    }

    [Fact]
    public void LateDpiReport_IsDiscarded_AndFallbackStands()
    {
        var report = new SplashDpiReport();
        Check.That(report.TryAccept(0, out _, out _)).IsFalse();   // nothing reported yet
        report.Discard();
        report.Accept(2.0, 2.0);                                    // the "late" report
        Check.That(report.TryAccept(50, out _, out _)).IsFalse();   // discarded forever
    }

    [Fact]
    public void HandleAfterDispose_IsACompleteNoOp()
    {
        var handle = StartHandle();
        handle.Dispose();                       // shuts the splash dispatcher down
        handle.ShowNoTrackerSentence();         // must not throw
        handle.BeginExit();                     // must not throw
        handle.Close();                         // must not throw — the fatal path with the thread gone
        handle.Inspect(_ => { });               // must not throw
    }
}
