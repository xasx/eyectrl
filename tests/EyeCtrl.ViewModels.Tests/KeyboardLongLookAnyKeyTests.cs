namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// F8: the long look (F4) re-fires ANY key, not only the text and deletion keys. Owner decision
/// 18.11 (KB11): "I want to be able to apply any suggestion without looking away. This holds for all
/// keys. That is the expected behavior" — every key except the page switches (123, #+=, Fn, ABC, the
/// accents and emoji page keys) and the keyboard's close/move buttons.
///
/// <para>
/// The F4 mechanism is unchanged and shared: the latched key's second dwell is
/// <see cref="KeyboardLongLook.RepeatDwellUs"/> of ON-KEY time after the post-fire cooldown, an
/// excursion of at most <see cref="KeyboardLongLook.RepeatExcursionToleranceUs"/> pauses it, a longer
/// one resets it, and a real leave (120 ms off the keyboard) re-arms the key normally. The comment on
/// the excluded classes lives in <see cref="KeyboardLongLookTests"/>; this file proves the key path
/// behaves that way.
/// </para>
/// </summary>
public class KeyboardLongLookAnyKeyTests
{
    private const int DwellMs = 450;
    private const int CooldownMs = 400;
    private const int StepMs = 30;

    private static readonly string[] Ids =
    [
        "key_0_0", "key_0_1", "key_0_2", "key_0_3",
        "enter", "tab", "space", "backspace", "deleteWord",
        "modifier:shift", "modifier:ctrl", "modifier:alt", "modifier:win", "toggle:capslock",
        "key:ArrowUp",
        "switchSub:numbers", "switchSub:letters", "Keyboard_close", "Keyboard_flip",
        "emoji_3", "special_emojiPage:next",
    ];

    private static readonly Dictionary<string, PhysicalPoint> Positions =
        Ids.Select((id, i) => (id, p: new PhysicalPoint(100 + 220 * i, 100)))
            .ToDictionary(t => t.id, t => t.p);

    private static readonly Dictionary<string, PhysicalRect> Boxes =
        Ids.Select((id, i) => (id, r: new PhysicalRect(220 * i, 0, 200, 200)))
            .ToDictionary(t => t.id, t => t.r);

    /// <summary>Far outside the key area: an actual look off the keyboard.</summary>
    private static readonly PhysicalPoint OffTheKeyboard = new(20000, 20000);

    /// <summary>80 px above the top row: inside the key area's reach, on no key (an excursion that
    /// must pause or reset the clock without firing a neighbour).</summary>
    private static readonly PhysicalPoint AboveTheKeys = new(100, -80);

