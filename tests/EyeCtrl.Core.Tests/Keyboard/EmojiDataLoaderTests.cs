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

        Check.That(data.Categories).HasSize(1);
        Check.That(data.Categories[0].Id).IsEqualTo("smileys");
        Check.That(data.Categories[0].Emojis.Count).IsEqualTo(3);
        Check.That(data.SkinToneBase.Count).IsEqualTo(2);
        Check.That(data.SkinToneBase).Contains("👋");
    }

    [Fact]
    public void Load_EmptySkinToneBase_ReturnsEmptySet()
    {
        File.WriteAllText(_tempFile, """
        { "categories": [], "skinToneBase": [] }
        """);

        var data = EmojiDataLoader.Load(_tempFile);

        Check.That(data.SkinToneBase).IsEmpty();
    }
}
