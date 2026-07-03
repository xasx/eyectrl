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
}
