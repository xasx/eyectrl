namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;

/// <summary>
/// U2 — the verdict rules: the 8/10 boundary in both sessions, a static block that is two-sided and
/// never convicts, and per-block void caps that remove only the verdicts their block decides.
/// </summary>
public class SubPixelAbVerdictTests
{
    private static readonly SubPixelAbBlockTally NeutralStatic = new(APicks: 6, BPicks: 4, Voids: 0);

    [Fact]
    public void Session1_DriftBoundary_SevenAcquits_EightConvicts()
    {
        var sevenCorrect = SubPixelAbVerdict.Session1(new(3, 7, 0), NeutralStatic);
        var eightCorrect = SubPixelAbVerdict.Session1(new(2, 8, 0), NeutralStatic);

        Check.That(sevenCorrect.Outcome).IsEqualTo(SubPixelAbOutcome.Acquits);
        Check.That(eightCorrect.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void Session1_StaticNeverConvicts_EvenWhenStillRingsWereToldApart()
    {
        // Drift below threshold, static at either extreme: the readout notices, the verdict does not
        // move. A whole-pixel still ring is not measurably crisper, so the static block has no
        // correct answer and must never convict.
        var allA = SubPixelAbVerdict.Session1(new(5, 5, 0), new(10, 0, 0));
        var allB = SubPixelAbVerdict.Session1(new(5, 5, 0), new(0, 10, 0));

        Check.That(allA.Outcome).IsEqualTo(SubPixelAbOutcome.Acquits);
        Check.That(allA.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.CanTellStillRingsApart);
        Check.That(allB.Outcome).IsEqualTo(SubPixelAbOutcome.Acquits);
        Check.That(allB.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.CanTellStillRingsApart);
    }

    [Fact]
    public void Session1_StaticReadout_IsTwoSided()
    {
        // ≥ 8 or ≤ 2 picks of A means "can tell still rings apart". Checking both tails pins the
        // two-sidedness: a one-sided implementation would read the low tail as "cannot tell", which
        // is a structural false acquittal for a real static difference.
        foreach ((int aPicks, SubPixelAbStaticReadout expected) in new[]
        {
            (10, SubPixelAbStaticReadout.CanTellStillRingsApart),
            (8, SubPixelAbStaticReadout.CanTellStillRingsApart),
            (7, SubPixelAbStaticReadout.CannotTellStillRingsApart),
            (6, SubPixelAbStaticReadout.CannotTellStillRingsApart),
            (5, SubPixelAbStaticReadout.CannotTellStillRingsApart),
            (3, SubPixelAbStaticReadout.CannotTellStillRingsApart),
            (2, SubPixelAbStaticReadout.CanTellStillRingsApart),
            (0, SubPixelAbStaticReadout.CanTellStillRingsApart),
        })
        {
            var verdict = SubPixelAbVerdict.Session1(new(3, 7, 0), new(aPicks, 10 - aPicks, 0));
            Check.That(verdict.StaticReadout).IsEqualTo(expected);
        }
    }

    [Fact]
    public void Session2_LiveBoundary_SevenAcquits_EightConvicts()
    {
        Check.That(SubPixelAbVerdict.Session2(new(3, 7, 0)).Outcome).IsEqualTo(SubPixelAbOutcome.Acquits);
        Check.That(SubPixelAbVerdict.Session2(new(2, 8, 0)).Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void VoidCap_ThreeVoidsStillScore_FourMakeTheBlockIncomplete()
    {
        var driftAtCap = SubPixelAbVerdict.Session1(new(2, 8, 3), new(5, 5, 3));
        Check.That(driftAtCap.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(driftAtCap.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.CannotTellStillRingsApart);

        var driftOverCap = SubPixelAbVerdict.Session1(new(2, 8, 4), new(5, 5, 0));
        Check.That(driftOverCap.Outcome).IsEqualTo(SubPixelAbOutcome.Incomplete);

        var liveOverCap = SubPixelAbVerdict.Session2(new(2, 8, 4));
        Check.That(liveOverCap.Outcome).IsEqualTo(SubPixelAbOutcome.Incomplete);
    }

    [Fact]
    public void Session1_StaticCap_KeepsTheDriftVerdict()
    {
        var verdict = SubPixelAbVerdict.Session1(new(1, 9, 0), new(6, 4, 4));

        Check.That(verdict.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Incomplete);
    }

    [Fact]
    public void Session1_DriftCap_RemovesTheVerdictAndSkipsTheStaticBlock()
    {
        // Static is skipped when drift caps out, so its tally is absent. A tally passed anyway does
        // not change the outcome: the static block's fate is not what removes the session-1 verdict.
        var skipped = SubPixelAbVerdict.Session1(new(0, 10, 4), staticBlock: null);
        var tallyIgnored = SubPixelAbVerdict.Session1(new(0, 10, 4), new(5, 5, 0));

        Check.That(skipped.Outcome).IsEqualTo(SubPixelAbOutcome.Incomplete);
        Check.That(skipped.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Skipped);
        Check.That(tallyIgnored.Outcome).IsEqualTo(SubPixelAbOutcome.Incomplete);
        Check.That(tallyIgnored.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Skipped);
    }

    [Fact]
    public void Session1_WithNoStaticBlock_RunReportsStaticSkipped()
    {
        var verdict = SubPixelAbVerdict.Session1(new(2, 8, 0), staticBlock: null);

        Check.That(verdict.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
        Check.That(verdict.StaticReadout).IsEqualTo(SubPixelAbStaticReadout.Skipped);
    }

    [Fact]
    public void LogLine_MatchesTheRecordedFormat()
    {
        Check.That(SubPixelAbVerdict.Session1(new(1, 9, 0), new(6, 4, 0)).LogLine)
            .IsEqualTo("Sub-pixel A/B — drift 9/10 → session 1 CONVICTS (static 6/10: cannot tell still rings apart)");

        Check.That(SubPixelAbVerdict.Session1(new(5, 5, 0), new(0, 10, 0)).LogLine)
            .IsEqualTo("Sub-pixel A/B — drift 5/10 → session 1 ACQUITTED (static 0/10: can tell still rings apart)");

        Check.That(SubPixelAbVerdict.Session1(new(1, 9, 0), new(6, 4, 4)).LogLine)
            .IsEqualTo("Sub-pixel A/B — drift 9/10 → session 1 CONVICTS (static incomplete)");

        Check.That(SubPixelAbVerdict.Session1(new(2, 8, 4), new(5, 5, 0)).LogLine)
            .IsEqualTo("Sub-pixel A/B — drift incomplete (4 voided) → session 1 has no verdict");

        Check.That(SubPixelAbVerdict.Session2(new(2, 8, 0)).LogLine)
            .IsEqualTo("Sub-pixel A/B — live 8/10 → session 2 CONVICTS");

        Check.That(SubPixelAbVerdict.Session2(new(3, 7, 4)).LogLine)
            .IsEqualTo("Sub-pixel A/B — live incomplete (4 voided) → session 2 has no verdict");
    }
}
