namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

public partial class ActionMenuItemViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction? Action { get; }

    /// <summary>
    /// Which drawn icon this item renders. Replaced a <c>Symbol</c> string of Unicode glyphs on
    /// 2026-08-26: those rendered in whatever font Windows fell back to, and one of them (a
    /// pushpin) was a colour emoji that ignored the theme entirely.
    /// </summary>
    public ActionIcon Icon { get; }

    public PhysicalPoint Position { get; }
    public bool IsBackButton { get; }

    [ObservableProperty] private double _dwellProgress;

    public ActionMenuItemViewModel(string id, InteractionAction? action, ActionIcon icon, PhysicalPoint position, bool isBackButton = false)
    {
        Id = id;
        Action = action;
        Icon = icon;
        Position = position;
        IsBackButton = isBackButton;
    }
}