    private static KeyboardLayout Layout() => new()
    {
        Name = "LongLook",
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
                        new KeyDefinition { Label = "↵", SpecialAction = "enter" },
                        new KeyDefinition { Label = "⇥", SpecialAction = "tab" },
                        new KeyDefinition { Label = "_", SpecialAction = "space" },
                        new KeyDefinition { Label = "⌫", SpecialAction = "backspace" },
                        new KeyDefinition { Label = "Aa", SpecialAction = "deleteWord" },
                        new KeyDefinition { Label = "⇧", SpecialAction = "modifier:shift" },
                        new KeyDefinition { Label = "Strg", SpecialAction = "modifier:ctrl" },
                        new KeyDefinition { Label = "Alt", SpecialAction = "modifier:alt" },
                        new KeyDefinition { Label = "Win", SpecialAction = "modifier:win" },
                        new KeyDefinition { Label = "⬆", SpecialAction = "toggle:capslock" },
                        new KeyDefinition { Label = "↑", SpecialAction = "key:ArrowUp" },
                        new KeyDefinition { Label = "123", SpecialAction = "switchSub:numbers" },
                        new KeyDefinition { Label = "ABC", SpecialAction = "switchSub:letters" },
                        // The edge pair as ordinary grid keys. Production keeps them on the edge
                        // bar, but the rig needs them in the layout or IsRepeatableKey returns
                        // false before the close/move rule is asked and the cases are vacuous
                        // (review 01 F3).
                        new KeyDefinition { Label = "✕", SpecialAction = "Keyboard_close" },
                        new KeyDefinition { Label = "⇄", SpecialAction = "Keyboard_flip" },
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

        public long LastFireTs { get; private set; }

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

        public void HoldAt(PhysicalPoint at, int ms, bool inside = true)
        {
            var until = Ts + (ms * 1000L);
            while (Ts <= until)
            {
                Vm.ProcessKeyGaze(Positions, at, inside, Ts, Boxes);
                Ts += StepMs * 1000L;
            }
        }

        /// <summary>Fires the key once with the requested hold, then rests a sample past the fire.</summary>
        public void Fire(string keyId, int holdMs)
        {
            Hold(keyId, holdMs);
            Check.WithCustomMessage($"arrange: {keyId} must fire once").That(Fired).ContainsExactly(keyId);
        }

        /// <summary>The key just fired: hold through the cooldown and complete <paramref name="ms"/>
        /// of the second dwell.</summary>
        public void SecondDwell(string keyId, int ms)
        {
            var cooldownEnd = LastFireTs + (CooldownMs * 1000L);
            while (Ts <= cooldownEnd)
                Hold(keyId, 0);
            Hold(keyId, ms);
        }

        /// <summary>
        /// The key just fired: anchor the second-dwell clock exactly at the cooldown's end and
        /// credit exactly <paramref name="ms"/> of continuous on-key time (a sample at the anchor,
        /// then every 30 ms, the last one at anchor + ms). The 30 ms cadence alone cannot land on
        /// an exact millisecond, so the boundary tests drive their own timestamps.
        /// </summary>
        public void SecondDwellExactly(string keyId, int ms)
        {
            var anchor = LastFireTs + (CooldownMs * 1000L);
            One(keyId, anchor);
            for (var t = anchor + (StepMs * 1000L); t < anchor + (ms * 1000L); t += StepMs * 1000L)
                One(keyId, t);
            One(keyId, anchor + (ms * 1000L));
        }

        private void One(string keyId, long ts)
        {
            Vm.ProcessKeyGaze(Positions, Positions[keyId], true, ts, Boxes);
            Ts = ts + (StepMs * 1000L);
        }
    }

    // ---- every key repeats ---------------------------------------------------------------------

    [Theory]
    [InlineData("enter", 700)]              // the 600 ms app-escaping floor
    [InlineData("tab", 700)]
    [InlineData("space", 450)]
    [InlineData("modifier:shift", 450)]
    [InlineData("modifier:ctrl", 450)]
    [InlineData("modifier:alt", 450)]
    [InlineData("modifier:win", 450)]
    [InlineData("toggle:capslock", 450)]    // Caps is a modifier to the owner
    [InlineData("key:ArrowUp", 450)]        // an Fn-page action
    [InlineData("backspace", 450)]
    [InlineData("deleteWord", 450)]
    public void TheLongLook_RepeatsTheKey(string keyId, int firstHoldMs)
    {
        var rig = new Rig();
        rig.Fire(keyId, firstHoldMs);

        rig.SecondDwell(keyId, 1250);

        Check.That(rig.Fired).ContainsExactly(keyId, keyId);
    }

    [Theory]
    [InlineData("enter", 700)]
    [InlineData("space", 450)]
    [InlineData("tab", 700)]
    [InlineData("modifier:shift", 450)]
    [InlineData("key:ArrowUp", 450)]
    public void ARestShorterThanTheLongLook_DoesNotRepeatTheKey(string keyId, int firstHoldMs)
    {
        var rig = new Rig();
        rig.Fire(keyId, firstHoldMs);

        rig.SecondDwell(keyId, 900);   // under KeyboardLongLook.RepeatDwellUs

        Check.That(rig.Fired).ContainsExactly(keyId);
    }

    [Theory]
    [InlineData("key_0_0", 450)]   // a letter
    [InlineData("enter", 700)]     // the 600 ms app-escaping floor
    public void TheLongLookBoundary_999MsDoesNotRepeat(string keyId, int firstHoldMs)
    {
        var rig = new Rig();
        rig.Fire(keyId, firstHoldMs);

        rig.SecondDwellExactly(keyId, 999);

        Check.That(rig.Fired).ContainsExactly(keyId);
    }

