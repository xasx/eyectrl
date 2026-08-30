using System.Text;

namespace EyeCtrl.Core.Prediction;

/// <summary>Trie-based index for sentence completion predictions, persisted in a binary format.</summary>
public class SentenceCompletionIndex
{
    private const int Magic = 0x54524945;
    private const int Version = 2;

    private readonly TrieNode _root = new();

    /// <summary>Inserts a sentence with a frequency weight into the trie for later completion queries.</summary>
    /// <param name="sentence">The full sentence to index.</param>
    /// <param name="frequency">Frequency weight for this sentence.</param>
    public void Insert(string sentence, int frequency)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return;
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return;
        Insert(words, frequency);
    }

    private void Insert(string[] words, int frequency)
    {
        for (int prefixLen = 1; prefixLen < words.Length; prefixLen++)
        {
            var node = _root;
            for (int i = 0; i < prefixLen; i++)
            {
                var word = words[i].ToLowerInvariant();
                if (!node.Children.TryGetValue(word, out var child))
                {
                    child = new TrieNode();
                    node.Children[word] = child;
                }
                node.Frequency += frequency;
                node = child;
            }
            var completion = string.Join(' ', words[prefixLen..]);
            node.Completions[completion] = node.Completions.GetValueOrDefault(completion) + frequency;
        }
    }

    /// <summary>Returns completion suggestions for a given prefix from the trie.</summary>
    /// <param name="prefix">The partial sentence to complete.</param>
    /// <param name="maxCount">Maximum number of completions to return.</param>
    /// <returns>List of (completion, frequency) tuples ordered by descending frequency.</returns>
    public List<(string completion, int frequency)> GetCompletions(string prefix, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return [];

        var words = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var node = _root;
        foreach (var word in words)
        {
            if (!node.Children.TryGetValue(word.ToLowerInvariant(), out var child))
                return [];
            node = child;
        }

        return node.Completions
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Serializes the trie to a binary stream.</summary>
    /// <param name="stream">The output stream (left open after writing).</param>
    public void Save(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        SaveNode(writer, _root);
    }

    private void SaveNode(BinaryWriter writer, TrieNode node)
    {
        byte flags = 0;
        if (node.Completions.Count > 0) flags |= 1;
        writer.Write(flags);

        if (node.Completions.Count > 0)
        {
            writer.Write(node.Completions.Count);
            foreach (var (word, freq) in node.Completions)
            {
                writer.Write(word);
                writer.Write(freq);
            }
        }

        writer.Write(node.Children.Count);
        foreach (var (word, child) in node.Children)
        {
            writer.Write(word);
            SaveNode(writer, child);
        }
    }

    /// <summary>Deserializes a trie from a binary stream.</summary>
    /// <param name="stream">The input stream.</param>
    /// <returns>A new <see cref="SentenceCompletionIndex"/> populated from the stream.</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream has an invalid magic number or unsupported version.</exception>
    public static SentenceCompletionIndex Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Invalid trie file: expected magic {Magic:X8}, got {magic:X8}");
        var version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported trie version: {version}");

        var index = new SentenceCompletionIndex();
        LoadNode(reader, index._root);
        return index;
    }

    private static void LoadNode(BinaryReader reader, TrieNode node)
    {
        var flags = reader.ReadByte();
        var hasCompletions = (flags & 1) != 0;

        if (hasCompletions)
        {
            var count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var word = reader.ReadString();
                var freq = reader.ReadInt32();
                node.Completions[word] = freq;
                node.Frequency += freq;
            }
        }

        var childCount = reader.ReadInt32();
        for (int i = 0; i < childCount; i++)
        {
            var word = reader.ReadString();
            var child = new TrieNode();
            node.Children[word] = child;
            LoadNode(reader, child);
            node.Frequency += child.Frequency;
        }
    }

    private class TrieNode
    {
        public int Frequency;
        public readonly Dictionary<string, TrieNode> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Completions = new(StringComparer.OrdinalIgnoreCase);
    }
}
