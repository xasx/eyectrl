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
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 450_000);
        Check.That(completed).IsTrue(); // 450 ms >= effective 700−0.40×700 = 420, not the 700 baseline
    }

    [Fact]
    public void ProviderChange_MidDwell_ReEvaluatesWithoutReset()
    {
        var (vm, provider, _) = Make();
        vm.Configure(keyDwellMs: 700, cooldownMs: 0);
        var keys = Keys();
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 350_000); // half of 700

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
        vm.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, 650_000);

        Check.That(vm.NearestKeyProgress).IsCloseTo(650.0 / 700.0, 0.01); // still accumulating vs 700
    }
}
