namespace EyeCtrl.App.Tests.Settings;

using EyeCtrl.App.Views.Settings;
using EyeCtrl.App.Views;                    // SettingsWindow internals (TabContainerHeight etc.)

public class SettingsSidebarModelTests
{
    // PRODUCTION order, from SettingsWindow's _tabDefs initializer (verify against
    // SettingsTabDriftTests.RenderedTabDefs() when writing — do not hand-shuffle).
    private static readonly string[] All15 =
    [
        "Cursor", "Dwell", "Selection", "Actions", "Layout", "Keyboard", "Models", "Toolbar",
        "Calibration", "EyeCare", "Adaptation", "General", "QuickActions", "Advanced", "Diagnostics"
    ];

    private const double TallBudget = 1080 - 116 - 196 - 16;   // ~6 tabs per page at the owner's height
    private const double Slot = SettingsWindow.TabContainerHeight + 2 * SettingsWindow.TabButtonMargin;

    [Fact]
    public void Advanced_ShowsAllTabs_WithPager_NoSnap()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "Adaptation");

        Check.That(layout.VisibleIds).HasSize(15);
        Check.That(layout.PagerPresent).IsTrue();
        Check.That(layout.SnapTo).IsNull();
        Check.That(layout.Slice.Count).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Simple_HidesNonCuratedTabs_SnapsToTheLeader_AtThePostSnapPage()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, TallBudget, Slot, "Adaptation");

        Check.That(layout.VisibleIds).HasSize(4);
        Check.That(layout.SnapTo).IsEqualTo("Cursor");          // Adaptation is hidden -> snap to the leader
        Check.That(layout.PageIndex).IsEqualTo(0);              // post-snap page, not a stale clamp
        Check.That(layout.Slice).IsEqualTo((0, 4));
        Check.That(layout.PagerPresent).IsFalse();
    }

    [Fact]
    public void Simple_ToAdvanced_OnGeneral_ReDerivesGeneralsPage_NotClampedToZero()
    {
        // The round-3 I4 regression: simple always sits on page 0; a clamp would strand General.
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "General");

        Check.That(layout.SnapTo).IsNull();                     // General survives the switch
        var generalIndex = Array.IndexOf(All15, "General");
        Check.That(layout.PageIndex).IsEqualTo(generalIndex / layout.TabsPerPage);
    }

    [Fact]
    public void Simple_OnShortScreens_StillPaginates()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, sidebarBudget: 320, Slot, "General");

        Check.That(layout.PageCount).IsStrictlyGreaterThan(1);  // ~3 tabs/page at this budget
        Check.That(layout.PagerPresent).IsTrue();
    }

    [Fact]
    public void Simple_GeneralIsAlwaysVisible_TheDeepLinkTarget()
    {
        // Spec L14's model-side half: General is simple-tier, so the language deep link's
        // lookup can never miss in either mode.
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: true, TallBudget, Slot, "General");
        Check.That(layout.VisibleIds).Contains("General");
        Check.That(layout.SnapTo).IsNull();
    }

    [Fact]
    public void EmptyCurrentTab_LandsOnPageZero()
    {
        var layout = SettingsSidebarModel.Compute(
            All15, SimpleSettingsFilter.SimpleTabIds, simple: false, TallBudget, Slot, "");
        Check.That(layout.PageIndex).IsEqualTo(0);
    }
}
