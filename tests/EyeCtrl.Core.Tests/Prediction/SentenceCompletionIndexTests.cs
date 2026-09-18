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

        Check.That(results).HasSize(1);
        Check.That(results[0].completion).IsEqualTo("worth two in the bush");
        Check.That(results[0].frequency).IsEqualTo(5);
    }

    [Fact]
    public void GetCompletions_ShorterPrefix_ReturnsMultiple()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("thank you very much", 10);
        index.Insert("thank you for your help", 7);

        var results = index.GetCompletions("thank you", maxCount: 5);

        Check.That(results.Count).IsEqualTo(2);
        Check.That(results).HasElementThatMatches(r => r.completion == "very much");
        Check.That(results).HasElementThatMatches(r => r.completion == "for your help");
    }

    [Fact]
    public void GetCompletions_NoMatch_ReturnsEmpty()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("hello world", 3);

        var results = index.GetCompletions("goodbye", maxCount: 5);

        Check.That(results).IsEmpty();
    }

    [Fact]
    public void GetCompletions_RespectsMaxCount()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("a b c", 10);
        index.Insert("a b d", 9);
        index.Insert("a b e", 8);

        var results = index.GetCompletions("a b", maxCount: 2);

        Check.That(results.Count).IsEqualTo(2);
    }

    [Fact]
    public void GetCompletions_OrderedByFrequencyDescending()
    {
        var index = new SentenceCompletionIndex();
        index.Insert("the quick brown fox", 1);
        index.Insert("the quick red fox", 10);
        index.Insert("the quick blue fox", 5);

        var results = index.GetCompletions("the quick", maxCount: 5);

        Check.That(results.Count).IsEqualTo(3);
        Check.That(results[0].completion).IsEqualTo("red fox");
        Check.That(results[1].completion).IsEqualTo("blue fox");
        Check.That(results[2].completion).IsEqualTo("brown fox");
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
        Check.That(results.Count).IsEqualTo(2);
        Check.That(results[0].completion).IsEqualTo("very much");
        Check.That(results[0].frequency).IsEqualTo(10);
        Check.That(results[1].completion).IsEqualTo("for your help");

        results = loaded.GetCompletions("the quick", maxCount: 5);
        Check.That(results).HasSize(1);
        Check.That(results[0].completion).IsEqualTo("brown fox");
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
        Check.That(results).IsEmpty();
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

        Check.ThatCode(() => SentenceCompletionIndex.Load(stream)).Throws<InvalidDataException>();
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

        Check.ThatCode(() => SentenceCompletionIndex.Load(stream)).Throws<InvalidDataException>();
    }

    [Fact]
    public void Load_DeepSingleChildChain_ThrowsCatchableInvalidData_NotStackOverflow()
    {
        // The attack/corruption shape: 300 chained one-child nodes. The old recursive loader
        // descended once per edge and died with an uncatchable StackOverflowException — no
        // caller-side try/catch (PredictionStackFactory's included) could do anything. The
        // budgeted iterative loader must refuse with a CATCHABLE InvalidDataException so the
        // app degrades to "no sentence completions" instead of dying.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x54524945); // magic
            writer.Write(1);          // version
            for (var i = 0; i < 300; i++)
            {
                writer.Write((byte)0); // flags: no completions
                writer.Write(1);       // exactly one child
                writer.Write("a");     // edge label
            }
            writer.Write((byte)0);     // deepest node: no completions
            writer.Write(0);           // no children
        }
        stream.Position = 0;

        Check.ThatCode(() => SentenceCompletionIndex.Load(stream)).Throws<InvalidDataException>();
    }

    [Fact]
    public void Load_NegativeChildCount_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x54524945); // magic
            writer.Write(1);          // version
            writer.Write((byte)0);    // flags: no completions
            writer.Write(-1);         // corrupt child count
        }
        stream.Position = 0;

        Check.ThatCode(() => SentenceCompletionIndex.Load(stream)).Throws<InvalidDataException>();
    }
}
