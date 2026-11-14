// tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The splash thread + marshalled handle (spec §7): every public call marshals to the splash
/// thread, calls after shutdown are no-ops, only the main thread reads the DPI report, a late
/// DPI report is discarded (r3 #2), and a timed-out or failing factory start degrades to a
/// splash-less start (r4 #8). Tests run against the real window via the ISplashWindow factory
/// seam — no test shows a visible splash (r2 #3, spec §9 rev 3.2).
///
/// <para>The factory hands back <see cref="InvisibleTestWindow.PrepareCloaked"/> (DWM cloak), the
/// same form SplashWindowExitSequenceTests uses and documents: the splash cannot join the
/// Opacity=0 pin family because its XAML animates ELEMENT opacities, so a future "fade the
/// window itself" refactor would put a visible card on screen during dotnet test — the DWM cloak
/// hides the window whatever its Opacity does. The launcher SHOWS the window on the splash
/// thread (sanctioned invisible-show: the prepared tag is trusted by the runtime guard).</para>
/// </summary>
[Collection("WpfApplication")]
public class SplashThreadLauncherTests : IDisposable
{
    private SplashThreadHandle? _handle;

    public SplashThreadLauncherTests()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        RestoreDefaultFactory();
    }

    public void Dispose() { _handle?.Dispose(); SplashThreadLauncher.WindowFactoryForTests = null; }

    /// <summary>The factory the constructor installed; a test that swaps in a throwing or
    /// blocking factory restores this before it ends (Dispose then nulls it).</summary>
    private static void RestoreDefaultFactory() =>
        SplashThreadLauncher.WindowFactoryForTests = () => InvisibleTestWindow.PrepareCloaked(new SplashWindow());

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

    /// <summary>
    /// Spec §9, r4 #8: a factory that cannot deliver a window inside the 500 ms handle wait
    /// cancels the splash BEFORE its one visible step — Start() returns null and the app starts
    /// splash-less. The stray splash thread wakes late, finds _startCancelled set and shuts down
    /// without ever showing; the post-assertion sleep stays past that wake point so the flag
    /// cannot be reset by the next test's Start() before the stray thread has read it (the F1
    /// residual race).
    /// </summary>
    [Fact]
    public void Start_FactoryBlocksPastTheWait_ReturnsNull()
    {
        SplashThreadLauncher.WindowFactoryForTests = () =>
        {
            Thread.Sleep(600);                  // > the 500 ms handle wait
            return InvisibleTestWindow.PrepareCloaked(new SplashWindow());
        };
        try
        {
            var handle = SplashThreadLauncher.Start();
            Check.That(handle).IsNull();        // splash-less start — the designed degradation
            Thread.Sleep(400);                  // > the 100 ms the stray thread still needs past the timeout
        }
        finally { RestoreDefaultFactory(); }
    }

    /// <summary>Spec §9, r2 #1: a faulting factory degrades to a splash-less, logged start —
    /// the thread entry's containment completes the (null) handle and shuts its dispatcher down;
    /// Start() returns null instead of throwing.</summary>
    [Fact]
    public void Start_FactoryThrows_ReturnsNull()
    {
        SplashThreadLauncher.WindowFactoryForTests = () => throw new InvalidOperationException("the factory failed");
        try
        {
            var handle = SplashThreadLauncher.Start();
            Check.That(handle).IsNull();        // containment, not a process kill
        }
        finally { RestoreDefaultFactory(); }
    }
}
