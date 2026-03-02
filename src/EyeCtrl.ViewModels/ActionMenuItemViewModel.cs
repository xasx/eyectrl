namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ActionMenuItemViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction? Action { get; }
    public string Symbol { get; }
    public ScreenPoint Position { get; }
    public bool IsBackButton { get; }

    [ObservableProperty] private double _dwellProgress;

    public ActionMenuItemViewModel(string id, InteractionAction? action, string symbol, ScreenPoint position, bool isBackButton = false)
    {
        Id = id;
        Action = action;
        Symbol = symbol;
        Position = position;
        IsBackButton = isBackButton;
    }
}
