namespace EyeCtrl.App.Views;

/// <summary>
/// Boundary mapping, glyphs, dwell times and activation actions for the settings window's two
/// pagers: the content pager (<c>pag_prev</c>/<c>pag_next</c>) and the sidebar tab pager
/// (<c>tab_prev</c>/<c>tab_next</c>).
///
/// <para>
/// Both pagers turn their END buttons into wrap-jumps: at page 0 the previous button jumps to the
/// last page, and at the last page the next button jumps to the first. A jump is drawn as a
/// double arrow (⏭ / ⏮) at full opacity and dwells at the jump (toolbar) time; a non-boundary
/// button keeps its single arrow and dwells at <see cref="StepPagerDwellMs"/>.
/// </para>
///
/// <para>
/// Pure arithmetic and string choice, extracted from <see cref="SettingsWindow"/> so the headless
/// suite can pin the behaviour the window cannot be constructed to exercise (it takes eight
/// non-optional dependencies). The view keeps the buttons and the mouse wiring; every decision
/// ABOUT a pager activation — boundary, glyph, dwell, action — lives here.
/// </para>
/// </summary>
internal static class SettingsPagerChrome
{
    /// <summary>
    /// Dwell time for a pager button that merely STEPS one page, in milliseconds.
    ///
    /// Shorter than the jump dwell on purpose: a step is a low-consequence, easily reversed
    /// action, while a wrap-jump lands the user at the far end of the tab or page list, which is
    /// a much larger change to take back. View-local by settlement — per-target dwell at the
    /// registration site, no new <c>DwellSettings</c> property.
    /// </summary>
    internal const int StepPagerDwellMs = 450;

    /// <summary>Glyph on a previous button that wraps to the last page: jump-to-end.</summary>
    internal const string JumpToEndGlyph = "⏭";

    /// <summary>Glyph on a next button that wraps to the first page: jump-to-start.</summary>
    internal const string JumpToStartGlyph = "⏮";

    /// <summary>
    /// True when the button for <paramref name="direction"/> at <paramref name="pageIndex"/> is at
    /// its end of the list and must wrap instead of step. <paramref name="direction"/> is -1 for
    /// previous, +1 for next; a page count of 1 makes BOTH directions boundary buttons, which is
    /// unreachable in the view (a single page renders no pager) but kept coherent here.
    /// </summary>
    internal static bool IsBoundary(int pageIndex, int pageCount, int direction) =>
        direction < 0 ? pageIndex <= 0 : pageIndex >= pageCount - 1;

    /// <summary>
    /// The page a pager activation lands on: the far end when the button is a boundary button,
    /// otherwise the one-page step. The step is never clamped here because a non-boundary page is
    /// by definition one step away from both ends.
    /// </summary>
    internal static int TargetPage(int pageIndex, int pageCount, int direction) =>
        IsBoundary(pageIndex, pageCount, direction)
            ? direction < 0 ? pageCount - 1 : 0
            : pageIndex + direction;

    /// <summary>
    /// The glyph for a pager button. Boundary buttons wear the double arrows — ⏭ on a previous
    /// button (it jumps to the END) and ⏮ on a next button (it jumps to the START); non-boundary
    /// buttons keep the single arrows, content pager sideways (◀ ▶) and tab pager up/down (▲ ▼).
    /// </summary>
    /// <param name="id">One of the four pager ids; anything else throws rather than drawing "?".</param>
    /// <param name="boundary">True when the button wraps at this page.</param>
    internal static string GlyphFor(string id, bool boundary) => id switch
    {
        "pag_prev" => boundary ? JumpToEndGlyph : "◀",
        "pag_next" => boundary ? JumpToStartGlyph : "▶",
        "tab_prev" => boundary ? JumpToEndGlyph : "▲",
        "tab_next" => boundary ? JumpToStartGlyph : "▼",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Not a settings pager id."),
    };

    /// <summary>
    /// The <c>settings</c> key for a pager button's accessible name (D11).
    ///
    /// <para>
    /// The buttons are glyph-only, so WPF would expose the arrow itself — "◀" or "⏭" — as the
    /// accessible name, which tells a screen reader nothing. Boundary buttons name the far end
    /// they jump to; step buttons name the one-page step. The view resolves the key through
    /// <c>StringTable.Current</c> and sets it on the button's TextBlock (the node UIA exposes in
    /// a transparent overlay window).
    /// </para>
    /// </summary>
    /// <param name="id">One of the four pager ids; anything else throws rather than naming nothing.</param>
    /// <param name="boundary">True when the button wraps at this page.</param>
    internal static string AccessibleNameKey(string id, bool boundary) => id switch
    {
        "pag_prev" => boundary ? "settings.pager.jump_to_last_page" : "settings.pager.previous_page",
        "pag_next" => boundary ? "settings.pager.jump_to_first_page" : "settings.pager.next_page",
        "tab_prev" => boundary ? "settings.pager.jump_to_last_tabs" : "settings.pager.previous_tabs",
        "tab_next" => boundary ? "settings.pager.jump_to_first_tabs" : "settings.pager.next_tabs",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Not a settings pager id."),
    };

    /// <summary>
    /// The dwell time a pager button registers with: the jump (toolbar) time at a boundary,
    /// <see cref="StepPagerDwellMs"/> otherwise. <paramref name="jumpDwellMs"/> is the window's
    /// <c>_dwellMs</c> (ToolbarButtonDwellMs), so jumps follow the user's tuning while steps stay
    /// the fixed 450 ms.
    /// </summary>
    internal static int DwellMsFor(bool boundary, int jumpDwellMs) =>
        boundary ? jumpDwellMs : StepPagerDwellMs;

    /// <summary>
    /// The action a pager button runs.
    ///
    /// <para>
    /// At a boundary it sets the page to the far end and then calls <paramref name="onJumped"/>,
    /// which is the view's cue to arm the re-arm guard (see
    /// <see cref="SettingsPagerRearmGuard"/>) so a resting gaze cannot walk the list.
    /// </para>
    /// <para>
    /// Off a boundary, the content pager's DWELL action is deliberately empty
    /// (<paramref name="vmAutoSteps"/> true): the ViewModel's Pagination arm already auto-steps
    /// <c>pag_prev</c>/<c>pag_next</c>, so a non-empty action would step twice. The MOUSE action
    /// (<paramref name="vmAutoSteps"/> false) must step itself, because a click never enters the
    /// ViewModel. The tab pager passes false on BOTH paths — after the Tab-to-Pagination move no
    /// arm auto-steps <c>tab_*</c>, so the registered action carries both the step and the jump.
    /// </para>
    /// </summary>
    /// <param name="id">The pager id, passed through to <paramref name="onJumped"/>.</param>
    /// <param name="direction">-1 for previous, +1 for next.</param>
    /// <param name="pageIndex">The page/tab page the button was rendered for.</param>
    /// <param name="pageCount">The page/tab page count it was rendered for.</param>
    /// <param name="vmAutoSteps">True when the ViewModel's arm already steps this id.</param>
    /// <param name="setPage">Sets the target page (the view's own page state or the VM's).</param>
    /// <param name="onJumped">Arms the re-arm guard after a wrap-jump.</param>
    internal static Action ActionFor(
        string id, int direction, int pageIndex, int pageCount,
        bool vmAutoSteps, Action<int> setPage, Action<string> onJumped)
    {
        var target = TargetPage(pageIndex, pageCount, direction);
        if (IsBoundary(pageIndex, pageCount, direction))
            return () => { setPage(target); onJumped(id); };
        if (vmAutoSteps)
            return static () => { };
        return () => setPage(target);
    }
}
