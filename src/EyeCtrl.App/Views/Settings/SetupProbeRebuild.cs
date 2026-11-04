namespace EyeCtrl.App.Views.Settings;

using System;

/// <summary>
/// The decision behind the catalogue probe's completion, extracted from
/// <see cref="EyeCtrl.App.Views.SettingsWindow" /> so the headless tests can pin it (the window
/// itself needs a display and eight dependencies).
/// </summary>
internal static class SetupProbeRebuild
{
    /// <summary>The „Erweitert" tab id, as the tab definitions and the page sources name it.</summary>
    internal const string AdvancedTab = "Advanced";

    /// <summary>
    /// Review 02 R2: true only for the „Erweitert" tab. Every other tab has nothing to change when
    /// the probe lands, and <see cref="EyeCtrl.App.Views.SettingsWindow" />'s rebuild clears every
    /// control dwell target — an extra fixation for the owner, while an update waits.
    /// </summary>
    internal static bool IsAdvancedTab(string? currentTabName) =>
        string.Equals(currentTabName, AdvancedTab, StringComparison.Ordinal);

    /// <summary>
    /// Review 02 R3: the closed-window check runs INSIDE the dispatched action — a window that
    /// closes between the worker's completion and the queued rebuild must never touch a view model
    /// its Closed handler has already disposed.
    /// </summary>
    internal static bool ShouldRebuild(bool windowClosed, string? currentTabName) =>
        !windowClosed && IsAdvancedTab(currentTabName);
}
