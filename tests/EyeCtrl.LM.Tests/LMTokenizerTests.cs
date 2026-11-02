namespace EyeCtrl.LM.Tests;

using EyeCtrl.LM;

public class LMTokenizerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tokenizerJsonPath;

    public LMTokenizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"eyectrl_lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _tokenizerJsonPath = Path.Combine(_tempDir, "tokenizer.json");
        TestTokenizerJson.Write(_tokenizerJsonPath);
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
        Check.That(tokenizer.VocabSize).IsStrictlyGreaterThan(0);
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
        var ids = tokenizer.Encode("hello");
        Check.That(ids).ContainsExactly(3L, 4L, 5L, 5L, 6L);
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
        Check.That(decoded).Not.IsEmpty();
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
        Check.That(tokenizer.BosTokenId).IsEqualTo(0);
        Check.That(tokenizer.EosTokenId).IsEqualTo(1);
    }

    [Fact]
    public void VocabSize_MatchesConfig()
    {
        using var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        Check.That(tokenizer.VocabSize).IsEqualTo(11);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var tokenizer = new LMTokenizer(_tokenizerJsonPath);
        tokenizer.Dispose();
        tokenizer.Dispose();
    }
}
