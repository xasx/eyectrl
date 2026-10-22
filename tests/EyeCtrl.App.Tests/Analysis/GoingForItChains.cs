namespace EyeCtrl.App.Tests.Analysis;

using System;
using System.Collections.Generic;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using static EyeCtrl.App.Tests.Analysis.RingPinModeReplayTests;

/// <summary>
/// The scripted chains for the going-for-it keep (the run-end pin at P kept while the gaze is on its way
/// to B), ported from the design's replay (<c>scratch/going-for-it</c>, <c>ScriptedChains</c>) so the
/// oracle compares the real VM with the replay model on them: P = (2400, 800), B on its diagonal toward
/// the screen centre. "To B" chains go for the button, "away" chains look elsewhere, "near miss" parks
/// short of it. Each ends with a far hold, so the ring's last move is part of the comparison.
/// </summary>
internal static class GoingForItChains
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalRect ToolbarRect = new(914, 0, 2012, 280);

    private sealed class Chain(int seed)
    {
        private readonly Random _rng = new(seed);
        private long _us = 1_000_000;
        public List<GazePoint> Samples { get; } = new();
        public (double X, double Y) At { get; private set; }

        public Chain Hold(double x, double y, int ms)
        {
            for (int t = 0; t < ms; t += 30) Add(x, y);
            return this;
        }

        /// <summary>A gap in the sample clock (the eye tracker lost the gaze): the next sample lands later.</summary>
        public Chain Skip(int ms)
        {
            _us += ms * 1000L;
            return this;
        }

        public Chain Move(double x, double y, int samples)
        {
            var from = At;
            for (int k = 1; k <= samples; k++)
                Add(from.X + ((x - from.X) * k / samples), from.Y + ((y - from.Y) * k / samples));
            return this;
        }

        private void Add(double x, double y)
        {
            Samples.Add(new GazePoint(x + (_rng.NextDouble() * 6) - 3, y + (_rng.NextDouble() * 6) - 3, _us, true));
            _us += 30_000;
            At = (x, y);
        }
    }

    private static PhysicalPoint ButtonFor(PhysicalPoint p) =>
        ActivationButtonPlacement.Place(p, SessionEnd.RingSizeDip, LayoutConstants.ActivationButtonOffset, 2.0,
            LayoutConstants.ActivationButtonHitRadiusPx, SessionEnd.RadiusPx, Screen, [ToolbarRect]).ButtonCenter;

    internal static List<(string Name, List<GazePoint> Samples)> All()
    {
        var p = new PhysicalPoint(2400, 800);
        var b = ButtonFor(p);
        double d = p.DistanceTo(b);
        (double X, double Y) u = ((b.X - p.X) / d, (b.Y - p.Y) / d);
        (double X, double Y) n = (-u.Y, u.X);
        (double X, double Y) Pt(double along, double lateral) =>
            (p.X + (u.X * along) + (n.X * lateral), p.Y + (u.Y * along) + (n.Y * lateral));
        (double X, double Y) Rot(double along, double deg)
        {
            double r = deg * Math.PI / 180;
            return (p.X + (along * ((u.X * Math.Cos(r)) - (u.Y * Math.Sin(r)))),
                p.Y + (along * ((u.X * Math.Sin(r)) + (u.Y * Math.Cos(r)))));
        }

        var list = new List<(string, List<GazePoint>)>();
        Chain Start(int seed) => new Chain(seed).Hold(p.X, p.Y, 1200);
        void Add(string name, Chain c) => list.Add((name, c.Hold(1000, 1700, 2600).Samples));

        Add("to B: saccade (3 samples)", Start(21).Move(b.X, b.Y, 3).Hold(b.X, b.Y, 800));
        Add("to B: drift 240 ms (8 samples)", Start(22).Move(b.X, b.Y, 8).Hold(b.X, b.Y, 800));
        Add("to B: drift 450 ms (15 samples)", Start(23).Move(b.X, b.Y, 15).Hold(b.X, b.Y, 800));
        var mid = Pt(0.5 * d, 50);
        Add("to B: stop halfway 300 ms", Start(24).Move(mid.X, mid.Y, 3).Hold(mid.X, mid.Y, 300).Move(b.X, b.Y, 3).Hold(b.X, b.Y, 800));
        Add("to B: stop halfway 600 ms", Start(25).Move(mid.X, mid.Y, 3).Hold(mid.X, mid.Y, 600).Move(b.X, b.Y, 3).Hold(b.X, b.Y, 800));
        var off = Pt(d, 120);
        Add("to B: lands 120 px off-axis", Start(26).Move(off.X, off.Y, 4).Hold(off.X, off.Y, 800));
        var miss = Pt(d, 190);
        Add("near miss: parks 190 px from B", Start(27).Move(miss.X, miss.Y, 4).Hold(miss.X, miss.Y, 1500));
        var perp = Pt(0, 500);
        Add("away: perpendicular 500 px", Start(28).Move(perp.X, perp.Y, 3).Hold(perp.X, perp.Y, 1200));
        var back = Pt(-500, 0);
        Add("away: behind P 500 px", Start(29).Move(back.X, back.Y, 3).Hold(back.X, back.Y, 1200));
        var past = Pt(900, 0);
        Add("away: past B toward the centre (900 px)", Start(30).Move(past.X, past.Y, 4).Hold(past.X, past.Y, 1200));
        var a30 = Rot(600, 30);
        Add("away: 30 degrees off B's direction, 600 px", Start(31).Move(a30.X, a30.Y, 3).Hold(a30.X, a30.Y, 1200));
        var a15 = Rot(450, 15);
        Add("away: 15 degrees off B's direction, 450 px", Start(32).Move(a15.X, a15.Y, 3).Hold(a15.X, a15.Y, 1200));
        // The sitting's typical start: the run ends while the filtered gaze is still ~40 px from P (a small
        // first step), then a slow approach; and the same small first step followed by a look elsewhere.
        var first = Pt(40, 0);
        Add("to B: small first step, then 300 ms to the hit", Start(33).Move(Pt(95, 0).X, Pt(95, 0).Y, 2).Move(first.X, first.Y, 1).Move(b.X, b.Y, 10).Hold(b.X, b.Y, 800));
        var side = Pt(40, 0);
        Add("away: small first step, then perpendicular 500 px", Start(34).Move(Pt(95, 0).X, Pt(95, 0).Y, 2).Move(side.X, side.Y, 1).Move(perp.X, perp.Y, 3).Hold(perp.X, perp.Y, 1200));
        // The fixation wanders off P first (real holds are a random walk), then the eye goes for B, or away.
        var wander = Pt(-60, 40);
        Add("to B: hold wanders 70 px off P, then to B", Start(35).Move(wander.X, wander.Y, 12).Hold(wander.X, wander.Y, 300).Move(b.X, b.Y, 3).Hold(b.X, b.Y, 800));
        Add("away: hold wanders 70 px off P, then perpendicular", Start(36).Move(wander.X, wander.Y, 12).Hold(wander.X, wander.Y, 300).Move(perp.X, perp.Y, 3).Hold(perp.X, perp.Y, 1200));
        // The run ends while the gaze is still near P: the sample clock gaps (700 ms > the detector's 500 ms
        // gap abort), and the first sample after the gap is 40 px from P (the sitting's first post-run frames
        // were 15-81 px from P). That frame is 360+ px from B: outside B's no-move zone but inside the P-B
        // corridor, so only the corridor decides these. The clean walks above end their run 85+ px from P.
        var near = Pt(40, 0);
        Chain Gapped(int seed) => Start(seed).Skip(700);
        Add("to B: run ends 40 px from P (a gap), then 330 ms to the hit", Gapped(41).Hold(near.X, near.Y, 60).Move(b.X, b.Y, 11).Hold(b.X, b.Y, 800));
        var early = Pt(60, 20);
        Add("to B: run ends 40 px from P, stops 60 px past P for 300 ms, then to B", Gapped(42).Hold(near.X, near.Y, 60).Move(early.X, early.Y, 2).Hold(early.X, early.Y, 300).Move(b.X, b.Y, 6).Hold(b.X, b.Y, 800));
        Add("to B: run ends 40 px from P, stops 60 px past P for 600 ms (the keep times out)", Gapped(43).Hold(near.X, near.Y, 60).Move(early.X, early.Y, 2).Hold(early.X, early.Y, 600).Move(b.X, b.Y, 6).Hold(b.X, b.Y, 800));
        Add("near miss: run ends 40 px from P, parks 190 px from B", Gapped(44).Hold(near.X, near.Y, 60).Move(miss.X, miss.Y, 6).Hold(miss.X, miss.Y, 1500));
        Add("away: run ends 40 px from P, then perpendicular 500 px", Gapped(45).Hold(near.X, near.Y, 60).Move(perp.X, perp.Y, 3).Hold(perp.X, perp.Y, 1200));
        var swerve = Pt(150, 130);
        Add("away: run ends 40 px from P, swerves 130 px off the axis", Gapped(46).Hold(near.X, near.Y, 60).Move(swerve.X, swerve.Y, 4).Hold(swerve.X, swerve.Y, 1200));
        var sideFar = Pt(40, 100);
        Add("away: run ends 100 px off the axis (outside the corridor)", Gapped(47).Hold(sideFar.X, sideFar.Y, 600));
        return list;
    }
}
