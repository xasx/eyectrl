namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class KeyboardViewModel : ObservableObject,
    IRecipient<ShowKeyboardMessage>,
    IRecipient<HideKeyboardMessage>
{
    private const int EmojisPerPage = 40;

    private readonly KeyboardSettings _kbSettings;
    private readonly DwellSettings _dwellSettings;
    private readonly IMessenger _messenger;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isShifted;
    [ObservableProperty] private bool _isEmojiMode;
    [ObservableProperty] private int _emojiPage;
    [ObservableProperty] private string _activeEmojiCategory = "";
    [ObservableProperty] private string? _nearestKeyId;
    [ObservableProperty] private double _nearestKeyProgress;

    public KeyboardViewModel(KeyboardSettings kbSettings, DwellSettings dwellSettings, IMessenger messenger)
    {
        _kbSettings = kbSettings;
        _dwellSettings = dwellSettings;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    public void Receive(ShowKeyboardMessage message)
    {
        IsVisible = true;
    }

    public void Receive(HideKeyboardMessage message)
    {
        IsVisible = false;
    }

    public void ToggleShift()
    {
        IsShifted = !IsShifted;
    }

    public void ToggleEmojiMode()
    {
        IsEmojiMode = !IsEmojiMode;
        EmojiPage = 0;
    }

    public void OnKeyActivated(string key)
    {
        var actualKey = IsShifted && key.Length == 1 && char.IsLetter(key[0])
            ? key.ToUpperInvariant()
            : key;

        _messenger.Send(new KeyActivatedMessage(actualKey));

        // Auto-unshift after typing a letter (like a phone keyboard)
        if (IsShifted && key.Length == 1 && char.IsLetter(key[0]))
            IsShifted = false;
    }

    public int CalculateEmojiPageCount(int totalEmojis)
    {
        return (int)Math.Ceiling((double)totalEmojis / EmojisPerPage);
    }

    public void NextEmojiPage(int totalEmojis)
    {
        var maxPage = CalculateEmojiPageCount(totalEmojis) - 1;
        EmojiPage = Math.Min(EmojiPage + 1, maxPage);
    }

    public void PreviousEmojiPage()
    {
        EmojiPage = Math.Max(0, EmojiPage - 1);
    }
}
