namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Settings;

/// <summary>
/// Tab/page state for the settings window shell. The window renders from its own
/// <c>_tabDefs</c> registry and the <c>ISettingsPageSource</c> classes — this VM owns
/// only which tab is active and which page of it is shown, plus the reset-all action
/// the General page's "Reset ALL Settings" button invokes.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly EyeCtrlSettings _settings;

    [ObservableProperty] private int _currentPage;
    // Owned by the SettingsWindow shell (set from the real page list); nothing in the VM may recompute it.
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private string _currentTabName = "";

    public SettingsViewModel(EyeCtrlSettings settings)
    {
        _settings = settings;
    }

    public void ShowTab(string tabName)
    {
        CurrentTabName = tabName;
        CurrentPage = 0;
    }

    public void NextPage()
    {
        if (CurrentPage < TotalPages - 1)
            CurrentPage++;
    }

    public void PreviousPage()
    {
        if (CurrentPage > 0)
            CurrentPage--;
    }

    /// <summary>
    /// Restores every App-owned sub-setting (plus SelectionMode and ViewPosition) from a fresh
    /// default <see cref="EyeCtrlSettings"/>. Callers follow with ApplyAndSave +
    /// RebuildContent, so no UI refresh happens here.
    ///
    /// <para><b>Supervision is deliberately skipped.</b> The App does not own that section
    /// (<c>SettingsWriter.Supervisor</c>): its enable flag is written only through the sanctioned
    /// exception, and crash recovery is turned off through the supervision row's RequestDisable
    /// (persist + Run-key), never a bulk reset. Resetting it in memory would disarm the running
    /// app's crash policy while disk and the Supervisor stayed on.</para>
    ///
    /// <para><b>The language survives (D10, the user's call 2026-10-07).</b> A bulk reset must not
    /// undo a language choice — for a user who cannot read the reset button's language, being
    /// dropped back into it on the next restart is its own recovery problem. Reset General leaves
    /// <c>UiLanguage</c> alone for the same reason. The interface mode survives for the same
    /// reason — a bulk reset must not flip fifteen tabs into a novice's face. So does
    /// <c>SafetySetupDone</c>: the one-time safety questions are not re-asked by a reset (the
    /// General page's "Run safety setup again" row asks on demand).</para>
    /// </summary>
    public void ResetAllSettings()
    {
        var defaults = new EyeCtrlSettings();
        var language = _settings.General.UiLanguage;
        var simpleUi = _settings.General.SimpleSettingsUi;
        var safetySetupDone = _settings.General.SafetySetupDone;
        var defaultSubs = defaults.EnumerateSubSettings().ToDictionary(t => t.Name, t => t.Settings);
        foreach (var (name, sub) in _settings.EnumerateSubSettings())
        {
            if (name == nameof(EyeCtrlSettings.Supervision))
                continue;
            CopySettings(defaultSubs[name], sub);
        }
        _settings.General.UiLanguage = language;
        _settings.General.SimpleSettingsUi = simpleUi;
        _settings.General.SafetySetupDone = safetySetupDone;
        _settings.SelectionMode = defaults.SelectionMode;
        _settings.ViewPosition = defaults.ViewPosition;
        CurrentPage = 0;
    }

    private static void CopySettings(object source, object target)
    {
        foreach (var prop in source.GetType().GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(target, prop.GetValue(source));
        }
    }
}
