namespace EyeCtrl.App.Tests;

using System;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// No dwell surface may blend its own fill toward the accent as progress accumulates.
///
/// That "wash" destroys the label's contrast exactly when the user is staring hardest at it.
/// Measured on the keyboard, where it ran at full strength: a normal key's label fell from
/// 10.42:1 at rest to <b>1.08:1</b> at full dwell under Midnight, 12.91 to 1.18 under Ember,
/// and 19.17 to 1.25 under Contrast — the theme built for low vision. Modifier keys, the
/// prediction bar and the emoji category tabs collapsed identically. The keyboard is the
/// most-dwelled surface in the app.
///
/// Choosing a different wash target does not repair it on those surfaces: keys carry light
/// labels on dark fills, so washing toward light destroys the label and washing toward dark is
/// imperceptible (a full blend to StrokeHalo changes a key by only 1.41:1 under Midnight,
/// 1.10:1 under Contrast). The feedback therefore moved off the fill — an accent outline that
/// thickens inward, <c>DwellOutlineOverlay</c>.
///
/// <c>DwellOutlineOverlayTests</c> proves the outline works. This test proves nobody quietly
/// goes back to washing, which is the failure that would otherwise be invisible: the previous
/// wash threw nothing and looked deliberate.
///
/// A source census rather than a measurement, for the reason
/// <see cref="ContainerRatioCensusTests"/> records: these surfaces size themselves at runtime
/// from a user setting or the screen, and this suite may not construct and lay out those
/// windows. Counted over comment-stripped text so the doc comments describing the old wash
/// (including this one) do not register.
/// </summary>
public class DwellWashCensusTests
{
    /// <summary>
    /// A blend whose final argument is a dwell progress value, in either spelling the overlays
    /// used: <c>ColorBlend.Blend(base, Tokens.Accent, progress)</c> and the hand-rolled
    /// per-channel lerp the prediction bar had.
    /// </summary>
    private static readonly Regex AccentWash =
        new(@"Blend\([^;]*Tokens\.Accent\s*,\s*progress", RegexOptions.Compiled);

    private static readonly Regex ManualLerp =
        new(@"\*\s*\(1\s*-\s*progress\)", RegexOptions.Compiled);

    /// <summary>
    /// The surfaces that used to wash. Each must now drive an outline instead — asserted
    /// positively, and by COUNT, so deleting one of several call sites is caught too.
    ///
    /// Expected SetProgress calls: KeyboardWindow drives keys on progress, clears them on
    /// reset, and drives predictions — three. EmojiGridManager drives category tabs — one.
    /// </summary>
    private static readonly (string File, int Attaches, int Advances)[] MustOutline =
    [
        ("src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs", 2, 3),
        ("src/EyeCtrl.App/Overlays/EmojiGridManager.cs", 1, 1),
    ];

    /// <summary>
    /// Sites that still wash, each with the reason it is not fixed here. A RATCHET, not a
    /// snapshot: the assertion is that offenders are a SUBSET of this list, so fixing one never
    /// turns the build red — only introducing a NEW wash does. Prune an entry when its site is
    /// fixed.
    /// </summary>
    private static readonly (string File, string Why)[] KnownRemaining =
    [
        ("src/EyeCtrl.Controls/ColorBlend.cs",
            "Defines Blend; it is the tool, not a use of it. The regex cannot tell a definition "
            + "from a call, and narrowing it by shape would weaken it."),
        ("src/EyeCtrl.App/Views/SettingsWindow.xaml.cs",
            "Tab-pager and content-pager blend via one shared helper (BlendProgress) at factor "
            + "0.4, so 10.42:1 falls to 3.39:1 — over the 3:1 bar, unlike the overlays' "
            + "full-strength blend. The close button uses 0.5 and lands in the 2.62:1 class "
            + "that the settings controls' bar fixed. All are hand-rolled rather than "
            + "DwellButton, so none is covered by that change; they belong with the settings "
            + "restyle."),
    ];

    [Fact]
    public void NoNewDwellSurface_BlendsItsOwnFillTowardTheAccentAsProgressAccumulates()
    {
        var known = KnownRemaining.Select(k => k.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = SourceScanner.ScannedFiles()
            .Select(f => (Relative: f.Relative.Replace('\\', '/'), Text: SourceScanner.StripComments(f.Text)))
            .Where(f => AccentWash.IsMatch(f.Text) || ManualLerp.IsMatch(f.Text))
            .Select(f => f.Relative)
            .Where(r => !known.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        Check.WithCustomMessage(
                "These files blend a fill toward Tokens.Accent by dwell progress, which collapses "
                + "the label's contrast on that fill as the dwell completes (measured: 10.42:1 -> "
                + "1.08:1 on a keyboard key). Move the feedback off the fill — see "
                + "DwellOutlineOverlay. If the site genuinely cannot, add it to KnownRemaining "
                + $"WITH a reason.\n  {string.Join("\n  ", offenders)}")
            .That(offenders).IsEmpty();
    }

    [Theory]
    [InlineData("src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs", 2, 3)]
    [InlineData("src/EyeCtrl.App/Overlays/EmojiGridManager.cs", 1, 1)]
    public void EveryFormerWashSite_DrivesAnOutline(string relative, int attaches, int advances)
    {
        var file = SourceScanner.ScannedFiles().SingleOrDefault(f =>
            f.Relative.Replace('\\', '/').EndsWith(relative, StringComparison.OrdinalIgnoreCase));

        Check.WithCustomMessage($"{relative} was not scanned — has it moved?")
            .That(file.Text).IsNotNull();

        var text = SourceScanner.StripComments(file.Text);

        Check.WithCustomMessage(
                $"{relative} attaches {Count(text, "DwellOutlineOverlay.Attach")} outline overlays, "
                + $"expected {attaches}. A dwell target without one shows no feedback at all, and "
                + "nothing throws. If a target was legitimately removed, update the count here.")
            .That(Count(text, "DwellOutlineOverlay.Attach")).IsEqualTo(attaches);

        Check.WithCustomMessage(
                $"{relative} advances {Count(text, "SetProgress(")} outlines, expected {advances}. "
                + "An attached outline that never advances leaves the target inert for the whole "
                + "dwell and then firing — exactly the defect this replaced.")
            .That(Count(text, "SetProgress(")).IsEqualTo(advances);
    }

    /// <summary>
    /// Guards the census itself. If the scanner stops reaching these files the tests above pass
    /// vacuously, which is the failure mode a source census is most prone to.
    /// </summary>
    [Fact]
    public void TheScanner_ActuallyReachesTheOverlayFiles()
    {
        var scanned = SourceScanner.ScannedFiles()
            .Select(f => f.Relative.Replace('\\', '/'))
            .ToArray();

        foreach (var (expected, _, _) in MustOutline)
        {
            Check.WithCustomMessage($"SourceScanner never yielded {expected}")
                .That(scanned.Any(s => s.EndsWith(expected, StringComparison.OrdinalIgnoreCase)))
                .IsTrue();
        }
    }

    private static int Count(string text, string needle)
    {
        var (n, i) = (0, 0);
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }

        return n;
    }
}
