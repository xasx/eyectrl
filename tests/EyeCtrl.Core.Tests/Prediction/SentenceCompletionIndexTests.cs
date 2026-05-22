namespace EyeCtrl.Core.Tests.Prediction;

using System.Text;
using EyeCtrl.Core.Prediction;

public class SentenceCompletionIndexTests
{
    [Fact]
    public void GetCompletions_ExactPrefix_ReturnsCompletion()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("the hand is worth two in the bush", 5);

        var results = index.GetCompletions("the hand is", maxCount: 5);

        Assert.Single(results);
        Assert.Equal("worth two in the bush", results[0].completion);
        Assert.Equal(5, results[0].frequency);
    }

    [Fact]
    public void GetCompletions_ShorterPrefix_ReturnsMultiple()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("thank you very much", 10);
        index.Insert("thank you for your help", 7);

        var results = index.GetCompletions("thank you", maxCount: 5);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.completion == "very much");
        Assert.Contains(results, r => r.completion == "for your help");
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("hello world", 3);

        var results = index.GetCompletions("goodbye", maxCount: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void GetCompletions_RespectsMaxCount()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("a b c", 10);
        index.Insert("a b d", 9);
        index.Insert("a b e", 8);

        var results = index.GetCompletions("a b", maxCount: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void GetCompletions_OrderedByFrequencyDescending()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("the quick brown fox", 1);
        index.Insert("the quick red fox", 10);
        index.Insert("the quick blue fox", 5);

        var results = index.GetCompletions("the quick", maxCount: 5);

        Assert.Equal(3, results.Count);
        Assert.Equal("red fox", results[0].completion);
        Assert.Equal("blue fox", results[1].completion);
        Assert.Equal("brown fox", results[2].completion);
    }

    [Fact]
    public void SaveAndLoad_Roundtrip_PreservesData()
    {
        var original = new SentenceCompletionIndex();
        original.Insert("thank you very much", 10);
        original.Insert("thank you for your help", 7);
        original.Insert("the quick brown fox", 3);

        using var stream = new MemoryStream();
        original.Save(stream);
        stream.Position = 0;

        var loaded = SentenceCompletionIndex.Load(stream);

        var results = loaded.GetCompletions("thank you", maxCount: 5);
        Assert.Equal(2, results.Count);
        Assert.Equal("very much", results[0].completion);
        Assert.Equal(10, results[0].frequency);
        Assert.Equal("for your help", results[1].completion);

        results = loaded.GetCompletions("the quick", maxCount: 5);
        Assert.Single(results);
        Assert.Equal("brown fox", results[0].completion);
    }

    [Fact]
    public void SaveAndLoad_EmptyIndex_RoundTripSucceeds()
    {
        var original = new SentenceCompletionIndex();
        using var stream = new MemoryStream();
        original.Save(stream);
        stream.Position = 0;

        var loaded = SentenceCompletionIndex.Load(stream);
        var results = loaded.GetCompletions("anything", maxCount: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Load_BadMagic_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0xDEADBEEF); // Wrong magic
            writer.Write(1); // valid version
        }
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => SentenceCompletionIndex.Load(stream));
    }

    [Fact]
    public void Load_BadVersion_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x54524945); // Correct magic "TRIE"
            writer.Write(999); // Wrong version
        }
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => SentenceCompletionIndex.Load(stream));
    }

    [Fact]
    public void BoostCompletion_IncreasesFrequency()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("thank you very much", 5);
        index.BoostCompletion("thank you very much");

        var results = index.GetCompletions("thank you", maxCount: 5);
        Assert.Single(results);
        Assert.Equal(6, results[0].frequency);
    }

    [Fact]
    public void DismissCompletion_DecreasesFrequency()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("thank you very much", 5);
        index.DismissCompletion("thank you very much");

        var results = index.GetCompletions("thank you", maxCount: 5);
        Assert.Single(results);
        Assert.Equal(4, results[0].frequency);
    }
}
