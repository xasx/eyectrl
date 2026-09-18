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
    /// <exception cref="InvalidDataException">Thrown when the stream has an invalid magic number,
    /// unsupported version, or fails a depth/node-count budget (corrupt or crafted file).</exception>
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

        // Iterative with explicit budgets. The recursive LoadNode descended once per child edge,
        // so a corrupt or crafted file whose nodes each declare exactly one child recursed ~one
        // frame per few bytes of file — a StackOverflowException, which NO caller can catch (the
        // PredictionStackFactory try/catch is useless against it; the process simply dies).
        // Iteration plus depth/node caps turns that corruption into a catchable
        // InvalidDataException, which the caller already degrades to "no sentence completions".
        const int MaxDepth = 128;          // trie depth is word length; real words are far shorter
        const long MaxNodes = 2_000_000;   // generous multiple of the largest shipped trie

        long nodes = 1;
        var rootChildren = ReadNodeHeader(reader, index._root);
        // Frame = (node, parent, child edges still unread, depth). A node finishes when its last
        // child edge is consumed; finishing folds its frequency into its parent's — exactly the
        // recursive post-order `node.Frequency += child.Frequency`.
        var stack = new Stack<(TrieNode Node, TrieNode? Parent, int Remaining, int Depth)>();
        stack.Push((index._root, null, rootChildren, 0));

        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            if (frame.Remaining == 0)
            {
                stack.Pop();
                if (frame.Parent is { } parent)
                    parent.Frequency += frame.Node.Frequency;
                continue;
            }

            if (frame.Depth + 1 > MaxDepth)
                throw new InvalidDataException($"Trie file corrupt: depth exceeds {MaxDepth}");
            if (++nodes > MaxNodes)
                throw new InvalidDataException($"Trie file corrupt: node count exceeds {MaxNodes}");

            var label = reader.ReadString();
            var child = new TrieNode();
            var childCount = ReadNodeHeader(reader, child);
            frame.Node.Children[label] = child;

            // Tuples are copies: republish the parent frame with one fewer pending edge.
            stack.Pop();
            stack.Push((frame.Node, frame.Parent, frame.Remaining - 1, frame.Depth));
            stack.Push((child, frame.Node, childCount, frame.Depth + 1));
        }
        return index;
    }

    /// <summary>Reads one node's own payload (flags, completions) and returns its child count.
    /// Corruption guard: negative counts are rejected instead of underflowing the loops.</summary>
    private static int ReadNodeHeader(BinaryReader reader, TrieNode node)
    {
        var flags = reader.ReadByte();
        var hasCompletions = (flags & 1) != 0;

        if (hasCompletions)
        {
            var count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException($"Trie file corrupt: negative completion count {count}");
            for (int i = 0; i < count; i++)
            {
                var word = reader.ReadString();
                var freq = reader.ReadInt32();
                node.Completions[word] = freq;
                node.Frequency += freq;
            }
        }

        var childCount = reader.ReadInt32();
        if (childCount < 0)
            throw new InvalidDataException($"Trie file corrupt: negative child count {childCount}");
        return childCount;
    }

    private class TrieNode
    {
        public int Frequency;
        public readonly Dictionary<string, TrieNode> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Completions = new(StringComparer.OrdinalIgnoreCase);
    }
}
