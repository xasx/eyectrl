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
        Dictionary<string, PhysicalPoint> keys, long throughUs)
    {
        for (long t = 50_000; t <= throughUs; t += 50_000)
            vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, t);
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
        // 1500 + 0.50×1500 = 2250, which the provider's union clamp [100, 2000] caps at 2000. The
        // keyboard's own [100, 1500] clamp must hold the line at 1500 — without it, key dwell
        // would silently wait until 2000.
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
}
