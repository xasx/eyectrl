namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;
using System.Collections.Generic;
using System.Linq;

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
        InteractionAction.PinNote,
        InteractionAction.Keyboard
    };

    /// <summary>
    /// Whether <see cref="EnsureDefaultsOnce"/> has already run for this settings file. Absent
    /// from every pre-2026-08-17 file (so it loads false) and set on the first migration; from
    /// then on the action list is user-owned — the Settings Action Menu tab edits it, and the
    /// startup migration must not re-add a default the user deliberately removed.
    /// </summary>
    [ObservableProperty] private bool _defaultsMigrated;

    /// <summary>
    /// The actions shown on the ring's FIRST layer, alongside a More target that opens the rest.
    /// A subset of <see cref="VisibleActions"/>; anything visible but not listed here lands on the
    /// second layer.
    ///
    /// Capped at four by <see cref="Validate"/>. The cap buys SEPARATION, not the absence of
    /// overlap — at the default CircleSize the neighbour gap is 160 DIP at five slots, 115 at
    /// six and 34 at nine, and six only actually overlap below CircleSize 20. Four plus More is
    /// the point at which a ring can be aimed at without scanning it.
    /// </summary>
    [ObservableProperty] private List<InteractionAction> _primaryActions = new()
    {
        InteractionAction.LeftClick, InteractionAction.RightClick,
        InteractionAction.DoubleClick, InteractionAction.ScrollMode
    };

    /// <summary>
    /// Whether <see cref="MigratePrimaryOnce"/> has already run for this settings file. Absent
    /// from every pre-2026-08-26 file (so it loads false) and set on the first migration.
    ///
    /// Exactly the <see cref="DefaultsMigrated"/> pattern, for exactly its reason: the primary
    /// list is user-owned once the Settings tab can edit it, and a per-startup migration would
    /// re-promote an action the user deliberately demoted every time they relaunched.
    /// </summary>
    [ObservableProperty] private bool _primaryMigrated;

    /// <summary>The maximum number of primary actions. Four, plus the More target, fills the ring.</summary>
    public const int MaxPrimaryActions = 4;

    /// <summary>
    /// <see cref="PrimaryActions"/> restricted to what is actually switched on. A primary action
    /// the user turned off in the Settings tab must vanish from the ring, not linger on layer one.
    /// </summary>
    public IReadOnlyList<InteractionAction> EffectivePrimaryActions =>
        (PrimaryActions ?? new List<InteractionAction>())
            .Where(a => VisibleActions?.Contains(a) == true)
            .ToList();

    /// <summary>
    /// Everything visible that is not primary — the ring's second layer, reached through More.
    /// Derived rather than stored so it cannot drift out of sync with the two lists it spans.
    /// </summary>
    public IReadOnlyList<InteractionAction> SecondaryActions =>
        (VisibleActions ?? new List<InteractionAction>())
            .Where(a => PrimaryActions?.Contains(a) != true)
            .ToList();

    /// <summary>
    /// Populates <see cref="PrimaryActions"/> exactly once per settings file, then never touches
    /// it again. The startup caller must use this rather than assigning the defaults directly —
    /// see <see cref="PrimaryMigrated"/>.
    /// </summary>
    public void MigratePrimaryOnce()
    {
        if (PrimaryMigrated) return;

        PrimaryActions = new List<InteractionAction>
        {
            InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.ScrollMode
        };
        PrimaryMigrated = true;
    }

    /// <summary>
    /// Whether <see cref="EnsureKeyboardActionOnce"/> has already run for this settings file.
    /// Absent from every pre-2026-09 file (so it loads false) and set on the first migration.
    /// Same pattern and reason as <see cref="DefaultsMigrated"/>: the action did not exist
    /// when any existing file was written, so the one-shot add cannot re-add anything the
    /// user deliberately removed.
    /// </summary>
    [ObservableProperty] private bool _keyboardActionMigrated;

    /// <summary>
    /// Adds <see cref="InteractionAction.Keyboard"/> to <see cref="VisibleActions"/> exactly
    /// once per settings file. The startup caller must use this rather than editing the list —
    /// see <see cref="KeyboardActionMigrated"/>.
    /// </summary>
    public void EnsureKeyboardActionOnce()
    {
        if (KeyboardActionMigrated) return;
        if (VisibleActions is null) VisibleActions = new List<InteractionAction>();
        if (!VisibleActions.Contains(InteractionAction.Keyboard))
            VisibleActions.Add(InteractionAction.Keyboard);
        KeyboardActionMigrated = true;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        var primary = PrimaryActions ?? new List<InteractionAction>();

        if (primary.Count > MaxPrimaryActions)
            errors.Add($"{nameof(PrimaryActions)} must hold at most {MaxPrimaryActions} actions; "
                       + $"found {primary.Count}. Four plus the More target fills the ring's five slots.");

        if (primary.Count != primary.Distinct().Count())
            errors.Add($"{nameof(PrimaryActions)} must not repeat an action.");

        return errors;
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
            InteractionAction.PinNote,
            InteractionAction.Keyboard
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
