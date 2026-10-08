namespace EyeCtrl.Core.Tests.ActionMenu;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// The G2 fixture matrix: 1..9 visible actions × 0..4 primary × Back on/off, plus the named
/// regressions the plan exists to prevent.
///
/// The composition rules pinned here are the runtime ring's, quoted from
/// <c>ActionMenuViewModel.BuildLayer</c> (the App-side copy is what this plan will replace in
/// T3): slots follow the VISIBLE list's order filtered by primary membership, More is appended
/// only when the second layer has something on it, layer two always ends in a Back to layer one,
/// and the zoom Back lives on layer one alone.
/// </summary>
public class RingPlanTests
{
    private static readonly IReadOnlyList<InteractionAction> AllActions = Enum.GetValues<InteractionAction>();

    private static double DefaultCircleSize => new CursorSettings().CircleSize;

    private sealed record ExpectedSlot(
        InteractionAction? Action, ActionMenuItemKind Kind, string Id, ActionIcon Icon, string Label);

    private static ExpectedSlot ActionSlot(InteractionAction action) => new(
        action, ActionMenuItemKind.Action, action.ToString(), ActionIconMap.For(action), RingLabelMap.For(action));

    private static readonly ExpectedSlot MoreSlot =
        new(null, ActionMenuItemKind.OpenMore, "More", ActionIcon.More, "More");

    private static readonly ExpectedSlot BackToPrimarySlot =
        new(null, ActionMenuItemKind.BackToPrimary, "BackToPrimary", ActionIcon.Back, "Back");

    private static readonly ExpectedSlot ExitMenuSlot =
        new(null, ActionMenuItemKind.ExitMenu, "Back", ActionIcon.Back, "Back");

    /// <summary>The fixture matrix: every (visible, primary, back) combination the ring can be asked to draw.</summary>
    public static IEnumerable<object[]> FixtureMatrix()
    {
        for (var visibleCount = 1; visibleCount <= 9; visibleCount++)
        {
            for (var primaryCount = 0; primaryCount <= 4; primaryCount++)
            {
                for (var showBack = 0; showBack < 2; showBack++)
                    yield return new object[] { visibleCount, primaryCount, showBack == 1 };
            }
        }
    }

    /// <summary>
    /// The primary list is handed over in REVERSE visible order on purpose: the plan must emit
    /// slots in visible order, and a regression to primary-list order (the deleted
    /// <c>EffectivePrimaryActions</c>' semantics) would flip every angle here. When the count
    /// exceeds the visible list, the extras are hidden primaries, which must be dropped.
    /// </summary>
    private static List<InteractionAction> PrimarySetFor(IReadOnlyList<InteractionAction> visible, int primaryCount)
        => primaryCount <= visible.Count
            ? visible.TakeLast(primaryCount).Reverse().ToList()
            : AllActions.Take(primaryCount).Reverse().ToList();

    [Theory]
    [MemberData(nameof(FixtureMatrix))]
    public void BothLayers_MatchTheCompositionAndGeometryRules(int visibleCount, int primaryCount, bool showBack)
    {
        var visible = AllActions.Take(visibleCount).ToList();
        var primary = PrimarySetFor(visible, primaryCount);
        var plan = RingPlan.Create(visible, primary, DefaultCircleSize, RingLabelSource.English);

        var expectedPrimary = visible.Where(primary.Contains).ToList();
        var expectedSecondary = visible.Where(a => !primary.Contains(a)).ToList();

        Check.WithCustomMessage($"{visibleCount} visible / {primaryCount} primary: partition")
             .That(plan.PrimaryActions).ContainsExactly(expectedPrimary);
        Check.WithCustomMessage($"{visibleCount} visible / {primaryCount} primary: secondary")
             .That(plan.SecondaryActions).ContainsExactly(expectedSecondary);

        var expectedLayerOne = expectedPrimary.Select(ActionSlot).ToList();
        if (expectedSecondary.Count > 0)
            expectedLayerOne.Add(MoreSlot);
        if (showBack)
            expectedLayerOne.Add(ExitMenuSlot);

        var expectedLayerTwo = expectedSecondary.Select(ActionSlot).ToList();
        expectedLayerTwo.Add(BackToPrimarySlot);

        AssertLayer(plan.BuildLayer(RingLayer.Primary, showBack), expectedLayerOne);
        AssertLayer(plan.BuildLayer(RingLayer.Secondary, showBack), expectedLayerTwo);
    }

    private static void AssertLayer(RingLayerPlan layer, IReadOnlyList<ExpectedSlot> expected)
    {
        Check.That(layer.Slots.Select(s => s.Action)).ContainsExactly(expected.Select(e => e.Action));
        Check.That(layer.Slots.Select(s => s.Kind)).ContainsExactly(expected.Select(e => e.Kind));
        Check.That(layer.Slots.Select(s => s.Id)).ContainsExactly(expected.Select(e => e.Id));
        Check.That(layer.Slots.Select(s => s.Icon)).ContainsExactly(expected.Select(e => e.Icon));
        Check.That(layer.Slots.Select(s => s.Label)).ContainsExactly(expected.Select(e => e.Label));

        var expectedStep = expected.Count == 0 ? 0.0 : 360.0 / expected.Count;
        Check.That(layer.AngleStepDegrees).IsCloseTo(expectedStep, 1e-9);

        for (var i = 0; i < layer.Slots.Count; i++)
        {
            Check.That(layer.Slots[i].Index).IsEqualTo(i);
            Check.That(layer.Slots[i].AngleDegrees).IsCloseTo(expectedStep * i - 90.0, 1e-9);
        }
    }

