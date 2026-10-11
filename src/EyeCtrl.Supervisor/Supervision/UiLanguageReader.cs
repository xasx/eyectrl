namespace EyeCtrl.Supervisor.Supervision;

using System;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

/// <summary>
/// Reads <c>general.uiLanguage</c> from the raw settings file so the Supervisor can tell "the file
/// says System" apart from "the file cannot be read". <c>SettingsService.Load()</c> answers a
/// damaged or unparseable file with whole-file defaults without throwing, so asking Load() would
/// silently show the wrong language exactly when something is already wrong.
/// <see cref="TryRead"/> returns <c>null</c> when the answer cannot be told; the caller then acts
/// on its last known value (<see cref="SupervisorLanguageTracker"/>).
/// </summary>
/// <remarks>
/// The mutex-guarded read itself is <see cref="GuardedSettingsReader"/>, shared with
/// <see cref="StartOnBootReader"/>.
/// </remarks>
internal static class UiLanguageReader
{
    /// <summary>The language when the file positively says so; null when it cannot be told.</summary>
    /// <param name="settingsPath">The settings file to read.</param>
    /// <param name="writerMutexName">Writer mutex name; injectable so tests never touch the
    /// production name (box-wide rule).</param>
    public static UiLanguage? TryRead(string settingsPath, string? writerMutexName = null) =>
        GuardedSettingsReader.TryRead(
            settingsPath, "general", "uiLanguage", UiLanguage.System,
            jsonValue =>
            {
                // The settings writer emits camelCase enum tokens, but JsonStringEnumConverter
                // also accepts raw integers on load — accept both, and only values this enum
                // defines; anything else is damaged rather than a guess.
                if (jsonValue.TryGetValue(out string? token))
                    return UiLanguageTokens.TryFromJson(token, out var fromToken) ? fromToken : null;
                if (jsonValue.TryGetValue(out int number) && Enum.IsDefined((UiLanguage)number))
                    return (UiLanguage)number;
                return null;
            },
            writerMutexName);
}
