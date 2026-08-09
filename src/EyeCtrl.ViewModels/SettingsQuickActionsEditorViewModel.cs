namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Editor ViewModel for the Quick Actions settings tab. Loads every <see cref="QuickAction"/>
/// (including disabled) from the store and provides gaze-authorable create + manage operations.
/// </summary>
public partial class SettingsQuickActionsEditorViewModel : ObservableObject
{
    private readonly IQuickActionsStore _store;
    private readonly IInstalledAppProvider _appProvider;

    /// <summary>All stored actions (enabled and disabled) for the editor list.</summary>
    public ObservableCollection<QuickAction> Actions { get; } = new();

    /// <summary>Installed apps for the "Launch App" picker (provider-cached).</summary>
    public IReadOnlyList<InstalledApp> InstalledApps => _appProvider.GetInstalledApps();

    public SettingsQuickActionsEditorViewModel(IQuickActionsStore store, IInstalledAppProvider appProvider)
    {
        _store = store;
        _appProvider = appProvider;
    }

    /// <summary>Reloads every action from the store into <see cref="Actions"/>.</summary>
    public void Load()
    {
        Actions.Clear();
        foreach (var a in _store.LoadAll())
            Actions.Add(a);
    }

    /// <summary>Persists the current <see cref="Actions"/> collection.</summary>
    public void Save() => _store.Save(Actions.ToList());

    /// <summary>Adds a keyboard-shortcut action (payload is the chord string) and saves.</summary>
    public void AddKeyboardShortcut(string label, string chord)
        => Add(QuickActionType.KeyboardShortcut, label, chord);

    /// <summary>Adds a media-control action (payload is the command string) and saves.</summary>
    public void AddMediaControl(string label, string command)
        => Add(QuickActionType.MediaControl, label, command);

    /// <summary>Adds a window-action (payload is the WindowAction enum name) and saves.</summary>
    public void AddWindowAction(string label, string payload)
        => Add(QuickActionType.WindowAction, label, payload);

    /// <summary>Adds a launch-Windows-app action (payload is the AUMID; label is the app name) and saves.</summary>
    public void AddApp(InstalledApp app)
        => Add(QuickActionType.LaunchWindowsApp, app.DisplayName, app.Aumid);

    private void Add(QuickActionType type, string label, string payload)
    {
        Actions.Add(new QuickAction { Type = type, Label = label, Payload = payload });
        Save();
    }

    /// <summary>Removes the action with the given id (if present) and saves.</summary>
    public void DeleteAction(string id)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action != null)
        {
            Actions.Remove(action);
            Save();
        }
    }

    /// <summary>Toggles the <see cref="QuickAction.Enabled"/> flag of the action with the given id and saves.</summary>
    public void ToggleEnabled(string id)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action != null)
        {
            action.Enabled = !action.Enabled;
            Save();
        }
    }
}
