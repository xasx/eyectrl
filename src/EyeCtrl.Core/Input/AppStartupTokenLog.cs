namespace EyeCtrl.Core.Input;

/// <summary>
/// C10's one startup token line, as a contract shared by the two ends: the App writes it, and the
/// installer (C9 S6a's pass rule, plus Q12's degraded detection) reads it. The template is a
/// Serilog message template; the App's file sink renders the message through <c>{Message:lj}</c>,
/// where <c>j</c> writes a bool in JSON form — lowercase <c>true</c>/<c>false</c>. The first real
/// install (S8 sitting, 05.11) failed because a reader looked for <c>UIAccess=True</c> instead, so
/// a reader must take the value as a bool, never as one spelling of it.
/// </summary>
public static class AppStartupTokenLog
{
    /// <summary>The Serilog message template of the line.</summary>
    public const string TokenLineTemplate =
        "Token: IL={IntegrityLevel} UIAccess={UIAccess} Elevated={Elevated} exe={ExecutablePath}";

    /// <summary>The literal every token line begins with, after the output template's timestamp,
    /// level and source context.</summary>
    public const string TokenMarker = "Token: IL=";

    /// <summary>The property the pass rule reads, with its separator.</summary>
    public const string UiAccessMarker = "UIAccess=";
}
