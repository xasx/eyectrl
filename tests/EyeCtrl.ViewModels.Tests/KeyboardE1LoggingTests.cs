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
        ["key_1_0"] = "k", ["key_1_1"] = "k",     // the row-band test's layout
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

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
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

        public readonly FakeSensitive Sensitive = new();

        public Rig(bool logging = true, Dictionary<string, int>? words = null)
        {
            Vm = new KeyboardViewModel(new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } },
                new WeakReferenceMessenger(), sensitiveContext: Sensitive);
            // A key far from the rest (4.6 key pitches from Space) for long-saccade cases; it is not in the
            // layout, so it logs as a non-text key.
            Positions["far"] = new PhysicalPoint(2000, 100);
            BoxesByKey["far"] = new PhysicalRect(1900, 0, 200, 200);
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
    public void ALetterActivation_SaysOnlyThatATextKeyWasTyped()
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
        Check.That(f["sub"]).IsEqualTo("letters");
        Check.That(f["edgeq"]).IsEqualTo("-");              // 5 % of the box off centre: the inner half
        Check.That(f["dwell"]).IsEqualTo("450");
        Check.That(f["penalty"]).IsEqualTo("False");
        Check.That(f["typing"]).IsEqualTo("False");
        Check.That(f["bs"]).IsEqualTo("-");
        Check.That(f["edge"]).IsEqualTo("-");               // no window supplied geometry
        Check.That(f["movems"]).IsEqualTo("-1");
        // Nothing that is, or leaks, the key: no position or id, no box size, no pixel offset, no saccade,
        // no restart count, no likelihood.
        foreach (var banned in new[] { "r", "c", "act", "boxw", "boxh", "dx", "dy", "mdx", "mdy", "saccade",
                     "restarts", "likely", "start", "nocont" })
            Check.WithCustomMessage($"A letter line carries '{banned}'").That(f.ContainsKey(banned)).IsFalse();
    }

    [Fact]
    public void TheWindowsGeometry_AndTheEdgeQuarterOfTheMeanOffset_AreOnTheLine()
    {
        using var rig = new Rig();
        rig.Vm.E1Geometry = () => new KeyboardE1Geometry("top", 500, 0, 2800, 1000, 7000);
        var c = rig.Positions["key_0_0"];

        // 11 of the 16 dwell samples 80 px (40 % of the 200 px box) west of centre, the last 5 on the centre:
        // the fire offset is 0 (inner), the MEAN is about -55 px (27 %): the west outer quarter.
        rig.Hold(new PhysicalPoint(c.X - 80, c.Y), 300);
        rig.Hold(c, 150);

        var f = Fields(rig.Log.Of("key").Single());
        Check.That(f["edge"]).IsEqualTo("top");
        Check.That(f["winx"]).IsEqualTo("500");
        Check.That(f["winw"]).IsEqualTo("2800");
        Check.That(f["movems"]).IsEqualTo("7000");
        Check.That(f["edgeq"]).IsEqualTo("W");
    }

    [Fact]
    public void RestartsSinceTheLastActivation_CountDiscardedDwells_OnANonTextKeyLine()
    {
        using var rig = new Rig();
        var space = rig.Positions["space"];
        var backspace = rig.Positions["backspace"];
        rig.Hold(space, 120);        // start on Space ...
        rig.Hold(backspace, 120);    // ... drift to Backspace (restart 1)
        rig.Hold(space, 60);         // ... back to Space (restart 2)
        rig.Hold(space, DwellMs);    // and finally dwell it

        var f = Fields(rig.Log.Of("key").Single());
        Check.That(f["restarts"]).IsEqualTo("2");

        // The count is "since the last activation": it starts over.
        rig.Hold(new PhysicalPoint(5000, 5000), CooldownMs + 100, inside: false);
        rig.Type("backspace");
        Check.That(Fields(rig.Log.Of("key")[1])["restarts"]).IsEqualTo("0");
    }

    [Fact]
    public void ABlinkGap_RestartsTheDwell_AndCountsAsARestart()
    {
        using var rig = new Rig();
        var space = rig.Positions["space"];
        rig.Hold(space, 240);
        rig.Ts += 400_000;       // a 400 ms hole in the stream (a blink)
        rig.Hold(space, DwellMs);

        Check.That(Fields(rig.Log.Of("key").Single())["restarts"]).IsEqualTo("1");
    }

    // ---- a non-text key's path facts must not carry the previous LETTER's position -----------------

    [Fact]
    public void ANonTextKeyRightAfterALetter_WithholdsSaccadeAndRestarts()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");                       // a letter
        rig.Hold(rig.Positions["space"], 120);
        rig.Hold(rig.Positions["backspace"], 60);  // drift (a restart) on the way to Space
        rig.Hold(rig.Positions["space"], DwellMs + 400);

        var space = Fields(rig.Log.Of("key").Select(l => l).Last(l => l.Contains("cls=space")));
        Check.That(space["saccade"]).IsEqualTo("-");
        Check.That(space["restarts"]).IsEqualTo("-1");
    }

    [Fact]
    public void ANonTextKeyAfterAnotherNonTextKey_LogsItsPathFacts()
    {
        using var rig = new Rig();
        rig.Type("space");
        rig.Hold(rig.Positions["backspace"], CooldownMs + DwellMs + 60);

        var backspace = Fields(rig.Log.Of("key").Last(l => l.Contains("cls=backspace")));
        Check.That(backspace["saccade"]).IsEqualTo("mid");
        Check.That(backspace["restarts"]).IsEqualTo("0");
    }

    // ---- the saccade: previous fire point to this dwell's start, whatever the cooldown does ------

    [Fact]
    public void TheSaccade_IsTheDisplacementFromThePreviousFirePointToTheDwellStart_InPitchClasses()
    {
        using var rig = new Rig();
        rig.Hold(rig.Positions["space"], DwellMs);                       // fires at (980, 100)
        rig.Hold(rig.Positions["far"], CooldownMs + DwellMs + 60);       // looks 1020 px (4.6 pitches) away and dwells

        var far = Fields(rig.Log.Of("key")[1]);
        Check.That(far["saccade"]).IsEqualTo("long");
    }

    [Fact]
    public void TheSaccade_DoesNotDependOnHowSoonAfterTheFireTheNextDwellMayStart()
    {
        // A gaze that reaches the next key 100 ms after the fire is inside the 400 ms cooldown: no dwell can
        // begin yet. The saccade is still the full displacement (one pitch = mid), not the jitter-sized step
        // between the last two samples (which would read "short").
        using var rig = new Rig();
        rig.Hold(rig.Positions["space"], DwellMs);                       // fires at (980, 100)
        rig.Hold(rig.Positions["backspace"], CooldownMs + DwellMs + 60); // arrives within the cooldown, 220 px away

        var second = Fields(rig.Log.Of("key")[1]);
        Check.That(second["cls"]).IsEqualTo("backspace");
        Check.That(second["saccade"]).IsEqualTo("mid");
    }

    [Fact]
    public void TheSaccade_EndsWhereTheDwellBegan_NotWhereItFired()
    {
        // The gaze reaches the key's far side (3.5 pitches from the previous fire point, long), rests,
        // and drifts to the near side (1.3 pitches, mid) before the dwell completes. The saccade is the
        // arrival, so it reads "long"; a measurement to the fire point would read "mid".
        using var rig = new Rig();
        rig.Positions["wide"] = new PhysicalPoint(1500, 100);
        rig.BoxesByKey["wide"] = new PhysicalRect(1100, 0, 800, 200);
        rig.Hold(rig.Positions["space"], DwellMs);                                  // fires at (980, 100)
        rig.Hold(new PhysicalPoint(1740, 100), CooldownMs + 60);                    // arrives far side; dwell begins
        rig.Hold(new PhysicalPoint(1260, 100), DwellMs);                            // drifts near side, fires

        var keys = rig.Log.Of("key").Select(Fields).ToList();
        Check.That(keys).HasSize(2);
        Check.That(keys[1]["cls"]).IsEqualTo("other");
        Check.That(keys[1]["saccade"]).IsEqualTo("long");
    }

    [Fact]
    public void ADwellOnTheSamePlaceAgain_IsAShortSaccade_AndTheFirstEverIsUnknown()
    {
        using var rig = new Rig();
        var space = rig.Positions["space"];
        rig.Hold(space, DwellMs);
        rig.Hold(space, CooldownMs + DwellMs + 60);

        var keys = rig.Log.Of("key").Select(Fields).ToList();
        Check.That(keys[0]["saccade"]).IsEqualTo("-");       // nothing came before it
        Check.That(keys[1]["saccade"]).IsEqualTo("short");
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
    public void TheShadowLikelyBit_IsLoggedOnlyForACorrectedLetter_AndMatchesThePredictorsLetterSet()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100, ["hand"] = 90, ["halt"] = 80 });

        rig.Type("key_0_0");   // 'h' at a word start
        rig.Type("key_0_1");   // 'a' after "h": the most likely continuation
        rig.Type("backspace"); // ... taken back: THIS is what logs a's likelihood
        rig.Type("key_0_2");   // 'x' after "h": no word continues with x
        rig.Type("backspace");

        var corr = rig.Log.Of("corr").Select(Fields).ToList();
        Check.That(corr).HasSize(2);
        Check.That(corr[0]["likely"]).IsEqualTo("True");
        Check.That(corr[0]["start"]).IsEqualTo("False");
        Check.That(corr[0]["nocont"]).IsEqualTo("False");
        Check.That(corr[1]["likely"]).IsEqualTo("False");
        Check.That(corr[1]["nocont"]).IsEqualTo("False");    // "h" does continue: x just is not one of them
        // ...and not on any letter line.
        foreach (var letter in rig.Log.Of("key").Select(Fields).Where(f => f["cls"] == "letter"))
            Check.That(letter.ContainsKey("likely")).IsFalse();
    }

    [Fact]
    public void ACorrectedWordStart_IsFlagged_SoTheParserCanExcludeIt()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("key_0_0");     // 'h': a word start
        rig.Type("backspace");

        var corr = Fields(rig.Log.Of("corr").Single());
        Check.That(corr["start"]).IsEqualTo("True");
        Check.That(corr["likely"]).IsEqualTo("True");
    }

    [Fact]
    public void ANoContinuationPrefix_IsFlaggedOnTheCorrection()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("key_0_2");     // x: a word start
        rig.Type("key_0_2");     // "x" has no continuation in the dictionary
        rig.Type("backspace");

        var corr = Fields(rig.Log.Of("corr").Single());
        Check.That(corr["nocont"]).IsEqualTo("True");
        Check.That(corr["likely"]).IsEqualTo("False");
        Check.That(corr["start"]).IsEqualTo("False");
    }

    [Fact]
    public void ABackspaceIntoCommittedText_OrAfterANonText_Key_HasNoCorrectionLine()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("space");
        rig.Type("backspace");   // nothing was slipped

        Check.That(rig.Log.Of("corr")).IsEmpty();
        Check.That(Fields(rig.Log.Of("key")[0]).ContainsKey("likely")).IsFalse();
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

        rig.Hold(new PhysicalPoint(c.X + 80, c.Y), 240);                       // > 30 % of 450 ms, 40 % east
        rig.Hold(rig.Positions["key_0_2"], 30);                                // moves on: NearestChanged

        var f = Fields(rig.Log.Of("abandon").Single());
        Check.That(f["cls"]).IsEqualTo("letter");
        foreach (var banned in new[] { "r", "c", "act", "mdx", "mdy", "dx", "dy" })
            Check.That(f.ContainsKey(banned)).IsFalse();
        Check.That(f["why"]).IsEqualTo("NearestChanged");
        // The class is the MEAN over the abandoned dwell (where the gaze rested on the key). The sample that
        // ended it already sits on the NEXT key, 220 px right: using it would hand over the direction from
        // one key to the next.
        Check.That(f["edgeq"]).IsEqualTo("E");
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

        var slip = Fields(rig.Log.Of("slip").Single());
        Check.That(slip["adj"]).IsEqualTo("True");
        Check.That(slip["dir"]).IsEqualTo("E");
        Check.That(slip["dist"]).IsEqualTo("1");
        Check.That(slip["ms"]).IsEqualTo("800");
        Check.That(slip["reg"]).IsEqualTo("top");
    }

    [Fact]
    public void ASlipOntoAFarKey_IsNotAdjacent_HasNoDirection_AndKeepsTheDistance()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Clock = 300;
        rig.Type("backspace");
        rig.Type("key_0_2");                  // two keys away

        var slip = Fields(rig.Log.Of("slip").Single());
        Check.That(slip["adj"]).IsEqualTo("False");
        Check.That(slip["dir"]).IsEqualTo("-");
        Check.That(slip["dist"]).IsEqualTo("2");
        Check.That(slip["ms"]).IsEqualTo("300");
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
    public void ABackspaceIntoCommittedText_RightAfterAShortcutLetter_IsNotASlip()
    {
        // A letter pressed with Ctrl armed types nothing into the word, so the Backspace that follows
        // it goes into COMMITTED text. That is editing, not a slip, even though it directly follows a letter.
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Type("space");                              // "h " is now committed
        rig.Vm.ToggleModifier(ModifierKey.Ctrl);
        rig.Type("key_0_1");                            // Ctrl+letter: no CurrentInput
        rig.Type("backspace");                          // bs=committed
        rig.Type("key_0_2");

        var backspace = rig.Log.Of("key").Select(Fields).Single(f => f["cls"] == "backspace");
        Check.That(backspace["bs"]).IsEqualTo("committed");
        Check.That(rig.Log.Of("slip")).IsEmpty();
    }

    [Fact]
    public void ASlipLine_CarriesTheRelationAndTheSlippedKeysOwnFactsOnly()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("key_0_0");
        rig.Type("backspace");
        rig.Type("key_0_1");

        Check.That(Fields(rig.Log.Of("slip").Single()).Keys.OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly("adj", "dir", "dist", "likely", "ms", "nocont", "reg", "restarts", "saccade", "start");
    }

    [Fact]
    public void TheBackspaceThatTakesALetterBack_LogsAKeyedCorrectionLine()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Type("key_0_0");
        rig.Type("backspace");

        var backspaceSeq = Fields(rig.Log.Of("key")[1])["seq"];
        var corr = Fields(rig.Log.Of("corr").Single());
        Check.That(corr["seq"]).IsEqualTo(backspaceSeq);
        Check.That(corr.Keys.OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly("likely", "nocont", "restarts", "saccade", "seq", "start");
    }

    [Fact]
    public void TheSlippedKeysArrival_IsLogged_FromTheKeyThatWasSlipped_NotTheReplacement()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");                       // first ever: its saccade is unknown ("-")
        rig.Type("backspace");
        rig.Type("key_0_1");                       // the replacement

        var slip = Fields(rig.Log.Of("slip").Single());
        Check.That(slip["saccade"]).IsEqualTo("-");
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

    [Fact]
    public void TheCorrectionFacts_DoNotDependOnWhichKeyWasSlipped()
    {
        // Two corrections with the same gaze behaviour, on keys in different columns: every field of the
        // corr line (and of the slip line's own-key facts) must be identical, whatever the key.
        static (Dictionary<string, string> Corr, Dictionary<string, string> Slip) Run(string slipped, string repair)
        {
            using var rig = new Rig(words: new() { ["haus"] = 100, ["xxx"] = 50 });
            rig.Type(slipped);
            rig.Type("backspace");
            rig.Type(repair);
            var corr = Fields(rig.Log.Of("corr").Single());
            var slip = Fields(rig.Log.Of("slip").Single());
            corr.Remove("seq");
            return (corr, slip);
        }

        var (corrA, slipA) = Run("key_0_0", "key_0_1");
        var (corrB, slipB) = Run("key_0_1", "key_0_2");     // another column, the same behaviour

        // likely/start/nocont legitimately depend on the DICTIONARY (h is likely, a is not a word start...).
        foreach (var field in new[] { "saccade", "restarts" })
            Check.WithCustomMessage($"corr {field} differs by the slipped key").That(corrB[field]).IsEqualTo(corrA[field]);
        foreach (var field in new[] { "saccade", "restarts" })
            Check.WithCustomMessage($"slip {field} differs by the slipped key").That(slipB[field]).IsEqualTo(slipA[field]);
    }

    [Fact]
    public void TheRowBand_CountsOnlyTheRowsThatHoldTextKeys()
    {
        // Three rows of text keys and a Space/function row: the middle text row is "mid". Counting the
        // special-only row too would make rows 0 and 1 both "top".
        static List<KeyDefinition> Row(int n) =>
            Enumerable.Range(0, n).Select(i => new KeyDefinition { Label = "k", Output = "k" }).ToList();
        var layout = new KeyboardLayout
        {
            Name = "Rows",
            DefaultSubLayout = "letters",
            SubLayouts = new Dictionary<string, SubLayout>
            {
                ["letters"] = new()
                {
                    Label = "ABC",
                    Rows =
                    [
                        Row(4), Row(4), Row(4),
                        [
                            new KeyDefinition { Label = "<", SpecialAction = "backspace" },
                            new KeyDefinition { Label = "_", SpecialAction = "space" },
                        ],
                    ],
                },
            },
        };
        using var rig = new Rig();
        rig.Vm.SetLayout(layout);
        // Key geometry for key_1_0 and key_1_1 (the middle text row), one pitch apart.
        rig.Positions["key_1_0"] = new PhysicalPoint(100, 400);
        rig.Positions["key_1_1"] = new PhysicalPoint(320, 400);
        rig.BoxesByKey["key_1_0"] = new PhysicalRect(0, 300, 200, 200);
        rig.BoxesByKey["key_1_1"] = new PhysicalRect(220, 300, 200, 200);

        rig.Type("key_1_0");
        rig.Type("backspace");
        rig.Type("key_1_1");

        Check.That(Fields(rig.Log.Of("slip").Single())["reg"]).IsEqualTo("mid");
    }

    // ---- F4: the letter box is logged once per layout, never per key ---------------------------

    [Fact]
    public void TheLetterBox_IsLoggedOnALayoutLine_OncePerLayout_NeverOnAKeyLine()
    {
        using var rig = new Rig();
        rig.Vm.E1LetterBox = () => (157, 158);

        rig.Type("key_0_0");
        rig.Type("key_0_1");                       // same layout: no second line
        rig.Vm.SwitchSubLayout("numbers");         // a relayout
        rig.Vm.SwitchSubLayout("letters");
        rig.Type("key_0_2");

        Check.That(rig.Log.Of("layout")).ContainsExactly(
            "KB-E1 layout sub=letters boxw=157 boxh=158",
            "KB-E1 layout sub=letters boxw=157 boxh=158");
        Check.That(rig.Log.Of("key").Select(l => l.Contains("boxw="))).ContainsExactly(false, false, false);
        // The layout line comes BEFORE the first key line it describes.
        var firstLayout = rig.Log.Lines.FindIndex(l => l.StartsWith("KB-E1 layout ", StringComparison.Ordinal));
        var firstKey = rig.Log.Lines.FindIndex(l => l.StartsWith("KB-E1 key ", StringComparison.Ordinal));
        Check.That(firstLayout).IsStrictlyLessThan(firstKey);
    }

    // ---- F1: nothing per key in a password field ------------------------------------------------

    [Fact]
    public void InASensitiveContext_NoPerKeyLineIsLogged_AndTheMarkerCarriesNoCount()
    {
        using var rig = new Rig(words: new() { ["haus"] = 100 });
        rig.Sensitive.IsSensitiveActive = true;

        rig.Type("key_0_0");
        rig.Type("key_0_1");
        rig.Type("backspace");
        rig.Type("key_0_2");
        rig.Type("space");

        Check.That(rig.Fired).HasSize(5);                      // the keyboard still TYPES
        Check.That(rig.Log.Lines).ContainsExactly("KB-E1 sensitive");
    }

    [Fact]
    public void LeavingAndReEnteringASensitiveContext_WritesTheMarkerAgain_AndLoggingResumesBetween()
    {
        using var rig = new Rig();
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("key_0_0");
        rig.Sensitive.IsSensitiveActive = false;
        rig.Type("key_0_1");
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("key_0_2");

        Check.That(rig.Log.Lines.Count(l => l == "KB-E1 sensitive")).IsEqualTo(2);
        Check.That(rig.Log.Of("key")).HasSize(1);
    }

    [Fact]
    public void ASlipStartedBeforeEnteringASensitiveContext_IsNotCompletedInside()
    {
        using var rig = new Rig();
        rig.Type("key_0_0");
        rig.Type("backspace");
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("key_0_1");                        // the replacement, typed into a password field
        rig.Sensitive.IsSensitiveActive = false;
        rig.Type("key_0_2");

        Check.That(rig.Log.Of("slip")).IsEmpty();
    }

    // ---- F10: logging never swallows a key press -------------------------------------------------

    [Fact]
    public void AThrowingGeometryCallback_StillLetsTheKeyFire()
    {
        using var rig = new Rig();
        rig.Vm.E1Geometry = () => throw new InvalidOperationException("geometry");

        rig.Type("key_0_1");

        Check.That(rig.Fired).ContainsExactly("key_0_1");
        Check.That(rig.Vm.CurrentInput).IsEqualTo("a");
    }

    [Fact]
    public void AThrowingLetterBoxCallback_NeverReachesTheKeyOrTheAbandonment()
    {
        using var rig = new Rig();
        rig.Vm.E1LetterBox = () => throw new InvalidOperationException("box");

        rig.Hold(rig.Positions["key_0_1"], 240);
        rig.Hold(rig.Positions["key_0_2"], 30);     // an abandonment
        rig.Type("key_0_1");

        Check.That(rig.Fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void AThrowingGeometryCallback_NeverReachesTheGazePathOnAnAbandonment()
    {
        using var rig = new Rig();
        rig.Vm.E1Geometry = () => throw new InvalidOperationException("geometry");

        rig.Hold(rig.Positions["key_0_1"], 240);
        rig.Hold(rig.Positions["key_0_2"], 30);     // an abandonment: the geometry callback is read for its line
        rig.Type("key_0_1");

        Check.That(rig.Fired).ContainsExactly("key_0_1");
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
