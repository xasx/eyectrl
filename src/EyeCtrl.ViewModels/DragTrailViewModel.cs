namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public enum DragSubState
{
    FreeDrag,
    Settling,
    ManualSelectionActive
}

public partial class DragTrailViewModel : DwellOverlayViewModelBase,
    IRecipient<StartDragTrailMessage>
{
    private const string ReleaseTargetId = "Drag_release";
    private const string CancelTargetId = "Drag_cancel";
    private const string BackTargetId = "Drag_back";

    private readonly EyeCtrlSettings _settings;
    private readonly FixationDetector _settleDetector;

    [ObservableProperty] private bool _isDragActive;
    [ObservableProperty] private PhysicalPoint _grabOrigin;
    [ObservableProperty] private PhysicalPoint _cursorPosition;
    [ObservableProperty] private DragSubState _dragState = DragSubState.FreeDrag;
    [ObservableProperty] private double _settleProgress;
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private PhysicalPoint _releaseButtonCenter;
    [ObservableProperty] private PhysicalPoint _cancelButtonCenter;
    [ObservableProperty] private PhysicalPoint _backButtonCenter;

    public DragTrailViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _settings = settings;

        _settleDetector = new FixationDetector(
            settings.Drag.SettleRadiusPixels,
            settings.Drag.SettleDwellMs);
        _settleDetector.FixationStarted += OnSettleStarted;
        _settleDetector.FixationEnded += OnSettleEnded;
    }

    public void Receive(StartDragTrailMessage message)
    {
        GrabOrigin = message.GrabOrigin;
        CursorPosition = message.GrabOrigin;
        IsDragActive = true;
        DragState = DragSubState.FreeDrag;
        SettleProgress = 0;
        ShowBack = false;

        // Right-edge cancel/back button centers are supplied by the App layer (screen geometry
        // lives there); the release button follows the cursor.
        CancelButtonCenter = message.CancelButtonCenter;
        BackButtonCenter = message.BackButtonCenter;
        UpdateReleaseButtonPosition(message.GrabOrigin);
        SetupDwellTargets();
    }

    public override void Receive(GazeCursorMovedMessage message)
    {
        if (!IsDragActive) return;

        CursorPosition = message.Position;
        DwellDetector.Update(message.Position, message.TimestampMicroseconds);

        if (DragState == DragSubState.FreeDrag || DragState == DragSubState.Settling)
        {
            _settleDetector.Update(message.Position, message.TimestampMicroseconds);
            SettleProgress = _settleDetector.GetProgress();

            if (SettleProgress > 0 && DragState == DragSubState.FreeDrag)
                DragState = DragSubState.Settling;
            else if (SettleProgress == 0 && DragState == DragSubState.Settling)
                DragState = DragSubState.FreeDrag;
        }

        UpdateReleaseButtonPosition(message.Position);
        DwellDetector.UpdateTargetCenter(ReleaseTargetId, ReleaseButtonCenter);
    }

    private void OnSettleStarted(PhysicalPoint point)
    {
        if (_settings.SelectionMode == SelectionMode.ManualSelection)
        {
            DragState = DragSubState.ManualSelectionActive;
            ShowBack = true;
            SetupDwellTargets(includeBack: true);
        }
        else
        {
            CompleteDrag(point);
        }
    }

    private void OnSettleEnded()
    {
        SettleProgress = 0;
        if (DragState == DragSubState.Settling)
            DragState = DragSubState.FreeDrag;
    }

    private void UpdateReleaseButtonPosition(PhysicalPoint cursorPos)
    {
        var offset = DpiHelper.LogicalToPhysicalX(UIConstants.DragReleaseButtonOffset);
        var btnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);

        if (cursorPos.DistanceSquaredTo(ReleaseButtonCenter) <= btnRadius * btnRadius)
            return;

        ReleaseButtonCenter = new PhysicalPoint(cursorPos.X - offset, cursorPos.Y - offset);
    }

    private void SetupDwellTargets(bool includeBack = false)
    {
        DwellDetector.ClearTargets();
        var btnRadius = DpiHelper.LogicalToPhysicalX(UIConstants.DragButtonRadius);

        DwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter, btnRadius,
            _settings.Dwell.DragReleaseButtonDwellMs));
        DwellDetector.AddTarget(new DwellTarget(
            CancelTargetId, CancelButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));

        if (includeBack)
        {
            DwellDetector.AddTarget(new DwellTarget(
                BackTargetId, BackButtonCenter, btnRadius,
                _settings.Dwell.DragRightEdgeButtonDwellMs));
        }
    }

    protected override void OnTargetActivated(string targetId)
    {
        if (targetId == ReleaseTargetId)
            CompleteDrag(CursorPosition);
        else if (targetId == CancelTargetId)
            CancelDrag();
        else if (targetId == BackTargetId)
            DismissManualSelection();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
    }

    private void CompleteDrag(PhysicalPoint dropPoint)
    {
        IsDragActive = false;
        _settleDetector.Reset();
        DwellDetector.ClearTargets();
        DragState = DragSubState.FreeDrag;
        Messenger.Send(new DragDropMessage(dropPoint));
    }

    private void CancelDrag()
    {
        IsDragActive = false;
        _settleDetector.Reset();
        DwellDetector.ClearTargets();
        DragState = DragSubState.FreeDrag;
        Messenger.Send(new DragCancelMessage());
    }

    private void DismissManualSelection()
    {
        DragState = DragSubState.FreeDrag;
        ShowBack = false;
        SettleProgress = 0;
        _settleDetector.Reset();
        SetupDwellTargets();
        Messenger.Send(new DragDismissManualSelectionMessage());
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        _settleDetector.FixationStarted -= OnSettleStarted;
        _settleDetector.FixationEnded -= OnSettleEnded;
    }
}
