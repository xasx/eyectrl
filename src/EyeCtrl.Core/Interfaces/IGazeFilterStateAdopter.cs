namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// Marker for a piece of filter state that is worth carrying across a rebuild. Opaque by design: only
/// the filter that produced it knows how to read it, so a mode switch hands over something the incoming
/// filter does not recognise and correctly ignores.
/// </summary>
internal interface IGazeFilterState;

/// <summary>
/// Implemented by a gaze filter that holds LEARNED state worth carrying across a rebuild — state that
/// describes the signal or the device rather than the filter's own configuration.
/// </summary>
/// <remarks>
/// <para>
/// <c>GazeEngine</c> does not retune filters in place; it constructs a fresh one and swaps it in
/// (<c>ApplyPendingInterpolatorRebuild</c>). That happens on every cursor settings change — once per gaze
/// frame while a slider is dragged — and up to once a second while the adaptation controller is ramping.
/// Position state already survives it, through <see cref="Processing.GapInterpolator.CopyStateFrom"/> and
/// <see cref="Processing.GapInterpolator.WarmStartFromCurrentState"/> (design R1, no cursor jump on
/// reconfiguration). This interface is the same idea for everything else.
/// </para>
/// <para>
/// <b>Why a capture/adopt PAIR rather than <c>AdoptStateFrom(IGazeFilter other)</c>.</b> The obvious shape
/// works only while the pipeline is a single filter. Both sides of a rebuild have the SAME composition, so
/// the moment a decorator exists the outgoing filter is a decorator too — and a type test for the concrete
/// filter fails on the source side exactly as it does on the destination side. Passing an opaque state
/// object instead means neither end needs to know the other's shape, and it composes through any depth of
/// decoration with no unwrapping anywhere.
/// </para>
/// <para>
/// <b>The forwarding contract, written here rather than discovered later.</b> A DECORATOR — an
/// <see cref="IGazeFilter"/> that wraps another one, which is how the Stage 3 median prefilter is planned
/// — sits between <see cref="Processing.GapInterpolator"/> and the filter that owns the state. If it does
/// not implement this interface and forward to its inner filter, the hand-over silently stops happening:
/// the pipeline keeps working, nothing throws, and the only symptom is that a defect the hand-over was
/// added to fix quietly returns. Measured for a PURE PASS-THROUGH wrapper at sigma = 15 px, first
/// post-rebuild displacement: ~5 px composed directly, ~13.5 px behind a decorator that does not forward
/// — the latter being the pre-fix number exactly.
/// </para>
/// <para>
/// So: <b>a decorator wrapping a filter that implements this interface must implement it too and forward
/// both members to its inner filter.</b> <c>GazeFilterCompositionTests</c> fails the build for any
/// wrapping filter in this assembly that does not.
/// </para>
/// <para>
/// <b>Internal on purpose.</b> Nothing outside <c>EyeCtrl.Core</c> implements <see cref="IGazeFilter"/>,
/// so a public contract here would be surface with no consumer. <c>InternalsVisibleTo</c> covers the
/// tests, and the reflective guard walks the assembly's own types, so it sees internal implementations.
/// Implementers that are themselves public implement these members EXPLICITLY — an implicit
/// implementation would have to be <c>public</c>, and a public member may not expose an internal type.
/// </para>
/// </remarks>
internal interface IGazeFilterStateAdopter : IGazeFilter
{
    /// <summary>
    /// Returns this filter's carryable state, or <c>null</c> when it has none yet. A decorator returns its
    /// inner filter's.
    /// </summary>
    IGazeFilterState? CaptureState();

    /// <summary>
    /// Takes over state previously captured from the outgoing pipeline. A decorator forwards to its inner
    /// filter.
    /// </summary>
    /// <param name="state">
    /// State from <see cref="CaptureState"/>, or <c>null</c>. Implementations MUST tolerate a state they
    /// do not recognise by doing nothing — a mode switch legitimately has nothing to carry, and must start
    /// clean rather than guess.
    /// </param>
    void AdoptState(IGazeFilterState? state);
}
