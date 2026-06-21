namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

public class RecentEmojiStoreTests : IDisposable
{
    private readonly string _tempFile;

    public RecentEmojiStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"recent_emoji_test_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void Add_InsertsAtFront()
    {
        var store = new RecentEmojiStore(_tempFile);
        store.Add("\U0001f600");
        store.Add("\U0001f603");

        Check.That(store.Recent[0]).IsEqualTo("\U0001f603");
        Check.That(store.Recent[1]).IsEqualTo("\U0001f600");
    }

    [Fact]
    public void Add_MovesDuplicateToFront()
    {
        var store = new RecentEmojiStore(_tempFile);
        store.Add("\U0001f600");
        store.Add("\U0001f603");
        store.Add("\U0001f600"); // move to front

        Check.That(store.Recent.Count).IsEqualTo(2);
        Check.That(store.Recent[0]).IsEqualTo("\U0001f600");
    }

    [Fact]
    public void Add_RespectsMaxCount()
    {
        var store = new RecentEmojiStore(_tempFile, maxCount: 3);
        store.Add("A");
        store.Add("B");
        store.Add("C");
        store.Add("D");

        Check.That(store.Recent.Count).IsEqualTo(3);
        Check.That(store.Recent.Contains("A")).IsFalse();
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var store1 = new RecentEmojiStore(_tempFile);
        store1.Add("\U0001f600");
        store1.Add("\u2764\ufe0f");
        store1.Save();

        var store2 = new RecentEmojiStore(_tempFile);
        Check.That(store2.Recent.Count).IsEqualTo(2);
        Check.That(store2.Recent[0]).IsEqualTo("\u2764\ufe0f");
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new RecentEmojiStore(_tempFile);
        Check.That(store.Recent).IsEmpty();
    }
}
