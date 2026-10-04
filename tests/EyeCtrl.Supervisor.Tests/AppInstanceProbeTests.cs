namespace EyeCtrl.Supervisor.Tests;

using System.Threading;
using EyeCtrl.Supervisor;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 06-A: the app-instance probe behind <c>ShouldLaunchAtStartup</c>. Every test uses a
/// GUID-suffixed mutex name — never the production one — so a live EyeCtrl on this box can never
/// be touched by the test, and the test can never make the probe claim a false positive.
/// </summary>
public class AppInstanceProbeTests
{
    [Fact]
    public void IsRunning_NoMutexWithTheName_False() =>
        Check.That(AppInstanceProbe.IsRunning(UniqueName())).IsFalse();

    [Fact]
    public void IsRunning_MutexExists_True()
    {
        var name = UniqueName();
        using var held = new Mutex(initiallyOwned: true, name, out _);
        Check.That(AppInstanceProbe.IsRunning(name)).IsTrue();
    }

    [Fact]
    public void IsRunning_MutexWasReleased_False()
    {
        var name = UniqueName();
        using (var held = new Mutex(initiallyOwned: true, name, out _))
        {
            Check.That(AppInstanceProbe.IsRunning(name)).IsTrue();
        }
        Check.That(AppInstanceProbe.IsRunning(name)).IsFalse();
    }

    private static string UniqueName() => @"Local\EyeCtrl.App.Singleton.ProbeTest." + Guid.NewGuid().ToString("N");
}
