namespace EyeCtrl.Core.Tests.Prediction;

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Prediction;

/// <summary>
/// The base dictionary's display casing must not move the ranking: for every short prefix of
/// the shipped German and English dictionaries, the top five is the same with and without the
/// "casing" table (compared lowercase).
/// </summary>
public class DictionaryCasingRankingTests : IDisposable
{
    private readonly string _dir;

    public DictionaryCasingRankingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dict_rank_casing_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static string FindDictionaries()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName, "src", "EyeCtrl.App", "Resources", "Dictionaries");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the dictionaries folder by searching upward from '{AppDomain.CurrentDomain.BaseDirectory}'.");
    }

    [Theory]
    [InlineData("de.json")]
    [InlineData("en.json")]
    public void TopFiveForEveryShortPrefix_IsUnchangedByTheBaseCasing(string fileName)
    {
        var path = Path.Combine(FindDictionaries(), fileName);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var words = root["words"]!.AsObject().Select(entry => entry.Key).ToList();

        var withoutCasing = root.DeepClone().AsObject();
        withoutCasing.Remove("casing");
        var withoutPath = Path.Combine(_dir, "nocasing_" + fileName);
        File.WriteAllText(withoutPath, withoutCasing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var withCasing = new WordPredictor(new FileDictionaryProvider(path, _dir, "xx"), "QWERTY");
        var noCasing = new WordPredictor(new FileDictionaryProvider(withoutPath, _dir, "xx"), "QWERTY");

        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            if (word.Length >= 2) prefixes.Add(word[..2]);
            if (word.Length >= 3) prefixes.Add(word[..3]);
        }

        var checkedPrefixes = 0;
        foreach (var prefix in prefixes)
        {
            var with = withCasing.GetSuggestions(prefix, null, 5)
                .Select(result => result.Word.ToLowerInvariant()).ToArray();
            var without = noCasing.GetSuggestions(prefix, null, 5)
                .Select(result => result.Word.ToLowerInvariant()).ToArray();

            Check.WithCustomMessage(
                    $"{fileName} '{prefix}': with casing [{string.Join(",", with)}] vs without [{string.Join(",", without)}]")
                .That(with).ContainsExactly(without);
            checkedPrefixes++;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{fileName}: {checkedPrefixes} prefixes checked");
        Check.That(checkedPrefixes).IsStrictlyGreaterThan(1000);
    }
}
