namespace EyeCtrl.Core.Enums;

/// <summary>Type of gaze data source for the eye tracking pipeline.</summary>
public enum GazeProviderType
{
    /// <summary>Auto-detect the best available provider (StreamEngine preferred, falls back to Mouse).</summary>
    Auto = 0,
    /// <summary>Tobii Interaction Library provider (32-bit, deprecated).</summary>
    [Obsolete("The 32-bit Interaction Library is no longer supported. Use StreamEngine instead.")]
    InteractionLib = 1,
    /// <summary>Tobii Stream Engine provider (64-bit, current Tobii SDK).</summary>
    StreamEngine = 2,
    /// <summary>Simulated gaze using the mouse cursor position (for testing without a tracker).</summary>
    Mouse = 3
}
