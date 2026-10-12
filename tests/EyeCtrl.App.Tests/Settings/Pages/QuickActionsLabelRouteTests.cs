namespace EyeCtrl.App.Tests.Settings.Pages;

using System;
using System.IO;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// The preset-label seam at its display sites (D2): the Settings list caption, the delete modal,
/// the picker's add path and the grid's binding. Each settings-site test stores a preset tile
/// whose stored label deliberately differs from the preset's English label, so a site that reads
/// the stored label directly shows the stale text and fails.
/// </summary>
public class QuickActionsLabelRouteTests
{
    /// <summary>A preset tile whose stored label is stale German, so a site that reads the stored
    /// label instead of the host's resolution shows the stale text. Internal for T12's
    /// shipped-German variant of the same sites.</summary>
    internal static QuickAction StalePresetTile() => new()
    {
        Type = QuickActionType.KeyboardShortcut,
        Payload = "Ctrl+C",
        Label = "Kopie (veraltet)",
        PresetId = "kbd.copy",
    };

    internal static QaTestSupport.FakeQuickActionsStore StoreWith(params QuickAction[] actions)
    {
        var store = new QaTestSupport.FakeQuickActionsStore();
        store.Save(actions);
        return store;
    }

    [Fact]
    public void ListCaption_ShowsTheHostsPresetLabel_NotTheStoredOne()
    {
        var src = new QuickActionsPageSource(
            new RecordingSettingsPageContext(viewModel: QaTestSupport.MakeViewModel(StoreWith(StalePresetTile()))).Context);

        var caption = src.Build().SelectMany(p => p.Rows).OfType<ToggleSpec>()
            .Single(t => t.Id.StartsWith("qa_en_")).Label;

        Check.That(caption).Contains("Copy");
        Check.That(caption).Not.Contains("Kopie (veraltet)");
    }

    [Fact]
    public void DeleteModal_ShowsTheHostsPresetLabel_NotTheStoredOne()
    {
        var rec = new RecordingSettingsPageContext(
            viewModel: QaTestSupport.MakeViewModel(StoreWith(StalePresetTile())));
        var src = new QuickActionsPageSource(rec.Context);

        var del = src.Build().SelectMany(p => p.Rows).OfType<ButtonSpec>()
            .Single(b => b.Id.StartsWith("qa_del_"));
        del.OnActivate();

        Check.That(rec.LastModalMessage).IsNotNull();
        Check.That(rec.LastModalMessage!).Contains("Copy");
        Check.That(rec.LastModalMessage!).Not.Contains("Kopie (veraltet)");
    }

    [Theory]
    [InlineData("PickShortcut", QuickActionType.KeyboardShortcut, "kbd.copy", "Copy", "Ctrl+C")]
    [InlineData("PickMedia", QuickActionType.MediaControl, "media.play_pause", "Play / Pause", "PlayPause")]
    [InlineData("PickWindowAction", QuickActionType.WindowAction, "window.minimize", "Minimize Window", "Minimize")]
    public void PickerAdd_StoresThePresetId_BesideTheEnglishLabel(
        string viewName, QuickActionType expectedType, string expectedPresetId,
        string expectedLabel, string expectedPayload)
    {
        var rec = new RecordingSettingsPageContext(
            viewModel: QaTestSupport.MakeViewModel(StoreWith()));
        var editor = rec.Context.ViewModel.QuickActionsEditor;
        var src = new QuickActionsPageSource(rec.Context);

        src.GoTo(Enum.Parse<QuickActionsPageSource.QaView>(viewName));
        var preset = src.Build().First().Rows.OfType<ButtonSpec>().First(b => !b.Id.StartsWith("qa_back"));
        preset.OnActivate();

        Check.That(editor.Actions).HasSize(1);
        Check.That(editor.Actions[0].Type).IsEqualTo(expectedType);
        Check.That(editor.Actions[0].PresetId).IsEqualTo(expectedPresetId);
        Check.That(editor.Actions[0].Label).IsEqualTo(expectedLabel);
        Check.That(editor.Actions[0].Payload).IsEqualTo(expectedPayload);
    }

    [Fact]
    public void GridTile_BindsTheTileViewModelsLabel_NotTheStoredActionLabel()
    {
        var xamlPath = Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Overlays", "QuickActionsGridWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Check.WithCustomMessage(
                "The grid tile must show QuickActionTileViewModel.Label — the host-resolved name")
             .That(xaml).Contains("{Binding Label}");
        Check.WithCustomMessage("Binding Action.Label would bypass the preset label seam")
             .That(xaml).Not.Contains("Action.Label");
    }
}
