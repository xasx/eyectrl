namespace EyeCtrl.App.Tests.Settings;

using System.IO;

/// <summary>
/// Source scans for the mode toggle's dwell wiring (Task 11): its own guard-aware registration
/// at its own 50-DIP hit radius, the 85-DIP release-radius arming, the re-arm switch arm, the
/// lambda-free frame-loop push, and the PagProgress wash through the toggle's own brush
/// dictionary. The window stays un-constructible in this suite (eight non-optional
/// dependencies), so — the <see cref="SettingsPagerWiringTests"/> technique — the pins read the
/// source. The behaviour this routing exists for lives in
/// <see cref="SettingsModeToggleRestingGazeTests"/> (a real VM, real detector, DPI 2.0).
/// </summary>
public class SettingsModeToggleWiringTests
{
    private static string WindowSource() => File.ReadAllText(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"SettingsWindow.xaml.cs no longer declares `{signature}`. "
                + "Repoint this scan at wherever it moved; do not delete it.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.WithCustomMessage($"The `{signature}` body is unterminated.")
            .That(end).IsStrictlyGreaterThan(start);
        return source[start..end];
    }

    /// <summary>
    /// Substring from the first marker to the next. A switch ARM slice, not a whole-method scan:
    /// a call landed in the neighbouring ModalProgress case would still be found by the wider
    /// net (plan-review m11).
    /// </summary>
    private static string SliceBetween(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Check.WithCustomMessage($"SettingsWindow.xaml.cs no longer declares `{from}`. "
                + "Repoint this scan at wherever it moved; do not delete it.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        Check.WithCustomMessage($"The slice from `{from}` never reaches `{to}` — the switch arm "
                + "order changed. Re-slice against the new order.")
            .That(end).IsStrictlyGreaterThan(start);
        return source[start..end];
    }

    [Fact]
    public void Constructor_BuildsTheToggle_ThenRegistersItsDwellTarget()
    {
        // Final review F1: both lines were unpinned — deleting either passed the whole 6260-test
        // suite. BuildModeToggle without RegisterModeToggleTarget leaves the toggle gaze-dead.
        var body = ExtractMethod(WindowSource(), "public SettingsWindow(");
        Check.WithCustomMessage(
                "The ctor must build the mode toggle — there is nothing to register without it "
                + "(final review F1).")
            .That(body).Contains("BuildModeToggle(");
        Check.WithCustomMessage(
                "The ctor must register the toggle's dwell target (final review F1). "
                + "BuildTabButtons_RegistersTheCloseButton (plan-review I4) is the precedent: an "
                + "unpinned registration line is how gaze-critical chrome silently dies.")
            .That(body).Contains("RegisterModeToggleTarget()");
        Check.WithCustomMessage(
                "Registration must follow construction — registering before the toggle is built "
                + "registers nothing (final review F1).")
            .That(body.IndexOf("BuildModeToggle("))
            .IsStrictlyLessThan(body.IndexOf("RegisterModeToggleTarget()"));
    }

    [Fact]
    public void ModeToggle_RegistersUnderPagination_AtItsOwnRadius_ThroughItsOwnMethod()
    {
        var body = ExtractMethod(WindowSource(), "private void RegisterModeToggleTarget(");
        Check.WithCustomMessage(
                "The toggle must NOT ride RegisterPagerTarget: that path hard-codes PagBtnRadius "
                + "= 60, and a 60-hit/50-release mismatch re-opens the oscillation (round-3 I3).")
            .That(body).Contains("DwellCategory.Pagination");
        Check.That(body).Contains("ModeToggleHitRadius");
        Check.That(body).Contains("IsDisarmed");
    }

    [Fact]
    public void ModeToggle_GuardUsesTheReleaseRadius_AndTheTrackedCentre()
    {
        var body = ExtractMethod(WindowSource(), "private void GuardModeToggle(");
        Check.That(body).Contains("_pagerCenters");
        // Impl review F1 (mutation d2): a whole-method Contains was satisfied by ONE surviving
        // occurrence — swapping the tracked-centre branch (the one every dwell activation
        // takes; the frame loop has already pushed _pagerCenters["settings_mode"]) to the hit
        // radius passed the whole suite green, leaving a 50-hit/50-release guard that re-arms
        // under the resting gaze that made the flip. So the count is the pin: BOTH Disarm
        // paths carry the release radius, and the hit radius appears on NEITHER — the guard
        // never uses it.
        Check.WithCustomMessage(
                "GuardModeToggle must pass ModeToggleReleaseRadius on BOTH Disarm paths — the "
                + "tracked-centre branch AND the border-fallback branch (exactly two occurrences). "
                + "Mutation d2 (impl review) swapped only the tracked-centre branch to "
                + "ModeToggleHitRadius and passed 2270/0: the gaze drift past 50 DIP re-arms the "
                + "toggle and the next dwell flips the mode back — the oscillation owner ruling "
                + "L16 exists to stop.")
            .That(CountOccurrences(body, "ModeToggleReleaseRadius")).IsEqualTo(2);
        Check.WithCustomMessage(
                "GuardModeToggle must never use ModeToggleHitRadius — 85 release over 50 hit IS "
                + "the hysteresis (mutation d2, impl review).")
            .That(body).Not.Contains("ModeToggleHitRadius");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void RearmPagerTarget_KnowsTheModeToggle()
    {
        var body = ExtractMethod(WindowSource(), "private void RearmPagerTarget(");
        Check.That(body).Contains("case ModeToggleId:");          // agrees with the wire-in
        Check.That(body).Contains("RegisterModeToggleTarget()");
    }

    [Fact]
    public void FrameLoop_PushesTheToggleWithoutALambda()
    {
        var body = ExtractMethod(WindowSource(), "private void UpdateAllTargetPositions(");
        Check.WithCustomMessage("No lambda may appear in the frame loop (allocation gate).")
            .That(body.Contains("=>")).IsFalse();
        Check.That(body).Contains("_modeToggleButtons, DwellCategory.Pagination");
    }

    [Fact]
    public void PagProgress_WashesTheToggleThroughItsOwnDictionary()
    {
        // Slice the switch statement to the NEXT case, or a call landed in ModalProgress would
        // pass this pin (plan-review m11).
        var body = SliceBetween(
            WindowSource(),
            "case nameof(SettingsWindowViewModel.PagProgress):",
            "case nameof(SettingsWindowViewModel.ModalProgress):");
        Check.That(body).Contains("UpdateModeToggleVisuals");
    }
}
