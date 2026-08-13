namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using NFluent;
using Xunit;

public class QuickActionsPageSourceTests
{
    private static SettingsPageContext Ctx() => TestSettingsPageContext.Build(viewModel: QaTestSupport.MakeViewModel());

    [Fact]
    public void ListPage_HasConfigControlsAndAddButton()
    {
        var rows = new QuickActionsPageSource(Ctx()).Build().First().Rows;
        Check.That(rows.OfType<ToggleSpec>().First().Id).IsEqualTo("qa_enabled");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("qa_add");
    }

    [Fact]
    public void ShortcutPicker_ButtonsUsePickerButtonWidth()
    {
        var src = new QuickActionsPageSource(Ctx());
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);   // direct call — GoTo/QaView are internal

        var preset = src.Build().First().Rows.OfType<ButtonSpec>().First(b => !b.Id.StartsWith("qa_back"));
        Check.That(preset.Width).IsEqualTo(SettingsWindow.PickerButtonWidth);
    }

    [Fact]
    public void ShortcutPicker_BackButtonHasPerPageId()
    {
        var src = new QuickActionsPageSource(Ctx());
        src.GoTo(QuickActionsPageSource.QaView.PickShortcut);
        var back = src.Build().First().Rows.OfType<ButtonSpec>().First(b => b.Id.StartsWith("qa_back"));
        Check.That(back.Id).IsEqualTo("qa_back_0");   // R5: per-page id preserved, not a constant "qa_back"
    }
}
