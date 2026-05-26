namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent from settings window when a text field is activated (user dwells on it).
/// Carries the target window handle for the receiving text field.
/// </summary>
public record ShowKeyboardRequestMessage
{
    public IntPtr TargetHandle { get; init; }
}

/// <summary>
/// Sent from settings window when the modal closes or text field loses focus.
/// </summary>
public record HideKeyboardRequestMessage;
