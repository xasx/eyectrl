namespace EyeCtrl.Core.Diagnostics;

/// <summary>The verdict of a block or a session: convict, acquit, or no verdict.</summary>
public enum SubPixelAbOutcome
{
    /// <summary>At or above the conviction threshold (≥ 8 of 10).</summary>
    Convicts,

    /// <summary>Below the conviction threshold.</summary>
    Acquits,

    /// <summary>More voids than the cap allows: the block or session has no verdict.</summary>
    Incomplete,
}

/// <summary>
/// The static block's two-sided readout. It is supporting evidence and never convicts: a whole-pixel
/// still ring is not measurably crisper, so the static question has no correct answer to score.
/// </summary>
public enum SubPixelAbStaticReadout
{
    /// <summary>Never run, because the drift block was incomplete.</summary>
    Skipped,

    /// <summary>More voids than the cap allows: no readout.</summary>
    Incomplete,

    /// <summary>The picks are spread out — still rings could not be told apart.</summary>
    CannotTellStillRingsApart,

    /// <summary>The picks went strongly to one condition (≥ 8 or ≤ 2 of 10): still rings were told apart.</summary>
    CanTellStillRingsApart,
}

/// <summary>
/// The owner's answers in one block, plus how many of its trials were voided. Voided trials are
/// repeated at the end of their block, so a completed block scores 10 trials; <see cref="Voids"/> is
/// reported separately for the cap.
/// </summary>
/// <param name="APicks">Trials whose answer was condition A (whole-pixel).</param>
/// <param name="BPicks">Trials whose answer was condition B (sub-pixel).</param>
/// <param name="Voids">Trials that were voided and repeated.</param>
public sealed record SubPixelAbBlockTally(int APicks, int BPicks, int Voids)
{
    /// <summary>Trials with an answer; 10 for a completed block.</summary>
    public int Scored => APicks + BPicks;
}

/// <summary>Session 1's verdict, with the static block's supporting readout.</summary>
/// <param name="Outcome">The session's verdict, decided by the drift block alone.</param>
/// <param name="StaticReadout">The static block's two-sided readout.</param>
/// <param name="LogLine">The one-line session summary, for the log.</param>
public sealed record SubPixelAbSession1Verdict(
    SubPixelAbOutcome Outcome,
    SubPixelAbStaticReadout StaticReadout,
    string LogLine);

/// <summary>Session 2's verdict.</summary>
/// <param name="Outcome">The session's verdict, decided by the live block alone.</param>
/// <param name="LogLine">The one-line session summary, for the log.</param>
public sealed record SubPixelAbSession2Verdict(
    SubPixelAbOutcome Outcome,
    string LogLine);

/// <summary>
/// The pre-registered verdict rules of the sub-pixel visibility test, applied to a block's answers.
/// <para>
/// Session 1 convicts on drift ≥ 8/10; the static block is two-sided and never convicts. Session 2
/// convicts on live ≥ 8/10. Each block may void at most 3 trials — a 4th void makes that block
/// incomplete, which removes only the verdicts that block decides: a static cap keeps the drift
/// verdict, a drift cap removes the session-1 verdict and skips the static block.
/// </para>
/// <para>
/// Log lines are English like every other Core diagnostic line (<see cref="RingQuantizationProbe"/>,
/// <see cref="DispatchLatencyProbe"/>); the string table holds user-facing text only, and a log line
/// is read by an operator from the log file.
/// </para>
/// </summary>
public static class SubPixelAbVerdict
{
    /// <summary>Correct trials needed to convict a block: ≥ 8 of 10.</summary>
    public const int ConvictionThreshold = 8;

    /// <summary>Voids a block may absorb; the next one makes it incomplete.</summary>
    public const int MaxVoidsPerBlock = 3;

    /// <summary>Upper two-sided bound of the static readout: ≥ 8 picks of A can tell rings apart.</summary>
    public const int StaticCanTellHigh = 8;

    /// <summary>Lower two-sided bound of the static readout: ≤ 2 picks of A can tell rings apart.</summary>
    public const int StaticCanTellLow = 2;

