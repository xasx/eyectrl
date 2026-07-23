namespace EyeCtrl.Core.Adaptation;

/// <summary>Whether a dwell interaction completed or was abandoned before completion.</summary>
public enum OutcomeKind
{
    /// <summary>The dwell reached its required time and fired.</summary>
    Activated,
    /// <summary>The dwell was abandoned before completion (gaze-driven).</summary>
    Abandoned,
}
