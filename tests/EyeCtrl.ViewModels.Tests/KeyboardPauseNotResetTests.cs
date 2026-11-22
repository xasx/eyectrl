namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// F15 / O3t, "pause instead of reset" (owner decision 22.11 after the F14 study): when the gaze
/// leaves the current key for a moment, its dwell PAUSES instead of starting over. A return within
/// <see cref="KeyboardViewModel.KeyDwellPauseUs"/> (300 ms) resumes the frozen fill; a longer
/// excursion is a real switch — the paused key is abandoned ONCE with the fill it had reached, and
/// the key the gaze moved to keeps the time it has collected since it was reached (inheritance).
///
/// <para>
/// The exclusions and the unchanged rules are pinned here too: an app-escaping key (Enter) keeps
/// today's immediate reset and a paused text key's time never completes one; "one look, one key"
/// still demands a real leave; and the 150 ms sample-gap clamp still restarts a dwell. The F4/F8
/// second dwell and the D13/landing guards are not touched and keep their own suites.
/// </para>
///
/// <para>
/// Pure arithmetic on a hand-built grid — no WPF and no window, like KeyboardGapCreditTests. The
/// sessions feed samples every 30 ms; the few boundary cases drive explicit timestamps.
/// </para>
/// </summary>
public class KeyboardPauseNotResetTests
{
    private const int DwellMs = 450;
    private const int CooldownMs = 400;
    private const int StepMs = 30;

    private static readonly string[] Ids = ["key_0_0", "key_0_1", "key_0_2", "enter", "space", "switchSub:numbers"];

    private static readonly Dictionary<string, PhysicalPoint> Positions =
        Ids.Select((id, i) => (id, p: new PhysicalPoint(100 + 220 * i, 100)))
            .ToDictionary(t => t.id, t => t.p);

    private static readonly Dictionary<string, PhysicalRect> Boxes =
        Ids.Select((id, i) => (id, r: new PhysicalRect(220 * i, 0, 200, 200)))
            .ToDictionary(t => t.id, t => t.r);

    /// <summary>Off the key area, still inside the window: an excursion with no candidate under it.</summary>
    private static readonly PhysicalPoint OffTheKeys = new(100, -80);

    /// <summary>Outside the window entirely: the GazeLeft edge.</summary>
    private static readonly PhysicalPoint OffTheWindow = new(20_000, 20_000);

