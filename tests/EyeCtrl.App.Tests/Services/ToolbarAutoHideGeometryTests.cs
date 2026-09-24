namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// The toolbar's screen bounds and summon rectangle must move together.
/// <para>
/// The screen size used to be two <c>readonly</c> doubles set in the constructor, so a resolution
/// change could never be applied and the summon region — the escape hatch a user reaches for when
/// anything else has gone wrong — was measured against a screen that no longer existed. Refreshing
/// the two independently would have replaced that with a subtler version of the same failure: a
/// rect expressed in the new geometry judged against the old bounds can leave the toolbar
/// unreachable with no bug anywhere in the logic.
/// </para>
/// </summary>
public class ToolbarAutoHideGeometryTests
{
    private static ToolbarAutoHideController Docked(string edge, double screenW, double screenH, PhysicalRect rect) =>
        new(enabled: true, edge: edge, screenW: screenW, screenH: screenH,
            toolbarRect: rect, hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);

    /// <summary>Drives one off-edge sample and reports whether it began a summon run.</summary>
    private static bool WouldSummon(ToolbarAutoHideController c, double x, double y)
    {
        var vis = new RecordingVisibility();
        c.Attach(vis);
        c.HideForTest(vis);   // only a hidden toolbar can be summoned

        // Two samples: the first seeds the summon anchor, the second clears the delay.
        c.Feed(new GazePoint(x, y, 0, true), 0);
        c.Feed(new GazePoint(x, y, 400_000, true), 400_000);
        return vis.Shown;
    }

    private sealed class RecordingVisibility : IToolbarVisibility
    {
        public bool Shown { get; private set; }
        public void ShowToolbar() => Shown = true;
        public void HideToolbar(bool paused) { }
        public void ForceShow() { }
        public void SetActive(bool active) { }
        public void PositionIndicator(string edge) { }
        public void SetPauseIndicator(bool visible) { }
    }

    [Fact]
    public void AfterTheScreenGrows_TheOldEdgeNoLongerSummons()
    {
        // 1080 -> 2160. A sample at y=1131 was past the old bottom edge (1080 + 50 margin) and
        // would have summoned; on the taller screen it is mid-desktop and must not.
        var c = Docked("bottom", 1920, 1080, new PhysicalRect(0, 1040, 1920, 40));
        Check.That(WouldSummon(c, 500, 1200)).IsTrue();

        var c2 = Docked("bottom", 1920, 1080, new PhysicalRect(0, 1040, 1920, 40));
        c2.UpdateGeometry(3840, 2160, new PhysicalRect(0, 2120, 3840, 40));

        Check.That(WouldSummon(c2, 500, 1200)).IsFalse();
        Check.That(WouldSummon(c2, 500, 2400)).IsTrue();   // past the NEW edge
    }

    [Fact]
    public void AfterTheScreenShrinks_TheNewEdgeSummons()
    {
        // 2160 -> 1080. Without the refresh, summoning required passing y=2210 on a screen only
        // 1080 tall — i.e. the toolbar could never be summoned again at all.
        var c = Docked("bottom", 3840, 2160, new PhysicalRect(0, 2120, 3840, 40));
        Check.That(WouldSummon(c, 500, 1200)).IsFalse();

        var c2 = Docked("bottom", 3840, 2160, new PhysicalRect(0, 2120, 3840, 40));
        c2.UpdateGeometry(1920, 1080, new PhysicalRect(0, 1040, 1920, 40));

        Check.That(WouldSummon(c2, 500, 1200)).IsTrue();
    }

    [Fact]
    public void UpdateGeometry_MovesBoundsAndRectTogether()
    {
        // The pair, not just the bounds. The rect governs the hide countdown (gaze on the toolbar
        // keeps it visible) and the bounds govern the summon; a mismatch is what makes the escape
        // hatch unreachable.
        var c = Docked("bottom", 1920, 1080, new PhysicalRect(0, 1040, 1920, 40));
        c.UpdateGeometry(3840, 2160, new PhysicalRect(0, 2120, 3840, 40));

        var vis = new RecordingVisibility();
        c.Attach(vis);

        // A point inside the NEW rect must be recognised as on-toolbar (it does not summon, and it
        // does not begin a hide run). Under the old rect this point was off-toolbar.
        c.Feed(new GazePoint(100, 2130, 0, true), 0);
        Check.That(vis.Shown).IsFalse();
    }

    [Fact]
    public void UpdateGeometry_IgnoresATransientZeroSizedScreen()
    {
        // Windows reports 0x0 briefly during a mode switch. Accepting it would put the summon edge
        // at 50px from the origin, making almost the whole screen a summon trigger.
        var c = Docked("bottom", 1920, 1080, new PhysicalRect(0, 1040, 1920, 40));
        c.UpdateGeometry(0, 0, new PhysicalRect(0, 0, 0, 0));

        Check.That(WouldSummon(c, 500, 1200)).IsTrue();    // still the 1080 edge
        Check.That(WouldSummon(Docked("bottom", 1920, 1080, new PhysicalRect(0, 1040, 1920, 40)), 500, 900)).IsFalse();
    }

    [Fact]
    public void UpdateToolbarRect_LeavesTheScreenBoundsAlone()
    {
        // The pre-existing rect-only path (a dock-edge change or a re-layout) must not disturb the
        // bounds now that they share one record.
        var c = Docked("bottom", 1920, 1080, new PhysicalRect(0, 1040, 1920, 40));
        c.UpdateToolbarRect(new PhysicalRect(0, 1000, 1920, 80));

        Check.That(WouldSummon(c, 500, 1200)).IsTrue();    // 1080 edge unchanged
    }
}