    [Fact]
    public void PrimaryOrder_FollowsTheVisibleList_NotThePrimaryList()
    {
        var visible = new List<InteractionAction>
        {
            InteractionAction.Keyboard, InteractionAction.LeftClick,
            InteractionAction.PinNote, InteractionAction.Drag,
        };
        // Deliberately the reverse of the visible order.
        var primary = new List<InteractionAction> { InteractionAction.PinNote, InteractionAction.Keyboard };

        var plan = RingPlan.Create(visible, primary, DefaultCircleSize, RingLabelSource.English);

        Check.That(plan.PrimaryActions).ContainsExactly(
            InteractionAction.Keyboard, InteractionAction.PinNote);
        Check.That(plan.BuildLayer(RingLayer.Primary, showBack: false).Slots.Select(s => s.Action))
             .ContainsExactly(
                 InteractionAction.Keyboard, InteractionAction.PinNote, null);
    }

    [Fact]
    public void PrimaryList_IsNotClampedToFour()
    {
        // MaxPrimaryActions is enforced at the settings boundary (Validate), not by the plan:
        // BuildLayer never clamped, so a plan built from a hand-picked message must not drop
        // slots either.
        var layer = RingPlan.Create(AllActions, AllActions, DefaultCircleSize, RingLabelSource.English)
                            .BuildLayer(RingLayer.Primary, showBack: false);

        Check.That(layer.Slots).HasSize(AllActions.Count);
        Check.That(layer.Slots.Select(s => s.Kind)).ContainsOnlyElementsThatMatch(k => k == ActionMenuItemKind.Action);
    }

    [Fact]
    public void NullLists_AreTreatedAsEmpty()
    {
        var plan = RingPlan.Create(null, null, DefaultCircleSize, RingLabelSource.English);

        Check.That(plan.PrimaryActions).IsEmpty();
        Check.That(plan.SecondaryActions).IsEmpty();
        Check.That(plan.BuildLayer(RingLayer.Primary, showBack: false).Slots).IsEmpty();

        // Parity with BuildLayer: layer two always appends its Back, even when nothing precedes it.
        var layerTwo = plan.BuildLayer(RingLayer.Secondary, showBack: false);
        Check.That(layerTwo.Slots).HasSize(1);
        Check.That(layerTwo.Slots[0].Kind).IsEqualTo(ActionMenuItemKind.BackToPrimary);
    }

    [Fact]
    public void NoSecondaryActions_MeansNoMoreTarget()
    {
        var visible = new List<InteractionAction> { InteractionAction.LeftClick, InteractionAction.RightClick };

        var layer = RingPlan.Create(visible, visible, DefaultCircleSize, RingLabelSource.English)
                            .BuildLayer(RingLayer.Primary, showBack: false);

        Check.WithCustomMessage("A More target that opens an empty layer is a dead end")
             .That(layer.Slots.Select(s => s.Kind)).Not.Contains(ActionMenuItemKind.OpenMore);
    }

    [Fact]
    public void ShowBack_OnlyAffectsLayerOne()
    {
        var plan = RingPlan.Create(AllActions.Take(5).ToList(), AllActions.Take(2).ToList(), DefaultCircleSize, RingLabelSource.English);

        Check.That(plan.BuildLayer(RingLayer.Secondary, showBack: true).Slots.Select(s => s.Id))
             .ContainsExactly(plan.BuildLayer(RingLayer.Secondary, showBack: false).Slots.Select(s => s.Id));
        Check.That(plan.BuildLayer(RingLayer.Primary, showBack: true).Slots.Select(s => s.Kind))
             .Contains(ActionMenuItemKind.ExitMenu);
        Check.That(plan.BuildLayer(RingLayer.Primary, showBack: false).Slots.Select(s => s.Kind))
             .Not.Contains(ActionMenuItemKind.ExitMenu);
    }

    [Fact]
    public void HostLabelSource_DecidesEverySlotLabel_BackAndMoreIncluded()
    {
        var labels = new RingLabelSource(
            action => $"A:{action}",
            BackLabel: "Zurück",
            MoreLabel: "Mehr");

        var plan = RingPlan.Create(
            AllActions.Take(5).ToList(), AllActions.Take(3).ToList(), DefaultCircleSize, labels);

        Check.That(plan.BuildLayer(RingLayer.Primary, showBack: true).Slots.Select(s => s.Label))
             .ContainsExactly("A:LeftClick", "A:RightClick", "A:DoubleClick", "Mehr", "Zurück");

        // Layer two's Back-to-first-layer slot is the host's Back label too.
        Check.That(plan.BuildLayer(RingLayer.Secondary, showBack: false).Slots.Select(s => s.Label))
             .ContainsExactly("A:MiddleClick", "A:Drag", "Zurück");
    }

    [Fact]
    public void DefaultLabelSource_IsTheEnglishRingVocabulary()
    {
        var plan = RingPlan.Create(AllActions.Take(3).ToList(), AllActions.Take(1).ToList(), DefaultCircleSize, RingLabelSource.English);

        Check.That(plan.BuildLayer(RingLayer.Primary, showBack: true).Slots.Select(s => s.Label))
             .ContainsExactly("Left click", "More", "Back");
    }
}
