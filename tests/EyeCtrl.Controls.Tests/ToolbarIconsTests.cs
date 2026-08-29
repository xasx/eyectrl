namespace EyeCtrl.Controls.Tests;

using System;
using System.Windows;
using EyeCtrl.Controls.Icons;
using NFluent;
using Xunit;

/// <summary>
/// The toolbar's icons are drawn geometry, not font glyphs — a font missing the codepoint
/// renders tofu, which already happened to the window-switcher button and left it on the far
/// less readable arrows glyph. These tests cannot say an icon looks right; they say every key
/// resolves, every geometry is non-degenerate and frozen, and every one stays on the same
/// 24-unit grid the action menu's icons use, so the two sets scale and weigh identically.
/// </summary>
public class ToolbarIconsTests
{
    public static TheoryData<ToolbarIconKey> AllKeys()
    {
        var data = new TheoryData<ToolbarIconKey>();
        foreach (var key in Enum.GetValues<ToolbarIconKey>())
            data.Add(key);
        return data;
    }

    private static Rect UnionBounds(IconGeometry g)
    {
        var bounds = Rect.Empty;
        if (g.Stroke is not null) bounds.Union(g.Stroke.Bounds);
        if (g.Fill is not null) bounds.Union(g.Fill.Bounds);
        return bounds;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryKey_ResolvesToNonDegenerateFrozenGeometry(ToolbarIconKey key)
    {
        var g = ToolbarIcons.For(key);

        Check.That(g.Stroke is not null || g.Fill is not null).IsTrue();
        if (g.Stroke is not null) Check.That(g.Stroke.IsFrozen).IsTrue();
        if (g.Fill is not null) Check.That(g.Fill.IsFrozen).IsTrue();

        var bounds = UnionBounds(g);
        Check.WithCustomMessage($"{key} has empty bounds").That(bounds.IsEmpty).IsFalse();
        Check.WithCustomMessage($"{key} is under 4 grid units across")
            .That(Math.Max(bounds.Width, bounds.Height)).IsStrictlyGreaterThan(4.0);
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void EveryKey_StaysOnTheSharedGrid(ToolbarIconKey key)
    {
        // Same grid as ActionIcons: DwellArcButton scales by iconSize / ActionIcons.GridSize,
        // so an icon drawn outside the box renders clipped or off-centre at every call site.
        var bounds = UnionBounds(ToolbarIcons.For(key));
        var slack = ActionIcons.GridStrokeThickness / 2.0;

        Check.WithCustomMessage($"{key} bounds {bounds} escape the {ActionIcons.GridSize} grid")
            .That(bounds.Left >= -slack && bounds.Top >= -slack
                  && bounds.Right <= ActionIcons.GridSize + slack
                  && bounds.Bottom <= ActionIcons.GridSize + slack)
            .IsTrue();
    }

    [Fact]
    public void TheRegistry_CoversEveryKey()
    {
        // For() throws on a missing entry; this makes a new enum member fail here rather than at
        // window-construction time in front of a user whose only input channel is an eye tracker.
        foreach (var key in Enum.GetValues<ToolbarIconKey>())
            Check.ThatCode(() => ToolbarIcons.For(key)).DoesNotThrow();
    }
}