    [Theory]
    [InlineData("key_0_0", 450)]   // a letter
    [InlineData("enter", 700)]     // the 600 ms app-escaping floor
    public void TheLongLookBoundary_1000MsRepeats(string keyId, int firstHoldMs)
    {
        var rig = new Rig();
        rig.Fire(keyId, firstHoldMs);

        rig.SecondDwellExactly(keyId, 1000);

        Check.That(rig.Fired).ContainsExactly(keyId, keyId);
    }

    [Theory]
    [InlineData("switchSub:numbers")]   // 123
    [InlineData("switchSub:letters")]   // ABC
    [InlineData("Keyboard_close")]      // the keyboard's edge pair
    [InlineData("Keyboard_flip")]
    public void APageSwitchOrCloseMove_DoesNotRepeatByTheLongLook(string keyId)
    {
        var rig = new Rig();
        rig.Fire(keyId, DwellMs);

        // Cooldown plus more than TWICE the long look: a repeatable key would fire at least twice.
        rig.SecondDwell(keyId, CooldownMs + 2500);

        Check.That(rig.Fired).ContainsExactly(keyId);
    }

    // ---- emoji surface --------------------------------------------------------------------------

    [Fact]
    public void AnEmojiCell_RepeatsByTheLongLook()
    {
        var rig = new Rig();
        rig.Vm.IsEmojiMode = true;
        rig.Fire("emoji_3", DwellMs);

        rig.SecondDwell("emoji_3", 1250);

        Check.That(rig.Fired).ContainsExactly("emoji_3", "emoji_3");
    }

    [Fact]
    public void AnEmojiPageKey_DoesNotRepeatByTheLongLook()
    {
        var rig = new Rig();
        rig.Vm.IsEmojiMode = true;
        rig.Fire("special_emojiPage:next", DwellMs);

        rig.SecondDwell("special_emojiPage:next", CooldownMs + 2500);

        Check.That(rig.Fired).ContainsExactly("special_emojiPage:next");
    }

    // ---- the F4 behaviours still hold for the newly repeatable keys -----------------------------

    [Fact]
    public void AShortExcursion_PausesTheEnterLongLook()
    {
        var rig = new Rig();
        rig.Fire("enter", 700);
        rig.SecondDwell("enter", 600);

        // 300 ms off the key but still on the keyboard: within the 400 ms tolerance.
        rig.HoldAt(AboveTheKeys, 300);
        rig.Hold("enter", 700);

        Check.That(rig.Fired).ContainsExactly("enter", "enter");
    }

    [Fact]
    public void ALongExcursion_ResetsTheEnterLongLook()
    {
        var rig = new Rig();
        rig.Fire("enter", 700);
        rig.SecondDwell("enter", 600);

        rig.HoldAt(AboveTheKeys, 500);   // beyond the 400 ms tolerance: the progress resets
        rig.Hold("enter", 700);          // only ~700 of the new 1000 ms

        Check.That(rig.Fired).ContainsExactly("enter");
    }

    [Fact]
    public void ALookOffTheKeyboard_RearmsEnterNormally()
    {
        // The leave rule is unchanged: 120 ms off the keyboard clears the latch, so the key's normal
        // 600 ms dwell fires it again — much sooner than the shared 1000 ms long look.
        var rig = new Rig();
        rig.Fire("enter", 700);
        rig.SecondDwell("enter", 300);

        rig.HoldAt(OffTheKeyboard, 150, inside: false);
        rig.Hold("enter", 700);

        Check.That(rig.Fired).ContainsExactly("enter", "enter");
    }

    [Fact]
    public void APredictionPick_RearmsARepeatableKey()
    {
        var rig = new Rig();
        rig.Fire("space", 450);
        rig.SecondDwell("space", 300);

        rig.Vm.HandlePredictionSelected("wort");
        rig.Hold("space", 600);

        Check.That(rig.Fired).ContainsExactly("space", "space");
    }
}
