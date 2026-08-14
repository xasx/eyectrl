namespace EyeCtrl.App.Views.Settings;

/// <summary>One settings tab's page content, as a declarative spec list. Re-invoked on every rebuild.</summary>
internal interface ISettingsPageSource
{
    IReadOnlyList<PageSpec> Build();

    /// <summary>
    /// True when the shell may re-split this source's pages to fit the available content
    /// height (see <see cref="SettingsAutoPagination"/>). Sources that bake navigation
    /// semantics into page boundaries — QuickActions (per-page back-button ids) and Models
    /// ("next rebuild is the management view") — return false and keep their own paging.
    /// </summary>
    bool PrefersAutoPagination => false;
}
