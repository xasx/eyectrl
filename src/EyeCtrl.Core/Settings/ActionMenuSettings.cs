namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;
using System.Collections.Generic;

/// <summary>Settings controlling which actions are visible in the gaze action menu ring.</summary>
public partial class ActionMenuSettings : ObservableObject, IValidatable
{
    /// <summary>List of interaction actions displayed as buttons in the action menu ring.</summary>
    [ObservableProperty] private List<InteractionAction> _visibleActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.Drag,
        InteractionAction.ScrollUp, InteractionAction.ScrollDown,
        InteractionAction.ScrollMode,
        InteractionAction.PinNote
    };

    /// <summary>
    /// Whether <see cref="EnsureDefaultsOnce"/> has already run for this settings file. Absent
    /// from every pre-2026-08-17 file (so it loads false) and set on the first migration; from
    /// then on the action list is user-owned — the Settings Action Menu tab edits it, and the
    /// startup migration must not re-add a default the user deliberately removed.
    /// </summary>
    [ObservableProperty] private bool _defaultsMigrated;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        // ActionMenuSettings has no numeric ranges to validate
        return new List<string>();
    }

    /// <summary>Ensures the default set of actions is present in <see cref="VisibleActions"/>.</summary>
    public void EnsureDefaults()
    {
        if (VisibleActions is null)
            VisibleActions = new List<InteractionAction>();

        var defaults = new[]
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.Drag,
            InteractionAction.ScrollUp, InteractionAction.ScrollDown,
            InteractionAction.ScrollMode,
            InteractionAction.PinNote
        };

        foreach (var action in defaults)
        {
            if (!VisibleActions.Contains(action))
                VisibleActions.Add(action);
        }
    }

    /// <summary>
    /// Runs <see cref="EnsureDefaults"/> exactly once per settings file, then leaves the list
    /// alone forever. The startup caller must use this, not <see cref="EnsureDefaults"/>: the
    /// per-startup variant would silently re-add a default action the user removed via the
    /// Settings Action Menu tab, making that tab's toggles lie across restarts.
    /// </summary>
    public void EnsureDefaultsOnce()
    {
        if (DefaultsMigrated) return;
        EnsureDefaults();
        DefaultsMigrated = true;
    }
}
