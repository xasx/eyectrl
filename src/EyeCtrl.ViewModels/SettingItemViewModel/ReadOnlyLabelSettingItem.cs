namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ReadOnlyLabelSettingItem : SettingItemViewModel
{
    [ObservableProperty] private string? _displayText;

    public ReadOnlyLabelSettingItem(string key, string label, string? displayText = null)
        : base(key, label)
    {
        _displayText = displayText;
    }
}
