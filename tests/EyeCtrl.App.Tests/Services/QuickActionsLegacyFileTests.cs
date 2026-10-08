namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Linq;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The one-time preset match against the REAL <see cref="QuickActionsStore"/>, over files shaped
/// exactly as pre-T03d wrote them. Extends the editor-VM tests' in-memory fakes: these pin the
/// user-data guarantees on disk — ids, order, labels, flags and payloads survive, only exact
/// matches gain a <see cref="QuickAction.PresetId"/>, nothing is rewritten unless something
/// changed, and a corrupt file is never written over.
/// </summary>
public class QuickActionsLegacyFileTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"eyectrl_test_actions_{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    // A pre-T03d file, as 4b1ab232 wrote it: no PresetId anywhere, seven tiles in user order.
    private const string Legacy = """
{
  "Actions": [
    { "Id": "a1", "Type": 1, "Label": "Hello", "Payload": "hi", "Enabled": true },
    { "Id": "a2", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C", "Enabled": false },
    { "Id": "a3", "Type": 0, "Label": "My copy", "Payload": "Ctrl+C", "Enabled": true },
    { "Id": "a4", "Type": 5, "Label": "Play / Pause", "Payload": "PlayPause", "Enabled": true },
    { "Id": "a5", "Type": 6, "Label": "Close Window", "Payload": "Close", "Enabled": true },
    { "Id": "a6", "Type": 0, "Label": "copy", "Payload": "Ctrl+C", "Enabled": true },
    { "Id": "a7", "Type": 4, "Label": "Web", "Payload": "https://x", "Enabled": true }
  ]
}
""";

    private static string Dump(IReadOnlyList<QuickAction> xs) => string.Join("\n",
        xs.Select(a => $"{a.Id}|{a.Type}|{a.Label}|{a.Payload}|{a.Enabled}|{a.PresetId ?? "<null>"}"));

    [Fact]
    public void LegacyFile_LoadSave_IsEquivalent_OrderAndFieldsKept_OnlyExactMatchesGainIds()
    {
        File.WriteAllText(_file, Legacy);
        var store = new QuickActionsStore(_file);
        var before = Dump(store.LoadAll());

        var editor = new SettingsQuickActionsEditorViewModel(store, new QaTestSupport.FakeInstalledAppProvider());
        editor.Load();

        var after = store.LoadAll();
        Check.WithCustomMessage($"before:\n{before}\nafter:\n{Dump(after)}")
             .That(after.Select(a => a.Id)).ContainsExactly("a1", "a2", "a3", "a4", "a5", "a6", "a7");
        Check.That(after.Select(a => a.Type)).ContainsExactly(
            QuickActionType.CannedText, QuickActionType.KeyboardShortcut, QuickActionType.KeyboardShortcut,
            QuickActionType.MediaControl, QuickActionType.WindowAction, QuickActionType.KeyboardShortcut,
            QuickActionType.LaunchUrl);
        Check.That(after.Select(a => a.Label)).ContainsExactly(
            "Hello", "Copy", "My copy", "Play / Pause", "Close Window", "copy", "Web");
        Check.That(after.Select(a => a.Payload)).ContainsExactly(
            "hi", "Ctrl+C", "Ctrl+C", "PlayPause", "Close", "Ctrl+C", "https://x");
        Check.That(after.Select(a => a.Enabled)).ContainsExactly(true, false, true, true, true, true, true);
        Check.That(after.Select(a => a.PresetId)).ContainsExactly(
            null, "kbd.copy", null, "media.play_pause", "window.close", null, null);

        // Second open: no further write.
        var stamp = File.GetLastWriteTimeUtc(_file);
        var bytes = File.ReadAllText(_file);
        Thread.Sleep(50);
        new SettingsQuickActionsEditorViewModel(store, new QaTestSupport.FakeInstalledAppProvider()).Load();
        Check.That(File.ReadAllText(_file)).IsEqualTo(bytes);
        Check.That(File.GetLastWriteTimeUtc(_file)).IsEqualTo(stamp);
    }

    [Fact]
    public void LegacyFile_NoMatches_IsNotRewritten()
    {
        const string noMatch = """{ "Actions": [ { "Id": "a1", "Type": 1, "Label": "Hello", "Payload": "hi", "Enabled": true } ] }""";
        File.WriteAllText(_file, noMatch);
        var stamp = File.GetLastWriteTimeUtc(_file);
        Thread.Sleep(50);
        new SettingsQuickActionsEditorViewModel(new QuickActionsStore(_file), new QaTestSupport.FakeInstalledAppProvider()).Load();
        Check.That(File.ReadAllText(_file)).IsEqualTo(noMatch);
        Check.That(File.GetLastWriteTimeUtc(_file)).IsEqualTo(stamp);
    }

    [Fact]
    public void LegacyFile_NullPayload_SurvivesTheMatchedSave()
    {
        // N1: the null omission is scoped to PresetId alone, so a legacy explicit null Payload
        // stays null through the matched save instead of becoming the property's "" default.
        const string withNull = """{ "Actions": [ { "Id": "a1", "Type": 1, "Label": "Hello", "Payload": null, "Enabled": true }, { "Id": "a2", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C", "Enabled": true } ] }""";
        File.WriteAllText(_file, withNull);
        var store = new QuickActionsStore(_file);
        Check.That(store.LoadAll()[0].Payload).IsNull();

        new SettingsQuickActionsEditorViewModel(store, new QaTestSupport.FakeInstalledAppProvider()).Load();

        var after = store.LoadAll();
        Check.That(after[0].Payload).IsNull();
        Check.That(after[1].PresetId).IsEqualTo("kbd.copy");
        Check.That(File.ReadAllText(_file)).Contains("\"Payload\": null");
    }

    [Fact]
    public void CorruptFile_IsNotOverwrittenByTheMatch()
    {
        const string corrupt = """{ "Actions": [ { "Id": "a2", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C" """;
        File.WriteAllText(_file, corrupt);
        new SettingsQuickActionsEditorViewModel(new QuickActionsStore(_file), new QaTestSupport.FakeInstalledAppProvider()).Load();
        Check.That(File.ReadAllText(_file)).IsEqualTo(corrupt);
    }
}
