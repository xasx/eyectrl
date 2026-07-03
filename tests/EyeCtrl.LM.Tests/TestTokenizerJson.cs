namespace EyeCtrl.LM.Tests;

using System.Text.Json;

internal static class TestTokenizerJson
{
    public static void Write(string path)
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
                    ["<s>"] = 0, ["</s>"] = 1, ["<unk>"] = 2,
                    ["h"] = 3, ["e"] = 4, ["l"] = 5, ["o"] = 6,
                    ["w"] = 7, ["r"] = 8, ["d"] = 9, [" "] = 10
                },
                merges = Array.Empty<string>()
            }
        });
        File.WriteAllText(path, json);
    }
}
