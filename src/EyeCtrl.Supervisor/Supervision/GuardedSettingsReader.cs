namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using EyeCtrl.Core.Settings;

/// <summary>
/// Reads one App-owned key from the raw settings file so the Supervisor can tell "the file says
/// V" apart from "the file cannot be read". <c>SettingsService.Load()</c> answers a damaged or
/// unparseable file with whole-file defaults without throwing, and it takes no writer mutex, so
/// asking Load() would both show the wrong value exactly when something is already wrong and hold
/// a read handle that fails the App's atomic save. <see cref="TryRead{T}"/> returns <c>null</c>
/// when the answer cannot be told; the caller then acts on its last known value.
/// </summary>
/// <remarks>
/// The read takes the settings writer mutex (<see cref="SettingsService.WriterMutexName"/>) with a
/// bounded wait, modelled on <see cref="SupervisionEnabledReader"/>: the writers replace the file
/// with <c>File.Move(..., overwrite: true)</c>, which fails with access denied while ANY handle has
/// the target open, and the App holds the mutex during its synchronous <c>OnExit</c> save — right
/// when crises happen. When a writer owns the file the read returns null rather than opening it.
/// </remarks>
internal static class GuardedSettingsReader
{
    /// <summary>How long a read waits for the writer mutex. The writers hold it for one
    /// read-merge-replace, milliseconds.</summary>
    private static readonly TimeSpan WriterWait = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The value of <c>root[section][key]</c> when the file positively says so, the default when
    /// the file, the section or the key is absent, and null when it cannot be told.
    /// </summary>
    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="section">The section's JSON name (camelCase, e.g. <c>general</c>).</param>
    /// <param name="key">The key's JSON name within the section.</param>
    /// <param name="defaultValue">What an absent file, section or key means.</param>
    /// <param name="convert">Turns the key's JSON value into a value; null for a damaged one.</param>
    /// <param name="writerMutexName">Writer mutex name; injectable so tests never touch the
    /// production name (box-wide rule).</param>
    public static T? TryRead<T>(
        string settingsPath,
        string section,
        string key,
        T defaultValue,
        Func<JsonValue, T?> convert,
        string? writerMutexName = null)
        where T : struct
    {
        if (!File.Exists(settingsPath)) return defaultValue;   // no file: defaults apply

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
                if (!File.Exists(settingsPath)) return defaultValue;   // deleted while waiting
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return null;
                if (!root.ContainsKey(section)) return defaultValue;            // section missing -> default
                if (root[section] is not JsonObject values) return null;        // damaged section
                if (!values.ContainsKey(key)) return defaultValue;              // key missing -> default

                return values[key] is JsonValue value ? convert(value) : null;  // null/array/object: damaged
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
