namespace EyeCtrl.ViewModels;

public class SettingsTabViewModel
{
    public string Name { get; }
    public string Symbol { get; }
    public List<SettingItemViewModel> Items { get; }

    public SettingsTabViewModel(string name, string symbol, List<SettingItemViewModel> items)
    {
        Name = name;
        Symbol = symbol;
        Items = items;
    }
}
