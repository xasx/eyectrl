namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;

/// <summary>
/// E1 on the view-model: what a key activation, an abandonment, a hold-on, a mode change and the
/// mirror check log, driven through the REAL <c>ProcessKeyGaze</c> on a small hand-built layout.
/// The recorder's own rules (pick-undo, mode-undo windows) are in Core.Tests; this file pins the
/// view-model's gaze statistics and that logging changes no behaviour.
/// </summary>
public class KeyboardE1LoggingTests
{
    private const int DwellMs = 450;
    private const int CooldownMs = 400;
    private const int StepMs = 30;

    // Row 0: h, a, x, backspace(special), space(special). Centres 220 px apart, boxes 200 px.
    private static readonly string[] Ids = ["key_0_0", "key_0_1", "key_0_2", "backspace", "space"];
    private static readonly Dictionary<string, string> Outputs = new()
    {
        ["key_0_0"] = "h", ["key_0_1"] = "a", ["key_0_2"] = "x",
    };

    private static Dictionary<string, PhysicalPoint> Centres() =>
        Ids.Select((id, i) => (id, p: new PhysicalPoint(100 + 220 * i, 100))).ToDictionary(t => t.id, t => t.p);

    private static Dictionary<string, PhysicalRect> Boxes() =>
        Ids.Select((id, i) => (id, r: new PhysicalRect(220 * i, 0, 200, 200))).ToDictionary(t => t.id, t => t.r);

    private static KeyboardLayout Layout() => new()
    {
        Name = "Test",
        DefaultSubLayout = "letters",
        SubLayouts = new Dictionary<string, SubLayout>
        {
            ["letters"] = new()
            {
                Label = "ABC",
                Rows =
                [
                    [
                        new KeyDefinition { Label = "h", Output = "h" },
                        new KeyDefinition { Label = "a", Output = "a" },
                        new KeyDefinition { Label = "x", Output = "x" },
                        new KeyDefinition { Label = "<", SpecialAction = "backspace" },
                        new KeyDefinition { Label = "_", SpecialAction = "space" },
                    ],
                ],
            },
            ["numbers"] = new() { Label = "123", Rows = [] },
        },
    };

