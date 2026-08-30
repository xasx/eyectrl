namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Which <see cref="ContainerRatio"/> each file chose, pinned as a census.
///
/// <see cref="GlyphSizeCouplingTests"/> pins the ratio CHOICE at four sites, and it is the better
/// instrument where it applies: it constructs the window and compares the effective FontSize
/// against <c>const × ratio</c>, so it catches a broken coupling as well as a swapped bucket. It
/// only applies where the container is a compile-time const. Half the migrated sites size their
/// container at runtime — <c>KeyboardWindow._keySize</c> scales with a user setting,
/// <c>ToolbarWindow._buttonSize</c> and <c>EmojiGridManager.keySize</c> with the screen — so there
/// is no const for a test to multiply, and constructing those windows to lay them out is not
/// something this suite may do.
///
/// What was left, therefore: at eight of the sites, swapping <c>ContainerRatio.Small</c> for
/// <c>ContainerRatio.Dominant</c> passed every test in the repo.
/// <see cref="RawGeometryLiteralTests"/> sees a token-backed value and is satisfied;
/// <c>Legibility.MinimumGlyphFontSize</c> only stops a glyph going under 12px and says nothing
/// about one rendering two and a half times too large.
///
/// So this test asks the one question that needs no layout: **which bucket does each file name?**
/// It is a source census, not a measurement — it cannot tell you a glyph looks right, only that
/// nobody changed which ratio was chosen without saying so. That is deliberately the weaker claim,
/// and it is the strongest one available at a site whose container does not exist until runtime.
///
/// Keyed by file and counted, not by line: a census by line number would fail on every unrelated
/// edit above a site, and one that ignored counts would miss a site being deleted. Counts come
/// from <see cref="SourceScanner"/>'s comment-stripped text, so the several doc comments that
/// mention a ratio by name do not inflate them.
/// </summary>
public class ContainerRatioCensusTests
{
    /// <summary>Any <c>ContainerRatio.Member</c> reference in live code.</summary>
    private static readonly Regex Reference =
        new(@"\bContainerRatio\.(?<ratio>\w+)", RegexOptions.Compiled);

