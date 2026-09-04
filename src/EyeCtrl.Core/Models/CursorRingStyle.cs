namespace EyeCtrl.Core.Models;

/// <summary>How the gaze cursor ring keeps contrast against arbitrary desktop content.</summary>
public enum CursorRingStyle
{
    /// <summary>Accent core between two StrokeHalo rims. The shipped look, re-proportioned.</summary>
    WeightedSandwich,
    /// <summary>One ring alternating accent and StrokeHalo segments, interleaved not stacked.</summary>
    BiTonalSegments,
    /// <summary>Near-black core inside a TextPrimary outline. No hue at all.</summary>
    AchromaticReticle
}
