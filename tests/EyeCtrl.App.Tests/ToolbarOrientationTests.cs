namespace EyeCtrl.App.Tests;

using System.Windows.Controls;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

/// <summary>
/// Pins <c>ToolbarWindow.SetMode</c>'s axis flip: the strip must follow the docked edge in BOTH
/// directions, not just into vertical.
///
/// <para>
/// The bug this guards is real and shipped. The pre-unification code set <c>Orientation</c> only
/// in the left/right branches and never reset it, so a toolbar docked left and then re-docked top
/// kept rendering as a column until the next process start — <c>Orientation</c> is a plain XAML
/// local value and nothing else restores it, and <c>DockedEdge</c> is part of
/// <c>ToolbarSnapshot</c>, so every edge change in Settings lands here.
/// </para>
///
/// <para>
/// It was fixed on main (<c>b1eaa57e</c>) and recorded there as <i>"Not pinned by a test.
/// Instantiating ToolbarWindow headless is not viable here and a regex-over-source guard would
/// break on reformatting, so that one rests on review."</i> That is no longer true:
/// <see cref="ToolbarTestHelpers.NewBoundToolbar"/> constructs the window headlessly, and the
/// gaze-accessibility work rewrote this method — replacing <c>ButtonPanel</c> with
/// <c>StripWrapper</c>/<c>IndicatorPanel</c> and adding a band rebuild on the flip — so the
/// behaviour was re-expressed by hand during a rebase and wants a guard rather than review.
/// </para>
///
/// <para>
/// The panels are reached through their generated fields rather than <c>FindName("StripWrapper")</c>
/// deliberately: a rename must fail the build, not silently stop testing anything.
/// </para>
///
/// <para>No test here shows a window. <c>SetMode</c> lays out and repositions only.</para>
/// </summary>
[Collection("WpfApplication")]
public class ToolbarOrientationTests
{
    [StaTheory]
    [InlineData("left")]
    [InlineData("right")]
    public void DockingToASideEdge_TurnsTheStripVertical(string edge)
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();

        window.SetMode(isDocked: true, edge);

        Check.That(window.StripWrapper.Orientation).IsEqualTo(Orientation.Vertical);
        Check.That(window.IndicatorPanel.Orientation).IsEqualTo(Orientation.Vertical);
    }

    /// <summary>
    /// The regression itself: the return trip. Asserting only the outbound flip would pass against
    /// the broken one-directional code.
    /// </summary>
    [StaTheory]
    [InlineData("top")]
    [InlineData("bottom")]
    public void ReDockingFromASideEdgeToTopOrBottom_ReturnsTheStripToHorizontal(string edge)
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        window.SetMode(isDocked: true, "left");
        Check.That(window.StripWrapper.Orientation).IsEqualTo(Orientation.Vertical);   // precondition

        window.SetMode(isDocked: true, edge);

        Check.That(window.StripWrapper.Orientation).IsEqualTo(Orientation.Horizontal);
        Check.That(window.IndicatorPanel.Orientation).IsEqualTo(Orientation.Horizontal);
    }

    /// <summary>
    /// Undocking is the other way back to horizontal, and it keeps carrying the old edge string —
    /// so the axis must be decided by <c>isDocked</c> first, not by the edge alone.
    /// </summary>
    [StaFact]
    public void UndockingWhileTheEdgeIsStillASide_ReturnsTheStripToHorizontal()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        window.SetMode(isDocked: true, "right");
        Check.That(window.StripWrapper.Orientation).IsEqualTo(Orientation.Vertical);   // precondition

        window.SetMode(isDocked: false, "right");

        Check.That(window.StripWrapper.Orientation).IsEqualTo(Orientation.Horizontal);
    }

    /// <summary>
    /// <c>OverlayPositioning.PlaceAtEdge</c> takes <c>string?</c> and lowercases null-tolerantly;
    /// this method must not be stricter than the helper it calls.
    /// </summary>
    [StaFact]
    public void ANullEdge_IsTreatedAsHorizontal_AndDoesNotThrow()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        window.SetMode(isDocked: true, "left");

        Check.ThatCode(() => window.SetMode(isDocked: true, null!)).DoesNotThrow();
        Check.That(window.StripWrapper.Orientation).IsEqualTo(Orientation.Horizontal);
    }

    /// <summary>
    /// The flip rebuilds the band (<c>RebuildBand</c>), which clears and repopulates
    /// <c>_buttons</c>. A rebuild that dropped a button would leave it with no dwell target at all,
    /// so the axis change must not cost the band any of its six.
    /// </summary>
    [StaFact]
    public void TheAxisFlipRebuildsTheBandWithoutLosingAButton()
    {
        // includeReset: true — the id list below includes ResetAdaptation. Membership ACROSS the
        // flip (the stored flag, P17) is pinned by ToolbarButtonVisualTests.
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar(includeReset: true);
        string[] ids = { "Pause", "SelectionMode", "Keyboard", "Settings", "TaskSwitcher", "ResetAdaptation" };

        window.SetMode(isDocked: true, "left");

        foreach (var id in ids)
            Check.ThatCode(() => window.ButtonForTest(id)).DoesNotThrow();
    }
}
