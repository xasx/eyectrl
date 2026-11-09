namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Setup.Abstractions;
using Microsoft.Win32;

/// <summary>An in-memory registry. Writes/deletes land in a shared ordering log with the file fake.</summary>
public sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<(RegistryHive Hive, string Key, string Name), object> _values = new();
    private readonly HashSet<(RegistryHive Hive, string Key)> _keys = new();

    /// <summary>Creates the store with an optional shared operation log.</summary>
    public FakeRegistryStore(List<string>? log = null) => Log = log ?? [];

    /// <summary>The shared ordering log.</summary>
    public List<string> Log { get; }

    /// <summary>Adds a DWORD for test setup without logging.</summary>
    public void SeedDword(RegistryHive hive, string key, string name, int value)
    {
        _keys.Add((hive, Normalize(key)));
        _values[(hive, Normalize(key), name)] = value;
    }

    /// <summary>Adds a string for test setup without logging.</summary>
    public void SeedString(RegistryHive hive, string key, string name, string value)
    {
        _keys.Add((hive, Normalize(key)));
        _values[(hive, Normalize(key), name)] = value;
    }

    /// <summary>The current value, for assertions.</summary>
    public object? Get(RegistryHive hive, string key, string name) =>
        _values.GetValueOrDefault((hive, Normalize(key), name));

    /// <summary>The value names under a key, for assertions.</summary>
    public IReadOnlyList<string> Names(RegistryHive hive, string key) =>
        _values.Keys.Where(k => k.Hive == hive && k.Key == Normalize(key)).Select(k => k.Name).ToList();

    /// <inheritdoc />
    public int? ReadDword(RegistryHive hive, string subKey, string valueName) =>
        _values.GetValueOrDefault((hive, Normalize(subKey), valueName)) as int?;

    /// <inheritdoc />
    public string? ReadString(RegistryHive hive, string subKey, string valueName)
    {
        if (ThrowOnReadString is { } throwing && string.Equals(valueName, throwing, StringComparison.Ordinal))
            throw new IOException($"simulated read failure for {valueName}");
        return _values.GetValueOrDefault((hive, Normalize(subKey), valueName)) as string;
    }

    /// <inheritdoc />
    public void WriteString(RegistryHive hive, string subKey, string valueName, string value)
    {
        Log.Add($"reg:write:{valueName}");
        _keys.Add((hive, Normalize(subKey)));
        _values[(hive, Normalize(subKey), valueName)] = value;
    }

    /// <inheritdoc />
    public void WriteDword(RegistryHive hive, string subKey, string valueName, int value)
    {
        Log.Add($"reg:write:{valueName}");
        _keys.Add((hive, Normalize(subKey)));
        _values[(hive, Normalize(subKey), valueName)] = value;
    }

    /// <inheritdoc />
    public void DeleteValue(RegistryHive hive, string subKey, string valueName)
    {
        Log.Add($"reg:delete:{valueName}");
        _values.Remove((hive, Normalize(subKey), valueName));
    }

    /// <inheritdoc />
    public void DeleteKey(RegistryHive hive, string subKey)
    {
        Log.Add("reg:deleteKey:" + subKey);
        var key = Normalize(subKey);
        foreach (var entry in _values.Keys.Where(k => k.Hive == hive && k.Key == key).ToList())
            _values.Remove(entry);
        _keys.Remove((hive, key));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadValueNames(RegistryHive hive, string subKey) =>
        Names(hive, subKey);

    /// <summary>When set, a <see cref="ReadString"/> for this value name throws (review-01 F4).</summary>
    public string? ThrowOnReadString { get; set; }

    private static string Normalize(string key) => key.TrimEnd('\\').ToUpperInvariant();
}
