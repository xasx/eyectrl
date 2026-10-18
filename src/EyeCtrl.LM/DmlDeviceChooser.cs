namespace EyeCtrl.LM;

using System.Globalization;
using Microsoft.ML.OnnxRuntime;

/// <summary>One DirectML-capable adapter as ONNX Runtime reports it.</summary>
internal sealed record DmlDeviceInfo(
    string? Description,
    bool? Discrete,
    int? HighPerformanceIndex,
    int? AdapterNumber,
    long? VideoMemoryMb);

/// <summary>
/// Picks the DirectML adapter to run the model on. The choice is pure metadata logic, so it is
/// testable without a GPU; <see cref="FromEpDevice"/> is the tolerant bridge from ONNX Runtime.
/// </summary>
internal static class DmlDeviceChooser
{
    /// <summary>The EP name ONNX Runtime uses for the DirectML execution provider.</summary>
    internal const string DmlEpName = "DmlExecutionProvider";

    /// <summary>
    /// Returns the adapter to use, or null for an empty list. The lowest
    /// <see cref="DmlDeviceInfo.HighPerformanceIndex"/> wins: that is DXGI's own high-performance
    /// ranking, which puts the discrete GPU ahead of an integrated one on a hybrid machine. With
    /// no ranking, a discrete adapter wins, then the larger video memory, then the lower number.
    /// </summary>
    internal static DmlDeviceInfo? Choose(IReadOnlyList<DmlDeviceInfo> devices)
    {
        if (devices.Count == 0) return null;

        var best = devices[0];
        for (int i = 1; i < devices.Count; i++)
        {
            if (Compare(devices[i], best) < 0) best = devices[i];
        }

        return best;
    }

    /// <summary>Maps one ONNX Runtime EP device to the metadata the chooser needs.</summary>
    internal static DmlDeviceInfo FromEpDevice(OrtEpDevice device)
        => Parse(device.HardwareDevice.Metadata.Entries);

    /// <summary>
    /// Reads the adapter metadata. Every value is optional: a missing key, an empty string or a
    /// value the parser does not understand becomes null and never throws.
    /// </summary>
    internal static DmlDeviceInfo Parse(IReadOnlyDictionary<string, string> metadata)
        => new(
            Text(metadata, "Description"),
            Boolean(metadata, "Discrete"),
            Integer(metadata, "DxgiHighPerformanceIndex"),
            Integer(metadata, "DxgiAdapterNumber"),
            VideoMemory(metadata, "DxgiVideoMemory"));

    private static int Compare(DmlDeviceInfo candidate, DmlDeviceInfo best)
    {
        var ranking = CompareKnown(candidate.HighPerformanceIndex, best.HighPerformanceIndex, higherWins: false);
        if (ranking != 0) return ranking;

        if (candidate.Discrete != best.Discrete)
        {
            if (candidate.Discrete == true) return -1;
            if (best.Discrete == true) return 1;
        }

        ranking = CompareKnown(candidate.VideoMemoryMb, best.VideoMemoryMb, higherWins: true);
        if (ranking != 0) return ranking;

        return CompareKnown(candidate.AdapterNumber, best.AdapterNumber, higherWins: false);
    }

    /// <summary>Compares two optional values; an unknown value ranks after a known one.</summary>
    private static int CompareKnown<T>(T? candidate, T? best, bool higherWins) where T : struct, IComparable<T>
    {
        if (candidate is null) return best is null ? 0 : 1;
        if (best is null) return -1;
        return higherWins
            ? best.Value.CompareTo(candidate.Value)
            : candidate.Value.CompareTo(best.Value);
    }

    private static string? Text(IReadOnlyDictionary<string, string> metadata, string key)
    {
        var value = Get(metadata, key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? Boolean(IReadOnlyDictionary<string, string> metadata, string key)
    {
        var value = Text(metadata, key);
        return value switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(value, out var parsed) ? parsed : null,
        };
    }

    private static int? Integer(IReadOnlyDictionary<string, string> metadata, string key)
    {
        var value = Text(metadata, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Reads values such as "24137 MB"; the unit is ignored, a bare number is accepted.</summary>
    private static long? VideoMemory(IReadOnlyDictionary<string, string> metadata, string key)
    {
        var value = Text(metadata, key);
        if (value is null) return null;

        var space = value.IndexOf(' ');
        var number = space >= 0 ? value[..space] : value;
        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? Get(IReadOnlyDictionary<string, string> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var value)) return value;

        foreach (var entry in metadata)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)) return entry.Value;
        }

        return null;
    }
}