    private static KeyboardLayout Layout() => new()
    {
        Name = "Pause",
        DefaultSubLayout = "letters",
        SubLayouts = new Dictionary<string, SubLayout>
        {
            ["letters"] = new()
            {
                Label = "ABC",
                Rows =
                [
                    [
                        new KeyDefinition { Label = "a", Output = "a" },
                        new KeyDefinition { Label = "b", Output = "b" },
                        new KeyDefinition { Label = "c", Output = "c" },
                        new KeyDefinition { Label = "↵", SpecialAction = "enter" },
                        new KeyDefinition { Label = "_", SpecialAction = "space" },
                        new KeyDefinition { Label = "123", SpecialAction = "switchSub:numbers" },
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
        public long Clock;
        public long Ts = 1_000_000;
        /// <summary>The sample timestamp the most recent fire happened on.</summary>
        public long LastFireTs { get; private set; }
        private long _currentTs;

        public Rig(int cooldownMs = CooldownMs)
        {
            Vm = new KeyboardViewModel(new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } },
                new WeakReferenceMessenger());
            Vm.UseE1Recorder(new KeyboardE1Recorder(Log, () => Clock, edgeTallyMinFires: 1));
            Vm.Configure(DwellMs, cooldownMs);
            Vm.SetLayout(Layout());
            Vm.KeyDwellCompleted += id =>
            {
                Fired.Add(id);
                LastFireTs = _currentTs;
            };
        }

        /// <summary>One sample at an explicit timestamp; the recorder's fake clock follows it.</summary>
        public void Sample(PhysicalPoint at, long timestampUs, bool inside = true)
        {
            Clock = timestampUs / 1000;
            _currentTs = timestampUs;
            Vm.ProcessKeyGaze(Positions, at, inside, timestampUs, Boxes);
            Ts = timestampUs + (StepMs * 1000L);
        }

        public void Sample(string keyId, long timestampUs) => Sample(Positions[keyId], timestampUs);

        /// <summary>Samples every 30 ms from the current Ts through Ts + <paramref name="ms"/>.</summary>
        public void Hold(PhysicalPoint at, int ms, bool inside = true)
        {
            var until = Ts + (ms * 1000L);
            while (Ts <= until)
            {
                Sample(at, Ts, inside);
                Ts += StepMs * 1000L;
            }
        }

        public void Hold(string keyId, int ms) => Hold(Positions[keyId], ms);

        public void Dispose() => Vm.Dispose();
    }

    private static Dictionary<string, string> Fields(string line) =>
        line.Split(' ').Skip(2).Where(t => t.Contains('='))
            .Select(t => t.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    // ---- a short excursion resumes ------------------------------------------------------------

    [Fact]
    public void A200msExcursionOntoANeighbour_ThenBack_FiresTheOriginalAtItsOwnDwell_AndNotTheNeighbour()
    {
        // 240 ms of a 450 ms dwell (samples at 0..240), then 200 ms on the neighbour (samples at
        // 270..450: the switch is at 270), back at 500. The excursion is 230 ms — inside 300 — so
        // the frozen 240 ms continues and the key fires 210 ms after the return, at 710. A reset
        // would need 450 ms from the return (950); the feed stops at 800, short of that.
        using var rig = new Rig();
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        rig.Hold("key_0_1", 200);
        var back = 500_000L;
        for (var t = back; t <= 800_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);

        Check.WithCustomMessage("the neighbour fired on a glance that came back").That(rig.Fired)
            .ContainsExactly("key_0_0");
        // The frozen fill, not a credited pause: a paused-time credit would fire at 530.
        Check.WithCustomMessage("the resumed dwell did not keep its frozen elapsed time")
            .That(rig.LastFireTs).IsEqualTo(710_000L);
        // The RESUMED key is not abandoned. The only line is the neighbour's pause, still open when
        // the fire ends the look (left at 500, 210 ms before 710): fixup 02 writes it with the
        // expiry fields — pct at B's last on-key sample (40 %), the switch-to-last-key endms — so a
        // line for the fired key (which would read 100 %) would fail the size and the pct here.
        var abandon = Fields(rig.Log.Of("abandon").Single());
        Check.That(abandon["pct"]).IsEqualTo("40");
        Check.That(abandon["why"]).IsEqualTo("NearestChanged");
        Check.That(abandon["endms"]).IsEqualTo("50");
        Check.That(long.Parse(abandon["ts"])).IsEqualTo(710_000L);

        var key = Fields(rig.Log.Of("key").Single());
        Check.That(key["cls"]).IsEqualTo("letter");
        // One forgiven excursion, counted on the key's own line; no key id, no character.
        Check.That(key["paused"]).IsEqualTo("1");
    }

    [Fact]
    public void ThePauseBoundary_300msResumes_AndOneStepMoreAbandons()
    {
        // Resume exactly at LeftTimestampUs + 300: the comparison is `>`, so 300 still resumes and
        // the key fires 210 ms later. The same excursion one step longer is a real switch: the key
        // is abandoned and needs a full new dwell.
        static Rig Run(long returnUs)
        {
            var rig = new Rig();
            for (long t = 0; t <= 240_000; t += StepMs * 1000L)
                rig.Sample("key_0_0", t);
            for (var t = 270_000L; t < returnUs; t += StepMs * 1000L)
                rig.Sample("key_0_1", t);        // leave at 270: the pause's LeftTimestampUs
            for (var t = returnUs; t <= returnUs + 300_000; t += StepMs * 1000L)
                rig.Sample("key_0_0", t);
            return rig;
        }

        using var resumed = Run(570_000);    // 270 + 300
        using var abandoned = Run(600_000);  // 270 + 330

        Check.WithCustomMessage("an excursion of exactly 300 ms did not resume")
            .That(resumed.Fired).ContainsExactly("key_0_0");
        Check.WithCustomMessage("the boundary resume credited the 300 ms pause")
            .That(resumed.LastFireTs).IsEqualTo(780_000L);   // 570 + (450 - 240)
        // The resumed key is not abandoned; the only line is the neighbour's pause, open when the
        // fire ends the look, written with its expiry fields.
        var resumedAbandon = Fields(resumed.Log.Of("abandon").Single());
        Check.That(resumedAbandon["pct"]).IsEqualTo("60");
        Check.That(resumedAbandon["why"]).IsEqualTo("NearestChanged");
        Check.That(resumedAbandon["endms"]).IsEqualTo("30");
        Check.That(long.Parse(resumedAbandon["ts"])).IsEqualTo(780_000L);

        Check.WithCustomMessage("an excursion of 330 ms still resumed the key")
            .That(abandoned.Fired).IsEmpty();
        Check.That(Fields(abandoned.Log.Of("abandon").Single())["pct"]).IsEqualTo("53");
    }

    // ---- a long excursion is a real switch ----------------------------------------------------

    [Fact]
    public void AnExcursionLongerThan300ms_AbandonsThePausedKeyOnce_AndTheNeighbourInheritsItsOwnTime()
    {
        // 240 ms of a 450 ms dwell (53 %), then the gaze stays on the neighbour. The pause expires
        // at the first sample past 300 ms (600 ms, 330 after the switch at 270), where ONE abandon
        // is written with the last on-key pct and the old switch-to-key gap (endms 30). The
        // neighbour's own dwell started when the gaze reached it, so it fires at 270 + 450 = 720 —
        // inheritance. Without it the expiry would restart the neighbour and it could not fire
        // before 1050.
        using var rig = new Rig();
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        for (var t = 270_000L; t <= 750_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);

        Check.WithCustomMessage("the key the gaze left fired across the switch").That(rig.Fired)
            .ContainsExactly("key_0_1");

        var abandons = rig.Log.Of("abandon");
        Check.That(abandons).HasSize(1);
        var f = Fields(abandons[0]);
        Check.That(f["cls"]).IsEqualTo("letter");
        Check.That(f["why"]).IsEqualTo("NearestChanged");
        Check.That(f["pct"]).IsEqualTo("53");        // the fill at the last on-key sample, not 100 %
        Check.That(f["endms"]).IsEqualTo("30");      // the switch-to-last-key gap, not the excursion
        // The switch is only real once the 300 ms window has closed: the line is written at that
        // sample, not at the leave (a reset at the leave would write it at 270).
        Check.That(long.Parse(f["ts"]) - 270_000L).IsStrictlyGreaterThan(300_000L);

        // The abandon is decided before the neighbour's fire on the same sample stream.
        var lines = rig.Log.Lines;
        Check.That(lines.FindIndex(l => l.StartsWith("KB-E1 abandon ", StringComparison.Ordinal)))
            .IsStrictlyLessThan(lines.FindIndex(l => l.StartsWith("KB-E1 key ", StringComparison.Ordinal)));
    }

    // ---- a fire ends the look ----------------------------------------------------------------

    [Fact]
    public void AFire_ClearsPausedDwells_SoAWobbleAcrossTheCooldownTypesOnlyTheFiredKey()
    {
        // A is at 300 ms of 450 when the gaze moves to B for 210 ms, back to A, which fires at 720.
        // The 100 ms cooldown ends at 820 while B's pause (left at 570) is still live; a glance back
        // to B at 840 would resume its 210 ms and fire it about 240 ms later — two keys from one
        // wobble, which "one look, one key" must never allow. The fire clears the pause, so B starts
        // a fresh 450 ms dwell at 840 and does not fire inside the feed.
        using var rig = new Rig(cooldownMs: 100);
        for (long t = 0; t <= 300_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        for (var t = 330_000L; t <= 540_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);
        for (var t = 570_000L; t <= 750_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);

        Check.WithCustomMessage("arrange: A must fire from the resumed dwell")
            .That(rig.Fired).ContainsExactly("key_0_0");
        Check.That(rig.LastFireTs).IsEqualTo(720_000L);

        for (var t = 780_000L; t <= 1_150_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);

        Check.WithCustomMessage("a glance back at B after A fired typed a second key from one wobble")
            .That(rig.Fired).ContainsExactly("key_0_0");
        Check.That(rig.LastFireTs).IsEqualTo(720_000L);
    }

    [Fact]
    public void AFire_EndsTheOpenPauseOfAnotherKey_WithTheExpiryLine_AndNoLineForTheFiredKey()
    {
        // B is at 420 of 450 ms when the gaze spends 150 ms on A (33 % at A's last on-key sample);
        // back on B at 630, B fires at 660 while A's pause (left at 630) is still open. Fixup 02:
        // the fire writes A's abandon as A's expiry would have — pct at the last ON-KEY sample (33,
        // not the 40 % the switch sample's gate sees), the switch-to-last-key endms, A's own
        // reason — stamped at the fire (660), and the pause is gone. Main and b0ad09d3 logged that
        // crossing; the silent clear dropped it. B, the fired key, gets no abandon line of its own.
        using var rig = new Rig();
        for (long t = 0; t <= 420_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);
        for (var t = 450_000L; t <= 600_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        for (var t = 630_000L; t <= 660_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);

        Check.WithCustomMessage("the resumed key did not fire while the other key's pause was open")
            .That(rig.Fired).ContainsExactly("key_0_1");
        Check.That(rig.LastFireTs).IsEqualTo(660_000L);

        var abandons = rig.Log.Of("abandon");
        Check.That(abandons).HasSize(1);
        var f = Fields(abandons[0]);
        Check.That(f["cls"]).IsEqualTo("letter");
        Check.That(f["why"]).IsEqualTo("NearestChanged");
        Check.That(f["pct"]).IsEqualTo("33");      // the fill at A's last on-key sample, not the 40 % at the switch
        Check.That(f["endms"]).IsEqualTo("30");
        Check.That(long.Parse(f["ts"])).IsEqualTo(660_000L);   // the fire, not A's would-be expiry at 960

        // A's pause is gone: had the fire kept it, A would expire at 960 and write a second line.
        for (var t = 690_000L; t <= 1_000_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        Check.WithCustomMessage("the fire left A's pause to expire later")
            .That(rig.Log.Of("abandon")).HasSize(1);
        Check.That(rig.Fired).ContainsExactly("key_0_1");
    }

    [Fact]
    public void AFire_EndsEveryOpenPause_OneLineEach_AndNoneForTheFiredKey()
    {
        // A is at 420 of 450 ms (93 %). The look visits B (180 ms, 40 %) and back to A, then C
        // (180 ms), and back to A, which fires at 990 — with B's pause (left 750) and C's (left 960)
        // both still open. The fire writes BOTH lines, with expiry's fields: B pct 47 (its 210 ms at
        // the last on-key sample), C pct 40, both NearestChanged, endms 30, stamped at the fire. A
        // itself gets no abandon line: it fired, and a resume had already removed its record.
        using var rig = new Rig();
        for (long t = 0; t <= 420_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        for (var t = 450_000L; t <= 630_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);
        rig.Sample("key_0_0", 660_000);                      // resumes A (its 420 ms frozen)
        for (var t = 690_000L; t <= 720_000; t += StepMs * 1000L)
            rig.Sample("key_0_1", t);                        // resumes B: 180 + 30 = 210 ms
        for (var t = 750_000L; t <= 930_000; t += StepMs * 1000L)
            rig.Sample("key_0_2", t);                        // C: 180 ms
        rig.Sample("key_0_0", 960_000);                      // resumes A; C paused at 960
        rig.Sample("key_0_0", 990_000);                      // A fires (420 + 30)

        Check.That(rig.Fired).ContainsExactly("key_0_0");
        Check.That(rig.LastFireTs).IsEqualTo(990_000L);

        var abandons = rig.Log.Of("abandon");
        Check.That(abandons).HasSize(2);
        Check.That(abandons.Select(l => Fields(l)["pct"])).ContainsExactly("47", "40");
        Check.That(abandons.Select(l => Fields(l)["why"]))
            .ContainsExactly("NearestChanged", "NearestChanged");
        Check.That(abandons.Select(l => Fields(l)["endms"])).ContainsExactly("30", "30");
        Check.That(abandons.Select(l => long.Parse(Fields(l)["ts"]))).ContainsExactly(990_000L, 990_000L);
        Check.WithCustomMessage("the fired key itself got an abandon line")
            .That(abandons.Select(l => Fields(l)["pct"]).Contains("100")).IsFalse();

        // The fire's lines keep expiry's order: the crossings, then the key.
        var lines = rig.Log.Lines;
        Check.WithCustomMessage("the abandons were not written before the fired key's line")
            .That(lines.FindIndex(l => l.StartsWith("KB-E1 key ", StringComparison.Ordinal)))
            .IsStrictlyGreaterThan(lines.FindLastIndex(l => l.StartsWith("KB-E1 abandon ", StringComparison.Ordinal)));
    }

    // ---- app-escaping keys are excluded -------------------------------------------------------

    [Fact]
    public void EnterLeftFor90msAndBack_Resets_ItMustNotFireEarly()
    {
        // Enter has the 600 ms app-escaping floor. It reaches 570 ms, is left for 90 ms, and comes
        // back: the rule is today's immediate reset, so it needs 600 ms from the return and does not
        // fire at 630 (which a resume would do) or at 1200 (510 ms after the return).
        using var rig = new Rig();
        for (long t = 0; t <= 570_000; t += StepMs * 1000L)
            rig.Sample("enter", t);
        Check.That(rig.Fired).IsEmpty();

        rig.Hold("key_0_0", 90);
        var back = 720_000L;                      // Hold leaves Ts one step past its last sample
        for (var t = back; t <= 1_200_000; t += StepMs * 1000L)
            rig.Sample("enter", t);
        Check.WithCustomMessage("Enter resumed a dwell across a glance instead of resetting")
            .That(rig.Fired).IsEmpty();

        for (var t = 1_230_000L; t <= 1_350_000; t += StepMs * 1000L)
            rig.Sample("enter", t);
        Check.That(rig.Fired).ContainsExactly("enter");   // fires 600 ms after the return, at 1320

        // The leave itself is still one NearestChanged abandon, at once (not after a pause).
        var abandon = Fields(rig.Log.Of("abandon").Single());
        Check.That(abandon["why"]).IsEqualTo("NearestChanged");
        Check.That(abandon["pct"]).IsEqualTo("95");
        // The Enter line that finally fires reports no forgiven excursion.
        Check.That(Fields(rig.Log.Of("key").Single())["paused"]).IsEqualTo("0");
    }

    [Fact]
    public void APausedTextKeyDwell_NeverCompletesAnAppEscapingKey()
    {
        // A letter is at 420 of 450 ms (93 %) when the gaze moves to Enter. A's pause expires at 780
        // with one abandon; Enter holds the 600 ms floor and counts only ITS own time (from 450), so
        // it cannot fire at the expiry (330 ms in) — and it must not inherit A's 420 ms either,
        // which would fire it around 630. It fires at 450 + 600 = 1050.
        using var rig = new Rig();
        for (long t = 0; t <= 420_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        for (var t = 450_000L; t <= 810_000; t += StepMs * 1000L)
            rig.Sample("enter", t);

        Check.WithCustomMessage("Enter fired before its own 600 ms floor").That(rig.Fired).IsEmpty();
        var abandon = Fields(rig.Log.Of("abandon").Single());
        Check.That(abandon["cls"]).IsEqualTo("letter");
        Check.That(abandon["why"]).IsEqualTo("NearestChanged");
        Check.That(abandon["pct"]).IsEqualTo("93");

        for (var t = 840_000L; t <= 1_100_000; t += StepMs * 1000L)
            rig.Sample("enter", t);
        Check.That(rig.Fired).ContainsExactly("enter");   // 450 + 600 = 1050
    }

    // ---- "one look, one key" is unchanged -----------------------------------------------------

    [Fact]
    public void AfterAFire_TheLatchedKey_CannotFireAgainThroughAPause()
    {
        // The page switch fires, then a 300 ms look at a letter and back. The letter's dwell pauses
        // and expires on its own; the latched key needs a real leave, so it never dwells — a short
        // pause must not hand it a second fire. A real leave then re-arms it normally.
        using var rig = new Rig();
        rig.Hold("switchSub:numbers", DwellMs + 30);          // fires; cooldown to ~880
        Check.That(rig.Fired).ContainsExactly("switchSub:numbers");

        rig.Hold("key_0_0", CooldownMs + 300);               // through the cooldown, then a neighbour
        rig.Hold("switchSub:numbers", 500);

        Check.WithCustomMessage("a pause re-armed the latched key").That(rig.Fired)
            .ContainsExactly("switchSub:numbers");

        rig.Hold(OffTheWindow, 150, inside: false);           // a real leave, RearmLeaveUs
        rig.Hold("switchSub:numbers", DwellMs + 30);

        Check.That(rig.Fired).ContainsExactly("switchSub:numbers", "switchSub:numbers");
    }

    [Fact]
    public void AGlanceBackOntoTheLatchedKey_PausesTheNextKeysDwell()
    {
        // A fires, then B is dwelled for 300 ms after the cooldown; a 60 ms glance back onto the
        // latched A and back to B. B's dwell is paused, not restarted, so it fires 150 ms after the
        // return (1_410), not 450 ms later (1_710). A itself does not fire again: 60 ms is far short
        // of its own second dwell (cooldown + 1000 ms), whose clock is untouched.
        using var rig = new Rig();
        for (long t = 0; t <= 450_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        Check.That(rig.Fired).ContainsExactly("key_0_0");
        Check.That(rig.LastFireTs).IsEqualTo(450_000L);

        for (var t = 510_000L; t <= 1_170_000; t += StepMs * 1000L)   // cooldown to 850, then 300 ms on B
            rig.Sample("key_0_2", t);
        for (var t = 1_200_000L; t <= 1_230_000; t += StepMs * 1000L)   // the 60 ms glance at A
            rig.Sample("key_0_0", t);
        for (var t = 1_260_000L; t <= 1_500_000; t += StepMs * 1000L)
            rig.Sample("key_0_2", t);

        Check.WithCustomMessage("B restarted at the glance back instead of pausing")
            .That(rig.LastFireTs).IsEqualTo(1_410_000L);
        Check.WithCustomMessage("the glance fired the latched key again or typed a third key")
            .That(rig.Fired).ContainsExactly("key_0_0", "key_0_2");
    }

    [Fact]
    public void AGlanceBackOntoTheLatchedKey_StillResetsAnAppEscapingNextKey()
    {
        // Enter holds the 600 ms floor. It reaches 570 ms when the gaze glances 60 ms back onto the
        // latched letter: an app-escaping key keeps today's immediate reset, so Enter needs a full
        // 600 ms from the return (2_130) and does not fire 30 ms later as a resumed dwell would.
        using var rig = new Rig();
        for (long t = 0; t <= 450_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        Check.That(rig.Fired).ContainsExactly("key_0_0");

        for (var t = 510_000L; t <= 1_440_000; t += StepMs * 1000L)   // cooldown to 850, then 570 ms on Enter
            rig.Sample("enter", t);
        Check.That(rig.Fired).ContainsExactly("key_0_0");             // 570 < 600: Enter has not fired

        for (var t = 1_470_000L; t <= 1_500_000; t += StepMs * 1000L)  // the 60 ms glance at A
            rig.Sample("key_0_0", t);
        for (var t = 1_530_000L; t <= 2_100_000; t += StepMs * 1000L)  // 570 ms after the return
            rig.Sample("enter", t);
        Check.WithCustomMessage("Enter resumed its dwell across the glance instead of resetting")
            .That(rig.Fired).ContainsExactly("key_0_0");

        rig.Sample("enter", 2_130_000);
        Check.That(rig.Fired).ContainsExactly("key_0_0", "enter");
        Check.That(rig.LastFireTs).IsEqualTo(2_130_000L);
    }

    // ---- the unchanged clamps -----------------------------------------------------------------

    [Fact]
    public void ASampleGapOfMoreThan150ms_StillRestartsTheDwell()
    {
        // One sample on the key, then 600 ms of silence: the gap clamp restarts, so nothing fires on
        // the sample after it. A dwell that credited the silence would fire there (600 >= 450).
        using var rig = new Rig();
        rig.Sample("key_0_0", 0);
        rig.Sample("key_0_0", 600_000);

        Check.WithCustomMessage("a 600 ms sample gap was credited and fired the key").That(rig.Fired).IsEmpty();

        for (var t = 630_000L; t <= 900_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        Check.That(rig.Fired).IsEmpty();

        rig.Sample("key_0_0", 1_050_000);
        Check.That(rig.Fired).ContainsExactly("key_0_0");     // 450 ms after the restart at 600
    }

    // ---- leaving the window is an excursion too -----------------------------------------------

    [Fact]
    public void AGazeOffTheWindow_WithinThePauseWindow_Resumes()
    {
        // F15's rule 6: the window edge is treated like any other excursion (the reason a GazeLeft
        // abandon is still written, only at the window's end). 240 ms, 300 ms off the window
        // (samples at 270..540; back at 570, exactly the boundary), then the frozen fill fires at 780.
        using var rig = new Rig();
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        rig.Hold(OffTheWindow, 270, inside: false);
        for (var t = 570_000L; t <= 800_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);

        Check.That(rig.Fired).ContainsExactly("key_0_0");
        Check.WithCustomMessage("the off-window resume credited the pause")
            .That(rig.LastFireTs).IsEqualTo(780_000L);   // 570 + (450 - 240)
        Check.That(rig.Log.Of("abandon")).IsEmpty();
    }

    [Fact]
    public void AGazeOffTheWindow_LongerThanThePauseWindow_AbandonsWithGazeLeft_AndStartsOver()
    {
        // The same look held past 300 ms: one GazeLeft abandon at the window's end, with the fill
        // the key had reached. The return starts a fresh dwell, which needs the full 450 ms.
        using var rig = new Rig();
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        for (var t = 270_000L; t <= 600_000; t += StepMs * 1000L)
            rig.Sample(OffTheWindow, t, inside: false);

        var abandon = Fields(rig.Log.Of("abandon").Single());
        Check.That(abandon["why"]).IsEqualTo("GazeLeft");
        Check.That(abandon["pct"]).IsEqualTo("53");

        for (var t = 630_000L; t <= 1_050_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        Check.WithCustomMessage("a fresh dwell after the abandonment fired early").That(rig.Fired).IsEmpty();

        rig.Sample("key_0_0", 1_080_000);
        Check.That(rig.Fired).ContainsExactly("key_0_0");     // 630 + 450
    }

    // ---- the E1 count -------------------------------------------------------------------------

    [Fact]
    public void APausedDwell_DoesNotSurviveASubLayoutSwap()
    {
        // A swap rebuilds the grid and an id may name a different key afterwards, so a paused dwell
        // must be dropped rather than resumed. The pause is at 240 ms; back on the key after the
        // swap, a surviving pause would fire 210 ms after the return (at 510). The fresh dwell only
        // gets 420 ms in the feed, short of 450, so only a leak can fire it.
        using var rig = new Rig();
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        rig.Sample("key_0_1", 270_000);
        rig.Vm.SwitchSubLayout("numbers");
        rig.Vm.SwitchSubLayout("letters");

        for (var t = 300_000L; t <= 720_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);

        Check.WithCustomMessage("a paused dwell was resumed across a grid rebuild").That(rig.Fired).IsEmpty();
    }

    [Fact]
    public void APausedDwell_DoesNotSurviveAHide()
    {
        // Hiding is the recovery path: a pause left over must not resume into the next show. The
        // pause is at 240 ms; a leaked record would fire 210 ms after the return (at 510), while the
        // fresh dwell only gets 420 ms in the feed. The active sub-layout and emoji mode are already
        // at rest, so only the hide reset itself can drop the pause.
        using var rig = new Rig();
        rig.Vm.IsVisible = true;
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        rig.Sample("key_0_1", 270_000);

        rig.Vm.IsVisible = false;
        rig.Vm.IsVisible = true;

        for (var t = 300_000L; t <= 720_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);

        Check.WithCustomMessage("a paused dwell was resumed across a hide").That(rig.Fired).IsEmpty();
    }

    [Fact]
    public void TwoForgivenExcursions_LogPausedTwo_AndTheNextDwellStartsAtZero()
    {
        // A is left twice and resumed twice before it fires: paused=2 on A's own line. B is left
        // for 200 ms on its way (a paused attempt that later expires), and the next key C is typed
        // normally: its line carries paused=0, so the count never leaks between dwells.
        using var rig = new Rig();
        for (long t = 0; t <= 240_000; t += StepMs * 1000L)
            rig.Sample("key_0_0", t);
        rig.Hold("key_0_1", 110);                    // A pauses; B is entered
        rig.Hold("key_0_0", 120);                    // B pauses; A resumes (excursion 1)
        rig.Hold("key_0_1", 90);                     // A pauses again
        rig.Hold("key_0_0", 200);                    // B pauses; A resumes (excursion 2) and fires

        Check.That(rig.Fired).ContainsExactly("key_0_0");
        var key = rig.Log.Of("key").Single(l => l.Contains(" cls=letter "));
        Check.That(Fields(key)["paused"]).IsEqualTo("2");

        // After the cooldown, type a third key normally: its line must not carry the count.
        rig.Hold("key_0_2", CooldownMs + DwellMs);
        var next = rig.Log.Of("key").Last();
        Check.That(Fields(next)["paused"]).IsEqualTo("0");
    }
}
