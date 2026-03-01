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

        Assert.Equal("\U0001f603", store.Recent[0]);
        Assert.Equal("\U0001f600", store.Recent[1]);
    }

    [Fact]
    public void Add_MovesDuplicateToFront()
    {
        var store = new RecentEmojiStore(_tempFile);
        store.Add("\U0001f600");
        store.Add("\U0001f603");
        store.Add("\U0001f600"); // move to front

        Assert.Equal(2, store.Recent.Count);
        Assert.Equal("\U0001f600", store.Recent[0]);
    }

    [Fact]
    public void Add_RespectsMaxCount()
    {
        var store = new RecentEmojiStore(_tempFile, maxCount: 3);
        store.Add("A");
        store.Add("B");
        store.Add("C");
        store.Add("D");

        Assert.Equal(3, store.Recent.Count);
        Assert.DoesNotContain("A", store.Recent);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var store1 = new RecentEmojiStore(_tempFile);
        store1.Add("\U0001f600");
        store1.Add("\u2764\ufe0f");
        store1.Save();

        var store2 = new RecentEmojiStore(_tempFile);
        Assert.Equal(2, store2.Recent.Count);
        Assert.Equal("\u2764\ufe0f", store2.Recent[0]);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new RecentEmojiStore(_tempFile);
        Assert.Empty(store.Recent);
    }
}
