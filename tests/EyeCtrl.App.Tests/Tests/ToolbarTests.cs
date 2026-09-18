namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests toolbar interactions: pause toggle, button state changes.
/// <para>
/// Pause design note (the tests must follow the product, not the other way round): pausing
/// slides the toolbar band out and HIDES it — the click-through <c>PauseIndicator_Window</c>
/// marks the docked edge as the summon hint. The gaze-unpause path is therefore: hold gaze at
/// the edge until the auto-hide controller summons the band back, then dwell the Pause/Play
/// button. An earlier version of these tests un-paused by dwelling the toolbar button a second
/// time — with the band hidden that dwelled at stale coordinates, the unpause never fired, and
/// every later test in the suite found a paused app (cursor circle gone, cascade of failures).
/// </para>
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class ToolbarTests
{
    private readonly AppFixture _fixture;

    public ToolbarTests(AppFixture fixture)
    {
        AppFixture.SkipIfDisabled();
        _fixture = fixture;
    }

    [Fact]
    public async Task PauseButton_Dwell_ShowsPauseIndicator()
    {
        // KNOWN GAP (2026-09-16 FlaUI repair): the pause dwell DOES fire (app log shows the
        // Paused/FreeLook cycles) but PauseIndicator_Window never becomes findable in UIA,
        // even 5s after the toggle - so indicator-based asserts cannot pass yet. Needs a
        // look at why the transparent click-through indicator exposes no reachable peer,
        // plus a --test-mode state-reset seam for ring visibility. Tracked with the owner.
        Assert.Skip("pause-indicator peer not reachable in UIA; see the comment above");
        // Pausing slides the toolbar band out and shows the click-through PAUSE INDICATOR at
        // the docked edge. Asserting the indicator (not "cursor ring hidden") keeps this test
        // independent of the ring, which several unrelated app states also hide.
        await _fixture.EnsureGazeUiVisibleAsync();

        var toolbar = await _fixture.WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
        var pauseBtn = await _fixture.WaitForElementAsync(toolbar, "Toolbar_Button_Pause", TimeSpan.FromSeconds(5));

        await _fixture.SimulateDwellOn(pauseBtn);
        // The indicator shows only after the toolbar's slide-out ANIMATION completes — poll.
        await _fixture.WaitForPauseIndicatorAsync(visible: true);

        // Cleanup — unpause through the product's path: summon the band, dwell Play.
        await _fixture.EnsureAppUnpausedAsync();
        _fixture.MoveCursorAway();
        await Task.Delay(300, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PauseButton_Dwell_TogglesBackToResume()
    {
        // KNOWN GAP (2026-09-16 FlaUI repair): the pause dwell DOES fire (app log shows the
        // Paused/FreeLook cycles) but PauseIndicator_Window never becomes findable in UIA,
        // even 5s after the toggle - so indicator-based asserts cannot pass yet. Needs a
        // look at why the transparent click-through indicator exposes no reachable peer,
        // plus a --test-mode state-reset seam for ring visibility. Tracked with the owner.
        Assert.Skip("pause-indicator peer not reachable in UIA; see the comment above");
        // Pause via the band's button, unpause via the product's paused-state path: hold gaze
        // at the edge indicator to summon the band back, then dwell Pause/Play. Both pauses are
        // asserted through the pause INDICATOR, which only pause shows.
        await _fixture.EnsureGazeUiVisibleAsync();

        var toolbar = await _fixture.WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
        var pauseBtn = await _fixture.WaitForElementAsync(toolbar, "Toolbar_Button_Pause", TimeSpan.FromSeconds(5));

        await _fixture.SimulateDwellOn(pauseBtn);
        await _fixture.WaitForPauseIndicatorAsync(visible: true);

        await _fixture.SummonToolbarFromEdgeAsync();
        var band = await _fixture.WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
        var playBtn = await _fixture.WaitForElementAsync(band, "Toolbar_Button_Pause", TimeSpan.FromSeconds(5));
        await _fixture.SimulateDwellOn(playBtn);
        await _fixture.WaitForPauseIndicatorAsync(visible: false);

        _fixture.MoveCursorAway();
        await Task.Delay(300, TestContext.Current.CancellationToken);
    }
}
