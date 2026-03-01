namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

public class EmojiDataLoaderTests : IDisposable
{
    private readonly string _tempFile;

    public EmojiDataLoaderTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose() => File.Delete(_tempFile);

    [Fact]
    public void Load_ParsesCategories()
    {
        File.WriteAllText(_tempFile, """
        {
            "categories": [
                { "id": "smileys", "icon": "😀", "label": "Smileys", "emojis": ["😀","😃","😄"] }
            ],
            "skinToneBase": ["👋","👍"]
        }
        """);

        var data = EmojiDataLoader.Load(_tempFile);

        Assert.Single(data.Categories);
        Assert.Equal("smileys", data.Categories[0].Id);
        Assert.Equal(3, data.Categories[0].Emojis.Count);
        Assert.Equal(2, data.SkinToneBase.Count);
        Assert.Contains("👋", data.SkinToneBase);
    }

    [Fact]
    public void Load_EmptySkinToneBase_ReturnsEmptySet()
    {
        File.WriteAllText(_tempFile, """
        { "categories": [], "skinToneBase": [] }
        """);

        var data = EmojiDataLoader.Load(_tempFile);

        Assert.Empty(data.SkinToneBase);
    }
}
