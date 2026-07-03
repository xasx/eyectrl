namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ActionMenuViewModelSymbolTests
{
    [Fact]
    public void Receive_ShowActionMenu_ScrollModeItemHasGlyphSymbol()
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
        Check.That(item.Symbol).IsEqualTo("⇅");
    }

    [Fact]
    public void Receive_ShowActionMenu_EveryActionHasDistinctGlyph_NotEnumName()
    {
        // Guards the VM-as-single-source-of-symbols fix: every InteractionAction must map to its
        // intended glyph. A regression that dropped a case would fall through to action.ToString()
        // (the enum name), which this test rejects for each value.
        var expected = new Dictionary<InteractionAction, string>
        {
            [InteractionAction.LeftClick] = "☝",
            [InteractionAction.RightClick] = "☰",
            [InteractionAction.DoubleClick] = "✌",
            [InteractionAction.Drag] = "✊",
            [InteractionAction.ScrollUp] = "▲",
            [InteractionAction.ScrollDown] = "▼",
            [InteractionAction.ScrollLeft] = "◀",
            [InteractionAction.ScrollRight] = "▶",
            [InteractionAction.ScrollMode] = "⇅",
            [InteractionAction.PinNote] = "📌",
        };

        // Every enum value must be covered by the expected map (fails if a new action is added).
        var allActions = Enum.GetValues<InteractionAction>();
        Check.That(expected.Keys).IsEquivalentTo(allActions);

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

            var symbol = vm.Items.Single().Symbol;
            Check.WithCustomMessage($"{action} glyph").That(symbol).IsEqualTo(expected[action]);
            Check.WithCustomMessage($"{action} fell through to enum name").That(symbol).IsNotEqualTo(action.ToString());
        }
    }
}
