namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public class QuickActionTileViewModelTests
{
    // BMP glyphs only — the same constraint the toolbar region used (Supplementary-Plane
    // characters render as tofu in the WPF default font).
    [Theory]
    [InlineData("Minimize", "▁")]
    [InlineData("MaximizeRestore", "□")]
    [InlineData("Close", "✕")]
    public void Icon_WindowAction_UsesPerPayloadGlyph(string payload, string expected)
    {
        var action = new QuickAction
        {
            Type = QuickActionType.WindowAction,
            Label = "test",
            Payload = payload
        };

        var vm = new QuickActionTileViewModel(action);

        Check.That(vm.Icon).IsEqualTo(expected);
    }

    [Fact]
    public void Icon_WindowAction_UnknownPayload_FallsBackToGenericGlyph()
    {
        var action = new QuickAction
        {
            Type = QuickActionType.WindowAction,
            Label = "test",
            Payload = "NotAWindowAction"
        };

        var vm = new QuickActionTileViewModel(action);

        Check.That(vm.Icon).IsEqualTo("▣");
    }

    [Fact]
    public void Label_PresetTile_ShowsTheHostsLabel()
    {
        var action = new QuickAction
        {
            Type = QuickActionType.KeyboardShortcut,
            Payload = "Ctrl+C",
            Label = "Copy",
            PresetId = "kbd.copy",
        };

        var vm = new QuickActionTileViewModel(action, _ => "Kopieren");

        Check.That(vm.Label).IsEqualTo("Kopieren");
    }

    [Fact]
    public void Label_UserNamedTile_ShowsItsStoredLabel()
    {
        var action = new QuickAction { Type = QuickActionType.CannedText, Label = "Hi", Payload = "hi" };

        var vm = new QuickActionTileViewModel(action);

        Check.That(vm.Label).IsEqualTo("Hi");
    }
}
