namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging;

/// <summary>
/// E1's recorder: the line shapes, the pick-undo and mode-undo rules, and the closed id sets. The
/// clock is injected, so the 3 s and 5 s windows are exact rather than slept.
/// </summary>
public class KeyboardE1RecorderTests
{
    private sealed class Capture : ILogger
    {
        public readonly List<string> Lines = new();
        public readonly List<IReadOnlyList<KeyValuePair<string, object?>>> Props = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Check.That(logLevel).IsEqualTo(LogLevel.Information);
            Lines.Add(formatter(state, exception));
            Props.Add(state is IReadOnlyList<KeyValuePair<string, object?>> p ? p : []);
        }
    }

    private sealed class Clock
    {
        public long Ms;
        public long Now() => Ms;
    }

    private static (KeyboardE1Recorder Recorder, Capture Log, Clock Clock) New()
    {
        var log = new Capture();
        var clock = new Clock();
        return (new KeyboardE1Recorder(log, clock.Now), log, clock);
    }

    private static KeyE1 Key(string cls, string action = "-") => new(
        TimestampUs: 1_000, Row: 1, Col: 2, Class: cls, Action: action, BoxWidthPx: 210, BoxHeightPx: 210,
        SubLayout: "letters", Edge: "top", WindowX: 500, WindowY: 0, WindowWidth: 2800, WindowHeight: 1000,
        SinceWindowMoveMs: 7000, DxPx: -3, DyPx: 4, MeanDxPx: -2, MeanDyPx: 5, Restarts: 1, Saccade: "mid",
        EffectiveDwellMs: 450, PenaltyApplied: false, Typing: true, BackspaceClass: "-",
        Likely: true, WordStart: false, NoContinuation: false);

    [Fact]
    public void KeyLine_StartsWithThePrefixAndCarriesEveryField()
    {
        var (recorder, log, _) = New();

        var seq = recorder.RecordKey(Key(KeyE1.Letter));

        Check.That(seq).IsEqualTo(1L);
        Check.That(log.Lines).HasSize(1);
        // A letter is a TEXT key: no row, column or action id (they would spell the text).
        Check.That(log.Lines[0]).IsEqualTo(
            "KB-E1 key seq=1 ts=1000 cls=letter boxw=210 boxh=210 sub=letters edge=top " +
            "winx=500 winy=0 winw=2800 winh=1000 movems=7000 dx=-3 dy=4 mdx=-2 mdy=5 restarts=1 saccade=mid " +
            "sincekey=-1 dwell=450 penalty=False typing=True bs=- likely=True start=False nocont=False");
    }

    [Theory]
    [InlineData(KeyE1.Letter)]
    [InlineData(KeyE1.Digit)]
    [InlineData(KeyE1.Symbol)]
    [InlineData(KeyE1.Emoji)]
    public void ATextKeyLine_CarriesNoRowColumnOrActionId_WhateverTheCallerPasses(string cls)
    {
        var (recorder, log, _) = New();

        // The caller hands over a position and an id; the recorder must drop both on its own.
        recorder.RecordKey(Key(cls, "shortcut:Ctrl+C") with { Row = 3, Col = 7 });

        Check.That(log.Lines[0]).Not.Contains(" r=");
        Check.That(log.Lines[0]).Not.Contains(" c=");
        Check.That(log.Lines[0]).Not.Contains(" act=");
        Check.That(log.Lines[0]).Not.Contains("shortcut");
        Check.That(log.Props[0].Select(p => p.Key)).Not.Contains("Row");
        Check.That(log.Props[0].Select(p => p.Key)).Not.Contains("Col");
        Check.That(log.Props[0].Select(p => p.Key)).Not.Contains("Act");
    }

    [Theory]
    [InlineData(KeyE1.Space, "space")]
    [InlineData(KeyE1.Backspace, "backspace")]
    [InlineData(KeyE1.Enter, "enter")]
    [InlineData(KeyE1.Shift, "modifier:shift")]
    [InlineData(KeyE1.SwitchSub, "switchSub:numbers")]
    [InlineData(KeyE1.Other, "shortcut:Ctrl+C")]
    public void ANonTextKeyLine_KeepsItsPositionAndId_BecauseTheyDoNotSpellText(string cls, string action)
    {
        var (recorder, log, _) = New();

        recorder.RecordKey(Key(cls, action));

        Check.That(log.Lines[0]).Contains($" r=1 c=2 cls={cls} act={action} ");
    }

    [Theory]
    [InlineData(KeyE1.Letter, true)]
    [InlineData(KeyE1.Digit, true)]
    [InlineData(KeyE1.Symbol, true)]
    [InlineData(KeyE1.Emoji, true)]
    [InlineData(KeyE1.Space, false)]
    [InlineData(KeyE1.Backspace, false)]
    [InlineData(KeyE1.DeleteWord, false)]
    [InlineData(KeyE1.Enter, false)]
    [InlineData(KeyE1.Shift, false)]
    [InlineData(KeyE1.Caps, false)]
    [InlineData(KeyE1.Mode, false)]
    [InlineData(KeyE1.SwitchSub, false)]
    [InlineData(KeyE1.Other, false)]
    public void OnlyTextKeyClasses_AreIdentityFree(string cls, bool expected)
    {
        Check.That(KeyboardE1Recorder.IsIdentityFree(cls)).IsEqualTo(expected);
    }

    [Fact]
    public void ASlipLine_DescribesTheRelationOnly()
    {
        var (recorder, log, _) = New();

        recorder.RecordSlip(adjacent: true, direction: "W", distancePitches: 1, msToBackspace: 800, rowBand: "top");
        recorder.RecordSlip(adjacent: false, direction: "-", distancePitches: 3, msToBackspace: 1200, rowBand: "bot");

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 slip adj=True dir=W dist=1 ms=800 reg=top",
            "KB-E1 slip adj=False dir=- dist=3 ms=1200 reg=bot");
        Check.That(log.Props[0].Select(p => p.Key).Where(k => k != "{OriginalFormat}")
                .OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly("Adj", "Dir", "Dist", "Ms", "Reg");
    }

    [Fact]
    public void SinceKey_IsTheIntervalToThePreviousActivation_AndMinusOneBeforeTheFirst()
    {
        var (recorder, log, clock) = New();

        recorder.RecordKey(Key(KeyE1.Letter));
        clock.Ms = 850;
        recorder.RecordKey(Key(KeyE1.Letter));

        Check.That(log.Lines[0]).Contains("sincekey=-1 ");
        Check.That(log.Lines[1]).Contains("sincekey=850 ");
    }

    [Fact]
    public void ADisabledRecorder_LogsNothing_ButStillHandsOutSequenceNumbers()
    {
        var recorder = KeyboardE1Recorder.Disabled;

        Check.That(recorder.Enabled).IsFalse();
        Check.That(recorder.RecordKey(Key(KeyE1.Letter))).IsEqualTo(1L);
        recorder.RecordPick(0, "dict", 1, 5, 100, 0);
        recorder.RecordShown("WINWORD.exe", "top", "ring");
    }

    [Fact]
    public void Pick_CarriesLengthsAndTheReadingFingerprint_NeverTheWord()
    {
        var (recorder, log, clock) = New();
        recorder.RecordKey(Key(KeyE1.Letter));
        clock.Ms = 1200;

        recorder.RecordPick(slot: 2, "pair", prefixLength: 3, insertedLength: 9, renderToDwellMs: 640, slotsVisited: 1);

        Check.That(log.Lines[1]).IsEqualTo(
            "KB-E1 pick slot=2 src=pair prefixlen=3 inslen=9 sincekey=1200 sincepick=-1 renderms=640 visited=1");
    }

    [Fact]
    public void ASecondPick_ReportsTheTimeSinceThePreviousPick()
    {
        var (recorder, log, clock) = New();
        clock.Ms = 100;
        recorder.RecordPick(0, "dict", 0, 4, 200, 0);
        clock.Ms = 700;

        recorder.RecordPick(0, "dict", 0, 5, 20, 0);

        Check.That(log.Lines[1]).Contains("sincepick=600 ");
    }

    [Theory]
    [InlineData(KeyE1.Backspace)]
    [InlineData(KeyE1.DeleteWord)]
    public void ABackspaceWithin3sOfAPick_IsAPickUndo_Once(string cls)
    {
        var (recorder, log, clock) = New();
        recorder.RecordPick(0, "dict", 0, 4, 200, 0);
        clock.Ms = 2999;

        recorder.RecordKey(Key(cls, cls));
        clock.Ms = 3100;
        recorder.RecordKey(Key(cls, cls));

        var undos = log.Lines.Where(l => l.StartsWith("KB-E1 pickundo", StringComparison.Ordinal)).ToList();
        Check.That(undos).HasSize(1);
        Check.That(undos[0]).IsEqualTo($"KB-E1 pickundo ms=2999 by={cls}");
    }

    [Fact]
    public void ABackspaceAfter3s_IsNotAPickUndo()
    {
        var (recorder, log, clock) = New();
        recorder.RecordPick(0, "dict", 0, 4, 200, 0);
        clock.Ms = 3001;

        recorder.RecordKey(Key(KeyE1.Backspace, "backspace"));

        Check.That(log.Lines.Any(l => l.StartsWith("KB-E1 pickundo", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void ALetterBetweenThePickAndTheBackspace_DoesNotHideTheUndo()
    {
        // The undo rule is "a Backspace within 3 s", as the steward worded it: typing in between
        // does not make a Backspace any less likely to be the user taking the pick back.
        var (recorder, log, clock) = New();
        recorder.RecordPick(0, "dict", 0, 4, 200, 0);
        clock.Ms = 500;
        recorder.RecordKey(Key(KeyE1.Letter));
        clock.Ms = 1000;

        recorder.RecordKey(Key(KeyE1.Backspace, "backspace"));

        Check.That(log.Lines.Count(l => l.StartsWith("KB-E1 pickundo", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Fact]
    public void AModeChangeReversedWithin5s_IsAModeUndo()
    {
        var (recorder, log, clock) = New();
        recorder.RecordMode("sublayout", "letters", "numbers");
        clock.Ms = 4999;

        recorder.RecordMode("sublayout", "numbers", "letters");

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 mode kind=sublayout from=letters to=numbers",
            "KB-E1 mode kind=sublayout from=numbers to=letters",
            "KB-E1 modeundo kind=sublayout ms=4999");
    }

    [Fact]
    public void AReversalAfter5s_OrOfAnotherKind_OrInTheSameDirection_IsNotAnUndo()
    {
        var (recorder, log, clock) = New();
        recorder.RecordMode("sublayout", "letters", "numbers");
        clock.Ms = 5001;
        recorder.RecordMode("sublayout", "numbers", "letters");      // too late
        clock.Ms = 5100;
        recorder.RecordMode("emoji", "emoji", "text");               // another kind
        recorder.RecordMode("sublayout", "letters", "numbers");      // same direction again

        Check.That(log.Lines.Any(l => l.StartsWith("KB-E1 modeundo", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void TypingInTheNewMode_MeansALaterReversalIsNotAnUndo()
    {
        var (recorder, log, clock) = New();
        recorder.RecordMode("sublayout", "letters", "numbers");
        clock.Ms = 400;
        recorder.RecordKey(Key(KeyE1.Digit));
        clock.Ms = 900;

        recorder.RecordMode("sublayout", "numbers", "letters");

        Check.That(log.Lines.Any(l => l.StartsWith("KB-E1 modeundo", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void AnUndoIsNotItselfUndoable()
    {
        var (recorder, log, _) = New();
        recorder.RecordMode("emoji", "text", "emoji");
        recorder.RecordMode("emoji", "emoji", "text");   // undo
        recorder.RecordMode("emoji", "text", "emoji");   // re-enter: must not read as undoing the undo

        Check.That(log.Lines.Count(l => l.StartsWith("KB-E1 modeundo", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Theory]
    [InlineData("shift")]
    [InlineData("caps")]
    public void AModifierToggledOnAndOffWithin5s_IsAModeUndo(string kind)
    {
        var (recorder, log, _) = New();

        recorder.RecordMode(kind, "off", "on");
        recorder.RecordMode(kind, "on", "off");

        Check.That(log.Lines.Count(l => l.StartsWith("KB-E1 modeundo", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Fact]
    public void ARepeatedReversal_AfterAnUndo_IsNotASecondUndo()
    {
        // Defensive: the recorded state can be reset without a mode line (a layout reload sets the
        // sub-layout silently), so a reversal arriving AGAIN after an undo must not pair with the
        // original change a second time.
        var (recorder, log, _) = New();
        recorder.RecordMode("sublayout", "letters", "numbers");
        recorder.RecordMode("sublayout", "numbers", "letters");     // the undo
        recorder.RecordMode("sublayout", "numbers", "letters");     // arrives again

        Check.That(log.Lines.Count(l => l.StartsWith("KB-E1 modeundo", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Fact]
    public void AFlipFlippedBackWithin5s_IsAModeUndo()
    {
        var (recorder, log, clock) = New();
        recorder.RecordEdge("flip", edgeAfter: "bottom", edgeBefore: "top");
        clock.Ms = 3000;

        recorder.RecordEdge("flip", edgeAfter: "top", edgeBefore: "bottom");

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 edge button=flip edge=bottom",
            "KB-E1 edge button=flip edge=top",
            "KB-E1 modeundo kind=flip ms=3000");
    }

    [Fact]
    public void Close_IsLoggedButIsNotAMode()
    {
        var (recorder, log, _) = New();

        recorder.RecordEdge("close", "top", "top");

        Check.That(log.Lines).ContainsExactly("KB-E1 edge button=close edge=top");
    }

    [Fact]
    public void ShownAndHidden_CarryTheProcessNameAndTheCause()
    {
        var (recorder, log, clock) = New();
        recorder.RecordShown("WINWORD.exe", "top", "toolbar-toggle");
        clock.Ms = 12_500;

        recorder.RecordHidden("edge-close");

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 shown proc=WINWORD.exe edge=top via=toolbar-toggle",
            "KB-E1 hidden cause=edge-close visiblems=12500");
    }

    [Fact]
    public void Hiding_ForgetsAPendingPickUndoAndModeUndo()
    {
        var (recorder, log, clock) = New();
        recorder.RecordMode("emoji", "text", "emoji");
        recorder.RecordPick(0, "dict", 0, 4, 200, 0);
        recorder.RecordHidden("edge-close");
        clock.Ms = 500;

        recorder.RecordMode("emoji", "emoji", "text");
        recorder.RecordKey(Key(KeyE1.Backspace, "backspace"));

        Check.That(log.Lines.Any(l => l.Contains("modeundo") || l.Contains("pickundo"))).IsFalse();
    }

    [Fact]
    public void SpaceHoldOnAbandonAndMirror_HaveTheirDocumentedShapes()
    {
        var (recorder, log, _) = New();

        recorder.RecordSpace(7, barEmpty: true);
        recorder.RecordHoldOn(7, 600, capped: true);
        recorder.RecordAbandon(new AbandonE1(5_000, 0, 3, KeyE1.Letter, "-", 65, "NearestChanged", 12, -9, "letters", "top"));
        recorder.RecordAbandon(new AbandonE1(6_000, 3, 0, KeyE1.Shift, "modifier:shift", 40, "GazeLeft", 5, 6, "letters", "top"));
        recorder.RecordMirror("enter", "mismatch", mirrorLength: 14, controlLength: 14, commonSuffix: 6);

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 space seq=7 empty=True",
            "KB-E1 holdon seq=7 ms=600 capped=True",
            "KB-E1 abandon ts=5000 cls=letter pct=65 why=NearestChanged mdx=12 mdy=-9 sub=letters edge=top",
            "KB-E1 abandon ts=6000 r=3 c=0 cls=shift act=modifier:shift pct=40 why=GazeLeft mdx=5 mdy=6 sub=letters edge=top",
            "KB-E1 mirror at=enter res=mismatch mirrorlen=14 ctllen=14 common=6");
    }

    [Theory]
    [InlineData(PredictionType.ExactPrefix, "dict")]
    [InlineData(PredictionType.FuzzyMatch, "fuzzy")]
    [InlineData(PredictionType.NextWord, "pair")]
    [InlineData(PredictionType.Phrase, "phrase")]
    [InlineData(PredictionType.SentenceCompletion, "sentence")]
    [InlineData(PredictionType.LMSuggestion, "lm")]
    public void PickSource_IsAClosedIdPerPredictionType(PredictionType type, string expected)
    {
        Check.That(KeyboardE1Recorder.PickSource(type)).IsEqualTo(expected);
    }

    [Fact]
    public void EveryPredictionTypeHasADistinctSource()
    {
        var sources = Enum.GetValues<PredictionType>().Select(KeyboardE1Recorder.PickSource).ToList();

        Check.That(sources.Distinct().Count()).IsEqualTo(sources.Count);
        Check.That(sources).Not.Contains("other");
    }
}
