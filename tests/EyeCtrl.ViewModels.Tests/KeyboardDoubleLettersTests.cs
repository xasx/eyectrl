namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// F4: a TEXT key that just fired may type a deliberate double by a second dwell of
/// <see cref="KeyboardViewModel.RepeatDwellUs"/> AFTER the post-fire cooldown. Excursions of up to
/// <see cref="KeyboardViewModel.RepeatExcursionToleranceUs"/> pause the progress; longer ones reset
/// it. Non-text keys (Enter, Tab, Backspace, Wort löschen, modifiers, page switches) never repeat
/// this way. The numbers come from the KB7 replay (Phase 1 report): the intended double reached
/// 1208 ms of on-key time after the cooldown, the longest unintended pause 1087 ms.
///
/// <para>
/// The trace here is synthetic: key centres, boxes and a small hand-built layout. No owner data.
/// </para>
/// </summary>
public class KeyboardDoubleLettersTests
{
    private const int DwellMs = 450;
    private const int CooldownMs = 400;
    private const int StepMs = 30;

    private static readonly string[] Ids =
    [
        "key_0_0", "key_0_1", "key_0_2", "key_0_3", "key_0_4", "key_0_5",
        "key_0_6", "key_0_7", "key_0_8", "key_0_9", "key_0_10", "key_0_11",
        "enter", "backspace", "deleteWord", "tab", "space",
    ];

    private static readonly Dictionary<string, PhysicalPoint> Positions =
        Ids.Select((id, i) => (id, p: new PhysicalPoint(100 + 220 * i, 100)))
            .ToDictionary(t => t.id, t => t.p);

    private static readonly Dictionary<string, PhysicalRect> Boxes =
        Ids.Select((id, i) => (id, r: new PhysicalRect(220 * i, 0, 200, 200)))
            .ToDictionary(t => t.id, t => t.r);

    /// <summary>Inside the keyboard key area but on no key: a pure off-key excursion.</summary>
    private static readonly PhysicalPoint Nowhere = new(5000, 5000);

