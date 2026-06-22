namespace EyeCtrl.LM.Tests;

using EyeCtrl.Core.Prediction;
using EyeCtrl.LM;
using System.Text.Json;

public class LMTokenizerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tokenizerJsonPath;

    public LMTokenizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"eyectrl_lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _tokenizerJsonPath = Path.Combine(_tempDir, "tokenizer.json");
        WriteValidTokenizerJson(_tokenizerJsonPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Constructor_ValidTokenizerJson_LoadsSuccessfully()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        Check.That(tokenizer.VocabSize > 0).IsTrue();
    }

    [Fact]
    public void Constructor_MissingFile_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(_tempDir, "nonexistent.json");
        Check.ThatCode(() => new LMTokenizer(missingPath)).Throws<FileNotFoundException>();
    }

    [Fact]
    public void Encode_SimpleText_ReturnsTokenIds()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        var ids = tokenizer.Encode("hello world");
        Check.That(ids).Not.IsEmpty();
        Check.That(ids).ContainsOnlyElementsThatMatch(id => id >= 0);
    }

    [Fact]
    public void Encode_EmptyString_ReturnsEmptyArray()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        var ids = tokenizer.Encode("");
        Check.That(ids).IsEmpty();
    }

    [Fact]
    public void Decode_KnownIds_ReturnsText()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        var ids = tokenizer.Encode("hello");
        Check.That(ids).Not.IsEmpty();
        var decoded = tokenizer.Decode(ids);
        Check.That(string.IsNullOrEmpty(decoded)).IsFalse();
    }

    [Fact]
    public void Decode_EmptyArray_ReturnsEmptyString()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        var decoded = tokenizer.Decode(Array.Empty<long>());
        Check.That(decoded).IsEmpty();
    }

    [Fact]
    public void SpecialTokenIds_AreSet()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        Check.That(tokenizer.BosTokenId >= 0).IsTrue();
        Check.That(tokenizer.EosTokenId >= 0).IsTrue();
    }

    [Fact]
    public void VocabSize_MatchesConfig()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        Check.That(tokenizer.VocabSize >= 10).IsTrue();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        tokenizer.Dispose();
        tokenizer.Dispose();
    }

    private static void WriteValidTokenizerJson(string path)
    {
        // Minimal BPE tokenizer with Whitespace pre_tokenizer for testing.
        // Uses empty merges (char-level BPE) so vocabulary only needs individual characters.
        // Structure matches tokenizers 0.22.2 serialization format.
        var json = JsonSerializer.Serialize(new
        {
            version = "1.0",
            truncation = (object?)null,
            padding = (object?)null,
            added_tokens = new[]
            {
                new { id = 0, special = true, content = "<s>", single_word = false, lstrip = false, rstrip = false, normalized = true },
                new { id = 1, special = true, content = "</s>", single_word = false, lstrip = false, rstrip = false, normalized = true },
                new { id = 2, special = true, content = "<unk>", single_word = false, lstrip = false, rstrip = false, normalized = true }
            },
            normalizer = (object?)null,
            pre_tokenizer = new { type = "Whitespace" },
            post_processor = (object?)null,
            decoder = (object?)null,
            model = new
            {
                dropout = (object?)null,
                unk_token = "<unk>",
                continuing_subword_prefix = "",
                end_of_word_suffix = "",
                fuse_unk = false,
                vocab = new Dictionary<string, int>
                {
                    ["<s>"] = 0,
                    ["</s>"] = 1,
                    ["<unk>"] = 2,
                    ["h"] = 3,
                    ["e"] = 4,
                    ["l"] = 5,
                    ["o"] = 6,
                    ["w"] = 7,
                    ["r"] = 8,
                    ["d"] = 9,
                    [" "] = 10
                },
                merges = Array.Empty<string>()
            }
        });

        File.WriteAllText(path, json);
    }
}
