namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// The 2026-10-17 corner fix, T2: a settle just past the screen edge is clamped on-screen (P′)
/// instead of refused, so the activation button appears in the corners; beyond T it is still
/// refused, the docked toolbar's summon band never clamps, and the ring stays pinned at the RAW P.
/// The harness drives the real <see cref="GazeEngine"/> at the investigation's geometry
/// (3840×2400 @2, ring 200, hit 150, R 80).
/// </summary>
public class ActivationOfferCornerClampTests
{
    private const long FrameUs = 30_000;
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);

    /// <summary>The fix's tolerance T = 200 px, as a LITERAL: the boundary tests must not track the
    /// constant (a T−1/T+1 mutant then survives, which it did on the first run).</summary>
    private const double T = 200.0;

    private sealed class Harness : IDisposable
    {
        public ScriptedGazeProvider Provider { get; } = new();
        public RecordingInputSimulator Input { get; } = new();
        public GazeEngine Engine { get; }
        public InteractionManager Im => Engine.InteractionManager;
        public ActivationOfferEngine Offers => Im.ActivationOffers!;
        public List<PhysicalPoint> PinPoints { get; } = new();
        public List<PhysicalPoint> Captures { get; } = new();
        public List<ActivationOffer> Fired { get; } = new();

        private ActivationOffer? _pendingFire;
        private long _us;

        public Harness()
        {
            var settings = new EyeCtrlSettings();
            settings.Cursor.FilterMode = GazeFilterMode.Direct;
            settings.Cursor.CircleSize = 200;
            settings.Dwell.FixationRadiusPixels = 80;
            settings.Dwell.FixationDetectionMs = 300;
            settings.Dwell.ActivationButtonDwellMs = 700;
            settings.SelectionMode = SelectionMode.ManualSelection;
            Engine = new GazeEngine(Provider, Input, settings);
            Engine.ConfigureActivationOffers(Screen, scale: 2.0);
            Im.SelectionMode = SelectionMode.ManualSelection;

            Im.PinRing += point => PinPoints.Add(point);
            Im.StartManualSelection += point => Captures.Add(point);
            Im.ActivationOfferFired += (offer, _) => _pendingFire = offer;
        }

        public void Feed(PhysicalPoint point, int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
                if (_pendingFire is { } fired)
                {
                    _pendingFire = null;
                    if (Im.OnActivationButtonDwelled(fired) == ActivationCompletionResult.Accepted)
                        Fired.Add(fired);
                }
            }
        }

        /// <summary>Saccades between two far points: the run ends and no new one settles.</summary>
        public void FeedAway(int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                var point = i % 2 == 0 ? new PhysicalPoint(1000, 1000) : new PhysicalPoint(2800, 1800);
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
            }
        }

        public void Dispose() => Engine.Dispose();
    }

    [Theory]
    [InlineData(-40, -40, 0, 0)]
    [InlineData(3880, -40, 3839, 0)]
    [InlineData(-40, 2440, 0, 2399)]
    [InlineData(3880, 2440, 3839, 2399)]
    public void ANearCornerSettleOffScreen_IsClamped_AndTheCompletionCarriesPPrime(
        double rawX, double rawY, double expectedX, double expectedY)
    {
        // F1 (the fix): P up to T px past the edge is clamped to the screen; P′ is the offer's Point,
        // it feeds the placement (|B-P′| = 310), and the completion lands on it. The ring keeps the
        // RAW P. Mutant: drop the clamp (refuse every off-screen P again) → no offer, red.
        using var h = new Harness();
        var raw = new PhysicalPoint(rawX, rawY);
        var expected = new PhysicalPoint(expectedX, expectedY);

        h.Feed(raw, samples: 20);

        var offer = h.Im.ActivationOffers?.CurrentOffer;
        Check.WithCustomMessage($"no offer for the clamped settle at ({rawX},{rawY})").That(offer).IsNotNull();
        Check.That(offer!.Point).IsEqualTo(expected);
        Check.That(offer.Button.DistanceTo(expected)).IsCloseTo(310.0, 0.5);

        // The ring pin is unchanged: it stays at the raw settle point.
        Check.WithCustomMessage("the ring pin moved off the raw P").That(h.PinPoints).ContainsExactly(raw);

        // The completion (a manual-selection capture here) carries P′: dwell B and accept.
        h.Feed(offer.Button, samples: 25);
        Check.That(h.Fired).HasSize(1);
        Check.WithCustomMessage("the completion landed at the raw P, not at P′")
            .That(h.Captures[^1]).IsEqualTo(expected);
    }

    [Fact]
    public void TheClampBoundary_IncludesExactlyT_AndRefusesPastIt()
    {
        // Mutants: shift T by one (T-1/T+1) → one of these flips. The constant is pinned to the
        // spec's 200 separately, so moving it must also move the tests consciously.
        Check.WithCustomMessage("the tolerance constant left the spec's 200 px")
            .That(ActivationOfferEngine.CornerClampTolerancePx).IsEqualTo(200.0);
        using (var h = new Harness())
        {
            h.Feed(new PhysicalPoint(Screen.Width + T, 1200), samples: 20);
            Check.WithCustomMessage("a settle exactly T px past the edge was refused")
                .That(h.Im.ActivationOffers?.CurrentOffer?.Point).IsEqualTo(new PhysicalPoint(3839, 1200));
        }
        using (var h = new Harness())
        {
            h.Feed(new PhysicalPoint(Screen.Width + T + 1, 1200), samples: 20);
            Check.WithCustomMessage("a settle past T made an offer").That(h.Im.ActivationOffers?.CurrentOffer).IsNull();
        }
        using (var h = new Harness())
        {
            h.Feed(new PhysicalPoint(-T, 1200), samples: 20);
            Check.WithCustomMessage("a settle exactly T px past the left edge was refused")
                .That(h.Im.ActivationOffers?.CurrentOffer?.Point).IsEqualTo(new PhysicalPoint(0, 1200));
        }
        using (var h = new Harness())
        {
            h.Feed(new PhysicalPoint(-(T + 1), 1200), samples: 20);
            Check.WithCustomMessage("a settle past T on the left made an offer")
                .That(h.Im.ActivationOffers?.CurrentOffer).IsNull();
        }
    }

    [Fact]
    public void TheToolbarSummonBand_IsNeverClamped_WhileOtherTopEdgeSettlesAre()
    {
        // The exclusion: a P above the TOP edge whose x is inside the docked toolbar's span is a
        // toolbar summon, not a corner settle — clamping it would flash an offer before the toolbar
        // slides in. The band applies only on the docked edge. Mutant: ignore the band (clamp it)
        // → an offer appears at (1900, 0), red.
        using var h = new Harness();
        h.Im.SetActivationSummonBand(
            ActivationSummonBand.FromDockedEdge("Top", new PhysicalRect(1070, 0, 1700, 280)));
        Check.WithCustomMessage("the band's span must match the toolbar rect")
            .That(ActivationSummonBand.FromDockedEdge("Top", new PhysicalRect(1070, 0, 1700, 280)))
            .IsEqualTo(new ActivationSummonBand(ActivationSummonEdge.Top, 1070, 2770));

        h.Feed(new PhysicalPoint(1900, -40), samples: 20);        // inside the span: a summon

        Check.WithCustomMessage("the summon gaze made an offer").That(h.Im.ActivationOffers?.CurrentOffer).IsNull();
        Check.WithCustomMessage("the ring must still pin at the raw summon P")
            .That(h.PinPoints).ContainsExactly(new PhysicalPoint(1900, -40));

        h.FeedAway(samples: 20);                                  // end the run; the linger dies
        h.Feed(new PhysicalPoint(3600, -40), samples: 20);        // above the top edge, outside the span

        Check.WithCustomMessage("an above-the-edge settle outside the band must clamp and offer")
            .That(h.Im.ActivationOffers?.CurrentOffer?.Point).IsEqualTo(new PhysicalPoint(3600, 0));

        h.FeedAway(samples: 20);
        h.Feed(new PhysicalPoint(1900, 2440), samples: 20);       // BELOW the bottom edge, same x span

        Check.WithCustomMessage("the Top band must not apply to the bottom edge")
            .That(h.Im.ActivationOffers?.CurrentOffer?.Point).IsEqualTo(new PhysicalPoint(1900, 2399));
    }

    [Fact]
    public void TheSummonBand_AppliesOnWhateverEdgeIsDocked()
    {
        // "If the toolbar's edge is not Top, apply the band on that edge": a Left-docked toolbar's
        // band is the left of the screen over its y-span.
        using var h = new Harness();
        h.Im.SetActivationSummonBand(
            ActivationSummonBand.FromDockedEdge("Left", new PhysicalRect(0, 900, 280, 700)));

        h.Feed(new PhysicalPoint(-40, 1100), samples: 20);        // left of the screen, in the y-span
        Check.That(h.Im.ActivationOffers?.CurrentOffer).IsNull();

        h.FeedAway(samples: 20);
        h.Feed(new PhysicalPoint(-40, 2100), samples: 20);        // left of the screen, outside the span
        Check.That(h.Im.ActivationOffers?.CurrentOffer?.Point).IsEqualTo(new PhysicalPoint(0, 2100));

        Check.WithCustomMessage("an unknown edge must yield no band (never the exception)")
            .That(ActivationSummonBand.FromDockedEdge("Diagonal", new PhysicalRect(0, 900, 280, 700))).IsNull();
    }
}
