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
        // A minimum of ONE dwell: these tests pin the line formats; the 20-dwell minimum has its own tests.
        return (new KeyboardE1Recorder(log, clock.Now, edgeTallyMinFires: 1), log, clock);
    }

    private static KeyE1 Key(string cls, string action = "-") => new(
        TimestampUs: 1_000, Row: 1, Col: 2, Class: cls, Action: action, BoxWidthPx: 210, BoxHeightPx: 210,
        SubLayout: "letters", Edge: "top", WindowX: 500, WindowY: 0, WindowWidth: 2800, WindowHeight: 1000,
        SinceWindowMoveMs: 7000, DxPx: -3, DyPx: 4, MeanDxPx: -2, MeanDyPx: 5, EdgeQuarter: "NE", Restarts: 1,
        Saccade: "mid", EffectiveDwellMs: 450, PenaltyApplied: false, Typing: true, BackspaceClass: "-");

    private static CorrE1 Corr(bool likely = true) => new("mid", 1, likely, false, false);

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("sink down");
    }

    [Fact]
    public void KeyLine_StartsWithThePrefixAndCarriesEveryField()
    {
        var (recorder, log, _) = New();

        var seq = recorder.RecordKey(Key(KeyE1.Letter));

        Check.That(seq).IsEqualTo(1L);
        Check.That(log.Lines).HasSize(1);
        // A letter is a TEXT key: no row/column/id, no box size, no pixel offset, no saccade/restarts,
        // no likelihood and no edge class (the quarter is only COUNTED, in the per-edge tally line).
        // It says a text key was typed, plus timing and the dock edge.
        Check.That(log.Lines[0]).IsEqualTo(
            "KB-E1 key seq=1 ts=1000 cls=letter sub=letters edge=top winx=500 winy=0 winw=2800 winh=1000 " +
            "movems=7000 sincekey=-1 dwell=450 penalty=False typing=True bs=-");
    }

    [Theory]
    [InlineData(KeyE1.Letter)]
    [InlineData(KeyE1.Digit)]
    [InlineData(KeyE1.Symbol)]
    [InlineData(KeyE1.Emoji)]
    public void ATextKeyLine_CarriesNoBoxSizePixelOffsetSaccadeRestartsOrLikelihood(string cls)
    {
        var (recorder, log, _) = New();

        recorder.RecordKey(Key(cls));

        foreach (var banned in new[] { " boxw=", " boxh=", " dx=", " dy=", " mdx=", " mdy=", " saccade=",
                     " restarts=", " likely=", " start=", " nocont=", " edgeq=" })
            Check.That(log.Lines[0]).Not.Contains(banned);
        Check.That(log.Props[0].Select(p => p.Key).Where(k => k != "{OriginalFormat}").OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly("Bs", "Class", "DwellMs", "Edge", "Penalty", "Seq", "SinceKeyMs",
                "SinceMoveMs", "Sub", "TsUs", "Typing", "WinH", "WinW", "WinX", "WinY");
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
        // A non-text key keeps its box and pixel offsets and saccade/restarts, but never the likelihood bits.
        Check.That(log.Lines[0]).Contains(" boxw=210 boxh=210 ");
        Check.That(log.Lines[0]).Contains(" dx=-3 dy=4 mdx=-2 mdy=5 restarts=1 saccade=mid ");
        Check.That(log.Lines[0]).Not.Contains(" likely=");
        Check.That(log.Lines[0]).Not.Contains(" edgeq=");
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

        recorder.RecordSlip(adjacent: true, direction: "W", distancePitches: 1, msToBackspace: 800, rowBand: "top",
            Corr());
        recorder.RecordSlip(adjacent: false, direction: "-", distancePitches: 3, msToBackspace: 1200, rowBand: "bot",
            new CorrE1("long", 0, false, true, true));

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 slip adj=True dir=W dist=1 ms=800 reg=top saccade=mid restarts=1 likely=True start=False nocont=False",
            "KB-E1 slip adj=False dir=- dist=3 ms=1200 reg=bot saccade=long restarts=0 likely=False start=True nocont=True");
        Check.That(log.Props[0].Select(p => p.Key).Where(k => k != "{OriginalFormat}")
                .OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly("Adj", "Dir", "Dist", "Likely", "Ms", "NoCont", "Reg", "Restarts", "Saccade", "Start");
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
        recorder.RecordShown("WINWORD.exe", "top", "toolbar-toggle", 158, 157);
        clock.Ms = 12_500;

        recorder.RecordHidden("edge-close");

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 shown proc=WINWORD.exe edge=top via=toolbar-toggle boxw=158 boxh=157",
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
        recorder.RecordAbandon(new AbandonE1(5_000, 0, 3, KeyE1.Letter, "-", 65, "NearestChanged", 12, -9, "NE", "letters", "top"));
        recorder.RecordAbandon(new AbandonE1(6_000, 3, 0, KeyE1.Shift, "modifier:shift", 40, "GazeLeft", 5, 6, "-", "letters", "top"));
        recorder.RecordMirror("enter", "mismatch", mirrorLength: 14, controlLength: 14, commonSuffix: 6);

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 space seq=7 empty=True",
            "KB-E1 holdon seq=7 ms=600 capped=True",
            "KB-E1 abandon ts=5000 cls=letter pct=65 why=NearestChanged sub=letters edge=top",
            "KB-E1 abandon ts=6000 r=3 c=0 cls=shift act=modifier:shift pct=40 why=GazeLeft mdx=5 mdy=6 sub=letters edge=top",
            "KB-E1 mirror at=enter res=mismatch mirrorlen=14 ctllen=14 common=6");
    }

    // ---- the per-dock-edge tally (replaces the per-letter edge class) ------------------------------

    private static AbandonE1 Abandon(string cls, string quarter, string edge = "top") =>
        new(5_000, 0, 3, cls, "-", 65, "NearestChanged", 12, -9, quarter, "letters", edge);

    private const string TopTally =
        "KB-E1 edgeq edge=top fires=3 c=1 n=0 ne=1 e=1 se=0 s=0 sw=0 w=0 nw=0 " +
        "aborts=1 ac=0 an=0 ane=0 ae=1 ase=0 as=0 asw=0 aw=0 anw=0";

    [Fact]
    public void TheTally_CountsTextDwellsAndAbandonmentsPerQuarter_AndIsWrittenRightBeforeTheHiddenLine()
    {
        var (recorder, log, _) = New();
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "NE" });
        recorder.RecordKey(Key(KeyE1.Digit) with { EdgeQuarter = "-" });
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "E" });
        recorder.RecordAbandon(Abandon(KeyE1.Letter, "E"));
        log.Lines.Clear(); log.Props.Clear();

        recorder.RecordHidden("toolbar-toggle");

        Check.That(log.Lines).ContainsExactly(TopTally, "KB-E1 hidden cause=toolbar-toggle visiblems=-1");
    }

    [Fact]
    public void TheTally_StartsFresh_AfterAHide_SoNothingIsCountedTwice()
    {
        var (recorder, log, _) = New();
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "E" });
        recorder.RecordHidden("a");
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "W" });
        log.Lines.Clear(); log.Props.Clear();

        recorder.RecordHidden("b");

        Check.That(log.Lines[0]).Contains(" fires=1 ").And.Contains(" w=1 ").And.Contains(" e=0 ");
        recorder.RecordHidden("c");                                       // empty now: no tally line
        Check.That(log.Lines.Count(l => l.StartsWith("KB-E1 edgeq ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Fact]
    public void AFlip_WritesTheTallyOfTheEdgeBeingLeft_BeforeTheEdgeLine_AndStartsANewOne()
    {
        var (recorder, log, _) = New();
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "E" });
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "E" });
        log.Lines.Clear(); log.Props.Clear();

        recorder.RecordEdge("flip", "bottom", "top");
        recorder.RecordKey(Key(KeyE1.Letter) with { Edge = "bottom", EdgeQuarter = "S" });
        recorder.RecordHidden("x");

        Check.That(log.Lines.Where(l => !l.StartsWith("KB-E1 key ", StringComparison.Ordinal))).ContainsExactly(
            "KB-E1 edgeq edge=top fires=2 c=0 n=0 ne=0 e=2 se=0 s=0 sw=0 w=0 nw=0 " +
            "aborts=0 ac=0 an=0 ane=0 ae=0 ase=0 as=0 asw=0 aw=0 anw=0",
            "KB-E1 edge button=flip edge=bottom",
            "KB-E1 edgeq edge=bottom fires=1 c=0 n=0 ne=0 e=0 se=0 s=1 sw=0 w=0 nw=0 " +
            "aborts=0 ac=0 an=0 ane=0 ae=0 ase=0 as=0 asw=0 aw=0 anw=0",
            "KB-E1 hidden cause=x visiblems=-1");
    }

    [Fact]
    public void ADwellOnAnotherDockEdge_FlushesTheOldEdgesTallyFirst()
    {
        var (recorder, log, _) = New();
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "E" });
        log.Lines.Clear(); log.Props.Clear();

        recorder.RecordKey(Key(KeyE1.Letter) with { Edge = "bottom", EdgeQuarter = "W" });

        Check.That(log.Lines[0]).StartsWith("KB-E1 edgeq edge=top fires=1 ");
        Check.That(log.Lines[1]).StartsWith("KB-E1 key ");
    }

    [Fact]
    public void AnEmptyTally_WritesNothing_AtAHideOrAFlip()
    {
        var (recorder, log, _) = New();
        recorder.RecordEdge("flip", "bottom", "top");
        recorder.RecordHidden("x");

        Check.That(log.Lines.Any(l => l.StartsWith("KB-E1 edgeq ", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void OnlyTextKeysAreTallied_NotSpaceBackspaceOrModifiers()
    {
        var (recorder, log, _) = New();
        recorder.RecordKey(Key(KeyE1.Space, "space") with { EdgeQuarter = "E" });
        recorder.RecordKey(Key(KeyE1.Backspace, "backspace") with { EdgeQuarter = "E" });
        recorder.RecordKey(Key(KeyE1.Shift, "modifier:shift") with { EdgeQuarter = "E" });
        recorder.RecordAbandon(Abandon(KeyE1.Shift, "E"));
        recorder.RecordHidden("x");

        Check.That(log.Lines.Any(l => l.StartsWith("KB-E1 edgeq ", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void KeysInAPasswordField_AreNeverTallied()
    {
        var sensitive = false;
        var log = new Capture();
        var recorder = new KeyboardE1Recorder(log, () => 0, edgeTallyMinFires: 1) { IsSensitive = () => sensitive };
        sensitive = true;
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "NE" });
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "NE" });
        recorder.RecordAbandon(Abandon(KeyE1.Letter, "NE"));
        sensitive = false;

        recorder.RecordHidden("x");
        Check.WithCustomMessage("a tally that saw only muted keys writes nothing")
            .That(log.Lines.Any(l => l.StartsWith("KB-E1 edgeq ", StringComparison.Ordinal))).IsFalse();

        sensitive = true;
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "NE" });
        sensitive = false;
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "E" });
        recorder.RecordHidden("y");
        var tally = log.Lines.Single(l => l.StartsWith("KB-E1 edgeq ", StringComparison.Ordinal));
        Check.That(tally).Contains(" fires=1 ").And.Contains(" e=1 ").And.Contains(" ne=0 ");
    }

    [Fact]
    public void ATallyLine_CarriesCountsOnly()
    {
        var (recorder, log, _) = New();
        recorder.RecordKey(Key(KeyE1.Letter) with { EdgeQuarter = "NE", Row = 3, Col = 7 });
        log.Lines.Clear(); log.Props.Clear();
        recorder.RecordHidden("x");

        var names = log.Props[0].Select(p => p.Key).Where(k => k != "{OriginalFormat}").ToArray();
        Check.That(names).ContainsExactly("Edge", "Fires", "C", "N", "NE", "E", "SE", "S", "SW", "W", "NW",
            "Aborts", "AC", "AN", "ANE", "AE", "ASE", "AS", "ASW", "AW", "ANW");
        foreach (var banned in new[] { " r=", " c=3", " act=", " ts=", " seq=" })
            Check.That(log.Lines[0]).Not.Contains(banned);
    }

    [Fact]
    public void ACorrectionLine_IsKeyedToTheBackspace_AndCarriesTheSlippedKeysArrivalAndLikelihood()
    {
        var (recorder, log, _) = New();

        recorder.RecordCorrection(9, new CorrE1("short", 2, false, false, true));

        Check.That(log.Lines).ContainsExactly(
            "KB-E1 corr seq=9 saccade=short restarts=2 likely=False start=False nocont=True");
    }

    [Fact]
    public void ALayoutLine_CarriesTheLetterBoxOnce()
    {
        var (recorder, log, _) = New();

        recorder.RecordLayout("letters", 157, 158);

        Check.That(log.Lines).ContainsExactly("KB-E1 layout sub=letters boxw=157 boxh=158");
    }

    // ---- F1: nothing per key while a password field has focus ----------------------------------

    private sealed class Sensitivity
    {
        public bool On;
        public bool Probe() => On;
    }

    private static (KeyboardE1Recorder Recorder, Capture Log, Clock Clock, Sensitivity Sens) NewSensitive()
    {
        var log = new Capture();
        var clock = new Clock();
        var sens = new Sensitivity();
        return (new KeyboardE1Recorder(log, clock.Now, sens.Probe), log, clock, sens);
    }

    [Fact]
    public void InASensitiveContext_NoPerKeyLineIsWritten_OnlyOneMarkerWithNoCount()
    {
        var (recorder, log, _, sens) = NewSensitive();
        sens.On = true;

        var seq1 = recorder.RecordKey(Key(KeyE1.Letter));
        recorder.RecordKey(Key(KeyE1.Digit));
        recorder.RecordKey(Key(KeyE1.Backspace, "backspace"));
        recorder.RecordHoldOn(1, 300, capped: false);
        recorder.RecordAbandon(new AbandonE1(1, 0, 0, KeyE1.Letter, "-", 60, "GazeLeft", 0, 0, "-", "letters", "top"));
        recorder.RecordCorrection(2, Corr());
        recorder.RecordSlip(true, "E", 1, 500, "top", Corr());
        recorder.RecordPick(0, "dict", 1, 5, 100, 0);
        recorder.RecordSpace(3, true);
        recorder.RecordMode("shift", "off", "on");

        Check.That(seq1).IsEqualTo(0L);
        Check.That(log.Lines).ContainsExactly("KB-E1 sensitive");
        Check.That(log.Props[0].Select(p => p.Key).Where(k => k != "{OriginalFormat}")).IsEmpty();
    }

    [Fact]
    public void TheMarker_IsWrittenOncePerEntry_AndAgainOnTheNextEntry()
    {
        var (recorder, log, _, sens) = NewSensitive();

        sens.On = true;
        recorder.RecordKey(Key(KeyE1.Letter));
        recorder.RecordKey(Key(KeyE1.Letter));
        sens.On = false;
        recorder.RecordKey(Key(KeyE1.Letter));          // a normal line again
        sens.On = true;
        recorder.RecordKey(Key(KeyE1.Letter));

        Check.That(log.Lines.Count(l => l == "KB-E1 sensitive")).IsEqualTo(2);
        Check.That(log.Lines.Count(l => l.StartsWith("KB-E1 key ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Fact]
    public void EnteringASensitiveContext_DropsAPendingPickUndoAndModeUndo()
    {
        var (recorder, log, clock, sens) = NewSensitive();
        recorder.RecordMode("emoji", "text", "emoji");
        recorder.RecordPick(0, "dict", 0, 4, 200, 0);

        sens.On = true;
        recorder.RecordKey(Key(KeyE1.Letter));          // enters: state dropped
        sens.On = false;
        clock.Ms = 500;
        recorder.RecordMode("emoji", "emoji", "text");
        recorder.RecordKey(Key(KeyE1.Backspace, "backspace"));

        Check.That(log.Lines.Any(l => l.Contains("modeundo") || l.Contains("pickundo"))).IsFalse();
    }

    [Fact]
    public void ShownHiddenAndEdge_StillLog_InASensitiveContext()
    {
        var (recorder, log, _, sens) = NewSensitive();
        sens.On = true;

        recorder.RecordShown("chrome.exe", "top", "request", 1, 2);
        recorder.RecordEdge("close", "top", "top");
        recorder.RecordHidden("edge-close");

        Check.That(log.Lines.Count).IsEqualTo(3);
    }

    [Fact]
    public void AThrowingSensitivityProbe_CountsAsNotSensitive()
    {
        var log = new Capture();
        var recorder = new KeyboardE1Recorder(log, null, () => throw new InvalidOperationException("uia"));

        recorder.RecordKey(Key(KeyE1.Letter));

        Check.That(log.Lines).HasSize(1);
    }

    // ---- F10: logging never reaches the keyboard -----------------------------------------------

    [Fact]
    public void ASensitiveContext_TaintsTheRecorder_UntilTheTaintIsCleared()
    {
        var sensitive = false;
        var recorder = new KeyboardE1Recorder(new Capture(), () => 0) { IsSensitive = () => sensitive };
        Check.That(recorder.Tainted).IsFalse();
        Check.That(recorder.Muted()).IsFalse();
        Check.That(recorder.Tainted).IsFalse();

        sensitive = true;
        Check.That(recorder.Muted()).IsTrue();
        sensitive = false;
        Check.That(recorder.Muted()).IsFalse();
        Check.That(recorder.Tainted).IsTrue();                   // focus moved on; the record is still suspect

        recorder.ClearTaint();
        Check.That(recorder.Tainted).IsFalse();
    }

    [Fact]
    public void AFailingLogger_NeverThrowsOutOfTheRecorder()
    {
        var recorder = new KeyboardE1Recorder(new ThrowingLogger());

        recorder.RecordKey(Key(KeyE1.Letter));
        recorder.RecordKey(Key(KeyE1.Backspace, "backspace"));
        recorder.RecordCorrection(1, Corr());
        recorder.RecordSlip(true, "E", 1, 1, "top", Corr());
        recorder.RecordHoldOn(1, 1, false);
        recorder.RecordAbandon(new AbandonE1(1, 0, 0, KeyE1.Letter, "-", 60, "GazeLeft", 0, 0, "-", "letters", "top"));
        recorder.RecordPick(0, "dict", 1, 1, 1, 0);
        recorder.RecordSpace(1, true);
        recorder.RecordMode("emoji", "text", "emoji");
        recorder.RecordEdge("flip", "top", "bottom");
        recorder.RecordShown("p.exe", "top", "ring", 1, 1);
        recorder.RecordLayout("letters", 1, 1);
        recorder.RecordHidden("edge-close");
        recorder.RecordMirror("hide", "match", 1, 1, 1);
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

    // ---- the 20-dwell minimum, the per-edge carry-over and the shutdown flush ----------------------

    /// <summary>A recorder with the REAL minimum (<see cref="KeyboardE1Recorder.EdgeTallyMinFires"/>).</summary>
    private static (KeyboardE1Recorder Recorder, Capture Log) NewReal()
    {
        var log = new Capture();
        return (new KeyboardE1Recorder(log, new Clock().Now), log);
    }

    private static void Fire(KeyboardE1Recorder recorder, int count, string quarter, string edge = "top")
    {
        for (var i = 0; i < count; i++)
            recorder.RecordKey(Key(KeyE1.Letter) with { Edge = edge, EdgeQuarter = quarter });
    }

    private static string[] EdgeqLines(Capture log) =>
        log.Lines.Where(l => l.StartsWith("KB-E1 edgeq ", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void TheMinimumIsTwentyAcceptedTextDwells()
    {
        Check.That(KeyboardE1Recorder.EdgeTallyMinFires).IsEqualTo(20);
    }

    [Fact]
    public void NineteenDwellsAndAHide_WriteNoTally_TheTwentiethWritesOneLineWithTheSummedQuarters()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 10, "E");
        Fire(recorder, 9, "W");

        recorder.RecordHidden("a");
        Check.WithCustomMessage("19 dwells are below the minimum").That(EdgeqLines(log)).IsEmpty();

        Fire(recorder, 1, "NE");
        recorder.RecordHidden("b");

        var lines = EdgeqLines(log);
        Check.That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith("KB-E1 edgeq edge=top fires=20 c=0 n=0 ne=1 e=10 se=0 s=0 sw=0 w=9 nw=0 ");
    }

    [Fact]
    public void AWrittenTally_StartsFresh_SoTheNextShowCountsFromZero()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 20, "E");
        recorder.RecordHidden("a");
        Check.That(EdgeqLines(log)).HasSize(1);

        Fire(recorder, 19, "E");
        recorder.RecordHidden("b");
        Check.WithCustomMessage("the first 20 were written and cleared; 19 more are below the minimum")
            .That(EdgeqLines(log)).HasSize(1);
    }

    [Fact]
    public void TheAbandonCountersRideAlongWithTheirEdge_AndDoNotCountTowardTheMinimum()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 10, "E");
        for (var i = 0; i < 15; i++) recorder.RecordAbandon(Abandon(KeyE1.Letter, "W"));
        recorder.RecordHidden("a");
        Check.WithCustomMessage("10 dwells + 15 abandonments is still below 20 dwells").That(EdgeqLines(log)).IsEmpty();

        Fire(recorder, 10, "E");
        recorder.RecordHidden("b");

        var lines = EdgeqLines(log);
        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains(" fires=20 ").And.Contains(" aborts=15 ").And.Contains(" aw=15 ");
    }

    [Fact]
    public void TheCarryOver_IsPerEdge_NotOneAccumulatorForAllEdges()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 15, "E", "top");
        recorder.RecordEdge("flip", "bottom", "top");
        Fire(recorder, 15, "S", "bottom");
        recorder.RecordEdge("flip", "top", "bottom");
        Fire(recorder, 5, "E", "top");
        recorder.RecordHidden("x");

        var lines = EdgeqLines(log);
        Check.WithCustomMessage("one top line (15 + 5), and no bottom line (15 is below the minimum)").That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith("KB-E1 edgeq edge=top fires=20 c=0 n=0 ne=0 e=20 ");
    }

    [Fact]
    public void ADwellOnAnotherEdge_CarriesTheLeftEdgesSmallTally_AndWritesALargeOne()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 7, "E", "top");
        Fire(recorder, 1, "S", "bottom");                       // moving to another edge: 7 is carried
        Check.That(EdgeqLines(log)).IsEmpty();

        Fire(recorder, 30, "W", "bottom");
        Fire(recorder, 1, "E", "top");                          // moving back: bottom has 31 and is written
        var lines = EdgeqLines(log);
        Check.That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith("KB-E1 edgeq edge=bottom fires=31 ");
    }

    [Fact]
    public void AtShutdown_OnlyTheEdgesAtOrAboveTheMinimumAreWritten_TheRestIsDropped()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 7, "S", "bottom");                       // bottom first, so top is the CURRENT edge at shutdown
        Fire(recorder, 25, "E", "top");
        Check.That(EdgeqLines(log)).IsEmpty();

        recorder.FlushEdgeTalliesOnShutdown();

        var lines = EdgeqLines(log);
        Check.WithCustomMessage("top has 25, bottom 7: one line only").That(lines).HasSize(1);
        Check.That(lines[0]).StartsWith("KB-E1 edgeq edge=top fires=25 ");

        // The leftover was DROPPED, not kept: nothing more is ever written for it.
        Fire(recorder, 13, "S", "bottom");                       // 7 dropped + 13 = 13, still below 20
        recorder.RecordHidden("later");
        recorder.FlushEdgeTalliesOnShutdown();
        Check.That(EdgeqLines(log)).HasSize(1);
    }

    [Fact]
    public void TheShutdownFlush_IsIdempotent()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 25, "E", "top");

        recorder.FlushEdgeTalliesOnShutdown();
        recorder.FlushEdgeTalliesOnShutdown();

        Check.That(EdgeqLines(log)).HasSize(1);
    }

    [Fact]
    public void AShutdownWithOnlySmallTallies_WritesNothingAtAll()
    {
        var (recorder, log) = NewReal();
        Fire(recorder, 19, "E", "top");
        Fire(recorder, 3, "W", "bottom");

        recorder.FlushEdgeTalliesOnShutdown();

        Check.That(EdgeqLines(log)).IsEmpty();
    }

    [Fact]
    public void MutedKeys_NeverCountTowardTheMinimum_AndAMutedOnlyTallyNeverWrites()
    {
        var sensitive = false;
        var log = new Capture();
        var recorder = new KeyboardE1Recorder(log, () => 0) { IsSensitive = () => sensitive };

        sensitive = true;
        Fire(recorder, 40, "E");                                  // a long password session
        sensitive = false;
        recorder.RecordHidden("a");
        recorder.FlushEdgeTalliesOnShutdown();
        Check.WithCustomMessage("a muted-only accumulator never writes").That(EdgeqLines(log)).IsEmpty();

        Fire(recorder, 19, "E");
        sensitive = true;
        Fire(recorder, 10, "E");                                  // muted: not added to the 19
        sensitive = false;
        recorder.RecordHidden("b");
        Check.WithCustomMessage("19 + 10 muted is still 19").That(EdgeqLines(log)).IsEmpty();
    }

    [Fact]
    public void WhenLoggingIsOff_NothingIsCountedOrWritten()
    {
        var recorder = new KeyboardE1Recorder(null);
        Fire(recorder, 30, "E");
        recorder.FlushEdgeTalliesOnShutdown();
        recorder.RecordHidden("x");                              // must not throw
    }
}
