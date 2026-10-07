namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// F4 (T03d review 01): the one-time preset match runs when the quick-action store is first
/// resolved — at startup, before any surface reads the file — not only when the Settings window
/// opens. These tests resolve the real DI container over a throwaway app-data folder, so the
/// store under test is the production one, and assert the same user-data rules the editor tests
/// pin: order kept, only exact matches gain ids, no write when nothing changed.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class QuickActionsBootstrapMatchTests
{
    private static ServiceProvider BuildProvider(string appData)
        => AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(appData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: appData);

    private static string ActionsPath(string appData) => Path.Combine(appData, "actions.json");

    [Fact]
    public void ResolvingTheStore_MatchesLegacyTiles_Once_KeepsOrder_AndPersists()
    {
        var appData = TestAppDataDir.Create("qa-bootstrap");
        File.WriteAllText(ActionsPath(appData), """
            {
              "Actions": [
                { "Id": "a1", "Type": 1, "Label": "Hello", "Payload": "hi", "Enabled": true },
                { "Id": "a2", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C", "Enabled": true }
              ]
            }
            """);

        using var sp = BuildProvider(appData);
        var store = sp.GetRequiredService<IQuickActionsStore>();

        var loaded = store.LoadAll();
        Check.That(loaded.Select(a => a.Id)).ContainsExactly("a1", "a2");
        Check.That(loaded.Select(a => a.PresetId)).ContainsExactly(null, "kbd.copy");

        // The result is already on disk, before any editor has been constructed.
        var onDisk = new QuickActionsStore(ActionsPath(appData)).LoadAll();
        Check.That(onDisk.Select(a => a.PresetId)).ContainsExactly(null, "kbd.copy");
    }

    [Fact]
    public void ResolvingTheStore_NoMatches_WritesNothing()
    {
        var appData = TestAppDataDir.Create("qa-bootstrap-nomatch");
        const string noMatch = """{ "Actions": [ { "Id": "a1", "Type": 1, "Label": "Hello", "Payload": "hi", "Enabled": true } ] }""";
        File.WriteAllText(ActionsPath(appData), noMatch);
        var stamp = File.GetLastWriteTimeUtc(ActionsPath(appData));
        Thread.Sleep(50);

        using var sp = BuildProvider(appData);
        _ = sp.GetRequiredService<IQuickActionsStore>();

        Check.That(File.ReadAllText(ActionsPath(appData))).IsEqualTo(noMatch);
        Check.That(File.GetLastWriteTimeUtc(ActionsPath(appData))).IsEqualTo(stamp);
    }

    /// <summary>
    /// The safety rule of the startup match: a store that cannot be written must not take startup
    /// down — the user controls this PC only by eye-tracking, so a crash here leaves them without
    /// control. The match finds work to do (a1 matches on label), the write then fails on the
    /// read-only file, and the store still resolves; the file is untouched and still readable.
    /// </summary>
    [Fact]
    public void UnwritableFile_WithAMatch_DoesNotThrow_AndLeavesTheFileAlone()
    {
        var appData = TestAppDataDir.Create("qa-bootstrap-readonly");
        var path = ActionsPath(appData);
        const string json = """{ "Actions": [ { "Id": "a1", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C", "Enabled": true } ] }""";
        File.WriteAllText(path, json);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var sp = BuildProvider(appData);
            var store = sp.GetRequiredService<IQuickActionsStore>();   // must not throw

            Check.That(store).IsNotNull();
            Check.That(File.ReadAllText(path)).IsEqualTo(json);
            Check.That(store.LoadAll().Select(a => a.Id)).ContainsExactly("a1");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void CorruptFile_IsNotOverwrittenAtStartup()
    {
        var appData = TestAppDataDir.Create("qa-bootstrap-corrupt");
        var path = ActionsPath(appData);
        const string corrupt = """{ "Actions": [ { "Id": "a2", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C" """;
        File.WriteAllText(path, corrupt);

        using var sp = BuildProvider(appData);
        _ = sp.GetRequiredService<IQuickActionsStore>();

        Check.That(File.ReadAllText(path)).IsEqualTo(corrupt);
    }

    [Fact]
    public void ManyMatches_AllGainIds_AtStartup_InOrder()
    {
        var appData = TestAppDataDir.Create("qa-bootstrap-many");
        var path = ActionsPath(appData);
        File.WriteAllText(path, """
            { "Actions": [
              { "Id": "z", "Type": 6, "Label": "Close Window", "Payload": "Close", "Enabled": true },
              { "Id": "m", "Type": 1, "Label": "Mine", "Payload": "x", "Enabled": false },
              { "Id": "a", "Type": 5, "Label": "Play / Pause", "Payload": "PlayPause", "Enabled": true },
              { "Id": "b", "Type": 0, "Label": "Copy", "Payload": "Ctrl+C", "Enabled": true }
            ] }
            """);

        using var sp = BuildProvider(appData);
        _ = sp.GetRequiredService<IQuickActionsStore>();

        var onDisk = new QuickActionsStore(path).LoadAll();
        Check.That(onDisk.Select(a => a.Id)).ContainsExactly("z", "m", "a", "b");
        Check.That(onDisk.Select(a => a.Label)).ContainsExactly("Close Window", "Mine", "Play / Pause", "Copy");
        Check.That(onDisk.Select(a => a.Enabled)).ContainsExactly(true, false, true, true);
        Check.That(onDisk.Select(a => a.PresetId)).ContainsExactly("window.close", null, "media.play_pause", "kbd.copy");
    }
}
