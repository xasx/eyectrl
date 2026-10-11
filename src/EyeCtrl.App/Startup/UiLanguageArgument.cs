namespace EyeCtrl.App.Startup;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;

/// <summary>
/// The <c>--ui-language &lt;system|de|en&gt;</c> flag: the first argument that takes a value.
/// It is a PERSISTING setter — applying it writes <see cref="GeneralSettings.UiLanguage"/> through
/// the App's settings writer, so a later relaunch without the flag loses nothing (the synthesis'
/// fix for critique-01 #6). The Supervisor passes it when it launches the App on behalf of a
/// language chosen while the App was down (T05b).
/// </summary>
/// <remarks>
/// The token mapping lives in <see cref="UiLanguageTokens"/> alone — this class must not spell a
/// language out. An unknown or missing value is ignored with a log line; a failed save never
/// escapes into startup, because the flag is a convenience and the App must boot regardless.
/// </remarks>
internal static class UiLanguageArgument
{
    /// <summary>The flag's spelling on the command line.</summary>
    internal const string Flag = "--ui-language";

    /// <summary>
    /// Finds the first <paramref name="Flag"/> and the token after it.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="value">The following token, or null when the flag is last.</param>
    /// <returns>Whether the flag is present.</returns>
    internal static bool TryGetValue(IReadOnlyList<string> args, out string? value)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], Flag, StringComparison.OrdinalIgnoreCase)) continue;

            value = index + 1 < args.Count ? args[index + 1] : null;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Applies the flag to <paramref name="settings"/> and persists through
    /// <paramref name="save"/>. Never throws; the return value is the log line's outcome.
    /// </summary>
    /// <remarks>
    /// The save happens through the caller's App-writer service (the App is the only process that
    /// persists <c>UiLanguage</c>). A save failure leaves the choice applied in memory for this
    /// run — the user asked for it, and the table loads from <paramref name="settings"/> — but
    /// reports that it was not persisted, so a later launch reverting is explained in the log.
    /// </remarks>
    /// <param name="args">The process arguments.</param>
    /// <param name="settings">The loaded settings to mutate.</param>
    /// <param name="save">Persists the settings; may throw.</param>
    /// <returns>Null when the flag is absent, else a line for the startup log.</returns>
    internal static string? Apply(IReadOnlyList<string> args, EyeCtrlSettings settings, Action save)
    {
        if (!TryGetValue(args, out var value))
            return null;

        if (!UiLanguageTokens.TryFromCommandLine(value, out var language))
            return $"ignored: {value ?? "<missing>"} is not one of system|de|en";

        settings.General.UiLanguage = language;
        try
        {
            save();
        }
        catch (Exception ex)
        {
            return $"applied {UiLanguageTokens.ToCommandLine(language)} in memory but not saved: {ex.Message}";
        }

        return $"applied {UiLanguageTokens.ToCommandLine(language)}";
    }
}
