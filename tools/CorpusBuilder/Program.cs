using EyeCtrl.Core.Prediction;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CorpusBuilder <input-file> <output-file> [min-words] [max-words] [shortness-bias]");
    Console.Error.WriteLine("  Processes a text corpus (one sentence per line or raw text)");
    Console.Error.WriteLine("  and produces a sentence completion trie binary file.");
    Console.Error.WriteLine("  shortness-bias: rank frequency by (max-words + 1 - word-count)");
    Console.Error.WriteLine("  instead of corpus duplicates — shorter completions surface first.");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];
var minWords = args.Length > 2 && int.TryParse(args[2], out var mw) ? mw : 4;
var maxWords = args.Length > 3 && int.TryParse(args[3], out var Mw) ? Mw : 15;
var shortnessBias = args.Length > 4 && bool.TryParse(args[4], out var sb) && sb;

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

Console.WriteLine($"Processing: {inputPath}");
Console.WriteLine($"  Min words: {minWords}, Max words: {maxWords}");

var text = File.ReadAllText(inputPath);
var sentences = SplitSentences(text);
var filtered = FilterSentences(sentences, minWords, maxWords);
var deduped = DeduplicateSentences(filtered);

Console.WriteLine($"  Extracted {sentences.Count} sentences");
Console.WriteLine($"  After filter: {filtered.Count}");
Console.WriteLine($"  After dedup: {deduped.Count}");

var index = new SentenceCompletionIndex();
foreach (var (sentence, freq) in deduped)
{
    var weight = shortnessBias
        ? Math.Max(1, maxWords + 1 - sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
        : freq;
    index.Insert(sentence, weight);
}

using var output = File.Create(outputPath);
index.Save(output);

Console.WriteLine($"Wrote trie to: {outputPath}");
Console.WriteLine($"File size: {new FileInfo(outputPath).Length / 1024.0 / 1024.0:F1} MB");
return 0;

static List<string> SplitSentences(string text)
{
    var sentences = new List<string>();
    var current = new System.Text.StringBuilder();
    for (int i = 0; i < text.Length; i++)
    {
        var ch = text[i];
        current.Append(ch);
        if (ch is '.' or '!' or '?' or ';' || ch == '\n')
        {
            var s = current.ToString().Trim();
            if (s.Length > 0)
                sentences.Add(s);
            current.Clear();
        }
    }
    var last = current.ToString().Trim();
    if (last.Length > 0)
        sentences.Add(last);
    return sentences;
}

static List<string> FilterSentences(List<string> sentences, int minWords, int maxWords)
{
    return sentences.Where(s =>
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < minWords || words.Length > maxWords)
            return false;

        var specialCount = words.Sum(w => w.Count(ch => !char.IsLetterOrDigit(ch) && ch != '\'' && ch != '-'));
        var totalChars = words.Sum(w => w.Length);
        if (totalChars > 0 && (double)specialCount / totalChars > 0.3)
            return false;

        var digitCount = words.Sum(w => w.Count(char.IsDigit));
        if (totalChars > 0 && (double)digitCount / totalChars > 0.5)
            return false;

        return true;
    }).ToList();
}

static Dictionary<string, int> DeduplicateSentences(List<string> sentences)
{
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var s in sentences)
    {
        var key = s.ToLowerInvariant().Trim();
        result[key] = result.GetValueOrDefault(key) + 1;
    }
    return result;
}
