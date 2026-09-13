namespace EyeCtrl.Core.Tests.Processing;

using System.Reflection;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// The rebuild state hand-over, guarded at the seam where it will actually break.
/// <para>
/// <c>GapInterpolator.CopyStateFrom</c> carries a filter's learned state across a rebuild. That works
/// while the pipeline is one filter. The moment it becomes a CHAIN — which Stage 3 ticket 3 plans, as a
/// median prefilter decorating the One-Euro — the outer object is no longer the filter that owns the
/// state, and a hand-over that does not pass through the decorator silently stops happening. No
/// exception, no failing test, no symptom except that a fixed defect comes back weeks later attributed
/// to the median, which is *supposed* to change the behaviour it would be blamed for.
/// </para>
/// </summary>
public class GazeFilterCompositionTests
{
    private const long Period = 33_000;

    // ── the drift guard ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryFilterThatWrapsAnotherFilter_ForwardsTheStateHandOver()
    {
        // THE test that fires when ticket 3 lands. Everything else in this file demonstrates the pattern;
        // this one enforces it against code that does not exist yet, which is the only kind of guard that
        // survives the gap between writing the contract and someone needing it.
        //
        // Reflection over the production assembly rather than a hand-maintained list: a hand-maintained
        // list is exactly what a new decorator gets left out of.
        var wrappers = typeof(IGazeFilter).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IGazeFilter).IsAssignableFrom(t))
            .Where(WrapsAnotherFilter)
            .ToList();

        var offenders = wrappers
            .Where(t => !typeof(IGazeFilterStateAdopter).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Check.WithCustomMessage(
                $"These filters wrap another IGazeFilter but do not implement {nameof(IGazeFilterStateAdopter)}, "
                + "so a rebuild silently drops the inner filter's learned state. Implement it and forward "
                + "both members to the inner filter — see IGazeFilterStateAdopter's remarks.")
            .That(offenders).IsEmpty();

        // GapInterpolator holds the filter but is not one itself, so today this legitimately finds
        // nothing. Asserted so the guard's own reach is visible rather than assumed: if this ever starts
        // finding types, the check above became live without anyone noticing.
        Check.That(wrappers).IsEmpty();
    }

    /// <summary>
    /// Does <paramref name="type"/> hold one or more other filters as pipeline stages?
    /// </summary>
    /// <remarks>
    /// <para>
    /// An earlier version looked only at directly-typed fields, and a planted
    /// <c>CompositeProbe(params IGazeFilter[] stages)</c> produced zero failures — a composite is exactly
    /// the shape someone reaches for when the chain grows past one wrapper, so the guard would have gone
    /// quiet at the moment it mattered most. Arrays and generic arguments are now unwrapped, and the
    /// BASE CHAIN is walked, because <c>GetFields</c> does not return a base class's private fields and a
    /// shared decorator base is the other obvious way to grow this.
    /// </para>
    /// <para>
    /// <b>Delegates are deliberately excluded.</b> A <c>Func&lt;IGazeFilter&gt;</c> is a factory, not a
    /// held stage: it produces filters rather than delegating to one, so there is no hand-over for it to
    /// forward and flagging it would be a false positive that teaches people to work around the guard.
    /// </para>
    /// </remarks>
    private static bool WrapsAnotherFilter(Type type)
    {
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                 .Any(f => HoldsAFilter(f.FieldType, depth: 0)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HoldsAFilter(Type type, int depth)
    {
        if (depth > 4) return false;                                  // pathological nesting, not a pipeline
        if (typeof(Delegate).IsAssignableFrom(type)) return false;    // a factory is not a stage
        if (typeof(IGazeFilter).IsAssignableFrom(type)) return true;
        if (type.IsArray) return HoldsAFilter(type.GetElementType()!, depth + 1);
        return type.IsGenericType && type.GetGenericArguments().Any(a => HoldsAFilter(a, depth + 1));
    }

    // ── the guard's own detection logic ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(DirectFieldProbe))]      // the shape that already worked
    [InlineData(typeof(ArrayProbe))]            // params IGazeFilter[] — the demonstrated miss
    [InlineData(typeof(ListProbe))]             // List<IGazeFilter>
    [InlineData(typeof(NestedGenericProbe))]    // IReadOnlyList<IGazeFilter[]>
    [InlineData(typeof(DerivedProbe))]          // holds it on a BASE class
    [InlineData(typeof(ConcreteFieldProbe))]    // typed as the concrete filter, not the interface
    public void WrapDetection_FindsAFilterHeldAsAStage(Type probe)
    {
        // The guard above can only be as good as this predicate, and it cannot be exercised through the
        // assembly scan without planting types in production Core. So the predicate is tested directly
        // against probes here, and the scan is left to police the real assembly.
        //
        // The array row is the one that matters: this predicate previously looked only at directly-typed
        // fields, and a planted `CompositeProbe(params IGazeFilter[] stages)` produced ZERO failures.
        // A composite is exactly what someone reaches for when the chain grows past one wrapper.
        Check.That(WrapsAnotherFilter(probe)).IsTrue();
    }

    [Theory]
    [InlineData(typeof(FactoryProbe))]          // Func<IGazeFilter> — produces filters, holds none
    [InlineData(typeof(CallbackProbe))]         // Action<IGazeFilter> — mentions one, holds none
    [InlineData(typeof(StateOnlyProbe))]        // holds filter STATE, which is not a stage
    [InlineData(typeof(NoFilterProbe))]
    [InlineData(typeof(OneEuroFilter))]         // the real thing: a leaf, not a wrapper
    public void WrapDetection_DoesNotFlagATypeThatMerelyMentionsAFilter(Type probe)
    {
        // The other half, and the one that keeps the guard trustworthy. A predicate that fires on any
        // mention of IGazeFilter would flag factories and callbacks, and a guard people have to work
        // around stops being read as a real constraint.
        Check.That(WrapsAnotherFilter(probe)).IsFalse();
    }

    // These fields exist to be FOUND BY REFLECTION, not to be read — which is exactly what CS0414
    // ("assigned but never used") is for, and exactly why it is wrong here. Scoped to the probe block.
#pragma warning disable CS0414
    private sealed class DirectFieldProbe(IGazeFilter inner) { private readonly IGazeFilter _inner = inner; }
    private sealed class ConcreteFieldProbe { private readonly OneEuroFilter _inner = new(); }
    private sealed class ArrayProbe(params IGazeFilter[] stages) { private readonly IGazeFilter[] _stages = stages; }
    private sealed class ListProbe { private readonly List<IGazeFilter> _stages = []; }
    private sealed class NestedGenericProbe { private readonly IReadOnlyList<IGazeFilter[]> _stages = []; }
    private class BaseProbe(IGazeFilter inner) { private readonly IGazeFilter _inner = inner; }
    private sealed class DerivedProbe(IGazeFilter inner) : BaseProbe(inner);
    private sealed class FactoryProbe(Func<IGazeFilter> make) { private readonly Func<IGazeFilter> _make = make; }
    private sealed class CallbackProbe(Action<IGazeFilter> onBuilt) { private readonly Action<IGazeFilter> _onBuilt = onBuilt; }
    private sealed class StateOnlyProbe { private readonly IGazeFilterState? _state = null; }
    private sealed class NoFilterProbe { private readonly int _unrelated = 1; }
#pragma warning restore CS0414

    // ── the pattern, demonstrated end to end ─────────────────────────────────────────────────────

    [Fact]
    public void AForwardingDecorator_KeepsTheHandOverWorking()
    {
        // The shape ticket 3 will produce: a decorator composed around GazeFilterFactory.Create's output.
        // The factory does not compose decorators yet, so this builds the inner filter through it and
        // wraps the result — which is what the factory itself will end up doing.
        Check.That(MeanFirstPostRebuildStep(inner => new ForwardingPassThrough(inner)))
            .IsStrictlyLessThan(7.0);
    }

    [Fact]
    public void ANonForwardingDecorator_LosesIt_WhichIsWhatTheGuardAbovePrevents()
    {
        // The negative control, and the measurement behind the contract. This decorator changes NOTHING
        // about the filtering — it passes every sample straight through — yet the first post-rebuild
        // sample moves ~2.5x further, because the hand-over never reaches the One-Euro inside it. That
        // number is the pre-fix number: the whole M-3 fix is undone by wrapping it.
        double forwarding = MeanFirstPostRebuildStep(inner => new ForwardingPassThrough(inner));
        double silent = MeanFirstPostRebuildStep(inner => new SilentPassThrough(inner));

        Check.That(silent).IsStrictlyGreaterThan(forwarding * 2.0);
        Check.That(silent).IsStrictlyGreaterThan(12.0);
    }

    [Fact]
    public void TheHandOverSurvivesTwoLayersOfDecoration()
    {
        // Nothing in the design limits a chain to one wrapper, and the capture/adopt pair composes to any
        // depth precisely because neither end type-tests the other.
        Check.That(MeanFirstPostRebuildStep(inner => new ForwardingPassThrough(new ForwardingPassThrough(inner))))
            .IsStrictlyLessThan(7.0);
    }

    [Fact]
    public void AStatelessFilter_IsUnaffected()
    {
        // Direct and the EMA hold no learned state, so a rebuild of either must simply do nothing here —
        // no throw, no attempt to adopt something they cannot read.
        var settings = new CursorSettings { FilterMode = GazeFilterMode.Smoothed };
        var old = new GapInterpolator(GazeFilterFactory.Create(settings), maxGapMs: 150);
        long ts = 0;
        for (int i = 0; i < 20; i++, ts += Period) old.Process(new GazePoint(960, 540, ts, true));

        var rebuilt = new GapInterpolator(GazeFilterFactory.Create(settings), maxGapMs: 150);
        rebuilt.CopyStateFrom(old);
        rebuilt.WarmStartFromCurrentState();

        Check.That(rebuilt.Process(new GazePoint(960, 540, ts, true)).Position.X).IsCloseTo(960.0, 1e-9);
    }

    [Fact]
    public void AModeSwitch_HandsOverNothing_AndTheIncomingFilterStartsClean()
    {
        // EMA out, One-Euro in: CaptureState has nothing to offer and AdoptState must ignore it rather
        // than guess. Pinned on the threshold, which is the observable consequence.
        var old = new GapInterpolator(
            GazeFilterFactory.Create(new CursorSettings { FilterMode = GazeFilterMode.Smoothed }), 150);
        long ts = 0;
        for (int i = 0; i < 50; i++, ts += Period) old.Process(new GazePoint(960, 540, ts, true));

        var filter = new OneEuroFilter();
        var rebuilt = new GapInterpolator(filter, maxGapMs: 150);
        rebuilt.CopyStateFrom(old);
        rebuilt.WarmStartFromCurrentState();
        rebuilt.Process(new GazePoint(960, 540, ts, true));

        Check.That(filter.LastJumpThresholdPixels).IsEqualTo(15.0);
    }

    [Fact]
    public void AFreshFilter_CapturesNothing_SoAnEarlyRebuildCarriesNoFabricatedEstimate()
    {
        // Cast because the members are explicit interface implementations — the adoption contract is
        // internal to Core, so a public member on OneEuroFilter could not expose its state type.
        Check.That(((IGazeFilterStateAdopter)new OneEuroFilter()).CaptureState()).IsNull();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mean displacement of the first post-rebuild output from where the outgoing filter left the cursor,
    /// over 60 sigma = 15 px noise seeds. Ensemble for the same reason the M-3 tests are: the defect is
    /// probabilistic per sample, so a single seed decides nothing.
    /// </summary>
    private static double MeanFirstPostRebuildStep(Func<IGazeFilter, IGazeFilter> compose)
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.OneEuro };
        double total = 0;
        const int seeds = 60;

        for (int seed = 1; seed <= seeds; seed++)
        {
            var rng = new Random(seed);
            var old = new GapInterpolator(compose(GazeFilterFactory.Create(settings)), maxGapMs: 150);
            long ts = 0;
            var last = new PhysicalPoint(960, 540);
            for (int i = 0; i < 400; i++, ts += Period)
                last = old.Process(Noisy(rng, ts)).Position;

            var rebuilt = new GapInterpolator(compose(GazeFilterFactory.Create(settings)), maxGapMs: 150);
            rebuilt.CopyStateFrom(old);
            rebuilt.WarmStartFromCurrentState();
            total += rebuilt.Process(Noisy(rng, ts)).Position.DistanceTo(last);
        }
        return total / seeds;
    }

    private static GazePoint Noisy(Random rng, long ts) =>
        new(960 + (15.0 * Gaussian(rng)), 540 + (15.0 * Gaussian(rng)), ts, IsValid: true);

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>A decorator that honours the contract — the shape ticket 3's median must take.</summary>
    private sealed class ForwardingPassThrough(IGazeFilter inner) : IGazeFilterStateAdopter
    {
        public PhysicalPoint Filter(GazePoint rawGaze) => inner.Filter(rawGaze);
        public void Reset() => inner.Reset();
        public IGazeFilterState? CaptureState() => (inner as IGazeFilterStateAdopter)?.CaptureState();
        public void AdoptState(IGazeFilterState? state) => (inner as IGazeFilterStateAdopter)?.AdoptState(state);
    }

    /// <summary>The same decorator with the two forwarding lines missing — the failure being guarded.</summary>
    private sealed class SilentPassThrough(IGazeFilter inner) : IGazeFilter
    {
        public PhysicalPoint Filter(GazePoint rawGaze) => inner.Filter(rawGaze);
        public void Reset() => inner.Reset();
    }
}
