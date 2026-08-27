namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The action menu names a drawn icon per action. This file pinned Unicode glyph strings until
/// 2026-08-26; the glyphs are gone, but the property that mattered is kept verbatim — every
/// <see cref="InteractionAction"/> must map to a DISTINCT icon, and none may fall through to a
/// default.
///
/// The fall-through is the specific regression worth a test: the glyph switch ended in
/// <c>_ => action.ToString()</c>, so a dropped case rendered the enum name in the ring rather
/// than failing. An icon map has the same hazard in a different shape — a missing case throws at
/// the drawing layer instead, which for a user whose only input channel is an eye tracker means
/// an empty ring, not a wrong label.
/// </summary>
public class ActionMenuViewModelSymbolTests
{
    [Fact]
    public void Receive_ShowActionMenu_ScrollModeItemHasItsIcon()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500),
            new List<InteractionAction> { InteractionAction.ScrollMode },
            ShowBack: false,
            DwellTimeMs: 600,
            new PhysicalPoint(500, 500)));

        var item = vm.Items.Single();
        Check.That(item.Icon).IsEqualTo(ActionIcon.ScrollMode);
    }

    [Fact]
    public void EveryAction_MapsToADistinctIcon()
    {
        var expected = new Dictionary<InteractionAction, ActionIcon>
        {
            [InteractionAction.LeftClick] = ActionIcon.LeftClick,
            [InteractionAction.RightClick] = ActionIcon.RightClick,
            [InteractionAction.DoubleClick] = ActionIcon.DoubleClick,
            [InteractionAction.Drag] = ActionIcon.Drag,
            [InteractionAction.ScrollUp] = ActionIcon.ScrollUp,
            [InteractionAction.ScrollDown] = ActionIcon.ScrollDown,
            [InteractionAction.ScrollLeft] = ActionIcon.ScrollLeft,
            [InteractionAction.ScrollRight] = ActionIcon.ScrollRight,
            [InteractionAction.ScrollMode] = ActionIcon.ScrollMode,
            [InteractionAction.PinNote] = ActionIcon.PinNote,
        };

        // Fails if a new InteractionAction is added without an icon.
        var allActions = Enum.GetValues<InteractionAction>();
        Check.That(expected.Keys).IsEquivalentTo(allActions);

        // Two actions sharing an icon would render two identical targets.
        Check.That(expected.Values.Distinct().Count()).IsEqualTo(expected.Count);

        foreach (var action in allActions)
        {
            var messenger = new WeakReferenceMessenger();
            using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);
            messenger.Send(new ShowActionMenuMessage(
                new PhysicalPoint(500, 500),
                new List<InteractionAction> { action },
                ShowBack: false,
                DwellTimeMs: 600,
                new PhysicalPoint(500, 500)));

            Check.WithCustomMessage($"{action} icon")
                 .That(vm.Items.Single().Icon).IsEqualTo(expected[action]);
        }
    }

    [Fact]
    public void TheBackItem_TakesTheBackIcon()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new ActionMenuViewModel(new EyeCtrlSettings(), messenger);

        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500),
            new List<InteractionAction> { InteractionAction.LeftClick },
            ShowBack: true,
            DwellTimeMs: 600,
            new PhysicalPoint(500, 500)));

        var back = vm.Items.Single(i => i.IsBackButton);
        Check.That(back.Icon).IsEqualTo(ActionIcon.Back);
    }
}
