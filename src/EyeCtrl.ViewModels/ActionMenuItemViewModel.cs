namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.ActionMenu;
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

    /// <summary>
    /// The item's name, shown only while the Help target has labels switched on. Always populated
    /// — a label that only exists when shown is a label that is never tested.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// What a dwell on this item does. The enum moved to Core beside <c>RingPlan</c> on
    /// 2026-09-30: the plan's slots are read by both the runtime ring and the settings preview,
    /// and both must agree on what a slot IS.
    /// </summary>
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
