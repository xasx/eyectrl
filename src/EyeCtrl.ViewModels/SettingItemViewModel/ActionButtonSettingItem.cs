namespace EyeCtrl.ViewModels;

public class ActionButtonSettingItem : SettingItemViewModel
{
    public Action? OnActivate { get; }

    public ActionButtonSettingItem(string key, string label, Action? onActivate)
        : base(key, label)
    {
        OnActivate = onActivate;
    }
}
