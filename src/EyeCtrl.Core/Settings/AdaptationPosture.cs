namespace EyeCtrl.Core.Settings;

/// <summary>How eagerly self-adaptation responds to detected struggle.</summary>
public enum AdaptationPosture
{
    /// <summary>Intervenes only on clear, sustained struggle; small steps, slow recovery. Default.</summary>
    Conservative,
    /// <summary>Moderate response to moderate struggle.</summary>
    Balanced,
    /// <summary>Reacts quickly and in larger steps at the first signs of struggle.</summary>
    Responsive,
}
