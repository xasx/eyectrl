namespace EyeCtrl.Core.Calibration;

/// <summary>
/// Which calibration failure occurred, as a stable code.
/// <para>
/// The calibration window renders the user's sentence from the code and its arguments
/// (<see cref="CalibrationFailure"/>), so Core keeps no user-facing English of its own: a message
/// translated later never has to be dug out of Core, and the words live in exactly one place.
/// </para>
/// </summary>
public enum CalibrationFailureCode
{
    /// <summary>Gaze barely changed across the targets — the eye tracker was not following the user.</summary>
    GazeVariation,

    /// <summary>The fitted transform maps gaze off screen and is unusable.</summary>
    UnusableTransform,

    /// <summary>The fitted transform flips or amplifies gaze instead of fine-tuning it.</summary>
    DistortedTransform,

    /// <summary>One or more targets failed the per-target quality gate; see the failure's arguments.</summary>
    Quality,

    /// <summary>Gaze never reached a target before its attempt ended.</summary>
    TargetNeverReached,

    /// <summary>Gaze reached a target but never stayed in its band long enough to collect a sample.</summary>
    TargetNeverHeld,

    /// <summary>
    /// A fault outside the codes above — a degenerate fit that the technical guards refused, or a
    /// defect. The calibration window renders a generic sentence for it, so no fault can fall back to
    /// raw exception text.
    /// </summary>
    Unexpected,
}

/// <summary>
/// A calibration failure as a code plus its arguments — everything the calibration window needs to
/// render the user's sentence.
/// <para>
/// Sessions throw a <see cref="CalibrationFailedException"/> carrying one of these instead of a
/// message, so the words live in exactly one place: the renderer on the calibration-window side.
/// Argument conventions per code:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="CalibrationFailureCode.Quality"/> — one
/// <see cref="CalibrationQualityFailure"/> list (boxed as a single argument) describing each failing
/// target.</description></item>
/// <item><description><see cref="CalibrationFailureCode.TargetNeverHeld"/> — the band radius in pixels
/// (double) and the number of samples seen (int).</description></item>
/// <item><description>Every other code — no arguments.</description></item>
/// </list>
/// </summary>
public sealed class CalibrationFailure
{
    /// <summary>
    /// Initializes a failure from its code and the values the renderer needs.
    /// </summary>
    /// <param name="code">Which failure this is.</param>
    /// <param name="arguments">The code's values, in the order the calibration window's renderer expects.</param>
    public CalibrationFailure(CalibrationFailureCode code, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Code = code;
        Arguments = arguments;
    }

    /// <summary>Which failure this is.</summary>
    public CalibrationFailureCode Code { get; }

    /// <summary>The code's values, in the order the calibration window's renderer expects (see the type remarks).</summary>
    public IReadOnlyList<object?> Arguments { get; }

    /// <summary>
    /// The failure for a fault outside the typed set: a technical fitting fault the session did not
    /// classify, or a defect. The calibration window renders a generic sentence for it.
    /// </summary>
    public static CalibrationFailure Unexpected { get; } = new(CalibrationFailureCode.Unexpected);
}
