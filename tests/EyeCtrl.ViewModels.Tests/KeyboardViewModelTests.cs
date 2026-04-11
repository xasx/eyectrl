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

    // --- Text special key tests ---

    [Fact]
    public void HandleBackspace_RemovesLastCharAndSendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "hel";
        vm.HandleBackspace();

        Assert.Equal("he", vm.CurrentInput);
        Assert.Equal("\b", sentKey);
    }

    [Fact]
    public void HandleBackspace_DoesNothingWhenEmpty()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "";
        vm.HandleBackspace();

        Assert.Equal("", vm.CurrentInput);
        Assert.Equal("\b", sentKey);
    }

    [Fact]
    public void HandleEnter_ClearsInputAndSendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "hello";
        vm.HandleEnter();

        Assert.Equal("", vm.CurrentInput);
        Assert.Equal("\n", sentKey);
    }

    [Fact]
    public void HandleSpace_ClearsInputAndSendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.CurrentInput = "hello";
        vm.HandleSpace();

        Assert.Equal("", vm.CurrentInput);
        Assert.Equal(" ", sentKey);
    }

    [Fact]
    public void HandleTab_SendsMessage()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.HandleTab();

        Assert.Equal("\t", sentKey);
    }

    // --- Key activation tests ---

    [Fact]
    public void ActivateKey_SendsOutputAndAppendsToInput()
    {
        var vm = CreateViewModel();
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("a", shiftOutput: null);

        Assert.Equal("a", sentKey);
        Assert.Equal("a", vm.CurrentInput);
    }

    [Fact]
    public void ActivateKey_UsesShiftOutputWhenShifted()
    {
        var vm = CreateViewModel();
        vm.IsShifted = true;
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("a", shiftOutput: "A");

        Assert.Equal("A", sentKey);
        Assert.Equal("A", vm.CurrentInput);
        Assert.False(vm.IsShifted); // auto-unshift
    }

    [Fact]
    public void ActivateKey_NoAutoUnshiftWhenNoShiftOutput()
    {
        var vm = CreateViewModel();
        vm.IsShifted = true;
        string? sentKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => sentKey = msg.Key);

        vm.ActivateKey("1", shiftOutput: null);

        Assert.Equal("1", sentKey);
        Assert.True(vm.IsShifted); // no unshift for non-letter keys
    }

    // --- Prediction tests ---

    [Fact]
    public void GetSuggestions_DelegatesToPredictor()
    {
        var vm = CreateViewModel();
        var suggestions = vm.GetSuggestions();
        Assert.Empty(suggestions);
    }

    [Fact]
    public void HandlePredictionSelected_ClearsInputAndSendsMessage()
    {
        var vm = CreateViewModel();
        vm.CurrentInput = "hel";

        PredictionSelectedMessage? sentMsg = null;
        _messenger.Register<PredictionSelectedMessage>(_messenger, (_, msg) => sentMsg = msg);

        vm.HandlePredictionSelected("hello");

        Assert.Equal("", vm.CurrentInput);
        Assert.NotNull(sentMsg);
        Assert.Equal("hello", sentMsg!.Word);
        Assert.Equal(3, sentMsg.DeleteCount);
    }

    private KeyboardViewModel CreateViewModel()
    {
        return new KeyboardViewModel(_settings, _messenger);
    }
}
