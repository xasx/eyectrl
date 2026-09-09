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
            new FallbackCandidate("SnapCoreFirst", isInstalled: () => true, launch: () => { launched = true; return true; })
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
            new FallbackCandidate("TDControl", isInstalled: () => true, launch: () => { firstLaunched = true; return true; }),
            new FallbackCandidate("SnapCoreFirst", isInstalled: () => true, launch: () => { secondLaunched = true; return true; })
        };
        FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Check.That(firstLaunched).IsTrue();
        Check.That(secondLaunched).IsFalse();
    }

    [Fact]
    public void FallsThrough_WhenAPassingProbeFailsToLaunch()
    {
        // The defect this signature exists to prevent. A probe that passes is not a launch: the
        // TD Control probe read a registry VALUE while the launch required the exe to exist on
        // disk, so a key left behind by an uninstall or an upgrade that moved the exe committed
        // the whole fallback to a candidate that silently started nothing — and skipped
        // WindowsEyeControl, the one that always works, because it sorts last.
        var thirdLaunched = false;
        var candidates = new[]
        {
            new FallbackCandidate("TDControl", isInstalled: () => true, launch: () => false),
            new FallbackCandidate("SnapCoreFirst", isInstalled: () => true, launch: () => false),
            new FallbackCandidate("WindowsEyeControl", isInstalled: () => true, launch: () => { thirdLaunched = true; return true; })
        };

        var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);

        Check.That(launched).IsNotNull();
        Check.That(launched!.Id).IsEqualTo("WindowsEyeControl");
        Check.That(thirdLaunched).IsTrue();
    }

    [Fact]
    public void ReturnsNull_WhenEveryLaunchFails()
    {
        // Must not report success for a launch that did not happen. Program.OnFallenBack latches
        // _fallbackAtLaunched on a non-null return and logs "Fallback AT launched: {Id}", and
        // FALLEN_BACK is terminal for the session — so a false positive here both writes a
        // untrue log line and permanently blocks the retry.
        var candidates = new[]
        {
            new FallbackCandidate("TDControl", isInstalled: () => true, launch: () => false),
            new FallbackCandidate("SnapCoreFirst", isInstalled: () => true, launch: () => false)
        };

        Check.That(FallbackAtLauncher.LaunchFirstInstalled(candidates)).IsNull();
    }

    [Fact]
    public void AThrowingLaunch_IsTreatedAsFailure_AndDoesNotEscape()
    {
        // This runs on the supervisor's dispatcher after the app has already crashed past
        // MaxRetries, and the supervisor registers no global exception handler of its own. An
        // escaping Process.Start failure would take down the watchdog at the exact moment it is
        // the user's last resort, so a throw must degrade to "this candidate did not launch".
        var candidates = new[]
        {
            new FallbackCandidate("TDControl", isInstalled: () => true,
                launch: () => throw new InvalidOperationException("exe vanished between probe and start")),
            Fake("WindowsEyeControl", installed: true)
        };

        var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);

        Check.That(launched).IsNotNull();
        Check.That(launched!.Id).IsEqualTo("WindowsEyeControl");
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

    /// <summary>A candidate whose launch succeeds whenever its probe passes.</summary>
    private static FallbackCandidate Fake(string id, bool installed) =>
        new(id, isInstalled: () => installed, launch: () => true);
}
