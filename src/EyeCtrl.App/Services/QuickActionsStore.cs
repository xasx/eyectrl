namespace EyeCtrl.App.Services;

using System.IO;
using System.Text.Json;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// JSON file-backed implementation of <see cref="IQuickActionsStore"/>.
/// Persists the Quick Actions toolbar entries to a single JSON file, tolerating
/// missing or corrupted files by returning an empty collection.
/// </summary>
public class QuickActionsStore : IQuickActionsStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Wraps the serialized action list so the on-disk JSON is a single named property.</summary>
    private sealed record ActionsWrapper(List<QuickAction> Actions);

    /// <summary>
    /// Creates a store backed by the supplied file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the JSON file used for persistence.</param>
    public QuickActionsStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <inheritdoc />
    public IReadOnlyList<QuickAction> LoadAll()
    {
        try
        {
            if (!File.Exists(_filePath)) return Array.Empty<QuickAction>();
            var json = File.ReadAllText(_filePath);
            var wrapper = JsonSerializer.Deserialize<ActionsWrapper>(json, JsonOptions);
            return (IReadOnlyList<QuickAction>?)wrapper?.Actions ?? Array.Empty<QuickAction>();
        }
        catch
        {
            return Array.Empty<QuickAction>();
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<QuickAction> actions)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var wrapper = new ActionsWrapper(actions.ToList());
        var json = JsonSerializer.Serialize(wrapper, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    /// <inheritdoc />
    public QuickAction? FindById(string id)
    {
        return LoadAll().FirstOrDefault(a => a.Id == id);
    }
}
