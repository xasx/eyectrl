namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent from settings window when a text field is activated (user dwells on it).
/// Carries the target window handle so the keyboard can be positioned near the field.
/// </summary>
public record ShowKeyboardRequestMessage
{
    public IntPtr TargetHandle { get; init; }
}

/// <summary>
/// Sent from settings window when the modal closes or text field loses focus.
/// </summary>
public record HideKeyboardRequestMessage;
