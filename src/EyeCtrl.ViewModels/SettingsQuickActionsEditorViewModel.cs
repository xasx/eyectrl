namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Editor ViewModel for the Quick Actions settings tab. Loads the full set of
/// <see cref="QuickAction"/> entries (including disabled ones) from the store and
/// provides CRUD operations used by the action-card UI. The tab's first page (config
/// controls) binds directly to <see cref="EyeCtrlSettings.QuickActions"/>; this VM
/// drives the deferred Page-2 card editor and is kept construction-wired even when
/// the card UI is not yet rendered.
/// </summary>
public partial class SettingsQuickActionsEditorViewModel : ObservableObject
{
    private readonly IQuickActionsStore _store;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;

    /// <summary>Visibility flag for the deferred action-card editor surface.</summary>
    [ObservableProperty] private bool _isVisible;

    /// <summary>All stored actions (enabled and disabled) for the editor grid.</summary>
    public ObservableCollection<QuickAction> Actions { get; } = new();

    public SettingsQuickActionsEditorViewModel(
        IQuickActionsStore store,
        EyeCtrlSettings settings,
        IMessenger messenger)
    {
        _store = store;
        _settings = settings;
        _messenger = messenger;
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

    /// <summary>Appends a new action of the given type with a default label and saves.</summary>
    public void AddAction(QuickActionType type)
    {
        var action = new QuickAction { Type = type, Label = $"New {type}" };
        Actions.Add(action);
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

    /// <summary>
    /// Patch-style update for the action with the given id: only non-null arguments are applied.
    /// </summary>
    public void UpdateAction(string id, string? label, string? payload, QuickActionType? type)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action == null) return;
        if (label != null) action.Label = label;
        if (payload != null) action.Payload = payload;
        if (type.HasValue) action.Type = type.Value;
        Save();
    }
}
