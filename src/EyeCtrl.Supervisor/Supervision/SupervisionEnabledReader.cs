namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using EyeCtrl.Core.Settings;

/// <summary>
/// Ticket 06-B: reads <c>supervision.enabled</c> from the raw settings file so the supervisor can
/// tell "the file says false" apart from "the file cannot be read". <c>SettingsService.Load()</c>
/// answers a damaged or unparseable file with whole-file defaults (enabled=false) without
/// throwing, so asking Load() would silently disable recovery exactly when something is already
/// wrong. <see cref="TryRead"/> returns <c>null</c> when the answer cannot be told; the caller then
/// acts on its last known value (<see cref="SupervisionEnabledTracker"/>).
/// </summary>
/// <remarks>
/// Ticket 06 review F3: the read takes the settings writer mutex (<see cref="SettingsService.WriterMutexName"/>)
/// with a bounded wait. The writers replace the file with <c>File.Move(..., overwrite: true)</c>,
/// which fails with access denied while ANY handle has the target open — a sharing read does not
/// help (see <c>SupervisionEnabledReaderTests</c>). When a writer owns the file the read returns
/// null rather than opening it; the caller falls back to the last disk value.
/// </remarks>
internal static class SupervisionEnabledReader
{
    /// <summary>How long a read waits for the writer mutex. The writers hold it for one
    /// read-merge-replace, milliseconds.</summary>
    private static readonly TimeSpan WriterWait = TimeSpan.FromMilliseconds(250);

    /// <summary>true/false when the file positively says so; null when it cannot be told.</summary>
    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="writerMutexName">Writer mutex name; injectable so tests never touch the
    /// production name (box-wide rule).</param>
    public static bool? TryRead(string settingsPath, string? writerMutexName = null)
    {
        if (!File.Exists(settingsPath)) return false;   // no file: defaults say disabled

        Mutex? mutex = null;
        var owned = false;
        try
        {
            mutex = new Mutex(false, writerMutexName ?? SettingsService.WriterMutexName);
            try { owned = mutex.WaitOne(WriterWait); }
            catch (AbandonedMutexException) { owned = true; }   // prior holder died; we own it now

            if (!owned) return null;   // a writer owns the file; never open it under a replace

            try
            {
                if (!File.Exists(settingsPath)) return false;   // deleted while waiting
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
        catch
        {
            return null;
        }
        finally
        {
            if (owned)
            {
                try { mutex!.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
        }
    }
}