    private sealed class Capture : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }

        public string[] Of(string eventName)
        {
            lock (Lines)
                return Lines.Where(l => l.StartsWith($"KB-E1 {eventName} ", StringComparison.Ordinal)).ToArray();
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly KeyboardViewModel Vm;
        public readonly Capture Log = new();
        public readonly List<string> Fired = new();
        public readonly Dictionary<string, PhysicalPoint> Positions = Centres();
        public readonly Dictionary<string, PhysicalRect> BoxesByKey = Boxes();
        public long Clock;
        public long Ts = 1_000_000;   // µs

        public Rig(bool logging = true, Dictionary<string, int>? words = null)
        {
            Vm = new KeyboardViewModel(new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } },
                new WeakReferenceMessenger());
            if (logging)
                Vm.UseE1Recorder(new KeyboardE1Recorder(Log, () => Clock));
            Vm.Configure(DwellMs, CooldownMs);
            Vm.SetLayout(Layout());
            if (words is not null)
                Vm.SetOrchestrator(new PredictionOrchestrator(
                    new WordPredictor(new InMemoryDictionaryProvider(words), "QWERTY")));
            Vm.KeyDwellCompleted += id =>
            {
                Fired.Add(id);
                if (Outputs.TryGetValue(id, out var output)) Vm.ActivateKey(output, null);
                else if (id == "backspace") Vm.HandleBackspace();
                else if (id == "space") Vm.HandleSpace();
            };
        }

        /// <summary>Samples every 30 ms for <paramref name="ms"/> at <paramref name="gaze"/>.</summary>
        public void Hold(PhysicalPoint gaze, int ms, bool inside = true)
        {
            var until = Ts + ms * 1000L;
            while (Ts <= until)
            {
                Vm.ProcessKeyGaze(Positions, gaze, inside, Ts, BoxesByKey);
                Ts += StepMs * 1000L;
            }
        }

        /// <summary>Holds on a key's centre (plus an offset) long enough to fire it, then rests off-grid.</summary>
        public void Type(string keyId, double dx = 0, double dy = 0)
        {
            var c = Positions[keyId];
            Hold(new PhysicalPoint(c.X + dx, c.Y + dy), DwellMs);
            Hold(new PhysicalPoint(5000, 5000), CooldownMs + 100, inside: false);
        }

        public void Dispose() => Vm.Dispose();
    }

    private static Dictionary<string, string> Fields(string line) =>
        line.Split(' ').Skip(2).Where(t => t.Contains('='))
            .Select(t => t.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    // ---- the key line ------------------------------------------------------------------

    [Fact]
    public void AKeyActivation_LogsGeometryClassAndTheGazeStatistics()
    {
        using var rig = new Rig();
        rig.Hold(new PhysicalPoint(1500, 100), 0, inside: false);         // arrive from afar (outside the grid)
        rig.Ts += 30_000;
        var c = rig.Positions["key_0_1"];

        rig.Hold(new PhysicalPoint(c.X + 10, c.Y - 5), DwellMs);          // rest 10 px right, 5 px above centre

        Check.That(rig.Fired).ContainsExactly("key_0_1");
        var key = rig.Log.Of("key");
        Check.That(key).HasSize(1);
        var f = Fields(key[0]);
        Check.That(f["cls"]).IsEqualTo("letter");
        // A letter is a TEXT key: its position and id would spell the text, so neither is logged.
        Check.That(f.ContainsKey("r")).IsFalse();
        Check.That(f.ContainsKey("c")).IsFalse();
        Check.That(f.ContainsKey("act")).IsFalse();
        Check.That(f["boxw"]).IsEqualTo("200");
        Check.That(f["boxh"]).IsEqualTo("200");
        Check.That(f["sub"]).IsEqualTo("letters");
        Check.That(f["dx"]).IsEqualTo("10");
        Check.That(f["dy"]).IsEqualTo("-5");
        Check.That(f["mdx"]).IsEqualTo("10");
        Check.That(f["mdy"]).IsEqualTo("-5");
        Check.That(f["restarts"]).IsEqualTo("0");
        Check.That(f["dwell"]).IsEqualTo("450");
        Check.That(f["penalty"]).IsEqualTo("False");
        Check.That(f["typing"]).IsEqualTo("False");
        Check.That(f["bs"]).IsEqualTo("-");
        Check.That(f["edge"]).IsEqualTo("-");          // no window supplied geometry
        Check.That(f["movems"]).IsEqualTo("-1");
        // The saccade is a CLASS of the distance from the last sample before the key to its first
        // sample, in key pitches (here > 5 pitches away): never pixels.
        Check.That(f["saccade"]).IsEqualTo("long");
    }

    [Fact]
    public void TheWindowsGeometryAndTheMeanOverTheDwell_AreOnTheLine()
    {
        using var rig = new Rig();
        rig.Vm.E1Geometry = () => new KeyboardE1Geometry("top", 500, 0, 2800, 1000, 7000);
        var c = rig.Positions["key_0_0"];

        // First half of the dwell 40 px left of centre, second half 20 px right: fires at +20, mean ~ -10.
        rig.Hold(new PhysicalPoint(c.X - 40, c.Y), 210);
        rig.Hold(new PhysicalPoint(c.X + 20, c.Y), 240);

        var f = Fields(rig.Log.Of("key").Single());
        Check.That(f["edge"]).IsEqualTo("top");
        Check.That(f["winx"]).IsEqualTo("500");
        Check.That(f["winw"]).IsEqualTo("2800");
        Check.That(f["movems"]).IsEqualTo("7000");
        Check.That(f["dx"]).IsEqualTo("20");
        var mean = int.Parse(f["mdx"]);
        Check.That(mean).IsStrictlyLessThan(0);      // dominated by the early, left-of-centre samples
        Check.That(mean).IsStrictlyGreaterThan(-40);
    }

    [Fact]
    public void RestartsSinceTheLastActivation_CountDiscardedDwells()
    {
        using var rig = new Rig();
        var h = rig.Positions["key_0_0"];
        var a = rig.Positions["key_0_1"];
        rig.Hold(h, 120);        // start on h ...
        rig.Hold(a, 120);        // ... drift to a (restart 1)
        rig.Hold(h, 60);         // ... back to h (restart 2)
        rig.Hold(h, DwellMs);    // and finally dwell it

        var f = Fields(rig.Log.Of("key").Single());
        Check.That(f["restarts"]).IsEqualTo("2");

        // The count is "since the last activation": it starts over.
        rig.Hold(new PhysicalPoint(5000, 5000), CooldownMs + 100, inside: false);
        rig.Type("key_0_1");
        Check.That(Fields(rig.Log.Of("key")[1])["restarts"]).IsEqualTo("0");
    }

    [Fact]
    public void ABlinkGap_RestartsTheDwell_AndCountsAsARestart()
    {
        using var rig = new Rig();
        var h = rig.Positions["key_0_0"];
        rig.Hold(h, 240);
        rig.Ts += 400_000;       // a 400 ms hole in the stream (a blink)
        rig.Hold(h, DwellMs);

        Check.That(Fields(rig.Log.Of("key").Single())["restarts"]).IsEqualTo("1");
    }

    [Fact]
    public void SinceKey_IsTheIntervalToThePreviousActivation()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Clock = 1_300;
        rig.Type("key_0_1");

        Check.That(Fields(rig.Log.Of("key")[0])["sincekey"]).IsEqualTo("-1");
        Check.That(Fields(rig.Log.Of("key")[1])["sincekey"]).IsEqualTo("1300");
    }

    [Fact]
    public void SpecialKeys_CarryTheirActionId_AndTheirRowAndColumn()
    {
        using var rig = new Rig();
        rig.Type("space");

        var f = Fields(rig.Log.Of("key").Single());
        Check.That(f["cls"]).IsEqualTo("space");
        Check.That(f["act"]).IsEqualTo("space");
        Check.That(f["r"]).IsEqualTo("0");
        Check.That(f["c"]).IsEqualTo("4");
    }

    // ---- backspace class, typing flag, the shadow likely-bit ---------------------------------

    [Fact]
    public void Backspace_IsClassedInsideTheWord_OrIntoCommittedText_AndTypingIsAFlag()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");     // "h"
        rig.Type("backspace");   // inside the word
        rig.Type("key_0_0");     // "h"
        rig.Type("space");       // commit "h "
        rig.Type("backspace");   // into committed text

        var keys = rig.Log.Of("key").Select(Fields).ToList();
        Check.That(keys[0]["typing"]).IsEqualTo("False");
        Check.That(keys[1]["typing"]).IsEqualTo("True");
        Check.That(keys[1]["bs"]).IsEqualTo("word");
        Check.That(keys[4]["bs"]).IsEqualTo("committed");
        Check.That(keys[0]["bs"]).IsEqualTo("-");
    }

    [Fact]
    public void TheShadowLikelyBit_IsOnLetterLines_AndMatchesThePredictorsLetterSet()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100, ["hand"] = 90, ["halt"] = 80 });

        rig.Type("key_0_0");   // 'h' at a word start
        rig.Type("key_0_1");   // 'a' after "h": the most likely continuation
        rig.Type("key_0_2");   // 'x' after "ha": no known word continues with x

        var letters = rig.Log.Of("key").Select(Fields).ToList();
        Check.That(letters[0]["start"]).IsEqualTo("True");
        Check.That(letters[0]["likely"]).IsEqualTo("True");      // h is among the first letters
        Check.That(letters[1]["start"]).IsEqualTo("False");
        Check.That(letters[1]["likely"]).IsEqualTo("True");
        Check.That(letters[1]["nocont"]).IsEqualTo("False");
        Check.That(letters[2]["likely"]).IsEqualTo("False");
        Check.That(letters[2]["nocont"]).IsEqualTo("False");     // "ha" does continue: x just is not one of them
    }

    [Fact]
    public void ANoContinuationPrefix_IsFlaggedSoTheParserCanExcludeIt()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("key_0_2");   // x: word start, then...
        rig.Type("key_0_2");   // "x" has no continuation in the dictionary

        var second = Fields(rig.Log.Of("key")[1]);
        Check.That(second["nocont"]).IsEqualTo("True");
        Check.That(second["likely"]).IsEqualTo("False");
        Check.That(second["start"]).IsEqualTo("False");
    }

    [Fact]
    public void NonLetterKeys_CarryNoLikelyOrStartFlags()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("space");

        var f = Fields(rig.Log.Of("key").Single());
        Check.That(f["likely"]).IsEqualTo("False");
        Check.That(f["start"]).IsEqualTo("False");
        Check.That(f["nocont"]).IsEqualTo("False");
    }

    // ---- logging changes nothing -------------------------------------------------------------

    [Fact]
    public void LoggingOnOrOff_TheSameGazeFiresTheSameKeysAtTheSameSamples()
    {
        static (List<string> Fired, string Mirror) Run(bool logging)
        {
            using var rig = new Rig(logging, words: new() { ["haus"] = 100, ["hand"] = 90 });
            rig.Type("key_0_0");
            rig.Hold(rig.Positions["key_0_1"], 200);
            rig.Hold(rig.Positions["key_0_2"], 100);      // an abandonment on the way
            rig.Type("key_0_1");
            rig.Type("space");
            rig.Type("backspace");
            return (rig.Fired, rig.Vm.CurrentInput);
        }

        var on = Run(true);
        var off = Run(false);

        Check.That(on.Fired).ContainsExactly(off.Fired.ToArray());
        Check.That(on.Mirror).IsEqualTo(off.Mirror);
    }

    // ---- abandonment ------------------------------------------------------------------------

    [Fact]
    public void AnAbandonmentAtOrAboveTheThreshold_IsLogged_WithoutAnyTelemetrySink()
    {
        using var rig = new Rig();
        var c = rig.Positions["key_0_1"];

        rig.Hold(new PhysicalPoint(c.X + 4, c.Y + 6), 240);                    // > 30 % of 450 ms
        rig.Hold(rig.Positions["key_0_2"], 30);                                // moves on: NearestChanged

        var f = Fields(rig.Log.Of("abandon").Single());
        Check.That(f["cls"]).IsEqualTo("letter");
        Check.That(f.ContainsKey("r") || f.ContainsKey("c") || f.ContainsKey("act")).IsFalse();
        Check.That(f["why"]).IsEqualTo("NearestChanged");
        // The offset is the MEAN over the abandoned dwell (where the gaze rested on the key). The
        // sample that ended it already sits on the NEXT key, 220 px right: using it would hand over
        // the direction from one key to the next.
        Check.That(f["mdx"]).IsEqualTo("4");
        Check.That(f["mdy"]).IsEqualTo("6");
        Check.That(int.Parse(f["pct"])).IsStrictlyGreaterThan(29);
    }

    [Fact]
    public void APassThroughBelowTheThreshold_IsNotAnAbandonment()
    {
        using var rig = new Rig();
        rig.Hold(rig.Positions["key_0_1"], 60);
        rig.Hold(rig.Positions["key_0_2"], 30);

        Check.That(rig.Log.Of("abandon")).IsEmpty();
    }

    [Fact]
    public void LeavingTheKeyArea_IsAGazeLeftAbandonment()
    {
        using var rig = new Rig();
        rig.Hold(rig.Positions["key_0_1"], 240);
        rig.Hold(new PhysicalPoint(5000, 5000), 30, inside: false);

        Check.That(Fields(rig.Log.Of("abandon").Single())["why"]).IsEqualTo("GazeLeft");
    }

    // ---- hold-on ---------------------------------------------------------------------------

    [Fact]
    public void HoldOn_IsHowLongTheGazeStaysInTheBoxAfterTheFire_CappedAt600()
    {
        using var rig = new Rig();
        var c = rig.Positions["key_0_0"];
        rig.Hold(c, DwellMs);                       // fires
        rig.Hold(c, 900);                           // ...and the gaze stays well past the cap

        var hold = Fields(rig.Log.Of("holdon").Single());
        Check.That(hold["seq"]).IsEqualTo("1");
        Check.That(hold["ms"]).IsEqualTo("600");
        Check.That(hold["capped"]).IsEqualTo("True");
    }

    [Fact]
    public void HoldOn_EndsWhenTheGazeLeavesTheBox()
    {
        using var rig = new Rig();
        var c = rig.Positions["key_0_0"];
        rig.Hold(c, DwellMs);
        rig.Hold(c, 150);                           // stays ~150 ms ...
        rig.Hold(rig.Positions["key_0_2"], 60);     // ... then looks at another key

        var hold = Fields(rig.Log.Of("holdon").Single());
        var ms = int.Parse(hold["ms"]);
        Check.That(ms).IsStrictlyGreaterThan(100);
        Check.That(ms).IsStrictlyLessThan(250);
        Check.That(hold["capped"]).IsEqualTo("False");
    }

    [Fact]
    public void HoldOn_IsReadAcrossTheCooldown_WhereNoDwellRuns()
    {
        // The cooldown is 400 ms: a 300 ms stay is entirely inside it, and is still measured.
        using var rig = new Rig();
        var c = rig.Positions["key_0_0"];
        rig.Hold(c, DwellMs);
        rig.Hold(c, 300);
        rig.Hold(new PhysicalPoint(5000, 5000), 60, inside: false);

        var ms = int.Parse(Fields(rig.Log.Of("holdon").Single())["ms"]);
        Check.That(ms).IsStrictlyGreaterThan(250);
        Check.That(ms).IsStrictlyLessThan(350);
    }

    [Fact]
    public void EachActivationGetsExactlyOneHoldOn_KeyedToItsSeq()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Type("key_0_1");

        var holds = rig.Log.Of("holdon").Select(Fields).ToList();
        Check.That(holds.Select(h => h["seq"])).ContainsExactly("1", "2");
    }

    // ---- modes -----------------------------------------------------------------------------

    [Fact]
    public void ASubLayoutSwitchIsLogged_ButAnAutoReturnIsNot()
    {
        using var rig = new Rig();
        var layout = Layout();
        layout.SubLayouts["numbers"].AutoReturn = true;
        rig.Vm.SetLayout(layout);

        rig.Vm.SwitchSubLayout("numbers");
        rig.Vm.ActivateKey("1", null);       // text from an autoReturn sub-layout: the keyboard goes back by itself

        Check.That(rig.Log.Of("mode")).ContainsExactly("KB-E1 mode kind=sublayout from=letters to=numbers");
        Check.That(rig.Vm.ActiveSubLayoutId).IsEqualTo("letters");
    }

    [Fact]
    public void EmojiEnterAndExit_AreLogged_ButAHidesOwnResetIsNot()
    {
        using var rig = new Rig();
        rig.Vm.IsVisible = true;
        rig.Vm.IsEmojiMode = true;
        rig.Vm.IsEmojiMode = false;
        rig.Vm.IsEmojiMode = true;
        rig.Vm.IsVisible = false;           // the hide resets the flag: not the user's exit

        Check.That(rig.Log.Of("mode")).ContainsExactly(
            "KB-E1 mode kind=emoji from=text to=emoji",
            "KB-E1 mode kind=emoji from=emoji to=text",
            "KB-E1 mode kind=emoji from=text to=emoji");
        Check.That(rig.Log.Lines.Any(l => l.StartsWith("KB-E1 modeundo ", StringComparison.Ordinal))).IsTrue(); // enter+exit
        Check.That(rig.Log.Lines.Count(l => l.StartsWith("KB-E1 modeundo ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Fact]
    public void AModifierToggle_IsAMode_AndAOneShotConsumedByAKeyIsNotAnUndo()
    {
        using var rig = new Rig();
        rig.Vm.ToggleModifier(ModifierKey.Shift);
        rig.Vm.ActivateKey("a", "A");        // consumes the one-shot Shift
        rig.Vm.ToggleModifier(ModifierKey.Shift);   // toggles it back ON: not the reverse of the first

        Check.That(rig.Log.Of("mode")).ContainsExactly(
            "KB-E1 mode kind=shift from=off to=on",
            "KB-E1 mode kind=shift from=off to=on");
        Check.That(rig.Log.Lines.Any(l => l.StartsWith("KB-E1 modeundo ", StringComparison.Ordinal))).IsFalse();
    }

    // ---- the slip line: a relation, never a key ------------------------------------------------

    [Fact]
    public void ASlipOntoANeighbour_LogsAdjacencyDirectionAndDistance_ButNoKey()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");                  // the slipped letter
        rig.Clock = 800;
        rig.Type("backspace");                // taken back inside the word, 800 ms later
        rig.Clock = 5_000;
        rig.Type("key_0_1");                  // the replacement: the key to its right

        Check.That(rig.Log.Of("slip")).ContainsExactly("KB-E1 slip adj=True dir=E dist=1 ms=800 reg=top");
    }

    [Fact]
    public void ASlipOntoAFarKey_IsNotAdjacent_HasNoDirection_AndKeepsTheDistance()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Clock = 300;
        rig.Type("backspace");
        rig.Type("key_0_2");                  // two keys away

        Check.That(rig.Log.Of("slip")).ContainsExactly("KB-E1 slip adj=False dir=- dist=2 ms=300 reg=top");
    }

    [Fact]
    public void ASlipToTheLeft_ReadsWest()
    {
        using var rig = new Rig();
        rig.Type("key_0_1");
        rig.Type("backspace");
        rig.Type("key_0_0");

        Check.That(rig.Log.Of("slip").Single()).Contains("adj=True dir=W dist=1 ");
    }

    [Fact]
    public void TheReplacementBecomesTheNextSlipCandidate()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Type("backspace");
        rig.Type("key_0_1");                  // slip 1: h -> a
        rig.Type("backspace");
        rig.Type("key_0_2");                  // slip 2: a -> x

        Check.That(rig.Log.Of("slip")).HasSize(2);
        Check.That(rig.Log.Of("slip")[1]).Contains("adj=True dir=E dist=1 ");
    }

    [Fact]
    public void NoSlipLine_WhenTheBackspaceCameTooLate_IntoCommittedText_OrWasFollowedByAnotherBackspace()
    {
        using var rig = new Rig();

        // Too late: more than 3 s after the letter.
        rig.Type("key_0_0");
        rig.Clock = 3_001;
        rig.Type("backspace");
        rig.Type("key_0_1");

        // Into committed text: the word was committed with Space first.
        rig.Clock = 10_000;
        rig.Type("key_0_0");
        rig.Type("space");
        rig.Type("backspace");
        rig.Type("key_0_1");

        // Two Backspaces in a row: not one slip and one repair.
        rig.Clock = 20_000;
        rig.Type("key_0_0");
        rig.Type("key_0_1");
        rig.Type("backspace");
        rig.Type("backspace");
        rig.Type("key_0_2");

        Check.That(rig.Log.Of("slip")).IsEmpty();
    }

    [Fact]
    public void ASlipLine_CarriesOnlyTheFiveRelationFields()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Type("backspace");
        rig.Type("key_0_1");

        Check.That(Fields(rig.Log.Of("slip").Single()).Keys.OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly("adj", "dir", "dist", "ms", "reg");
    }

    [Fact]
    public void AnythingBetweenTheBackspaceAndTheReplacement_EndsTheSlip()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Type("backspace");
        rig.Type("space");                    // not a letter or digit
        rig.Type("key_0_1");

        Check.That(rig.Log.Of("slip")).IsEmpty();
    }

    // ---- mirror check ----------------------------------------------------------------------

    private sealed class FakeProbe : IKeyboardContextProbe
    {
        public Func<string?> Tail = () => null;
        public string? GetForegroundProcessName() => "WINWORD.exe";
        public string? ReadFocusedTextTail(int maxChars) => Tail();
    }

    [Fact]
    public async Task TheMirrorCheck_ComparesTheKeyboardsRecordWithTheControl_AndLogsOnlyTheVerdict()
    {
        var probe = new FakeProbe { Tail = () => "ha" };
        using var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(),
            contextProbe: probe);
        var log = new Capture();
        vm.UseE1Recorder(new KeyboardE1Recorder(log));
        vm.SetLayout(Layout());
        vm.ActivateKey("h", null);
        vm.ActivateKey("a", null);

        vm.BeginMirrorCheck("enter");
        for (var i = 0; i < 200 && log.Of("mirror").Length == 0; i++) await Task.Delay(10, TestContext.Current.CancellationToken);

        Check.That(log.Of("mirror")).ContainsExactly(
            "KB-E1 mirror at=enter res=match mirrorlen=2 ctllen=2 common=2");
    }

    [Fact]
    public void TheForegroundProcessName_ComesFromTheProbe_AndFailureIsUnknown()
    {
        using var ok = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(),
            contextProbe: new FakeProbe());
        using var none = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());

        Check.That(ok.ForegroundProcessName()).IsEqualTo("WINWORD.exe");
        Check.That(none.ForegroundProcessName()).IsEqualTo("unknown");
    }
}
