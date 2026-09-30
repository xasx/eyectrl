namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// G9: the ring-radius formula
/// (<c>circleSize/2 + ActionMenuButtonRadius + ActionMenuButtonOuterGap</c>) lives in exactly one
/// place — <c>src/EyeCtrl.Core/ActionMenu/RingPlan.cs</c> — and every consumer reads it from
/// there.
///
/// <para>
/// This scan is comment-stripped and <c>src/</c>-only. Tests are deliberately excluded: fixtures
/// restate the formula on purpose to pin the plan's output. The formula had two sites before the
/// plan superseded them — <c>ActionMenuViewModel.BuildLayer</c> (then
/// <c>ActionMenuViewModel.cs:119</c>) and <c>ActionMenuLayoutHelper.CalculateDisplayPoint</c>
/// (then <c>ActionMenuLayoutHelper.cs:32</c>) — and a third copy is exactly the drift this gate
/// exists to prevent: the drawn ring, the dwell hit circle and the screen-edge clamp would each
/// follow their own arithmetic.
/// </para>
///
/// <para>
/// The window-size formula (<c>(ringRadius + ButtonRadius) * 2 + margin</c>) is the same class of
/// arithmetic one step downstream, and it too had two sites — <c>RingPlan.WindowSizeFor</c> and
/// <c>ActionMenuGeometry.WindowSize</c> (deleted 2026-09-30, T4). It is scanned below by the same
/// walk, so the square the runtime window opens and the square the settings preview scales to
/// cannot drift apart either.
/// </para>
///
/// <para>
/// Line numbers are NOT matched: the superseded sites are named by member, because every file
/// around them moved while the tickets landed.
/// </para>
/// </summary>
public class RingRadiusFormulaScanTests
{
    /// <summary>
    /// The formula as code: something divided by two, plus a button radius, plus the outer gap.
    /// Whitespace is collapsed before matching, so a formula wrapped across lines is still one
    /// match. The identifier tails accept both spellings that existed — <c>LayoutConstants.X</c>
    /// and the ViewModels alias <c>UIConstants.X</c>.
    /// </summary>
    private static readonly Regex RadiusFormula = new(
        @"[\w.]+\s*/\s*2\s*\+\s*[\w.]+\.ActionMenuButtonRadius\s*\+\s*[\w.]+\.ActionMenuButtonOuterGap",
        RegexOptions.Compiled);

    /// <summary>
    /// The window-size formula as code: a parenthesised ring radius plus a button radius, doubled.
    /// The button-radius identifier accepts the qualified spellings (<c>LayoutConstants.</c>,
    /// <c>UIConstants.</c>) AND the local alias <c>ButtonRadius</c> the former
    /// <c>ActionMenuGeometry.WindowSize</c> body used — that alias was the site the scan has to
    /// catch if it is ever restated. The trailing margin operand is deliberately not matched:
    /// the doubling is the formula, and the margin is a name that can move.
    /// </summary>
    private static readonly Regex WindowSizeFormula = new(
        @"\(\s*[\w.]+\s*\+\s*[\w.]*(?:ActionMenu)?ButtonRadius\s*\)\s*\*\s*2\s*\+",
        RegexOptions.Compiled);

    /// <summary>Collapses all whitespace so a wrapped formula reports as one match.</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private const string PlanRelativePath = "src/EyeCtrl.Core/ActionMenu/RingPlan.cs";

    /// <summary>
    /// The pattern's own falsification pin. A scan that cannot recognise the formula cannot catch
    /// a reintroduction, so both spellings that actually existed are matched here — the
    /// ViewModels <c>UIConstants</c> form that filled <c>ActionMenuViewModel.cs:119</c> and the
    /// <c>LayoutConstants</c> form that filled <c>ActionMenuLayoutHelper.cs:32</c>. If either
    /// stops matching, widen the pattern before trusting the scan.
    /// </summary>
    [Theory]
    [InlineData("EffectiveRingRadius = message.CircleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;")]
    [InlineData("var effectiveRingRadius = circleSize / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;")]
    [InlineData("var r = circleSize / 2\n    + LayoutConstants.ActionMenuButtonRadius\n    + LayoutConstants.ActionMenuButtonOuterGap;")]
    public void ThePattern_RecognisesEveryFormerSpelling(string formerSite)
    {
        var collapsed = Whitespace.Replace(formerSite, " ");
        Check.WithCustomMessage($"The scan no longer recognises this formula spelling:\n  {formerSite}")
             .That(RadiusFormula.IsMatch(collapsed)).IsTrue();
    }

