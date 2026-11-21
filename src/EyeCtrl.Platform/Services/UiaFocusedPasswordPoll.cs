namespace EyeCtrl.Platform.Services;

using System.Windows.Automation;
using EyeCtrl.Core.Interfaces;

/// <summary>
/// D4's direct poll: <c>AutomationElement.FocusedElement.Current.IsPassword</c>, the same UI-Automation read
/// the E1 mirror probe (<see cref="Win32KeyboardContextProbe"/>) makes. It is a second opinion for the day log
/// only — <see cref="UiaSensitiveContextDetector"/> remains the verdict's sole owner, and nothing here writes
/// it.
/// <para>
/// Own-process focus returns <c>null</c> (the flag of EyeCtrl's own overlay is not evidence about the target
/// app), matching the mirror probe's own-UI guard. Exceptions are deliberately not caught: the caller time-boxes
/// the read and logs the exception TYPE only, so no provider message text can reach a log line.
/// </para>
/// </summary>
public sealed class UiaFocusedPasswordPoll : IFocusedPasswordPoll
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    /// <inheritdoc />
    public bool? ReadIsPassword()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null)
            return null;
        var current = focused.Current;
        if (current.ProcessId == OwnProcessId)
            return null;
        return current.IsPassword;
    }
}