    /// <summary>
    /// Every file that reads a ratio, what it reads, how many times, and why that bucket.
    ///
    /// The rationale is the part that earns this test its place. A bare count table would fail
    /// loudly and tell the next person nothing about whether the new value is better than the old
    /// one; the reason has to travel with the number, because <see cref="ContainerRatio"/>'s own
    /// doc comment warns that two of the five members name a SITUATION rather than a magnitude and
    /// that size intuition alone picks wrong.
    /// </summary>
    private static readonly Dictionary<string, (string Ratio, int Count, string Why)[]> Census =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["src/EyeCtrl.App/Overlays/DragTrailOverlay.xaml.cs"] =
            [
                ("Badge", 2, "grab/release glyphs in a 26px disc — under the ~32px where a "
                    + "smaller fraction stops being legible. The expression is repeated rather "
                    + "than read from IconSize, which is why the count is 2."),
            ],
            ["src/EyeCtrl.App/Overlays/EmojiGridManager.cs"] =
            [
                ("Large", 1, "the emoji cell itself — a prominent glyph filling most of its cell."),
                ("Medium", 2, "category tab and skin-tone cell — mid-weight icons beside other "
                    + "content."),
                ("Small", 1, "the grid's own chrome label, sitting above content."),
            ],
            ["src/EyeCtrl.App/Overlays/EyeCareBannerWindow.xaml.cs"] =
            [
                ("Medium", 2, "the two dwell-ring glyphs, each with a caption underneath, so the "
                    + "glyph is mid-weight content in its ring rather than the whole of it. Also "
                    + "pinned exactly by GlyphSizeCouplingTests."),
            ],
            ["src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs"] =
            [
                ("Small", 2, "key legend and prediction word — short labels in a key that RESIZES "
                    + "with the KeySize setting. No const to multiply, so this census is the only "
                    + "thing holding the bucket."),
            ],
            ["src/EyeCtrl.App/Overlays/NoteWindow.xaml.cs"] =
            [
                ("Small", 1, "the note's title-bar button glyph, beside other chrome."),
            ],
            ["src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml.cs"] =
            [
                ("Dominant", 1, "the 44px close button, whose entire content is one glyph with no "
                    + "label beside it. Also pinned exactly by GlyphSizeCouplingTests."),
                ("Small", 1, "TileIconSize — the tile icon has its action label underneath it. "
                    + "Also pinned exactly by GlyphSizeCouplingTests."),
            ],
            ["src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs"] =
            [
                ("Small", 1, "Task 6: the gaze-provider indicator's own glyph (an eye or a "
                    + "mouse), now drawn as a DwellArcButton-style icon Path rather than an "
                    + "emoji TextBlock. It sits beside the hairline rule that follows it — an "
                    + "icon beside other content is exactly Small's bucket — replacing the "
                    + "old Large-ratio emoji cell, which had no such neighbour. _buttonSize is "
                    + "runtime-sized: no const to multiply."),
                ("Dominant", 3, "one site unchanged: the half-length of the hairline that fences "
                    + "off the destructive Reset button, as a fraction of the button diameter it "
                    + "sits beside (NOT a glyph). Two new: Task 6's indicator rule reuses the same "
                    + "bucket for the same reason — a rule reading as the separator's whole "
                    + "content the way a Dominant glyph fills its button — sized on whichever of "
                    + "Width/Height carries the button-diameter arm of its vertical/horizontal "
                    + "ternary (the other arm is Tokens.BorderThicknessStandard, not a ratio)."),
            ],
            ["src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"] =
            [
                ("Large", 2, "the pagination digit and the tab-list pager's ▲/▼, each filling "
                    + "most of its 80px button (the tab pager reuses the content pager's "
                    + "button metrics)."),
                ("Medium", 1, "the settings tab icon, which sits beside its tab label."),
            ],
            ["src/EyeCtrl.Controls/DwellArcButton.cs"] =
            [
                ("Badge", 1, "diameters under BadgeDiameterThreshold — the edge bar's 40px "
                    + "drag/back buttons, where Small would give 9.2px, below the accessibility "
                    + "floor, on the affordance that CANCELS A DRAG."),
                ("Small", 1, "diameters at or above the threshold — the 100px hide button "
                    + "and ManualSelection side buttons, whose 22/100 is where Small was "
                    + "observed. No longer the action menu: it took Small while it rendered a "
                    + "Unicode glyph, and now draws an icon."),
                ("Dominant", 1, "the drawn icon on a 140px action-menu button while the ring is "
                    + "icon-only, so the icon IS the button's entire content — the situation "
                    + "Dominant names. 140 x 0.45 = 63px. Half of a ternary since 2026-08-26: "
                    + "the same line picks Medium once a label shares the face."),
                ("Medium", 1, "the icon on a 140px action-menu button that is ALSO showing a "
                    + "label — the icon is no longer the button's entire content, so Dominant "
                    + "stops applying. 140 x 0.32 = 44.8px."),
            ],
        };

    [Fact]
    public void EveryRatioChoice_IsStillTheOneOnRecord()
    {
        var actual = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relative, text) in SourceScanner.ScannedFiles())
        {
            // The declaration itself names all five members; it is the subject, not a consumer.
            if (relative.EndsWith("Theme/ContainerRatio.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (Match m in Reference.Matches(text))
            {
                if (!actual.TryGetValue(relative, out var counts))
                    actual[relative] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
                counts.TryGetValue(m.Groups["ratio"].Value, out var n);
                counts[m.Groups["ratio"].Value] = n + 1;
            }
        }

        var expected = Census.ToDictionary(
            e => e.Key,
            e => e.Value.ToDictionary(v => v.Ratio, v => v.Count, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();

        foreach (var file in expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var want = expected.GetValueOrDefault(file);
            var got = actual.GetValueOrDefault(file);

            if (want is null)
            {
                problems.Add(
                    $"{file}: reads {Describe(got!)} but is not in the census. A new ratio site "
                    + "needs an entry here saying WHICH bucket and WHY — that reason is the only "
                    + "record of the choice at a runtime-sized container.");
                continue;
            }

            if (got is null)
            {
                problems.Add(
                    $"{file}: the census expects {Describe(want)} but the file reads no ratio at "
                    + "all. If the site was removed or the size hard-coded again, drop the entry; "
                    + "if the file moved, repoint it.");
                continue;
            }

            foreach (var ratio in want.Keys.Union(got.Keys, StringComparer.Ordinal))
            {
                var w = want.GetValueOrDefault(ratio);
                var g = got.GetValueOrDefault(ratio);
                if (w == g) continue;

                var why = Census[file].FirstOrDefault(e => e.Ratio == ratio).Why
                          ?? "(no recorded reason — this bucket is not in the census for this file)";
                problems.Add(
                    $"{file}: ContainerRatio.{ratio} appears {g}x, census says {w}x. Recorded "
                    + $"reason: {why}");
            }
        }

        Check.WithCustomMessage(
                "The container-ratio census no longer matches the source. This is not a style "
                + "check: half these sites size their container at RUNTIME, so no other test in "
                + "the repo can tell Small from Dominant there, and getting it wrong renders a "
                + "glyph two and a half times too large or too small in front of a user whose only "
                + "input channel is an eye tracker. If the change is deliberate, update the entry "
                + "AND its reason against the buckets in ContainerRatio's doc comment — check "
                + "Badge and Dominant first, since they name situations rather than sizes.\n  "
                + string.Join("\n  ", problems))
            .That(problems).IsEmpty();

        static string Describe(Dictionary<string, int> counts) =>
            string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => $"{c.Key} x{c.Value}"));
    }

    [Fact]
    public void EveryCensusedRatio_IsARealContainerRatioMember()
    {
        // Stops the census drifting into fiction. A typo'd bucket name would otherwise sit in the
        // table forever: the count comparison above matches it against a source count of zero for
        // that spelling, which reads as "the site was removed" rather than "this bucket does not
        // exist". Reflected, so a renamed member fails here rather than silently emptying a group.
        var members = typeof(ContainerRatio)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => f.Name)
            .ToList();

        var unknown = Census
            .SelectMany(e => e.Value.Select(v => (e.Key, v.Ratio)))
            .Where(x => !members.Contains(x.Ratio, StringComparer.Ordinal))
            .Select(x => $"{x.Key}: ContainerRatio.{x.Ratio}")
            .ToList();

        Check.WithCustomMessage(
                "The census names a ContainerRatio member that does not exist. Real members are: "
                + string.Join(", ", members) + "\n  " + string.Join("\n  ", unknown))
            .That(unknown).IsEmpty();
    }
}
