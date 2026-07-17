namespace EyeCtrl.Supervisor.Tests.Fallback;

using EyeCtrl.Supervisor.Fallback;

public class FallbackAtLauncherTests
{
    [Fact]
    public void Launches_FirstInstalled_InOrder()
    {
        var candidates = new[]
        {
            Fake("TDControl", installed: false),
            Fake("SnapCoreFirst", installed: true),
            Fake("WindowsEyeControl", installed: true)
        };
        var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Check.That(launched).IsNotNull();
        Check.That(launched!.Id).IsEqualTo("SnapCoreFirst");
    }

    [Fact]
    public void Launches_VeryFirst_WhenAllInstalled()
    {
        var candidates = new[]
        {
            Fake("TDControl", installed: true),
            Fake("SnapCoreFirst", installed: true),
            Fake("WindowsEyeControl", installed: true)
        };
        var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Check.That(launched).IsNotNull();
        Check.That(launched!.Id).IsEqualTo("TDControl");
    }

    [Fact]
    public void CallsLaunch_OnTheChosenCandidate()
    {
        var launched = false;
        var candidates = new[]
        {
            Fake("TDControl", installed: false),
            new FallbackCandidate("SnapCoreFirst", isInstalled: () => true, launch: () => launched = true)
        };
        FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Check.That(launched).IsTrue();
    }

    [Fact]
    public void DoesNotCallLaunch_OnSkippedCandidates()
    {
        var firstLaunched = false;
        var secondLaunched = false;
        var candidates = new[]
        {
            new FallbackCandidate("TDControl", isInstalled: () => true, launch: () => firstLaunched = true),
            new FallbackCandidate("SnapCoreFirst", isInstalled: () => true, launch: () => secondLaunched = true)
        };
        FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Check.That(firstLaunched).IsTrue();
        Check.That(secondLaunched).IsFalse();
    }

    [Fact]
    public void ReturnsNull_WhenNoneInstalled()
    {
        var candidates = new[]
        {
            Fake("TDControl", false),
            Fake("SnapCoreFirst", false),
            Fake("WindowsEyeControl", false)
        };
        Check.That(FallbackAtLauncher.LaunchFirstInstalled(candidates)).IsNull();
    }

    [Fact]
    public void BuildCandidates_HonorsConfiguredOrder()
    {
        // The user's configured FallbackAtOrder must drive priority — not a hard-coded order.
        var ids = FallbackAtLauncher.BuildCandidates(new[] { "WindowsEyeControl", "TDControl" })
            .Select(c => c.Id).ToList();
        Check.That(ids).ContainsExactly("WindowsEyeControl", "TDControl");
    }

    [Fact]
    public void BuildCandidates_SkipsUnknownIds()
    {
        var ids = FallbackAtLauncher.BuildCandidates(new[] { "TDControl", "Nonsense", "SnapCoreFirst" })
            .Select(c => c.Id).ToList();
        Check.That(ids).ContainsExactly("TDControl", "SnapCoreFirst");
    }

    [Fact]
    public void BuildCandidates_EmptyOrder_YieldsNothing()
    {
        Check.That(FallbackAtLauncher.BuildCandidates(Array.Empty<string>())).IsEmpty();
    }

    private static FallbackCandidate Fake(string id, bool installed) =>
        new(id, isInstalled: () => installed, launch: () => { });
}
