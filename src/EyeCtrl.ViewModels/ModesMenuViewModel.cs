namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The modes menu (rework D1′/D18): a small panel under the toolbar, toggled by the toolbar's
/// Modes button, holding the input-mode switches — currently one row, "Repeat clicks". A row
/// dwell toggles its mode; the menu hides on selection. Follows the TaskSwitcher pattern: the
/// VM receives the toggle message itself and flips <see cref="DwellOverlayViewModelBase"/>
/// IsVisible; the binding coordinator shows/hides the window; the window reports the row's real
/// screen position back (no dwell target exists before that — the toolbar's (0,0) lesson).
///
/// <para>
/// Known shared behaviour, deliberately inherited: like the Task Switcher grid, this panel does
/// not suppress engine fixation, so resting on a row past the fixation window can raise the
/// activation button underneath. Same exposure class as the existing surfaces; revisit only if
/// it bothers in practice.
/// </para>
/// </summary>
public partial class ModesMenuViewModel : DwellOverlayViewModelBase,
    IRecipient<ModesMenuToggledMessage>,
    IRecipient<StateChangedMessage>
{
    /// <summary>Dwell for a mode row: same as toolbar buttons — this is toolbar-adjacent chrome.</summary>
    private readonly int _rowDwellMs;

    /// <summary>
    /// The rows: one icon-only button per mouse click. The labels are gone (user decision
    /// 2026-10-06 — "drop the labels and just have good icons"); the ring's own action names
    /// (<c>RingLabelSource</c>) are what the window exposes to UIA instead.
    /// </summary>
    private static readonly (string Id, InteractionAction Action)[] Rows =
    {
        ("modes_left", InteractionAction.LeftClick),
        ("modes_right", InteractionAction.RightClick),
        ("modes_double", InteractionAction.DoubleClick),
        ("modes_middle", InteractionAction.MiddleClick),
    };

    /// <summary>The action currently locked (null = mode off). Drives the active-button marker.</summary>
    [ObservableProperty] private InteractionAction? _activeAction;

    /// <summary>One view-model per dwell button — DwellProgress drives each button's arc.</summary>
    public ObservableCollection<ModesMenuButtonViewModel> Buttons { get; } = new();

    public ModesMenuViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null)
        : base(messenger, settings.Dwell.DwellCooldownMs, DwellContext.Toolbar, sink, dwellRegistry)
    {
        _rowDwellMs = settings.Dwell.ToolbarButtonDwellMs;
        RebuildButtons();
    }

    /// <summary>The rows the view should render, in order.</summary>
    public IReadOnlyList<(string Id, InteractionAction Action)> RowDefinitions => Rows;

    private void RebuildButtons()
    {
        Buttons.Clear();
        foreach (var (id, action) in Rows)
            Buttons.Add(new ModesMenuButtonViewModel(id, action));
    }

    public void Receive(ModesMenuToggledMessage message)
    {
        if (IsVisible)
            HideOverlay();
        else
            IsVisible = true;   // the binding coordinator shows the window; it reports the row position
    }

    public void Receive(StateChangedMessage message)
        => ActiveAction = message.NewState == InteractionState.RepeatMode ? LockedAction : null;

    /// <summary>The action Core currently has locked, or null. The state message carries only the
    /// state, so the menu tracks its own record of what it locked last (it is the only entry point).</summary>
    private InteractionAction? LockedAction { get; set; }

    /// <summary>
    /// Window feedback after placement: each row's real screen centre and hit radius. Until this
    /// arrives the VM holds no dwell targets — a target at a placeholder point could fire on a
    /// corner gaze (the ToolbarWindow lesson, mirrored deliberately).
    /// <para>
    /// The positions are kept: ticket 8's chrome rule reads them as the menu's zone (plain data, no
    /// window access), so a settle on a mode row never offers an activation button over it.
    /// </para>
    /// </summary>
    public void SetRowScreenPositions(IReadOnlyList<(string Id, PhysicalPoint Center, double Radius)> rows)
    {
        RowScreenPositions = rows;
        DwellDetector.ClearTargets();
        foreach (var (id, center, radius) in rows)
            DwellDetector.AddTarget(new DwellTarget(id, center, radius, _rowDwellMs));
    }

    /// <summary>The row targets the window last reported, in physical pixels; empty before the first
    /// placement. The modes menu's chrome zone is the union of these, not the panel's outer frame.</summary>
    public IReadOnlyList<(string Id, PhysicalPoint Center, double Radius)> RowScreenPositions { get; private set; }
        = Array.Empty<(string, PhysicalPoint, double)>();

    // Deliberately dwellable while paused (DwellOverlayViewModelBase.DwellActiveWhilePaused):
    // the modes menu holds the gaze-reachable pause toggle — gating it would make pause a one-way door.
    protected override bool DwellActiveWhilePaused => true;

    protected override void OnTargetActivated(string targetId)
    {
        var row = Rows.FirstOrDefault(r => r.Id == targetId);
        if (row == default) return;

        if (ActiveAction == row.Action)
        {
            LockedAction = null;
            Messenger.Send(new ExitRepeatModeMessage());
        }
        else
        {
            LockedAction = row.Action;
            Messenger.Send(new EnterRepeatModeMessage(row.Action));
        }
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var button = Buttons.FirstOrDefault(b => b.Id == targetId);
        if (button is not null)
            button.DwellProgress = progress;
    }

    public void HideOverlay()
    {
        IsVisible = false;
        foreach (var b in Buttons)
            b.DwellProgress = 0;
        DwellDetector.ClearTargets();
    }

    /// <summary>Drives one activation without a DwellDetector, for tests.</summary>
    internal void ActivateForTest(string targetId) => OnTargetActivated(targetId);
}
