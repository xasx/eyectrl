namespace EyeCtrl.Controls.Tests.Theme;

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Cross-language drift guard for the one absolute pixel floor in the token surface.
///
/// <see cref="Legibility.MinimumGlyphFontSize"/> exists because four keyboard and emoji glyph
/// sizes clamp against it, and it must be the SAME 12 that tools/palette_validate.py gates
/// <c>FontSizeBody</c> against — spec §8.4 chose one accessibility floor, not two. So this parses
/// the gate out of the validator rather than restating the number, the way
/// <see cref="ThemeParityTests"/> parses the palettes.
///
/// **Why the const is not simply <c>Tokens.FontSizeBody</c>, which holds the same 12.** The
/// validator and <c>ThemeShapeTests</c> both gate <c>FontSizeBody &gt;= 12</c>, never
/// <c>== 12</c>. Raising body text to 14 is an in-ramp change both guards permit, and while the
/// clamps read the ramp step it would silently grow four glyph sizes at the bottom of
/// <c>KeySizeMultiplier</c>'s range with nothing recording that a MINIMUM had been intended rather
/// than a step. <see cref="TheFloorAndTheRampStep_CoincideTodayButAreNotTheSameThing"/> pins that
/// separation so the two cannot be silently re-merged either.
/// </summary>
public class LegibilityFloorTests
{
    /// <summary>Walks up from the test assembly to the directory holding EyeCtrl.slnx.</summary>
    /// <returns>The validator's source text.</returns>
    private static string ValidatorSource()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(LegibilityFloorTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.WithCustomMessage("Could not locate the repo root (EyeCtrl.slnx)")
            .That(dir).IsNotNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "palette_validate.py"));
    }

    [Fact]
    public void TheMinimum_EqualsTheFloorTheValidatorGates()
    {
        // Matches the gate as written: check(f"[{name}] FontSizeBody floor", f_body, 12.0, ...).
        // The second argument to check() is the measured value and the third is the minimum it
        // must clear, so the third is the floor. Anchored on the label so a different check
        // cannot be read by mistake.
        var source = ValidatorSource();
        var gate = Regex.Match(
            source,
            @"check\(f""\[\{name\}\]\s*FontSizeBody floor""\s*,\s*f_body\s*,\s*(?<floor>[\d.]+)");

        Check.WithCustomMessage(
                "tools/palette_validate.py no longer contains a recognisable 'FontSizeBody floor' "
                + "gate. If the gate was renamed or restructured, update this pattern — do not "
                + "delete the test: it is the only thing keeping the C# minimum and the Python "
                + "floor from drifting apart.")
            .That(gate.Success).IsTrue();

        var floor = double.Parse(
            gate.Groups["floor"].Value, CultureInfo.InvariantCulture);

        Check.WithCustomMessage(
                $"Legibility.MinimumGlyphFontSize is {Legibility.MinimumGlyphFontSize} but "
                + $"tools/palette_validate.py gates the accessibility floor at {floor}. Spec §8.4 "
                + "chose ONE floor. Whichever moved, move the other — and re-run "
                + "`python tools/palette_validate.py` (must exit 0) before changing this.")
            .That(Legibility.MinimumGlyphFontSize).IsEqualTo(floor);
    }

    [Fact]
    public void TheFloorAndTheRampStep_CoincideTodayButAreNotTheSameThing()
    {
        // Recorded, not asserted as an invariant: FontSizeBody is FREE to rise above the floor,
        // and when it does this becomes the assertion that has to change — deliberately, with
        // someone reading this comment — instead of four glyph sizes changing silently.
        //
        // Not redundant with ThemeShapeTests.AllThreeThemes_DeclareIdenticalGeometryAndType, which
        // pins FontSizeBody against a LITERAL 12. Someone raising the ramp step edits that literal
        // and moves on; this is the assertion that then puts the four clamp sites in front of
        // them. It reads the ambient Tokens rather than a concrete theme because that same test
        // pins all three themes to one value, so whichever theme is active the expectation holds.
        Check.WithCustomMessage(
                "Tokens.FontSizeBody has moved off the accessibility floor. That is allowed: the "
                + "validator and ThemeShapeTests gate it >= 12, not == 12. Nothing is broken — but "
                + "confirm that the four clamp sites in KeyboardWindow and EmojiGridManager still "
                + "read Legibility.MinimumGlyphFontSize and NOT Tokens.FontSizeBody, then relax "
                + $"this assertion. Floor {Legibility.MinimumGlyphFontSize}, body "
                + $"{Tokens.FontSizeBody}.")
            .That(Tokens.FontSizeBody).IsEqualTo(Legibility.MinimumGlyphFontSize);

        Check.WithCustomMessage(
                "The ramp step must never fall BELOW the accessibility floor — that inverts the "
                + "guarantee that made the 9->12 raise permanent.")
            .That(Tokens.FontSizeBody).IsGreaterOrEqualThan(Legibility.MinimumGlyphFontSize);
    }
}
