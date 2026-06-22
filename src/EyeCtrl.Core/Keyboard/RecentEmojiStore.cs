namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

/// <summary>Persistent store of recently used emojis, kept in a JSON file.</summary>
public class RecentEmojiStore
{
    private readonly string _filePath;
    private readonly int _maxCount;
    private List<string> _recent;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecentEmojiStore"/> class,
    /// loading previously saved data from disk.
    /// </summary>
    /// <param name="filePath">Path to the JSON file for persistence.</param>
    /// <param name="maxCount">Maximum number of recent emojis to keep (default: 40).</param>
    public RecentEmojiStore(string filePath, int maxCount = 40)
    {
        _filePath = filePath;
        _maxCount = maxCount;
        _recent = Load();
    }

    /// <summary>Snapshot of the most recently used emojis, most recent first.</summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>Adds an emoji to the front of the recent list and persists to disk.</summary>
    /// <param name="emoji">The emoji character to record.</param>
    public void Add(string emoji)
    {
        _recent.Remove(emoji); // move to front if already present
        _recent.Insert(0, emoji);
        if (_recent.Count > _maxCount)
            _recent.RemoveAt(_recent.Count - 1);
    }

    /// <summary>Persists the current recent emoji list to the JSON file.</summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_recent);
        File.WriteAllText(_filePath, json);
    }

    private List<string> Load()
    {
        if (!File.Exists(_filePath)) return new List<string>();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
