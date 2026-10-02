namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>
/// What a dwell on a ring item does. Replaced a bool <c>IsBackButton</c> on 2026-08-26: the ring
/// gained a second layer, and with it three distinct non-action affordances that a bool cannot
/// separate.
/// </summary>
public enum ActionMenuItemKind
{
    /// <summary>Performs its <see cref="ActionMenuItemViewModel.Action"/> and closes the menu.</summary>
    Action,
    /// <summary>Opens the ring's second layer. Does not close the menu.</summary>
    OpenMore,
    /// <summary>Returns to the ring's first layer. Does not close the menu.</summary>
    BackToPrimary,
    /// <summary>Leaves the menu entirely — the Back that returns from zoom or manual selection.</summary>
    ExitMenu,
}

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

    /// <summary>
    /// The item's name, shown only while the Help target has labels switched on. Always populated
    /// — a label that only exists when shown is a label that is never tested.
    /// </summary>
    public string Label { get; }

    public ActionMenuItemKind Kind { get; }
    public PhysicalPoint Position { get; }

    /// <summary>
    /// Whether this item is dimmed as a non-dwell-target for the current menu session: with a
    /// modifier latched, Drag and ScrollMode make no sense (a modified drag or scroll is not a
    /// thing the dispatcher can perform), so they keep their slot but lose their dwell target.
    /// Immutable per build — the ring rebuilds on every dim-state change.
    /// </summary>
    public bool IsDisabled { get; }

    [ObservableProperty] private double _dwellProgress;

    public ActionMenuItemViewModel(
        string id,
        InteractionAction? action,
        ActionIcon icon,
        string label,
        PhysicalPoint position,
        ActionMenuItemKind kind = ActionMenuItemKind.Action,
        bool isDisabled = false)
    {
        Id = id;
        Action = action;
        Icon = icon;
        Label = label;
        Position = position;
        Kind = kind;
        IsDisabled = isDisabled;
    }
}
