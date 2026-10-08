namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

public class QuickActionsStoreTests : IDisposable
{
    private readonly string _tempFile;

    public QuickActionsStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"eyectrl_test_actions_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    private QuickActionsStore CreateStore() => new(_tempFile);

    [Fact]
    public void LoadAll_NewFile_ReturnsEmpty()
    {
        var store = CreateStore();
        Check.That(store.LoadAll()).IsEmpty();
    }

    [Fact]
    public void SaveAndLoadAll_RoundTrip()
    {
        var store = CreateStore();
        var actions = new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" },
            new() { Label = "Gmail", Type = QuickActionType.LaunchUrl, Payload = "https://mail.google.com" }
        };
        store.Save(actions);
        var loaded = store.LoadAll();
        Check.That(loaded).HasSize(2);
        Check.That(loaded[0].Label).IsEqualTo("Copy");
        Check.That(loaded[0].Type).IsEqualTo(QuickActionType.KeyboardShortcut);
        Check.That(loaded[0].Id).IsEqualTo(actions[0].Id);
        Check.That(loaded[1].Payload).IsEqualTo("https://mail.google.com");
        Check.That(loaded[1].Type).IsEqualTo(QuickActionType.LaunchUrl);
    }

    [Fact]
    public void SaveAndLoadAll_AnActionWhoseIdWasRepaired_KeepsItsPresetId()
    {
        // The one-time-match result must survive the store's own id repair: WithUniqueId rebuilds
        // a tile whose id was blank, and a dropped PresetId would silently un-translate it on the
        // next load.
        var store = CreateStore();
        var action = new QuickAction
        {
            Id = string.Empty,
            Type = QuickActionType.KeyboardShortcut,
            Label = "Copy",
            Payload = "Ctrl+C",
            PresetId = "kbd.copy",
        };

        store.Save(new[] { action });
        var loaded = store.LoadAll();

        Check.That(loaded).HasSize(1);
        Check.That(loaded[0].Id).Not.IsEqualTo(string.Empty);
        Check.That(loaded[0].PresetId).IsEqualTo("kbd.copy");
    }

    [Fact]
    public void Save_OmitsNullPresetId_SoExistingInstallsKeepTheirFileShape()
    {
        var store = CreateStore();
        store.Save(new[] { new QuickAction { Label = "Hi", Type = QuickActionType.CannedText, Payload = "hi" } });

        var json = File.ReadAllText(_tempFile);

        Check.That(json).Contains("\"Label\": \"Hi\"");
        Check.That(json).Not.Contains("PresetId");
    }

    [Fact]
    public void LoadAll_MissingAndDuplicateIds_AreNormalized()
    {
        File.WriteAllText(_tempFile, """
            {
              "Actions": [
                { "Label": "A" },
                { "Id": "duplicate", "Label": "B" },
                { "Id": "duplicate", "Label": "C" }
              ]
            }
            """);
        var store = CreateStore();

        var loaded = store.LoadAll();

        Check.That(loaded).HasSize(3);
        Check.That(loaded.All(a => !string.IsNullOrWhiteSpace(a.Id))).IsTrue();
        Check.That(loaded.Select(a => a.Id).Distinct()).HasSize(3);
        Check.That(loaded[1].Label).IsEqualTo("B");
        Check.That(loaded[2].Label).IsEqualTo("C");
    }

    [Fact]
    public void Save_NormalizesMissingAndDuplicateIds()
    {
        var store = CreateStore();
        var actions = new List<QuickAction>
        {
            new() { Id = string.Empty, Label = "A" },
            new() { Id = "duplicate", Label = "B" },
            new() { Id = "duplicate", Label = "C" }
        };

        store.Save(actions);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_tempFile));
        var ids = document.RootElement.GetProperty("Actions")
            .EnumerateArray()
            .Select(a => a.GetProperty("Id").GetString())
            .ToList();

        Check.That(ids.All(id => !string.IsNullOrWhiteSpace(id))).IsTrue();
        Check.That(ids.Distinct()).HasSize(3);
    }

    [Fact]
    public void FindById_ExistingId_ReturnsAction()
    {
        var store = CreateStore();
        var action = new QuickAction { Label = "Test" };
        store.Save(new[] { action });
        var found = store.FindById(action.Id);
        Check.That(found).IsNotNull();
        Check.That(found!.Label).IsEqualTo("Test");
    }

    [Fact]
    public void FindById_UnknownId_ReturnsNull()
    {
        var store = CreateStore();
        store.Save(new[] { new QuickAction { Label = "Test" } });
        Check.That(store.FindById("nonexistent")).IsNull();
    }

    [Fact]
    public void LoadAll_CorruptedFile_ReturnsEmpty()
    {
        File.WriteAllText(_tempFile, "not valid json");
        var store = CreateStore();
        Check.That(store.LoadAll()).IsEmpty();
    }
}