    /// <summary>
    /// The window-size pattern's own falsification pin, over every spelling the formula ever had:
    /// the deleted <c>ActionMenuGeometry.WindowSize</c> body (which read its local
    /// <c>ButtonRadius</c> alias), the plan's qualified spelling, and a wrapped restatement with a
    /// bare margin. If any stops matching, widen the pattern before trusting the scan.
    /// </summary>
    [Theory]
    [InlineData("internal static double WindowSize(double ringRadiusDip) => (ringRadiusDip + ButtonRadius) * 2 + WindowSizeMargin;")]
    [InlineData("var windowSize = (ringRadiusDip + LayoutConstants.ActionMenuButtonRadius) * 2 + RingPlan.WindowSizeMargin;")]
    [InlineData("var windowSize = (ringRadiusDip + UIConstants.ActionMenuButtonRadius)\n    * 2 + 20;")]
    public void TheWindowSizePattern_RecognisesEveryFormerSpelling(string formerSite)
    {
        var collapsed = Whitespace.Replace(formerSite, " ");
        Check.WithCustomMessage($"The scan no longer recognises this formula spelling:\n  {formerSite}")
             .That(WindowSizeFormula.IsMatch(collapsed)).IsTrue();
    }

    /// <summary>
    /// Every <c>src/</c> .cs file, comment-stripped with whitespace collapsed, as
    /// (repo-relative path, text) pairs. Shared by both scans so they cannot walk differently.
    /// </summary>
    /// <returns>One entry per source file.</returns>
    private static IEnumerable<(string Relative, string Text)> SrcFiles()
    {
        var root = SourceScanner.RepoRoot().FullName;
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            yield return (Path.GetRelativePath(root, file).Replace('\\', '/'),
                          Whitespace.Replace(SourceScanner.StripComments(File.ReadAllText(file)), " "));
        }
    }

    [Fact]
    public void TheRingRadiusFormula_LivesOnlyInTheCorePlan()
    {
        var scanned = 0;
        var planContainsFormula = false;
        var offenders = new List<string>();

        foreach (var (relative, text) in SrcFiles())
        {
            scanned++;
            if (!RadiusFormula.IsMatch(text)) continue;

            if (relative == PlanRelativePath) planContainsFormula = true;
            else offenders.Add(relative);
        }

        Check.WithCustomMessage(
                "The scan found no src/ .cs files at all — the repo root resolved wrongly, so a "
                + "green result would mean nothing.")
             .That(scanned).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                $"The radius formula is gone from {PlanRelativePath}, so this scan's positive "
                + "control failed: either the formula moved (update this gate's allowed file) or "
                + "the pattern no longer matches the plan's spelling.")
             .That(planContainsFormula).IsTrue();

        Check.WithCustomMessage(
                "The ring radius formula must live ONLY in "
                + $"{PlanRelativePath}. It superseded two sites: ActionMenuViewModel.BuildLayer "
                + "(formerly ActionMenuViewModel.cs:119) and "
                + "ActionMenuLayoutHelper.CalculateDisplayPoint (formerly "
                + "ActionMenuLayoutHelper.cs:32). Read RingPlan.EffectiveRingRadiusFor instead of "
                + "restating the arithmetic. Found in:\n  "
                + string.Join("\n  ", offenders))
             .That(offenders).IsEmpty();
    }

    /// <summary>
    /// The window-size companion: <c>(ringRadius + buttonRadius) * 2 + margin</c> lives only in
    /// the plan. The former second site was <c>ActionMenuGeometry.WindowSize</c>, which the
    /// runtime window called; T4 consolidated it, and this scan keeps the consolidation — the
    /// settings preview's fit scalar and the runtime window's size must read one definition or
    /// the preview scales against a square the menu does not open.
    /// </summary>
    [Fact]
    public void TheWindowSizeFormula_LivesOnlyInTheCorePlan()
    {
        var scanned = 0;
        var planContainsFormula = false;
        var offenders = new List<string>();

        foreach (var (relative, text) in SrcFiles())
        {
            scanned++;
            if (!WindowSizeFormula.IsMatch(text)) continue;

            if (relative == PlanRelativePath) planContainsFormula = true;
            else offenders.Add(relative);
        }

        Check.WithCustomMessage(
                "The scan found no src/ .cs files at all — the repo root resolved wrongly, so a "
                + "green result would mean nothing.")
             .That(scanned).IsStrictlyGreaterThan(0);

        Check.WithCustomMessage(
                $"The window-size formula is gone from {PlanRelativePath}, so this scan's positive "
                + "control failed: either the formula moved (update this gate's allowed file) or "
                + "the pattern no longer matches the plan's spelling.")
             .That(planContainsFormula).IsTrue();

        Check.WithCustomMessage(
                "The window-size formula must live ONLY in "
                + $"{PlanRelativePath}. Its second site was ActionMenuGeometry.WindowSize "
                + "(deleted 2026-09-30, T4). Read RingPlan.WindowSizeFor instead of restating "
                + "the arithmetic. Found in:\n  "
                + string.Join("\n  ", offenders))
             .That(offenders).IsEmpty();
    }
}
