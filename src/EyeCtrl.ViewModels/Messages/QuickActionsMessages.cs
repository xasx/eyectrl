namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

/// <summary>Sent to toggle the Quick Actions grid overlay open/closed.</summary>
public record QuickActionsToggledMessage;

/// <summary>Sent by a Quick Actions tile when the user selects an action.</summary>
/// <param name="Action">The quick action to execute.</param>
public record QuickActionSelectionMessage(QuickAction Action);
