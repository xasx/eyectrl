namespace EyeCtrl.Supervisor.Supervision;

using System.IO;
using System.Text.Json.Nodes;

/// <summary>
/// Ticket 06-B: reads <c>supervision.enabled</c> from the raw settings file so the supervisor can
/// tell "the file says false" apart from "the file cannot be read". <c>SettingsService.Load()</c>
/// answers a damaged or unparseable file with whole-file defaults (enabled=false) without
/// throwing, so asking Load() would silently disable recovery exactly when something is already
/// wrong. <see cref="TryRead"/> returns <c>null</c> when the answer cannot be told; the caller then
/// acts on its last known value (<see cref="SupervisionEnabledTracker"/>).
/// </summary>
internal static class SupervisionEnabledReader
{
    /// <summary>true/false when the file positively says so; null when it cannot be told.</summary>
    public static bool? TryRead(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return false;   // no file: defaults say disabled
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return null;
            if (!root.ContainsKey("supervision")) return false;       // section missing -> default
            if (root["supervision"] is not JsonObject sup) return null; // non-object/null section: damaged
            if (!sup.ContainsKey("enabled")) return false;            // key missing -> default
            if (sup["enabled"] is not JsonValue value || !value.TryGetValue(out bool enabled)) return null;
            return enabled;
        }
        catch
        {
            // Unreadable, unparseable, or an ambiguous duplicate-key lookup: cannot tell.
            return null;
        }
    }
}
