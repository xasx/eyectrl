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
