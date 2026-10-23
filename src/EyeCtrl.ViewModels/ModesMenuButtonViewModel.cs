namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>
/// One dwell button in the Modes menu: the action it locks and the live dwell progress that
/// drives the button's arc (design D29 — toolbar-style DwellArcButtons). It carries no label:
/// the window draws icon-only buttons and names them through <c>RingLabelSource</c>.
/// </summary>
public partial class ModesMenuButtonViewModel : ObservableObject
{
    public string Id { get; }
    public InteractionAction Action { get; }

    [ObservableProperty] private double _dwellProgress;
    /// <summary>
    /// True while this item's dwell is HELD: the gaze stepped off mid-dwell and <see cref="DwellProgress"/>
    /// is kept for the pause grace. The view dims the progress indicator; it never changes the value.
    /// Display-only — nothing reads it to decide whether a dwell holds.
    /// </summary>
    [ObservableProperty] private bool _isDwellPaused;

    public ModesMenuButtonViewModel(string id, InteractionAction action)
    {
        Id = id;
        Action = action;
    }
}
