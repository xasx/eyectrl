namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;

/// <summary>
/// Reads <c>general.uiLanguage</c> from the raw settings file so the Supervisor can tell "the file
/// says System" apart from "the file cannot be read". <c>SettingsService.Load()</c> answers a
/// damaged or unparseable file with whole-file defaults without throwing, so asking Load() would
/// silently show the wrong language exactly when something is already wrong.
/// <see cref="TryRead"/> returns <c>null</c> when the answer cannot be told; the caller then acts
/// on its last known value (<see cref="SupervisorLanguageTracker"/>).
/// </summary>
/// <remarks>
/// The read takes the settings writer mutex (<see cref="SettingsService.WriterMutexName"/>) with a
/// bounded wait, modelled on <see cref="SupervisionEnabledReader"/>: the writers replace the file
/// with <c>File.Move(..., overwrite: true)</c>, which fails with access denied while ANY handle has
/// the target open, and the App holds the mutex during its synchronous <c>OnExit</c> save — right
/// when crises happen. When a writer owns the file the read returns null rather than opening it.
/// </remarks>
internal static class UiLanguageReader
{
    /// <summary>How long a read waits for the writer mutex. The writers hold it for one
    /// read-merge-replace, milliseconds.</summary>
    private static readonly TimeSpan WriterWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The language when the file positively says so; null when it cannot be told.</summary>
    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="writerMutexName">Writer mutex name; injectable so tests never touch the
    /// production name (box-wide rule).</param>
    public static UiLanguage? TryRead(string settingsPath, string? writerMutexName = null)
    {
        if (!File.Exists(settingsPath)) return UiLanguage.System;   // no file: defaults say System

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
                if (!File.Exists(settingsPath)) return UiLanguage.System;   // deleted while waiting
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return null;
                if (!root.ContainsKey("general")) return UiLanguage.System;       // section missing -> default
                if (root["general"] is not JsonObject general) return null;       // damaged section
                if (!general.ContainsKey("uiLanguage")) return UiLanguage.System; // key missing -> default

                var value = general["uiLanguage"];
                if (value is JsonValue jsonValue)
                {
                    // The settings writer emits camelCase enum tokens, but JsonStringEnumConverter
                    // also accepts raw integers on load — accept both, and only values this enum
                    // defines; anything else is damaged rather than a guess.
                    if (jsonValue.TryGetValue(out string? token))
                        return UiLanguageTokens.TryFromJson(token, out var fromToken) ? fromToken : null;
                    if (jsonValue.TryGetValue(out int number) && Enum.IsDefined((UiLanguage)number))
                        return (UiLanguage)number;
                }

                return null;
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
