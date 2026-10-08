namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Globalization;
using System.IO;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

/// <summary>
/// Resolves the Supervisor's own <see cref="StringTable"/> lazily, each time a surface is shown —
/// the Supervisor never restarts for a language change. It reads <c>uiLanguage</c> through
/// <see cref="SupervisorLanguageTracker"/> (mutex-guarded, last-known on contention) and looks for
/// <c>strings.de.json</c> and <c>strings.de.manifest.json</c> beside the App exe it already
/// resolves; when they are absent it shows embedded English.
/// </summary>
internal sealed class SupervisorLanguageResolver
{
    private readonly string _appExeDirectory;
    private readonly SupervisorLanguageTracker _tracker;
    private readonly Func<UiLanguage?> _pending;

    /// <param name="settingsPath">The settings file holding <c>uiLanguage</c>.</param>
    /// <param name="appExePath">The resolved App exe path; null resolves it through
    /// <see cref="AppExePathResolver"/>. Injectable so tests lay out their own install folder.</param>
    /// <param name="writerMutexName">Writer mutex name; injectable so tests never touch the
    /// production mutex.</param>
    /// <param name="pending">The caretaker's language choice not yet known to be saved (T05b's
    /// row); it wins over the setting, so the Supervisor's own screens follow the choice at once.
    /// Null: none.</param>
    public SupervisorLanguageResolver(
        string settingsPath,
        string? appExePath = null,
        string? writerMutexName = null,
        Func<UiLanguage?>? pending = null)
    {
        _pending = pending ?? (() => null);
        var resolvedAppExe = appExePath ?? AppExePathResolver.Resolve();
        _appExeDirectory = Path.GetDirectoryName(resolvedAppExe) ?? AppContext.BaseDirectory;
        _tracker = new SupervisorLanguageTracker(settingsPath, writerMutexName);
    }

    /// <summary>Resolves the language to show this surface in: the pending choice first, then the
    /// setting (disk, last-known, then System).</summary>
    /// <returns>The language the Supervisor's own surfaces should use.</returns>
    public UiLanguage ResolveLanguage() => _pending() ?? _tracker.Current;

    /// <summary>Loads the table for the currently resolved language, resolving it anew.</summary>
    /// <param name="warn">Receives the loader's warning lines; null discards them.</param>
    /// <returns>The table, with embedded English as the floor.</returns>
    public StringTable ResolveTable(Action<string>? warn = null) =>
        StringTable.Load(
            ResolveLanguage(),
            CultureInfo.CurrentUICulture,
            _appExeDirectory,
            warn);
}
