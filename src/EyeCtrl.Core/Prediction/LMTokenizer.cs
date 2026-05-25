namespace EyeCtrl.Core.Prediction;

using System.Text.Json;
using Tokenizers.DotNet;

public class LMTokenizer : IDisposable
{
    private readonly Tokenizer _tokenizer;
    private volatile bool _disposed;

    public LMTokenizer(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath))
            throw new FileNotFoundException($"Tokenizer file not found: {tokenizerJsonPath}");

        try
        {
            _tokenizer = new Tokenizer(vocabPath: tokenizerJsonPath);
        }
        catch (TokenizerException ex)
        {
            throw new InvalidOperationException($"Failed to load tokenizer from {tokenizerJsonPath}", ex);
        }

        try
        {
            ParseTokenizerConfig(tokenizerJsonPath);
        }
        catch
        {
            _tokenizer.Dispose();
            throw;
        }

        if (VocabSize == 0)
        {
            _tokenizer.Dispose();
            throw new InvalidOperationException("Tokenizer configuration contains no vocabulary");
        }
    }

    public long[] Encode(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _tokenizer.Encode(text);
        return result.Select(id => (long)id).ToArray();
    }

    public string Decode(long[] ids)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uintIds = ids.Select(id => (uint)id).ToArray();
        return _tokenizer.Decode(uintIds);
    }

    public int VocabSize { get; private set; }
    public long BosTokenId { get; private set; } = -1;
    public long EosTokenId { get; private set; } = -1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenizer.Dispose();
    }

    // Tokenizers.DotNet does not expose VocabSize, BosTokenId, or EosTokenId natively,
    // so we re-parse the JSON config file. If the library adds these APIs in a future
    // version, the JSON parsing below should be replaced.
    private void ParseTokenizerConfig(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var model) &&
                model.TryGetProperty("vocab", out var vocab))
            {
                VocabSize = vocab.EnumerateObject().Count();
            }

            var bosCandidates = new[] { "<s>", "<bos>", "[CLS]", "<|beginoftext|>" };
            var eosCandidates = new[] { "</s>", "<eos>", "[SEP]", "<|endoftext|>" };

            BosTokenId = FindSpecialTokenId(root, bosCandidates);
            EosTokenId = FindSpecialTokenId(root, eosCandidates);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse tokenizer config from {path}", ex);
        }
    }

    private static long FindSpecialTokenId(JsonElement root, string[] candidates)
    {
        if (root.TryGetProperty("added_tokens", out var addedTokens))
        {
            foreach (var token in addedTokens.EnumerateArray())
            {
                if (token.TryGetProperty("content", out var content) &&
                    token.TryGetProperty("id", out var id))
                {
                    var contentStr = content.GetString();
                    if (contentStr != null && Array.Exists(candidates, c => string.Equals(c, contentStr, StringComparison.Ordinal)))
                        return id.GetInt64();
                }
            }
        }

        if (root.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var vocab))
        {
            foreach (var candidate in candidates)
            {
                if (vocab.TryGetProperty(candidate, out var id))
                    return id.GetInt64();
            }
        }

        return -1;
    }
}
