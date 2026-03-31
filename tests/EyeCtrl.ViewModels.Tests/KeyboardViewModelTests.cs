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
    public void TogglesShift_OnShiftActivation()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        Assert.False(vm.IsShifted);

        vm.ToggleShift();
        Assert.True(vm.IsShifted);

        vm.ToggleShift();
        Assert.False(vm.IsShifted);
    }

    [Fact]
    public void SendsKeyActivated_OnKeyDwell()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        string? activatedKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => activatedKey = msg.Key);

        vm.OnKeyActivated("a");

        Assert.Equal("a", activatedKey);
    }

    [Fact]
    public void SendsShiftedKey_WhenShifted()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        vm.ToggleShift();

        string? activatedKey = null;
        _messenger.Register<KeyActivatedMessage>(_messenger, (_, msg) => activatedKey = msg.Key);

        vm.OnKeyActivated("a");

        Assert.Equal("A", activatedKey);
    }

    [Fact]
    public void EmojiMode_TogglesOnAndOff()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        Assert.False(vm.IsEmojiMode);

        vm.ToggleEmojiMode();
        Assert.True(vm.IsEmojiMode);

        vm.ToggleEmojiMode();
        Assert.False(vm.IsEmojiMode);
    }

    [Fact]
    public void EmojiPagination_CalculatesCorrectPageCount()
    {
        var vm = new KeyboardViewModel(_settings, _messenger);
        // Simulate 85 emojis in a category with 40 per page
        var pageCount = vm.CalculateEmojiPageCount(85);
        Assert.Equal(3, pageCount); // ceil(85/40) = 3
    }
}
