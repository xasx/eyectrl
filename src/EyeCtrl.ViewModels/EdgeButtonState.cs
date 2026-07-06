namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Observable runtime state for one visible edge-button. The View binds to this
/// (one instance per button in <see cref="EdgeButtonBarViewModel"/>).
/// </summary>
public partial class EdgeButtonState : ObservableObject
{
    public string Id { get; }
    public string Symbol { get; }

    [ObservableProperty] private double _dwellProgress;

    public EdgeButtonState(string id, string symbol)
    {
        Id = id;
        Symbol = symbol;
    }
}
