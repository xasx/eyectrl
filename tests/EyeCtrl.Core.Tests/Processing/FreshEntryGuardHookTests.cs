namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;

/// <summary>
/// T3 — the two read-only hooks <see cref="FreshEntryGuard"/> gained for the switcher's session
/// log: when the distance gate (A) lifted, and when a target became dwell-eligible. They are
/// observations, not a second rule; the behavioural tests of the rule itself live with the two
/// surfaces that use it (<c>TaskSwitcherLandingGuardTests</c>, <c>ToolbarFreshEntryReArmTests</c>).
/// </summary>
[Collection("DpiHelper Tests")]
public class FreshEntryGuardHookTests
{
    // T1's radius is 50 DIP; at the default 1.0 scale that is 50 physical pixels.
    private static readonly PhysicalPoint Anchor = new(0, 0);
    private static readonly PhysicalPoint NearAnchor = new(30, 0);
    private static readonly PhysicalPoint PastTheRadius = new(60, 0);

    private static readonly PhysicalPoint InsideTile = new(250, 50);

    private static DwellTarget Tile(string id) =>
        new(id, new PhysicalRect(200, 0, 100, 100), 700);

    [Fact]
    public void AnchorLifted_FiresOnce_OnTheSamplePastTheReleaseDistance()
    {
        var lifts = 0;
        var guard = new FreshEntryGuard(onAnchorLifted: () => lifts++);
        guard.Arm(Anchor);

        guard.ObserveValidSample(NearAnchor, []);
        Check.That(lifts).IsEqualTo(0);

        guard.ObserveValidSample(PastTheRadius, []);
        Check.That(lifts).IsEqualTo(1);

        // Later samples keep the gate open; the hook is a transition, not a per-sample event.
        guard.ObserveValidSample(new PhysicalPoint(90, 0), []);
        Check.That(lifts).IsEqualTo(1);
    }

    [Fact]
    public void InvalidAnchor_DoesNotFireTheLiftHook_ButStillReportsEligibility()
    {
        // An invalid anchor starts with (A) already open — there is no lift to measure, and the
        // log writes `n/a`. The entry hook still reports the target.
        var lifts = 0;
        var entered = new List<string>();
        var guard = new FreshEntryGuard(onAnchorLifted: () => lifts++, onTargetEligible: entered.Add);
        guard.Arm(null);

        guard.ObserveValidSample(InsideTile, [Tile("tile-a")]);

        Check.That(lifts).IsEqualTo(0);
        Check.That(entered).ContainsExactly("tile-a");
        Check.That(guard.AllowsDwell("tile-a")).IsTrue();
    }

    [Fact]
    public void TargetEligible_FiresOncePerTarget_WithItsId_AndNeverAgainAfterReEntry()
    {
        var lifts = 0;
        var entered = new List<string>();
        var guard = new FreshEntryGuard(onAnchorLifted: () => lifts++, onTargetEligible: entered.Add);
        guard.Arm(Anchor);

        var targets = new[] { Tile("tile-a") };

        guard.ObserveValidSample(InsideTile, targets);      // this sample lifts (A) AND enters
        Check.That(lifts).IsEqualTo(1);
        Check.That(entered).ContainsExactly("tile-a");

        guard.ObserveValidSample(new PhysicalPoint(260, 50), targets);   // still inside
        guard.ObserveValidSample(new PhysicalPoint(400, 400), targets);  // leaves
        guard.ObserveValidSample(InsideTile, targets);                  // re-enters

        Check.That(entered).ContainsExactly("tile-a");
    }

    [Fact]
    public void DisarmedGuard_FiresNoHooks()
    {
        var lifts = 0;
        var entered = new List<string>();
        var guard = new FreshEntryGuard(onAnchorLifted: () => lifts++, onTargetEligible: entered.Add);

        guard.Arm(Anchor);
        guard.Disarm();
        guard.ObserveValidSample(InsideTile, [Tile("tile-a")]);

        Check.That(lifts).IsEqualTo(0);
        Check.That(entered).IsEmpty();
        Check.That(guard.AllowsDwell("tile-a")).IsTrue();
    }

    [Fact]
    public void ArmAgain_StartsAFreshSession_SoTheHooksFireAgain()
    {
        // The VM is a DI singleton: every show re-arms. A second session must be measurable, not
        // silently inherit the first session's "already lifted / already entered" state.
        var lifts = 0;
        var entered = new List<string>();
        var guard = new FreshEntryGuard(onAnchorLifted: () => lifts++, onTargetEligible: entered.Add);

        guard.Arm(Anchor);
        guard.ObserveValidSample(InsideTile, [Tile("tile-a")]);
        guard.Disarm();

        guard.Arm(Anchor);
        guard.ObserveValidSample(InsideTile, [Tile("tile-a")]);

        Check.That(lifts).IsEqualTo(2);
        Check.That(entered).ContainsExactly("tile-a", "tile-a");
    }
}
