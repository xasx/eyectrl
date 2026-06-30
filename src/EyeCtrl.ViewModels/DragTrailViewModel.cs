namespace EyeCtrl.ViewModels;

using System.Windows;
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
    ZoomActive
}

public partial class DragTrailViewModel : ObservableObject,
    IRecipient<StartDragTrailMessage>,
    IRecipient<GazeCursorMovedMessage>,
    IDisposable
{
    private const string ReleaseTargetId = "Drag_release";
    private const string CancelTargetId = "Drag_cancel";
    private const string BackTargetId = "Drag_back";

    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;
    private readonly DwellDetector _dwellDetector;
    private readonly FixationDetector _settleDetector;
    private bool _disposed;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private PhysicalPoint _grabOrigin;
    [ObservableProperty] private PhysicalPoint _cursorPosition;
    [ObservableProperty] private DragSubState _dragState = DragSubState.FreeDrag;
    [ObservableProperty] private double _settleProgress;
    [ObservableProperty] private bool _showBack;
    [ObservableProperty] private PhysicalPoint _releaseButtonCenter;
    [ObservableProperty] private PhysicalPoint _cancelButtonCenter;
    [ObservableProperty] private PhysicalPoint _backButtonCenter;

    public DragTrailViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        _dwellDetector = new DwellDetector(settings.Dwell.DwellCooldownMs);
        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;

        _settleDetector = new FixationDetector(
            settings.Drag.SettleRadiusPixels,
            settings.Drag.SettleDwellMs);
        _settleDetector.FixationStarted += OnSettleStarted;
        _settleDetector.FixationEnded += OnSettleEnded;

        messenger.RegisterAll(this);
    }

    public void Receive(StartDragTrailMessage message)
    {
        GrabOrigin = message.GrabOrigin;
        CursorPosition = message.GrabOrigin;
        IsActive = true;
        DragState = DragSubState.FreeDrag;
        SettleProgress = 0;
        ShowBack = false;

        UpdateButtonPositions(message.GrabOrigin);
        SetupDwellTargets();
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        if (!IsActive) return;

        CursorPosition = message.Position;
        _dwellDetector.Update(message.Position, message.TimestampMicroseconds);

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
        _dwellDetector.UpdateTargetCenter(ReleaseTargetId, ReleaseButtonCenter);
    }

    private void OnSettleStarted(PhysicalPoint point)
    {
        if (_settings.SelectionMode == SelectionMode.ManualSelection)
        {
            DragState = DragSubState.ZoomActive;
            ShowBack = true;
            SetupDwellTargetsWithBack();
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

    private void UpdateButtonPositions(PhysicalPoint cursorPos)
    {
        var screenWidth = DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth);
        var buttonX = screenWidth - DpiHelper.LogicalToPhysicalX(30);
        var centerY = DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight / 2.0);

        CancelButtonCenter = new PhysicalPoint(buttonX, centerY + DpiHelper.LogicalToPhysicalY(30));
        BackButtonCenter = new PhysicalPoint(buttonX, centerY - DpiHelper.LogicalToPhysicalY(30));

        UpdateReleaseButtonPosition(cursorPos);
    }

    private void UpdateReleaseButtonPosition(PhysicalPoint cursorPos)
    {
        var offset = DpiHelper.LogicalToPhysicalX(14);
        var btnRadius = DpiHelper.LogicalToPhysicalX(20);

        double dx = cursorPos.X - ReleaseButtonCenter.X;
        double dy = cursorPos.Y - ReleaseButtonCenter.Y;
        if (dx * dx + dy * dy <= btnRadius * btnRadius)
            return;

        ReleaseButtonCenter = new PhysicalPoint(cursorPos.X - offset, cursorPos.Y - offset);
    }

    private void SetupDwellTargets()
    {
        _dwellDetector.ClearTargets();
        var btnRadius = DpiHelper.LogicalToPhysicalX(20);

        _dwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter, btnRadius,
            _settings.Dwell.DragReleaseButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            CancelTargetId, CancelButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));
    }

    private void SetupDwellTargetsWithBack()
    {
        _dwellDetector.ClearTargets();
        var btnRadius = DpiHelper.LogicalToPhysicalX(20);

        _dwellDetector.AddTarget(new DwellTarget(
            ReleaseTargetId, ReleaseButtonCenter, btnRadius,
            _settings.Dwell.DragReleaseButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            CancelTargetId, CancelButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));
        _dwellDetector.AddTarget(new DwellTarget(
            BackTargetId, BackButtonCenter, btnRadius,
            _settings.Dwell.DragRightEdgeButtonDwellMs));
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == ReleaseTargetId)
            CompleteDrag(CursorPosition);
        else if (targetId == CancelTargetId)
            CancelDrag();
        else if (targetId == BackTargetId)
            DismissZoom();
    }

    private void OnTargetProgress(string targetId, double progress)
    {
    }

    private void CompleteDrag(PhysicalPoint dropPoint)
    {
        IsActive = false;
        _settleDetector.Reset();
        _dwellDetector.ClearTargets();
        DragState = DragSubState.FreeDrag;
        _messenger.Send(new DragDropMessage(dropPoint));
    }

    private void CancelDrag()
    {
        IsActive = false;
        _settleDetector.Reset();
        _dwellDetector.ClearTargets();
        DragState = DragSubState.FreeDrag;
        _messenger.Send(new DragCancelMessage());
    }

    private void DismissZoom()
    {
        DragState = DragSubState.FreeDrag;
        ShowBack = false;
        SettleProgress = 0;
        _settleDetector.Reset();
        SetupDwellTargets();
        _messenger.Send(new DragDismissZoomMessage());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dwellDetector.TargetActivated -= OnTargetActivated;
        _dwellDetector.TargetProgress -= OnTargetProgress;
        _settleDetector.FixationStarted -= OnSettleStarted;
        _settleDetector.FixationEnded -= OnSettleEnded;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
