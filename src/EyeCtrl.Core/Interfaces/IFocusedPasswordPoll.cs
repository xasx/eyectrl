namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// A read-only, second-opinion poll of the focused element's password flag — D4 of the password-detector
/// diagnostics. Implemented in <c>EyeCtrl.Platform</c> as one direct
/// <c>AutomationElement.FocusedElement.Current.IsPassword</c> read (the same UI-Automation path the E1 mirror
/// probe uses), and run only at the keyboard's show/hide moment.
/// <para>
/// <b>Evidence only.</b> Its answer is logged beside the event-driven detector's cached verdict and is
/// <b>never</b> fed into it: <see cref="ISensitiveContextDetector"/> stays the sole authority, so this poll
/// cannot change any gate, mute or dictionary decision. The read runs off the UI thread and is abandoned on a
/// timeout, so a hanging UI-Automation call costs a pooled thread, never a keyboard show or hide.
/// </para>
/// </summary>
public interface IFocusedPasswordPoll
{
    /// <summary>
    /// Reads the focused element's <c>IsPassword</c> once. Returns <c>null</c> when there is no readable
    /// focused element, or when it belongs to EyeCtrl's own UI (whose flag says nothing about the target
    /// app); throws when the read faults, which the caller logs as its error type only. May block — the
    /// caller owns the time box, and must call this off the UI thread.
    /// </summary>
    bool? ReadIsPassword();
}
