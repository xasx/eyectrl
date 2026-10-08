namespace EyeCtrl.App.Overlays;

using System.Windows.Automation.Peers;
using System.Windows.Controls;

/// <summary>
/// A TextBlock that exposes no automation peer. For caption pieces that are decoration under a
/// name a sibling already carries: the round-2 word-backspace key would otherwise show a stray
/// UIA element "Wort" with no AutomationId beside "Wort löschen" (review-03 F2).
/// </summary>
internal sealed class DecorativeTextBlock : TextBlock
{
    /// <summary>No peer: a peerless leaf disappears from both the Control and Content views.</summary>
    /// <returns>Always null.</returns>
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
