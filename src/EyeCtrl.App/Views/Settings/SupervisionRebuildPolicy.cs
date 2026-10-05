namespace EyeCtrl.App.Views.Settings;

/// <summary>
/// Ticket 07 review F3: when a supervision state change may rebuild the settings content, and
/// when it must not. Only the General tab shows the supervision rows; rebuilding another tab
/// resets its in-progress Control dwells under a gaze that cannot re-aim at a moving target.
/// A state change while another tab is open is not lost: the next switch to General rebuilds
/// the content anyway. Extracted so the decision is pinned by a test.
/// </summary>
internal static class SupervisionRebuildPolicy
{
    /// <summary>The General tab's id, matching <c>SettingsWindow._tabDefs</c> (whose literal the
    /// headless SettingsTabDriftTests drift guard keeps honest against the UI test's list).</summary>
    public const string GeneralTabId = "General";

    /// <summary>True when a supervision state change should rebuild the current content now.</summary>
    public static bool ShouldRebuildNow(string? currentTabName) => currentTabName == GeneralTabId;
}