    private static KeyboardLayout Layout() => new()
    {
        Name = "Double",
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
                        new KeyDefinition { Label = "1", Output = "1" },
                        new KeyDefinition { Label = "?", Output = "?" },
                        new KeyDefinition { Label = "e", Output = "e" },
                        new KeyDefinition { Label = "f", Output = "f" },
                        new KeyDefinition { Label = "g", Output = "g" },
                        new KeyDefinition { Label = "h", Output = "h" },
                        new KeyDefinition { Label = "i", Output = "i" },
                        new KeyDefinition { Label = "j", Output = "j" },
                        new KeyDefinition { Label = "k", Output = "k" },
                        new KeyDefinition { Label = "l", Output = "l" },
                        new KeyDefinition { Label = "↵", SpecialAction = "enter" },
                        new KeyDefinition { Label = "⌫", SpecialAction = "backspace" },
                        new KeyDefinition { Label = "Aa", SpecialAction = "deleteWord" },
                        new KeyDefinition { Label = "⇥", SpecialAction = "tab" },
                        new KeyDefinition { Label = "_", SpecialAction = "space" },
                    ],
                ],
            },
            ["numbers"] = new() { Label = "123", Rows = [] },
        },
    };

    private sealed class Rig
    {
        public readonly KeyboardViewModel Vm;
        public readonly List<string> Fired = new();
        public long Ts = 1_000_000;

        /// <summary>The sample timestamp of the most recent activation (set by the event).</summary>
        public long LastFireTs { get; private set; }

        public Rig()
        {
            Vm = new KeyboardViewModel(
                new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } },
                new WeakReferenceMessenger());
            Vm.Configure(DwellMs, CooldownMs);
            Vm.SetLayout(Layout());
            Vm.KeyDwellCompleted += id =>
            {
                Fired.Add(id);
                LastFireTs = Ts;
            };
        }

        /// <summary>Samples every 30 ms from the current Ts through Ts + ms, on the key's centre.</summary>
        public void Hold(string keyId, int ms)
        {
            var at = Positions[keyId];
            var until = Ts + (ms * 1000L);
            while (Ts <= until)
            {
                Vm.ProcessKeyGaze(Positions, at, true, Ts, Boxes);
                Ts += StepMs * 1000L;
            }
        }

        /// <summary>Samples every 30 ms up to an absolute timestamp, on the key's centre.</summary>
        public void HoldThrough(long untilUs, string keyId)
        {
            var at = Positions[keyId];
            while (Ts <= untilUs)
            {
                Vm.ProcessKeyGaze(Positions, at, true, Ts, Boxes);
                Ts += StepMs * 1000L;
            }
        }

        /// <summary>Samples every 30 ms at an arbitrary point. <paramref name="inside"/> is the
        /// window's key-area containment; false clears the re-arm latch after 120 ms.</summary>
        public void HoldAt(PhysicalPoint at, int ms, bool inside = true)
        {
            var until = Ts + (ms * 1000L);
            while (Ts <= until)
            {
                Vm.ProcessKeyGaze(Positions, at, inside, Ts, Boxes);
                Ts += StepMs * 1000L;
            }
        }

        /// <summary>Fires the key once and leaves Ts one sample after the fire.</summary>
        public void Fire(string keyId)
        {
            Hold(keyId, DwellMs);
            Check.WithCustomMessage($"arrange: {keyId} must fire once").That(Fired).ContainsExactly(keyId);
        }

        /// <summary>Fires the next key after the previous activation's cooldown; used by the trace.</summary>
        public void FireNext(string keyId, int dwellMs = DwellMs)
        {
            if (LastFireTs > 0)
                HoldThrough(LastFireTs + (CooldownMs * 1000L), keyId);
            Hold(keyId, dwellMs);
        }

        /// <summary>The key just fired; hold through the cooldown and complete a 1200 ms second dwell.</summary>
        public void SecondDwell(string keyId)
        {
            HoldThrough(LastFireTs + (CooldownMs * 1000L), keyId);
            Hold(keyId, 1250);
        }

        /// <summary>The key just fired; look off the keyboard >= 120 ms, then fire it normally again.</summary>
        public void LookAway(string keyId, int dwellMs, bool inside)
        {
            HoldAt(Nowhere, 150, inside);
            FireNext(keyId, dwellMs);
        }

        /// <summary>The key just fired; an off-key excursion and a return that must not fire.</summary>
        public void Wobble(string keyId, int offMs, int returnMs)
        {
            HoldThrough(LastFireTs + (CooldownMs * 1000L), keyId);
            HoldAt(Nowhere, offMs);
            Hold(keyId, returnMs);
        }
    }

    // ---- the deliberate double ----------------------------------------------------------------

    [Fact]
    public void ASecondDwellOf1200MsAfterTheCooldown_TypesTheDouble()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");

        // The cooldown is not dwell time; the 1200 ms clock starts after it (fire + 400 ms).
        rig.Hold("key_0_0", CooldownMs + 1200);

        Check.That(rig.Fired).ContainsExactly("key_0_0", "key_0_0");
    }

    [Fact]
    public void AShortSecondDwell_DoesNotTypeTheDouble()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");

        rig.Hold("key_0_0", CooldownMs + 1000);

        Check.That(rig.Fired).ContainsExactly("key_0_0");
    }

    [Fact]
    public void A1200MsCountedFromTheFire_DoesNotFire()
    {
        // The coordinator's pin: RepeatDwellMs is measured from when the latched key's dwell MAY
        // start, i.e. after the 400 ms cooldown. A trace that ends at fire + 1200 ms leaves only
        // ~800 ms of credited dwell; an implementation counting from the fire would fire here.
        var rig = new Rig();
        const long fireAt = 1_450_000;
        rig.Fire("key_0_0");

        rig.HoldThrough(fireAt + 1_200_000, "key_0_0");

        Check.That(rig.Fired).ContainsExactly("key_0_0");
    }

    [Theory]
    [InlineData("key_0_0")]   // letter
    [InlineData("key_0_2")]   // digit
    [InlineData("key_0_3")]   // symbol
    public void TextKeys_RepeatByASecondDwell(string keyId)
    {
        var rig = new Rig();
        rig.Fire(keyId);

        rig.Hold(keyId, CooldownMs + 1200);

        Check.That(rig.Fired).ContainsExactly(keyId, keyId);
    }

    // ---- excursions ---------------------------------------------------------------------------

    [Fact]
    public void A350MsExcursion_PausesTheSecondDwell()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");   // through the cooldown, ending at the first live sample

        rig.Hold("key_0_0", 700);                 // 700 ms of credited progress
        rig.HoldAt(Nowhere, 350);                 // <= 400 ms off the key: pause, do not reset
        rig.HoldThrough(3_500_000, "key_0_0");    // back on the key: completes the 1200 ms

        Check.That(rig.Fired).ContainsExactly("key_0_0", "key_0_0");
    }

    [Fact]
    public void A450MsExcursion_ResetsTheSecondDwell()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");

        rig.Hold("key_0_0", 700);
        rig.HoldAt(Nowhere, 450);                 // > 400 ms off the key: the progress resets
        rig.HoldThrough(3_500_000, "key_0_0");    // ~420 ms after the return: under 1200

        Check.That(rig.Fired).ContainsExactly("key_0_0");
    }

    [Fact]
    public void AWobbleOfAtMost400MsPastTheEdge_DoesNotRearmTheKey()
    {
        // The F4 defect: 80 px past the key's edge is on the neighbour, still on the keyboard. It
        // must not re-arm the fired key (old 120 ms rule), and the paused second dwell is only
        // 450 ms deep after the return, so it must not type a double either.
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");

        rig.HoldAt(new PhysicalPoint(280, 100), 400);   // 80 px past key_0_0's 200 px edge
        rig.Hold("key_0_0", DwellMs);

        Check.That(rig.Fired).ContainsExactly("key_0_0");
    }

    [Fact]
    public void ALookOffTheKeyboardFor120Ms_RearmsTheFiredKey()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");

        rig.HoldAt(Nowhere, 120, inside: false);   // a real look off the keyboard
        rig.Hold("key_0_0", 900);                  // a fresh 450 ms normal dwell fires

        Check.That(rig.Fired).ContainsExactly("key_0_0", "key_0_0");
    }

    [Fact]
    public void ALookAtAnotherKeyThatFires_RearmsTheFiredKey()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");

        rig.Hold("key_0_1", DwellMs);              // the other key fires and replaces the latch
        rig.Hold("key_0_0", 900);                  // back: a fresh normal dwell fires

        Check.That(rig.Fired).ContainsExactly("key_0_0", "key_0_1", "key_0_0");
    }

    [Fact]
    public void APredictionPick_RearmsTheFiredKey()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");

        rig.Vm.HandlePredictionSelected("a");      // a bar pick re-arms the keyboard latch
        rig.Hold("key_0_0", 900);

        Check.That(rig.Fired).ContainsExactly("key_0_0", "key_0_0");
    }

    // ---- scope --------------------------------------------------------------------------------

    [Theory]
    [InlineData("enter")]
    [InlineData("tab")]
    [InlineData("backspace")]
    [InlineData("deleteWord")]
    [InlineData("space")]
    public void NonTextKeys_NeverRepeatByASecondDwell(string keyId)
    {
        var rig = new Rig();
        rig.Hold(keyId, 700);   // covers the 600 ms app-escaping floor and the 450 ms plain one
        Check.WithCustomMessage($"arrange: {keyId} must fire once").That(rig.Fired).ContainsExactly(keyId);

        // Cooldown plus twice the repeat dwell: a text key would fire at least once more.
        rig.Hold(keyId, CooldownMs + 2400);

        Check.That(rig.Fired).ContainsExactly(keyId);
    }

    [Theory]
    [InlineData(true)]    // hide
    [InlineData(false)]   // sub-layout swap
    public void AHideOrSwap_DiscardsASecondDwellInProgress(bool hide)
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");
        rig.Hold("key_0_0", 600);                      // 600 ms of second-dwell progress

        if (hide)
        {
            rig.Vm.IsVisible = true;
            rig.Vm.IsVisible = false;
            rig.Vm.IsVisible = true;
        }
        else
        {
            rig.Vm.SwitchSubLayout("numbers");
            rig.Vm.SwitchSubLayout("letters");
        }

        Check.WithCustomMessage("the latch survived the session change")
            .That(rig.Vm.RearmNeedsLeave("key_0_0")).IsFalse();

        // A sub-layout swap also arms the existing D13 anchor at the point being looked at; a
        // short visit to the neighbour clears it (a swap's landing guard, not the F4 latch). The
        // visit is under the 450 ms dwell, so it fires nothing.
        rig.Hold("key_0_1", 150);
        rig.Hold("key_0_0", 900);                  // a fresh normal dwell fires at 450 ms

        Check.That(rig.Fired).ContainsExactly("key_0_0", "key_0_0");
    }

    // ---- the visual ---------------------------------------------------------------------------

    [Fact]
    public void TheSecondDwellProgress_FillsOverRepeatDwell()
    {
        var rig = new Rig();
        rig.Fire("key_0_0");
        rig.HoldThrough(1_850_000, "key_0_0");

        rig.Hold("key_0_0", 600);

        Check.That(rig.Vm.NearestKeyId).IsEqualTo("key_0_0");
        Check.That(rig.Vm.NearestKeyProgress).IsCloseTo(600.0 / 1200.0, 0.05);
        Check.That(rig.Fired).ContainsExactly("key_0_0");
    }

    // ---- the KB7-like synthetic trace ---------------------------------------------------------

    private static readonly string[] TextIds =
        ["key_0_0", "key_0_1", "key_0_2", "key_0_3", "key_0_4", "key_0_5",
         "key_0_6", "key_0_7", "key_0_8", "key_0_9", "key_0_10", "key_0_11"];

    /// <summary>
    /// Mirrors the KB7 event mix without any owner data: 37 fires (27 text, 10 non-text), 20
    /// A..B..A returns, 3 same-key repeats (2 genuine look-aways + 1 deliberate second dwell),
    /// and three repeat attempts the new rule must NOT fire: an unintended short wobble, a lost
    /// intended double (120 ms glance), and a non-text wobble.
    /// The numbers are the Phase 1 report's A+B column.
    /// </summary>
    [Fact]
    public void TheKB7LikeSyntheticTrace_ReproducesThePhase1Numbers()
    {
        var rig = new Rig();
        new Trace(rig).Run();

        Check.That(rig.Fired).HasSize(37);
        Check.That(rig.Fired.Count(id => TextIds.Contains(id))).IsEqualTo(27);
        Check.That(Repeats(rig.Fired)).IsEqualTo(3);
        Check.That(Returns(rig.Fired)).IsEqualTo(20);
    }

    private static int Repeats(List<string> fires)
    {
        var count = 0;
        for (var i = 1; i < fires.Count; i++)
            if (fires[i] == fires[i - 1]) count++;
        return count;
    }

    private static int Returns(List<string> fires)
    {
        var last = new Dictionary<string, int>();
        var count = 0;
        for (var i = 0; i < fires.Count; i++)
        {
            if (last.TryGetValue(fires[i], out var j) && i - j > 1) count++;
            last[fires[i]] = i;
        }
        return count;
    }

    /// <summary>A scripted, owner-data-free replica of the KB7 event mix.</summary>
    private sealed class Trace
    {
        private readonly Rig _rig;

        public Trace(Rig rig) => _rig = rig;

        public void Run()
        {
            // 12 first text fires: one per distinct key.
            for (var i = 0; i < TextIds.Length; i++) _rig.FireNext(TextIds[i]);

            // Five returns; after the fourth, the unintended wobble that must not re-arm.
            _rig.FireNext(TextIds[0]);
            _rig.FireNext(TextIds[1]);
            _rig.FireNext(TextIds[2]);
            _rig.FireNext(TextIds[3]);
            _rig.Wobble(TextIds[3], offMs: 300, returnMs: DwellMs);
            _rig.FireNext(TextIds[4]);

            // Five more returns.
            for (var i = 0; i < 5; i++) _rig.FireNext(TextIds[i]);

            // The deliberate double the new design types (>= 1200 ms after the cooldown).
            _rig.FireNext(TextIds[5]);
            _rig.SecondDwell(TextIds[5]);

            // The genuine look-away double (off the keyboard >= 120 ms) that stays.
            _rig.FireNext(TextIds[6]);
            _rig.LookAway(TextIds[6], DwellMs, inside: false);

            // The other intended double from the KB7 trace: a 120 ms glance and 900 ms back. The
            // new rule suppresses the normal re-fire and 900 < 1200, so it is not typed.
            _rig.FireNext(TextIds[7]);
            _rig.Wobble(TextIds[7], offMs: 120, returnMs: 900);

            // Ten non-text fires: seven alternating returns, a suppressed non-text wobble, and a
            // genuine off-keyboard look-away double (one repeat).
            _rig.FireNext("enter", 600);
            _rig.FireNext("deleteWord");
            _rig.FireNext("enter", 600);
            _rig.FireNext("deleteWord");
            _rig.FireNext("enter", 600);
            _rig.FireNext("deleteWord");
            _rig.FireNext("enter", 600);
            _rig.FireNext("deleteWord");
            _rig.Wobble("deleteWord", offMs: 500, returnMs: DwellMs);
            _rig.FireNext("enter", 600);
            _rig.LookAway("enter", 600, inside: false);
        }
    }
}
