namespace EyeCtrl.App.Services;

using System.Globalization;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;

/// <summary>
/// The App's startup string-table load, extracted so the production arguments (the persisted
/// selection, the process's UI culture, the folder beside the exe) are under test: a gate that
/// loads German through its own copy of the call cannot see this one go wrong, and a wrong
/// argument here turns German off silently (a null folder keeps the App on English for good).
/// <c>App.xaml.cs</c> activates the result; this only builds it.
/// </summary>
internal static class StartupStringTable
{
    /// <summary>The load <c>App.xaml.cs</c> runs: the process's own culture and install folder.</summary>
    /// <param name="settings">The loaded settings, whose <c>General.UiLanguage</c> is the selection.</param>
    /// <param name="warn">Receives the loader's warning lines (the App logs them).</param>
    /// <returns>The table to activate.</returns>
    internal static StringTable Load(EyeCtrlSettings settings, Action<string> warn) =>
        Load(settings, CultureInfo.CurrentUICulture, AppDomain.CurrentDomain.BaseDirectory, warn);

    /// <summary>The same load with the culture and folder named, for a test that lays out its own.</summary>
    /// <param name="settings">The loaded settings, whose <c>General.UiLanguage</c> is the selection.</param>
    /// <param name="culture">The UI culture <c>System</c> resolves against.</param>
    /// <param name="baseDirectory">The folder holding the loose German file and manifest.</param>
    /// <param name="warn">Receives the loader's warning lines.</param>
    /// <returns>The table to activate.</returns>
    internal static StringTable Load(
        EyeCtrlSettings settings,
        CultureInfo culture,
        string baseDirectory,
        Action<string> warn) =>
        StringTable.Load(settings.General.UiLanguage, culture, baseDirectory, warn);
}
