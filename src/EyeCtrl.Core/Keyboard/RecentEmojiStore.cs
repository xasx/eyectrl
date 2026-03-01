namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

public class RecentEmojiStore
{
    private readonly string _filePath;
    private readonly int _maxCount;
    private List<string> _recent;

    public RecentEmojiStore(string filePath, int maxCount = 40)
    {
        _filePath = filePath;
        _maxCount = maxCount;
        _recent = Load();
    }

    public IReadOnlyList<string> Recent => _recent;

    public void Add(string emoji)
    {
        _recent.Remove(emoji); // move to front if already present
        _recent.Insert(0, emoji);
        if (_recent.Count > _maxCount)
            _recent.RemoveAt(_recent.Count - 1);
    }

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
