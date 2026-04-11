namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class KeyboardViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void ShowsKeyboard_OnShowMessage()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new ShowKeyboardMessage());
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void HidesKeyboard_OnHideMessage()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        _messenger.Send(new ShowKeyboardMessage());
        _messenger.Send(new HideKeyboardMessage());
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void IsShifted_CanBeToggled()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsShifted);

        vm.IsShifted = true;
        Assert.True(vm.IsShifted);

        vm.IsShifted = false;
        Assert.False(vm.IsShifted);
    }

    [Fact]
    public void ProcessKeyGaze_FindsNearestKey()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
            ["key_0_1"] = new(200, 100),
        };

        // Gaze at (110, 105) — closest to key_0_0
        vm.ProcessKeyGaze(positions, new PhysicalPoint(110, 105), isGazeInsideWindow: true, timestampUs: 0);

        Assert.Equal("key_0_0", vm.NearestKeyId);
    }

    [Fact]
    public void ProcessKeyGaze_AccumulatesDwellProgress()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        // First frame: enter key
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        Assert.Equal(0.0, vm.NearestKeyProgress); // just entered, no progress yet

        // Second frame: 250ms later = 50% progress
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 250_000);
        Assert.Equal(0.5, vm.NearestKeyProgress, precision: 2);
    }

    [Fact]
    public void ProcessKeyGaze_FiresCallbackOnDwellComplete()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        string? activatedKeyId = null;
        vm.KeyDwellCompleted += id => activatedKeyId = id;

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 500_000);

        Assert.Equal("key_0_0", activatedKeyId);
    }

    [Fact]
    public void ProcessKeyGaze_CooldownPreventsReactivation()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        string? activatedKeyId = null;
        int activationCount = 0;
        vm.KeyDwellCompleted += id => { activatedKeyId = id; activationCount++; };

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        // Activate
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 500_000);
        Assert.Equal(1, activationCount);

        // During cooldown (100ms after activation, cooldown is 200ms)
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 600_000);
        Assert.Null(vm.NearestKeyId); // reset during cooldown

        // After cooldown (250ms after activation)
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 750_000);
        Assert.Equal("key_0_0", vm.NearestKeyId); // tracking again
    }

    [Fact]
    public void ProcessKeyGaze_ResetsWhenGazeOutsideWindow()
    {
        var vm = CreateViewModel();
        vm.Configure(keyDwellMs: 500, cooldownMs: 200);

        var positions = new Dictionary<string, PhysicalPoint>
        {
            ["key_0_0"] = new(100, 100),
        };

        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), true, 0);
        Assert.Equal("key_0_0", vm.NearestKeyId);

        // Gaze moves outside window
        vm.ProcessKeyGaze(positions, new PhysicalPoint(100, 100), isGazeInsideWindow: false, 100_000);
        Assert.Null(vm.NearestKeyId);
    }

    private KeyboardViewModel CreateViewModel()
    {
        return new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
    }
}
