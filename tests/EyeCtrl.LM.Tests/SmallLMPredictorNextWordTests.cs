namespace EyeCtrl.LM.Tests;

using EyeCtrl.Core.Prediction;
using EyeCtrl.LM;
using System.Text.Json;

/// <summary>
/// F7's next-word mode: the request is prompted with the committed text (no dangling trailing
/// space), the answer stops at the first complete word, and the prefix-completion mode keeps
/// behaving exactly as it did.
/// </summary>
public class SmallLMPredictorNextWordTests
{
    private const int Space = 10;   // TestTokenizerJson's " " token
    private const int H = 3;
    private const int E = 4;
    private const int L = 5;
    private const int O = 6;
    private const int W = 7;

    private static LMTokenizer CreateTokenizer(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), $"lm_nextword_{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tokenizer.json");
        WriteJoiningTokenizer(path);
        return new LMTokenizer(path);
    }

    /// <summary>
    /// A toy character tokenizer whose decoder CONCATENATES pieces. The shared
    /// <c>TestTokenizerJson</c> fixture has no decoder, so HuggingFace's default joins token pieces
    /// with spaces ("h e") and a multi-token word can never be observed as one word; these tests are
    /// about exactly that. Kept local so the shared fixture stays as every other suite expects it.
    /// </summary>
    private static void WriteJoiningTokenizer(string path)
    {
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
            decoder = new { type = "WordPiece", cleanup = false, prefix = "" },
            model = new
            {
                dropout = (object?)null,
                unk_token = "<unk>",
                continuing_subword_prefix = "",
                end_of_word_suffix = "",
                fuse_unk = false,
                vocab = new Dictionary<string, int>
                {
                    ["<s>"] = 0, ["</s>"] = 1, ["<unk>"] = 2,
                    ["h"] = 3, ["e"] = 4, ["l"] = 5, ["o"] = 6,
                    ["w"] = 7, ["r"] = 8, ["d"] = 9, [" "] = 10
                },
                merges = Array.Empty<string>()
            }
        });
        File.WriteAllText(path, json);
    }

    private static IReadOnlyList<PredictionResult>? WaitForResults(SmallLMPredictor predictor, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var results = predictor.TryGetResults();
            if (results != null) return results;
            Thread.Sleep(10);
        }

        return predictor.TryGetResults();
    }

    [Fact]
    public void PredictNextWord_PromptsWithTheCommittedText_AndNoTrailingSpace()
    {
        var tokenizer = CreateTokenizer(out var directory);
        string? observed = null;
        SmallLMPredictor? predictor = null;
        try
        {
            predictor = SmallLMPredictor.CreateModelFree(tokenizer,
                (step, token) => new SmallLMPredictor.StepOutput(step == 0 ? Space : H, 0.5),
                prompt => observed = prompt);

            predictor.PredictNextWord("Wir gehen jetzt ");

            WaitForResults(predictor, TimeSpan.FromSeconds(10));
            Check.WithCustomMessage($"observed prompt: '{observed}'").That(observed).IsEqualTo("Wir gehen jetzt");
        }
        finally
        {
            predictor?.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PredictNextWord_StopsAtTheFirstCompleteWord()
    {
        // " hello" then the space that would open the next word: the answer must stop there and
        // hand back exactly "hello", not run the eight-token schedule.
        var tokenizer = CreateTokenizer(out var directory);
        var ids = new[] { Space, H, E, L, L, O, Space, W, O, H };
        var steps = 0;
        SmallLMPredictor? predictor = null;
        try
        {
            predictor = SmallLMPredictor.CreateModelFree(tokenizer, (step, token) =>
            {
                Interlocked.Increment(ref steps);
                var id = ids[Math.Min(step, ids.Length - 1)];
                return new SmallLMPredictor.StepOutput(id, 0.5);
            });

            predictor.PredictNextWord("Hallo");
            var results = WaitForResults(predictor, TimeSpan.FromSeconds(10));

            Check.That(results).IsNotNull();
            Check.That(results!).HasSize(1);
            Check.That(results![0].Word).IsEqualTo("hello");
            // Six tokens make the word; the seventh opens the next word and is dropped, so the
            // loop never reaches MaxNewTokens (8) and never decodes a second word.
            Check.WithCustomMessage($"steps asked: {steps}").That(steps).IsEqualTo(7);
        }
        finally
        {
            predictor?.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Predict_WithAPrefix_StillCompletesTheWord()
    {
        // The mode that already worked: the prompt carries context + prefix, and the answer is
        // prefix + continuation.
        var tokenizer = CreateTokenizer(out var directory);
        string? observed = null;
        SmallLMPredictor? predictor = null;
        try
        {
            predictor = SmallLMPredictor.CreateModelFree(tokenizer,
                (step, token) => new SmallLMPredictor.StepOutput(
                    step == 0 ? H : step == 1 ? E : 1 /* EOS */, 0.5),
                prompt => observed = prompt);

            predictor.Predict("Das", "hi");
            var results = WaitForResults(predictor, TimeSpan.FromSeconds(10));

            Check.WithCustomMessage($"observed prompt: '{observed}'").That(observed).IsEqualTo("Das hi");
            Check.That(results).IsNotNull();
            Check.That(results![0].Word).IsEqualTo("hihe");
        }
        finally
        {
            predictor?.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PostProcessNextWord_ReturnsTheWordWithoutPrefixAndCutsTrailingPunctuation()
    {
        var results = SmallLMPredictor.PostProcessNextWord(" Wetter,", 0.25);

        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("Wetter");
        Check.That(results[0].Type).IsEqualTo(PredictionType.LMSuggestion);
        Check.That(results[0].Confidence).IsCloseTo(0.25, 1e-9);
    }

    [Fact]
    public void PostProcessNextWord_RefusesNonWordText()
    {
        Check.That(SmallLMPredictor.PostProcessNextWord(" ,", 0.5)).IsEmpty();
        Check.That(SmallLMPredictor.PostProcessNextWord("", 0.5)).IsEmpty();
    }
}
