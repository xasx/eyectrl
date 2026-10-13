namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>VM → AdaptationPreviewService: show/hide the panel for one engagement. The service reads
/// all placement geometry (toolbar bounds, docked edge, floating state) itself — the VM knows none of it (P10).</summary>
public record AdaptationPreviewVisibilityMessage(bool Visible, int EngagementId, PhysicalPoint ResetCenter);

public enum PreviewBoundsKind
{
    Rect,           // the live panel bounds (show or resize)
    HiddenKeepLast, // a POLICY hide (suppression, D17 collision): no panel, but the last rect stays in the VM's union until the leave completes
    NoPanel         // nothing was or is shown (never-shown collision, contained failure, timeout, service absent): union falls back to the hit area
}

/// <summary>Service → VM: the live panel bounds and what kind of absence a hide is (P2).</summary>
public record AdaptationPreviewBoundsMessage(int EngagementId, PreviewBoundsKind Kind, PhysicalRect? PanelBounds);
