namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>
/// One dwell button in the Modes menu: the action it locks, its label, and the live dwell
/// progress that drives the button's arc (design D29 — toolbar-style DwellArcButtons).
/// </summary>
public partial class ModesMenuButtonViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction Action { get; }
    public string Label { get; }

    [ObservableProperty] private double _dwellProgress;

    public ModesMenuButtonViewModel(string id, InteractionAction action, string label)
    {
        Id = id;
        Action = action;
        Label = label;
    }
}
