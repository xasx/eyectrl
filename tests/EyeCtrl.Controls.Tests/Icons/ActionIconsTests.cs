namespace EyeCtrl.Controls.Tests.Icons;

using System;
using System.Globalization;
using System.Linq;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using NFluent;
using Xunit;
using static EyeCtrl.Controls.Icons.ActionIcons;

/// <summary>
/// Pins the icon registry's completeness and its grid discipline.
///
/// The bounds check is the one that earns its place. Path data is hand-authored, and a
/// transposed digit produces a shape that still parses, still renders, and sits half outside the
/// button — a defect with no exception and no visual cue beyond "that icon looks wrong", on a
/// surface the user cannot inspect closely without spending dwell on it.
/// </summary>
public class ActionIconsTests
{
    public ActionIconsTests()
    {
        // Geometry.ToString() formats path data with the CURRENT culture (de-DE emits
        // "M4;8A2;2;…", en-US "M4,8A2,2,…"), so the LatchedRim substring assertion below is only
        // deterministic when the culture is pinned to the one LatchedRim is authored in. The
        // other checks in this class are culture-neutral.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Fact]
    public void EveryActionIcon_HasGeometry()
    {
        foreach (var icon in Enum.GetValues<ActionIcon>())
        {
            var geometry = ActionIcons.For(icon);

            Check.WithCustomMessage($"{icon} resolved to null").That(geometry).IsNotNull();
            Check.WithCustomMessage($"{icon} has neither a stroke nor a fill layer")
                 .That(geometry.Stroke is not null || geometry.Fill is not null).IsTrue();
        }
    }

    [Fact]
    public void EveryIcon_FitsTheGrid()
    {
        // Half the stroke sits outside the path's own bounds, so the usable box is inset by it.
        var slack = ActionIcons.GridStrokeThickness / 2.0;

        foreach (var icon in Enum.GetValues<ActionIcon>())
        {
            var geometry = ActionIcons.For(icon);

            foreach (var layer in new[] { geometry.Stroke, geometry.Fill }.Where(g => g is not null))
            {
                var b = layer!.Bounds;
                var msg = $"{icon} bounds {b} escape the {ActionIcons.GridSize}-unit grid";

                Check.WithCustomMessage(msg).That(b.Left).IsStrictlyGreaterThan(-slack);
                Check.WithCustomMessage(msg).That(b.Top).IsStrictlyGreaterThan(-slack);
                Check.WithCustomMessage(msg).That(b.Right).IsStrictlyLessThan(ActionIcons.GridSize + slack);
                Check.WithCustomMessage(msg).That(b.Bottom).IsStrictlyLessThan(ActionIcons.GridSize + slack);
            }
        }
    }

    [Fact]
    public void EveryGeometry_IsFrozen()
    {
        // The registry is a static shared across every button on screen. An unfrozen Geometry is
        // mutable and thread-affine; frozen ones are neither.
        foreach (var icon in Enum.GetValues<ActionIcon>())
        {
            var geometry = ActionIcons.For(icon);

            if (geometry.Stroke is not null)
                Check.WithCustomMessage($"{icon} stroke is not frozen").That(geometry.Stroke.IsFrozen).IsTrue();
            if (geometry.Fill is not null)
                Check.WithCustomMessage($"{icon} fill is not frozen").That(geometry.Fill.IsFrozen).IsTrue();
        }
    }

    [Theory]
    [InlineData(ActionIcon.Shift)]
    [InlineData(ActionIcon.Ctrl)]
    [InlineData(ActionIcon.Alt)]
    [InlineData(ActionIcon.ShiftLatched)]
    [InlineData(ActionIcon.CtrlLatched)]
    [InlineData(ActionIcon.AltLatched)]
    public void ModifierIcons_ResolveAndAreFrozen(ActionIcon icon)
    {
        var geometry = ActionIcons.For(icon);

        Check.That(geometry.Stroke).IsNotNull();
        Check.That(geometry.Stroke!.IsFrozen).IsTrue();
        if (geometry.Fill is not null)
            Check.That(geometry.Fill.IsFrozen).IsTrue();
    }

    [Fact]
    public void LatchedVariants_AddTheFilledMarginDot()
    {
        Check.That(ActionIcons.For(ActionIcon.Shift).Fill).IsNull();
        Check.That(ActionIcons.For(ActionIcon.ShiftLatched).Fill).IsNotNull();
        // The PRIMARY latched cue is the rim in the STROKE geometry (outline-level, peripheral-
        // vision rule) — pin that the latched stroke differs from the unlatched glyph:
        Check.That(ActionIcons.For(ActionIcon.ShiftLatched).Stroke!.ToString().Contains(LatchedRim)).IsTrue();
    }

    [Fact]
    public void LeftAndRightClick_AreNotTheSameShape()
    {
        // They differ only in which half of the mouse is filled, and a copy-paste slip that left
        // both filling the left would produce two identical targets in the ring.
        var left = ActionIcons.For(ActionIcon.LeftClick);
        var right = ActionIcons.For(ActionIcon.RightClick);

        Check.That(left.Fill!.Bounds).IsNotEqualTo(right.Fill!.Bounds);
    }

    [Fact]
    public void DoubleClick_BreaksTheMouseSilhouette()
    {
        // With labels off, double click must differ from left click in OUTLINE, not in detail:
        // a mark inside the same silhouette is invisible in the periphery, which is where a gaze
        // user finds a target before saccading to it. The ripples reach left of the mouse body
        // (x=7) and the numeral reaches right of it (x=17).
        var single = ActionIcons.For(ActionIcon.LeftClick).Stroke!.Bounds;
        var doubled = ActionIcons.For(ActionIcon.DoubleClick).Stroke!.Bounds;

        Check.WithCustomMessage("DoubleClick does not extend left of LeftClick")
             .That(doubled.Left).IsStrictlyLessThan(single.Left);
        Check.WithCustomMessage("DoubleClick does not extend right of LeftClick")
             .That(doubled.Right).IsStrictlyGreaterThan(single.Right);
    }
}
