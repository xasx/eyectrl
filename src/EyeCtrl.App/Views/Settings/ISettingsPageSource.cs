namespace EyeCtrl.App.Views.Settings;

/// <summary>One settings tab's page content, as a declarative spec list. Re-invoked on every rebuild.</summary>
internal interface ISettingsPageSource
{
    IReadOnlyList<PageSpec> Build();

    /// <summary>
    /// True when the shell may re-split this source's pages to fit the available content
    /// height (see <see cref="SettingsAutoPagination"/>). Sources that bake navigation
    /// semantics into page boundaries — QuickActions (per-page back-button ids) — return
    /// false and keep their own paging.
    /// </summary>
    bool PrefersAutoPagination => false;

    /// <summary>
    /// The navigable sub-views this source can put on screen, as (name, enter) pairs in display
    /// order. Empty for a single-view source, which is every source but QuickActions and Models.
    ///
    /// <para>
    /// The SHELL never reads this: it is the seam the reachability gate
    /// (<c>HandChunkedPageFitTests</c>) drives, so every page a multi-view source can show is
    /// measured rather than the default view alone. It is an interface member rather than a
    /// per-type switch in the gate, so a source that grows a view state machine declares its
    /// views here instead of being silently reduced to its landing page.
    /// </para>
    /// <para>
    /// Each <c>enter</c> action puts the source into that view; the caller invokes
    /// <see cref="Build"/> afterwards. Implementers typically wire it to the same private
    /// navigation the view's buttons use.
    /// </para>
    /// </summary>
    IReadOnlyList<(string Name, Action Enter)> Views => [];

    /// <summary>Resets any internal view/navigation state when the user leaves the tab; no-op by default.</summary>
    void ResetView() { }
}
