namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public abstract partial class SettingItemViewModel : ObservableObject
{
    public string Label { get; }
    public string Id { get; }

    protected SettingItemViewModel(string id, string label) { Id = id; Label = label; }
}

public partial class SliderSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private double _value;
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public Action<double> OnChanged { get; }

    public SliderSettingViewModel(string id, string label, double value, double min, double max, double step, Action<double> onChanged)
        : base(id, label)
    {
        _value = value; Minimum = min; Maximum = max; Step = step; OnChanged = onChanged;
    }

    partial void OnValueChanged(double value) => OnChanged(value);
}

public partial class ToggleSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private bool _isOn;
    public Action<bool> OnChanged { get; }

    public ToggleSettingViewModel(string id, string label, bool value, Action<bool> onChanged)
        : base(id, label)
    {
        _isOn = value; OnChanged = onChanged;
    }

    partial void OnIsOnChanged(bool value) => OnChanged(value);
}

public partial class ComboSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private string _selectedValue;
    public string[] Options { get; }
    public Action<string> OnChanged { get; }

    public ComboSettingViewModel(string id, string label, string value, string[] options, Action<string> onChanged)
        : base(id, label)
    {
        _selectedValue = value; Options = options; OnChanged = onChanged;
    }

    partial void OnSelectedValueChanged(string value) => OnChanged(value);
}

public partial class ColorSettingViewModel : SettingItemViewModel
{
    [ObservableProperty] private string _color;
    public Action<string> OnChanged { get; }

    public ColorSettingViewModel(string id, string label, string color, Action<string> onChanged)
        : base(id, label)
    {
        _color = color; OnChanged = onChanged;
    }

    partial void OnColorChanged(string value) => OnChanged(value);
}
