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

    /// <summary>
    /// Reloads every action from the store into <see cref="Actions"/>, matching any tile that
    /// still lacks a preset id to its preset ONCE and saving that result before returning.
    /// Nothing is dropped or renamed: only empty ids are filled, and only on an exact
    /// (type, payload, English label) match. A load that matches nothing writes nothing.
    /// </summary>
    public void Load()
    {
        Actions.Clear();
        var changed = false;
        foreach (var a in _store.LoadAll())
        {
            changed |= QuickActionPresets.TryAssignPresetId(a);
            Actions.Add(a);
        }
        if (changed) Save();
    }

    /// <summary>Persists the current <see cref="Actions"/> collection.</summary>
    public void Save() => _store.Save(Actions.ToList());

    /// <summary>Adds a keyboard-shortcut action (payload is the chord string) and saves.</summary>
    /// <param name="label">Display label; a preset add passes the preset's English label.</param>
    /// <param name="chord">The chord string.</param>
    /// <param name="presetId">The preset this action came from, or null for a user-named action.</param>
    public void AddKeyboardShortcut(string label, string chord, string? presetId = null)
        => Add(QuickActionType.KeyboardShortcut, label, chord, presetId);

    /// <summary>Adds a media-control action (payload is the command string) and saves.</summary>
    /// <param name="label">Display label; a preset add passes the preset's English label.</param>
    /// <param name="command">The media command string.</param>
    /// <param name="presetId">The preset this action came from, or null for a user-named action.</param>
    public void AddMediaControl(string label, string command, string? presetId = null)
        => Add(QuickActionType.MediaControl, label, command, presetId);

    /// <summary>Adds a window-action action (payload is the WindowAction enum name) and saves.</summary>
    /// <param name="label">Display label; a preset add passes the preset's English label.</param>
    /// <param name="payload">The WindowAction enum member name.</param>
    /// <param name="presetId">The preset this action came from, or null for a user-named action.</param>
    public void AddWindowAction(string label, string payload, string? presetId = null)
        => Add(QuickActionType.WindowAction, label, payload, presetId);

    /// <summary>Adds a launch-Windows-app action (payload is the AUMID; label is the app name) and saves.</summary>
    public void AddApp(InstalledApp app)
        => Add(QuickActionType.LaunchWindowsApp, app.DisplayName, app.Aumid);

    private void Add(QuickActionType type, string label, string payload, string? presetId = null)
    {
        Actions.Add(new QuickAction { Type = type, Label = label, Payload = payload, PresetId = presetId });
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
