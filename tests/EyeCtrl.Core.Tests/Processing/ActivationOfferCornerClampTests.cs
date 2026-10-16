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

    [Fact]
    public void Refusals_RaiseOncePerReasonChange_WithTheRawP()
    {
        // F3: one line per CHANGE of refusal reason, never per settle or per sample. This collector
        // is the fake sink the App's adapter renders; an offer made in between resets the latch.
        // Mutants: raise on every refusal → the second same-reason refusal adds a line; never raise
        // → the first line is missing.
        using var h = new Harness();
        var refusals = new List<ActivationOfferRefusal>();
        h.Im.ActivationOfferRefused += r => refusals.Add(r);

        h.Feed(new PhysicalPoint(Screen.Width + T + 10, 1200), samples: 20);   // far off-screen

        Check.That(refusals).HasSize(1);
        Check.That(refusals[0].Reason).IsEqualTo(ActivationOfferRefusalReason.OffScreen);
        Check.That(refusals[0].Point).IsEqualTo(new PhysicalPoint(Screen.Width + T + 10, 1200));

        h.FeedAway(samples: 20);
        h.Feed(new PhysicalPoint(Screen.Width + T + 10, 1200), samples: 20);   // same reason again

        Check.WithCustomMessage("a repeated same-reason refusal must not raise again")
            .That(refusals).HasSize(1);

        h.FeedAway(samples: 20);
        h.Im.SetActivationChromeZones(new PhysicalRect(1500, 1000, 800, 800));
        h.Feed(new PhysicalPoint(1900, 1400), samples: 20);                    // in chrome

        Check.That(refusals).HasSize(2);
        Check.That(refusals[1].Reason).IsEqualTo(ActivationOfferRefusalReason.InChrome);

        h.FeedAway(samples: 20);
        h.Im.SetActivationChromeZones();                                       // no zones
        h.Feed(new PhysicalPoint(1900, 1400), samples: 20);                    // ...an offer is made
        Check.That(h.Im.ActivationOffers?.CurrentOffer).IsNotNull();

        h.FeedAway(samples: 20);
        h.Feed(new PhysicalPoint(Screen.Width + T + 10, 1200), samples: 20);   // same reason, new line

        Check.WithCustomMessage("after an offer was made the next refusal must raise again")
            .That(refusals).HasSize(3);
        Check.That(refusals[2].Reason).IsEqualTo(ActivationOfferRefusalReason.OffScreen);

        h.Im.SetActivationOffersSuspended(true);
        h.FeedAway(samples: 20);
        h.Feed(new PhysicalPoint(1900, 1400), samples: 20);                    // a modal is up

        Check.That(refusals).HasSize(4);
        Check.That(refusals[3].Reason).IsEqualTo(ActivationOfferRefusalReason.Suspended);
    }

    [Fact]
    public void TonightsCornerSettles_ReplayAsExpected()
    {
        // The investigation's corner settles (2026-10-16, 23:33-23:46) with tonight's zone set:
        // the toolbar [1070,0 1700×280] and the lag-mark button [3280,1872 512×384] (the T0(a)
        // instrument was on for the whole look). Expected after the fix: on-screen rows behave
        // exactly as the recorded table; off-screen rows within T are clamped and placed -- EXCEPT
        // at the BR corner, where the lag zone still kills the clamped placement (it owns BR, and
        // is out of scope). The final block drops the lag zone: the same six BR rows then place at
        // their corner pixel, which is the fix's effect in an ordinary (lag-free) session.
        //
        // The table's "6 × TR, shown" group is represented by its two recorded points; the
        // (3479,170) row is excluded (a ManualSelection pin, never an offer settle).
        var toolbar = new PhysicalRect(1070, 0, 1700, 280);
        var lag = new PhysicalRect(3280, 1872, 512, 384);
        PhysicalPoint P(double x, double y) => new(x, y);

        (PhysicalPoint Settle, PhysicalRect[] Zones, PhysicalPoint? OfferedAt, ActivationOfferRefusalReason? Refused)[] rows =
        [
            // BR, tonight's full zone set: the six off-screen rows clamp, then the lag zone drops
            // the placement (NoSidePlaceable); the on-screen losers are the table's rows.
            (P(4017, 2380), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3783, 2346), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3716, 2228), [toolbar, lag], null, ActivationOfferRefusalReason.InChrome),
            (P(3881, 2470), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3900, 2517), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3696, 2149), [toolbar, lag], null, ActivationOfferRefusalReason.InChrome),
            (P(3637, 2321), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3750, 2408), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3628, 2269), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3695, 2328), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3795, 2587), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3876, 2498), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3814, 2326), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3787, 2169), [toolbar, lag], null, ActivationOfferRefusalReason.InChrome),
            (P(3653, 2260), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            (P(3786, 2287), [toolbar, lag], null, ActivationOfferRefusalReason.NoSidePlaceable),
            // TR: shown as before (on-screen), and the summon gaze now clamps instead of refusing.
            (P(3680, 52), [toolbar, lag], P(3680, 52), null),
            (P(3737, 348), [toolbar, lag], P(3737, 348), null),
            (P(3861, -48), [toolbar, lag], P(3839, 0), null),
            // The same six BR off-screen rows with no lag button: the clamp's effect, in an
            // ordinary session.
            (P(4017, 2380), [toolbar], P(3839, 2380), null),
            (P(3881, 2470), [toolbar], P(3839, 2399), null),
            (P(3900, 2517), [toolbar], P(3839, 2399), null),
            (P(3750, 2408), [toolbar], P(3750, 2399), null),
            (P(3795, 2587), [toolbar], P(3795, 2399), null),
            (P(3876, 2498), [toolbar], P(3839, 2399), null),
        ];

        foreach (var (settle, zones, offeredAt, refused) in rows)
        {
            using var h = new Harness();
            h.Im.SetActivationChromeZones(zones);
            var refusals = new List<ActivationOfferRefusal>();
            h.Im.ActivationOfferRefused += r => refusals.Add(r);

            h.Feed(settle, samples: 20);
            var offer = h.Im.ActivationOffers?.CurrentOffer;

            if (offeredAt is { } expected)
            {
                Check.WithCustomMessage(
                        $"({settle.X},{settle.Y}): expected an offer at ({expected.X},{expected.Y}); got "
                        + (offer is null ? "none" : $"({offer.Point.X},{offer.Point.Y})"))
                    .That(offer).IsNotNull();
                Check.That(offer!.Point).IsEqualTo(expected);
            }
            else
            {
                Check.WithCustomMessage(
                        $"({settle.X},{settle.Y}): expected a refusal; got "
                        + (offer is null ? "a refusal" : $"an offer at ({offer.Point.X},{offer.Point.Y})"))
                    .That(offer).IsNull();
                Check.WithCustomMessage($"({settle.X},{settle.Y}): refusal reason")
                    .That(refusals).HasSize(1);
                Check.That(refusals[0].Reason).IsEqualTo(refused);
            }
        }
    }
}
