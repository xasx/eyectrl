namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

public class KeyboardEffectiveDwellTests
{
    private static (KeyboardViewModel Vm, EffectiveSettingsProvider Provider, EyeCtrlSettings Settings) Make()
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        return (new KeyboardViewModel(settings, new WeakReferenceMessenger(), sink: null, effectiveSettings: provider),
            provider, settings);
    }

    private static Dictionary<string, PhysicalPoint> Keys() =>
        new() { ["a"] = new PhysicalPoint(100, 100) };

    /// <summary>
    /// Feeds a continuous stream (one sample per 50 ms) up to <paramref name="throughUs"/>. The
    /// tracker restarts a dwell whenever the stream stops for more than 150 ms (the F3 gap clamp),
    /// so sparse sample pairs no longer model a continuous hold.
    /// </summary>
    private static void HoldGaze(KeyboardViewModel vm,
        Dictionary<string, PhysicalPoint> keys, long throughUs) =>
        HoldGazeAt(vm, keys, new PhysicalPoint(100, 100), throughUs);

    private static void HoldGazeAt(KeyboardViewModel vm,
        Dictionary<string, PhysicalPoint> keys, PhysicalPoint at, long throughUs)
    {
        for (long t = 50_000; t <= throughUs; t += 50_000)
            vm.ProcessKeyGaze(keys, at, true, t);
    }

    [Fact]
    public void Configure_ComposesTheEffectiveDwell()
    {
        var (vm, provider, _) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);

        vm.Configure(keyDwellMs: 700, cooldownMs: 0);

        bool completed = false;
        vm.KeyDwellCompleted += _ => completed = true;
        var keys = Keys();
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        HoldGaze(vm, keys, 450_000);
        Check.That(completed).IsTrue(); // 450 ms >= effective 700−0.40×700 = 420, not the 700 baseline
    }

    [Fact]
    public void ProviderChange_MidDwell_ReEvaluatesWithoutReset()
    {
        var (vm, provider, _) = Make();
        vm.Configure(keyDwellMs: 700, cooldownMs: 0);
        var keys = Keys();
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        HoldGaze(vm, keys, 350_000); // half of 700

        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0); // → 420

        bool completed = false;
        vm.KeyDwellCompleted += _ => completed = true;
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 430_000); // 430 >= 420 → fires
        Check.That(completed).IsTrue();
    }

    [Fact]
    public void DisabledOrNoProvider_UsesTheBaseline()
    {
        var settings = new EyeCtrlSettings();
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger()); // no provider
        vm.Configure(keyDwellMs: 700, cooldownMs: 0);
        var keys = Keys();

        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        HoldGaze(vm, keys, 650_000);

        Check.That(vm.NearestKeyProgress).IsCloseTo(650.0 / 700.0, 0.01); // still accumulating vs 700
    }

    [Fact]
    public void FullTighteningFill_AtKeyboardGuardCeiling_ClampsAt1500()
    {
        // Baseline 1500 is KeyboardKeyDwellMs's own guard max; a full tightening fill asks for
        // 1500 + 0.50×1500 = 2250, which the provider's [100, 3000] union admits (the keyboard
        // family's range is the narrower one). The keyboard's own [300, 1500] clamp must hold the
        // line at 1500 — without it, key dwell would silently wait until 2250.
        var (vm, provider, _) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.0, tighteningFill: 1.0);
        vm.Configure(keyDwellMs: 1500, cooldownMs: 0);

        bool completed = false;
        vm.KeyDwellCompleted += _ => completed = true;
        var keys = Keys();

        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        HoldGaze(vm, keys, 1450_000);
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 1490_000);
        Check.That(completed).IsFalse(); // 1490 < 1500 — a 2000 effective would not have fired either, ...

        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 1500_000);
        Check.That(completed).IsTrue(); // ...but only the clamped-to-1500 value fires AT 1500
    }

    // --- the app-escaping floor (KeyboardSpecialActions.AppEscapingPolicyFloorMs) ---

    private static readonly PhysicalPoint LetterAt = new(100, 100);
    private static readonly PhysicalPoint EscapeAt = new(400, 100);
    private static readonly PhysicalPoint NavigationAt = new(700, 100);

    private static Dictionary<string, PhysicalPoint> LetterEscapeAndNavigation() => new()
    {
        ["a"] = LetterAt,
        ["enter"] = EscapeAt,
        ["key:ArrowUp"] = NavigationAt,
    };

    /// <summary>
    /// Maximum forgiving deflection: a full momentary fill plus the maximum learned fill. The
    /// composition clamps the sum to the forgiving band edge, so with a 450 ms baseline the knob
    /// asks for 450 − 0.40 × 450 = 270 ms. The APPLIED value is the family floor's business: the
    /// keyboard clamp (<c>AdaptationDwellRanges.KeyboardMinDwellMs</c> = 300) lifts 270 back to 300,
    /// and for baselines from 500 ms up the ask itself is at or above 300 and applies unchanged.
    /// Used to prove the escaping floor holds at the knob's extreme, not merely at a comfortable
    /// position — including when the extreme is the family floor.
    /// </summary>
    private static void DriveToMaximumForgivingDeflection(EffectiveSettingsProvider provider)
    {
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
    }

    /// <summary>
    /// A continuous 50 ms stream at <paramref name="at"/> from <paramref name="firstUs"/> through
    /// <paramref name="throughUs"/> inclusive. The F3 gap clamp restarts a dwell on any &gt;150 ms
    /// silence, so a hold has to be fed sample by sample.
    /// </summary>
    private static void HoldAt(KeyboardViewModel vm, Dictionary<string, PhysicalPoint> keys,
        PhysicalPoint at, long firstUs, long throughUs)
    {
        for (long t = firstUs; t <= throughUs; t += 50_000)
            vm.ProcessKeyGaze(keys, at, true, t);
    }

    [Fact]
    public void EscapingKey_HoldsThePolicyFloor_WhileTheLetterShortens()
    {
        var (vm, provider, _) = Make();
        DriveToMaximumForgivingDeflection(provider);
        vm.Configure(keyDwellMs: 450, cooldownMs: 0);

        var keys = LetterEscapeAndNavigation();
        var fired = new List<string>();
        vm.KeyDwellCompleted += fired.Add;

        // Positive control, same run, same provider state: the composed ask is 270 but the family
        // floor (300) is the applied value, so the letter must NOT fire at 280 and must fire by
        // 300. The 280 probe is what distinguishes the clamp from the raw composition — without it
        // the letter fires at 270 and this run goes red.
        vm.ProcessKeyGaze(keys, LetterAt, true, 0);
        HoldAt(vm, keys, LetterAt, 50_000, 250_000);
        Check.That(fired).Not.Contains("a"); // 250 < the 270 ask
        vm.ProcessKeyGaze(keys, LetterAt, true, 280_000);
        Check.WithCustomMessage(
                "The letter fired before the 300 ms family floor: the adaptive clamp is not applied, " +
                "so the forgiving knob shortened the keyboard dwell into the blink-credit window " +
                "(the composed ask at this baseline is 270).")
            .That(fired).Not.Contains("a");
        vm.ProcessKeyGaze(keys, LetterAt, true, 300_000);
        Check.WithCustomMessage(
                "The letter did not follow the composed shorter dwell (clamped to the family floor), " +
                "so the pipeline was not live and the escaping assertion below would be vacuous.")
            .That(fired).Contains("a");

        // The escaping key must NOT follow the shortened value: the floor-broken mutations fire it
        // at 270 (exemption gone) or 450 (constant reverted to 0), both during the hold below. It
        // holds the 600 ms absolute policy floor.
        vm.ProcessKeyGaze(keys, EscapeAt, true, 500_000); // latch
        HoldAt(vm, keys, EscapeAt, 550_000, 1_050_000);   // up to 550 ms elapsed
        Check.WithCustomMessage(
                "An app-escaping key fired below its floor under maximum forgiving deflection. The " +
                "keyboard is shortening the keys whose misfire the user cannot undo, fastest for " +
                "whoever is struggling most — the exact inversion this gate exists to stop.")
            .That(fired).Not.Contains("enter");
        Check.WithCustomMessage(
                "The progress ring reads full while the escaping key has not fired — the ring is " +
                "tracking the shortened composed value instead of the floor, so the user sees a " +
                "completed dwell and no activation, on the keys they must search for rather than " +
                "touch-type.")
            .That(vm.NearestKeyProgress).IsStrictlyLessThan(1.0); // 550 ms of a 600 ms floor = 0.917
        vm.ProcessKeyGaze(keys, EscapeAt, true, 1_100_000); // 600 ms elapsed
        Check.WithCustomMessage(
                "The escaping key never fired, so the floor became a dead key. The floor is the " +
                "600 ms absolute policy value.")
            .That(fired).Contains("enter");
    }

    [Fact]
    public void EscapingKey_DoesNotShortenAHigherConfiguredBaseline()
    {
        // KeyboardKeyDwellMs is user-settable up to 1500. A floor that substitutes the policy value
        // for the configured baseline (`return AppEscapingPolicyFloorMs`, or max(composed, policy)
        // with no baseline term) would SHORTEN this user's setting — and with the policy now at 600
        // this is the test that proves max(900, 600) = 900. The composition is
        // max(composed, max(baseline, policy)); composed here is 900 − 360 = 540. Do not "simplify"
        // it to the policy constant.
        var (vm, provider, _) = Make();
        DriveToMaximumForgivingDeflection(provider);
        vm.Configure(keyDwellMs: 900, cooldownMs: 0);

        var keys = LetterEscapeAndNavigation();
        var fired = new List<string>();
        vm.KeyDwellCompleted += fired.Add;

        // Positive control: the letter moved to 540 (fires between 500 and 550), so the knob is live.
        vm.ProcessKeyGaze(keys, LetterAt, true, 0);
        HoldAt(vm, keys, LetterAt, 50_000, 500_000);
        Check.That(fired).Not.Contains("a"); // 500 < 540
        vm.ProcessKeyGaze(keys, LetterAt, true, 550_000);
        Check.WithCustomMessage("The letter did not follow the composed 540 ms value.")
            .That(fired).Contains("a");

        // The escaping key holds the USER'S 900 ms, not the composed 540 and not a 600 policy value.
        vm.ProcessKeyGaze(keys, EscapeAt, true, 600_000); // latch
        HoldAt(vm, keys, EscapeAt, 650_000, 1_450_000);   // up to 850 ms elapsed
        Check.WithCustomMessage(
                "The escaping key fired below the user's configured 900 ms — the floor is shortening " +
                "the user's own setting or substituting a policy value for it.")
            .That(fired).Not.Contains("enter");
        vm.ProcessKeyGaze(keys, EscapeAt, true, 1_500_000); // 900 ms elapsed
        Check.WithCustomMessage("The escaping key never fired, so the floor became a dead key.")
            .That(fired).Contains("enter");
    }

    [Fact]
    public void EscapingFloor_ExceedsASmallConfiguredBaseline_Intentionally()
    {
        // The smallest keyboard baseline the guards admit is 300. With the 600 policy floor, an
        // escaping key then sits at 600 while the letters sit at 300 — a 2x spread inside one
        // keyboard. Intended: the floor is absolute, and nothing clamps it back toward the baseline
        // (the keyboard guard clamp is [300, 1500], and 600 is inside it). This also pins the
        // gate's discharged second clause: a lowered global key dwell no longer drags the escaping
        // keys down.
        var (vm, provider, _) = Make();
        DriveToMaximumForgivingDeflection(provider);
        vm.Configure(keyDwellMs: 300, cooldownMs: 0);

        var keys = LetterEscapeAndNavigation();
        var fired = new List<string>();
        vm.KeyDwellCompleted += fired.Add;

        // Positive control: the letter sits at the 300 ms keyboard minimum (the composed ask is
        // 180; the family floor lifts it back to 300), so the knob is live and the spread is real.
        vm.ProcessKeyGaze(keys, LetterAt, true, 0);
        HoldAt(vm, keys, LetterAt, 50_000, 250_000);
        Check.That(fired).Not.Contains("a"); // 250 < 300
        vm.ProcessKeyGaze(keys, LetterAt, true, 300_000);
        Check.WithCustomMessage("The letter did not fire at its 300 ms baseline.")
            .That(fired).Contains("a");

        vm.ProcessKeyGaze(keys, EscapeAt, true, 400_000); // latch
        HoldAt(vm, keys, EscapeAt, 450_000, 950_000);     // up to 550 ms elapsed
        Check.WithCustomMessage(
                "The escaping key did not hold 600 ms against a 300 ms baseline — the floor is " +
                "tracking the baseline again, so lowering the global key dwell shortens the keys " +
                "this gate exists to protect.")
            .That(fired).Not.Contains("enter");
        vm.ProcessKeyGaze(keys, EscapeAt, true, 1_000_000); // 600 ms elapsed
        Check.WithCustomMessage("The escaping key never fired at the 600 ms floor.")
            .That(fired).Contains("enter");
    }

    [Fact]
    public void EscapingKey_FollowsTightening_AboveTheFloor()
    {
        // The floor is a LOWER BOUND, not a replacement. When accidental-activation tightening raises
        // the composed dwell, escaping keys must rise with it: a floor written as `return floor`
        // leaves enter capped at 600 while the letters rise to 675 — the most consequential keys
        // become the easiest to hit, the same category error in the opposite direction. Full
        // tightening fill on a 450 ms baseline composes to 450 + 0.50×450 = 675.
        var (vm, provider, _) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.0, 1.0);
        vm.Configure(keyDwellMs: 450, cooldownMs: 0);

        var keys = LetterEscapeAndNavigation();
        var fired = new List<string>();
        vm.KeyDwellCompleted += fired.Add;

        // Positive control, same run: the letter follows the tightening fill to 675, so the escape
        // assertion below is measured on a live pipeline rather than an inert one.
        vm.ProcessKeyGaze(keys, LetterAt, true, 0);
        HoldAt(vm, keys, LetterAt, 50_000, 650_000); // 650 < 675
        Check.That(fired).Not.Contains("a");
        vm.ProcessKeyGaze(keys, LetterAt, true, 700_000); // 700 >= 675
        Check.WithCustomMessage("The letter did not follow the tightening fill to 675 ms.")
            .That(fired).Contains("a");

        // The escaping key must follow the SAME raised value — the floor bounds it from below only.
        vm.ProcessKeyGaze(keys, EscapeAt, true, 750_000); // latch
        HoldAt(vm, keys, EscapeAt, 800_000, 1_400_000);   // up to 650 ms elapsed
        Check.WithCustomMessage(
                "The escaping key did not follow the tightening fill (it fired before 675 ms, or the " +
                "composition dropped the tightening sub-band). A floor that replaces the composed " +
                "value instead of bounding it makes the most consequential keys the easiest to hit " +
                "while every letter slows down.")
            .That(fired).Not.Contains("enter");
        vm.ProcessKeyGaze(keys, EscapeAt, true, 1_425_000); // 675 ms elapsed
        Check.WithCustomMessage("The escaping key never fired at the tightened value.")
            .That(fired).Contains("enter");
    }

    [Fact]
    public void NavigationKeys_StayFullyAdaptive_NotPartOfTheEscapingClass()
    {
        // Arrows/Home/End/Page keys are pressed in runs and a wrong one is undone by one opposing
        // press. They must follow the shortened value down to the family floor (300 ms) — if the
        // class ever widens to "all key: actions", navigation silently loses the forgiving response
        // where speed matters most.
        var (vm, provider, _) = Make();
        DriveToMaximumForgivingDeflection(provider);
        vm.Configure(keyDwellMs: 450, cooldownMs: 0);

        var keys = LetterEscapeAndNavigation();
        var fired = new List<string>();
        vm.KeyDwellCompleted += fired.Add;

        vm.ProcessKeyGaze(keys, NavigationAt, true, 0);
        HoldAt(vm, keys, NavigationAt, 50_000, 250_000);
        Check.That(fired).Not.Contains("key:ArrowUp"); // 250 < 300
        vm.ProcessKeyGaze(keys, NavigationAt, true, 300_000);
        Check.WithCustomMessage(
                "A navigation key did not follow the composed shorter dwell — it is being treated as " +
                "app-escaping, which taxes every arrow run for no safety gain.")
            .That(fired).Contains("key:ArrowUp");
    }

    [Fact]
    public void HandEditedFileBelowTheFloor_LoadsClamped_AndACreditedStallCannotCompleteTheKey()
    {
        // The whole path a hand-editor actually walks: the file on disk holds the old minimum (100,
        // legal when it was written), SettingsService.Load must clamp it to the family floor rather
        // than discard the file, and the keyboard built from what Load returns must not be
        // completable by a single 150 ms silence — the configuration the credit bound used to hand
        // to a blink. The behavioural probes come FIRST so a full floor revert reds on the stall,
        // not on a configuration assertion; the loaded-value assertion that follows catches the
        // partial revert where Load's setter throw is answered with all-defaults (450, safe but the
        // user's file is gone, which is the failure the raw-JSON clamp exists to prevent).
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_keydwell_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, """
            {
                "dwell": { "keyboardKeyDwellMs": 100, "activationButtonDwellMs": 800 }
            }
            """);
            var loaded = new SettingsService(tempPath).Load();
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(800); // the rest of the file survives

            loaded.Adaptation.Enabled = true;
            var provider = new EffectiveSettingsProvider(loaded.Adaptation);
            var vm = new KeyboardViewModel(loaded, new WeakReferenceMessenger(),
                sink: null, effectiveSettings: provider);
            DriveToMaximumForgivingDeflection(provider);
            vm.Configure(keyDwellMs: loaded.Dwell.KeyboardKeyDwellMs, cooldownMs: 0);

            var keys = Keys();
            bool completed = false;
            vm.KeyDwellCompleted += _ => completed = true;

            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
            // 150 ms is at this tracker's credit bound, and it CREDITS it as held gaze (it restarts
            // only on a gap strictly greater than MaxContinuousSampleGapUs; the shared detector's
            // operator is the opposite, and its boundary is pinned in KeyboardDwellFloorRoutingTests).
            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 150_000);
            Check.WithCustomMessage(
                    "A single credited 150 ms stall completed a keyboard dwell: the loaded baseline " +
                    "or its adaptive composition reached the blink-credit zone.")
                .That(completed).IsFalse();

            // Past the composed ask (180 at this baseline) and the old 100 ms value, before the floor.
            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 200_000);
            Check.WithCustomMessage(
                    "The dwell completed before the 300 ms family floor, so the keyboard was " +
                    "reachable inside the blink-credit window.")
                .That(completed).IsFalse();

            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 250_000);
            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 300_000);
            Check.WithCustomMessage("The key never fired, so the assertions above were vacuous.")
                .That(completed).IsTrue();

            Check.WithCustomMessage(
                    "A below-floor keyboard dwell loaded from disk was not lifted to the family " +
                    "floor — the clamp is missing (whole-file default wipe) or the floor moved.")
                .That(loaded.Dwell.KeyboardKeyDwellMs).IsEqualTo(300);
        }
        finally { File.Delete(tempPath); }
    }
}