    /// <summary>
    /// Session 1: convicts on <c>drift BPicks ≥ 8</c>; the static block is read two-sided and never
    /// convicts. A drift cap removes the session-1 verdict and skips the static block; a static cap
    /// leaves the drift verdict standing and reads "incomplete".
    /// <para>
    /// Tallies are validated before they are scored: no count may be negative, no block may score
    /// more than 10 trials, and a block that did not hit the void cap must have scored all 10 — the
    /// "≥ 8 of 10" rule and the two-sided symmetry only hold out of 10. A capped block may be short:
    /// it never reached its 10th score. When the drift block is capped the static block was skipped,
    /// so <paramref name="staticBlock"/> must be <c>null</c>.
    /// </para>
    /// </summary>
    /// <param name="drift">The drift block's answers and voids.</param>
    /// <param name="staticBlock">The static block's tally, or <c>null</c> if it was not run.</param>
    /// <returns>The session-1 verdict and log line.</returns>
    /// <exception cref="ArgumentException">
    /// A tally is malformed, or a static tally is passed while the drift block is incomplete.
    /// </exception>
    public static SubPixelAbSession1Verdict Session1(SubPixelAbBlockTally drift, SubPixelAbBlockTally? staticBlock)
    {
        ValidateTally(drift, nameof(drift));

        if (drift.Voids > MaxVoidsPerBlock)
        {
            if (staticBlock is not null)
                throw new ArgumentException(
                    "The static block is skipped when the drift block is incomplete; pass null.",
                    nameof(staticBlock));

            return new SubPixelAbSession1Verdict(
                SubPixelAbOutcome.Incomplete,
                SubPixelAbStaticReadout.Skipped,
                $"Sub-pixel A/B — drift incomplete ({drift.Voids} voided) → session 1 has no verdict");
        }

        if (staticBlock is not null) ValidateTally(staticBlock, nameof(staticBlock));

        var outcome = drift.BPicks >= ConvictionThreshold
            ? SubPixelAbOutcome.Convicts
            : SubPixelAbOutcome.Acquits;

        var readout = StaticReadout(staticBlock);
        return new SubPixelAbSession1Verdict(
            outcome,
            readout,
            $"Sub-pixel A/B — drift {drift.BPicks}/{drift.Scored} → session 1 {OutcomeText(outcome)} "
            + $"({StaticText(staticBlock, readout)})");
    }

    /// <summary>
    /// Session 2: convicts on <c>live BPicks ≥ 8</c>. A live cap removes the session-2 verdict.
    /// <para>
    /// The tally is validated like session 1's: non-negative counts, at most 10 scored, and exactly
    /// 10 scored unless the block hit its void cap.
    /// </para>
    /// </summary>
    /// <param name="live">The live block's answers and voids.</param>
    /// <returns>The session-2 verdict and log line.</returns>
    /// <exception cref="ArgumentException">The tally is malformed.</exception>
    public static SubPixelAbSession2Verdict Session2(SubPixelAbBlockTally live)
    {
        ValidateTally(live, nameof(live));

        if (live.Voids > MaxVoidsPerBlock)
        {
            return new SubPixelAbSession2Verdict(
                SubPixelAbOutcome.Incomplete,
                $"Sub-pixel A/B — live incomplete ({live.Voids} voided) → session 2 has no verdict");
        }

        var outcome = live.BPicks >= ConvictionThreshold
            ? SubPixelAbOutcome.Convicts
            : SubPixelAbOutcome.Acquits;

        return new SubPixelAbSession2Verdict(
            outcome,
            $"Sub-pixel A/B — live {live.BPicks}/{live.Scored} → session 2 {OutcomeText(outcome)}");
    }

    // The rules are stated "of 10". Without this guard a partial block (an aborted run, one wrong
    // caller) would be scored against a threshold the pre-registration never set, and a
    // double-counted requeue could convict on 11 trials (review 01 F3). A capped block is the one
    // legitimate short block: it was cut off by its 4th void and never reached its 10th score.
    private static void ValidateTally(SubPixelAbBlockTally tally, string paramName)
    {
        if (tally.APicks < 0 || tally.BPicks < 0 || tally.Voids < 0)
            throw new ArgumentException("Block counts must be non-negative.", paramName);

        if (tally.Scored > SubPixelAbSchedule.TrialsPerBlock)
            throw new ArgumentException(
                $"A block cannot score more than {SubPixelAbSchedule.TrialsPerBlock} trials; got {tally.Scored}.",
                paramName);

        if (tally.Voids <= MaxVoidsPerBlock && tally.Scored != SubPixelAbSchedule.TrialsPerBlock)
            throw new ArgumentException(
                $"A block that did not hit the void cap must score all {SubPixelAbSchedule.TrialsPerBlock} "
                + $"trials; got {tally.Scored}.",
                paramName);
    }

    private static SubPixelAbStaticReadout StaticReadout(SubPixelAbBlockTally? staticBlock)
    {
        if (staticBlock is null) return SubPixelAbStaticReadout.Skipped;
        if (staticBlock.Voids > MaxVoidsPerBlock) return SubPixelAbStaticReadout.Incomplete;

        return staticBlock.APicks >= StaticCanTellHigh || staticBlock.APicks <= StaticCanTellLow
            ? SubPixelAbStaticReadout.CanTellStillRingsApart
            : SubPixelAbStaticReadout.CannotTellStillRingsApart;
    }

    private static string StaticText(SubPixelAbBlockTally? staticBlock, SubPixelAbStaticReadout readout) =>
        readout switch
        {
            SubPixelAbStaticReadout.Skipped => "static skipped",
            SubPixelAbStaticReadout.Incomplete => "static incomplete",
            SubPixelAbStaticReadout.CanTellStillRingsApart =>
                $"static {staticBlock!.APicks}/{staticBlock.Scored}: can tell still rings apart",
            _ => $"static {staticBlock!.APicks}/{staticBlock.Scored}: cannot tell still rings apart",
        };

    private static string OutcomeText(SubPixelAbOutcome outcome) => outcome switch
    {
        SubPixelAbOutcome.Convicts => "CONVICTS",
        SubPixelAbOutcome.Acquits => "ACQUITTED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Incomplete has no label here."),
    };
}
