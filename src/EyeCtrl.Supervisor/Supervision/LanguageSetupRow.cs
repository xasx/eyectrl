namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Globalization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

/// <summary>
/// What the setup window's language row shows and does (T05b). The selection is the pending
/// choice when there is one, otherwise the value on disk. A choice goes to the App over the request
/// channel, live (it may restart the App into the new language); when the App is not available it
/// stays pending, is passed as <c>--ui-language</c> on the Supervisor's next launch of the App, and
/// is retried — saved only — until a guarded re-read finds it on disk.
/// </summary>
internal sealed class LanguageSetupRow : IDisposable
{
    /// <summary>The choices, in the order the row shows them.</summary>
    public static readonly UiLanguage[] Choices = [UiLanguage.System, UiLanguage.German, UiLanguage.English];

    private readonly SettingDelivery<UiLanguage> _delivery;
    private readonly CultureInfo _culture;
    private UiLanguage _onDisk;

    /// <param name="delivery">The Supervisor's single language delivery; it outlives the window.</param>
    /// <param name="onDisk">Reads the saved setting (<see cref="SupervisorLanguageTracker.Current"/>);
    /// read once here, when the window opens.</param>
    /// <param name="culture">The Windows UI culture "System" resolves against (the note speaks the
    /// chosen language); default <see cref="CultureInfo.CurrentUICulture"/>.</param>
    public LanguageSetupRow(SettingDelivery<UiLanguage> delivery, Func<UiLanguage> onDisk, CultureInfo? culture = null)
    {
        _delivery = delivery;
        _culture = culture ?? CultureInfo.CurrentUICulture;
        _onDisk = onDisk();
        _delivery.Changed += OnDeliveryChanged;
    }

    /// <summary>Raised on the UI thread when what the row shows changes.</summary>
    public event Action? Changed;

    /// <summary>The selection the row marks: the pending (or in-flight) choice, else the disk value.</summary>
    public UiLanguage Selected => _delivery.Pending ?? _onDisk;

    /// <summary>Whether the row carries the pending note: a choice waits for the App.</summary>
    public bool IsPending => _delivery.State == SettingDeliveryState.Parked && _delivery.Pending.HasValue;

    /// <summary>A choice button was dwelled.</summary>
    public void Choose(UiLanguage language) => _delivery.Request(language);

    /// <summary>The row's label, readable in both languages.</summary>
    public static string Label(StringTable strings) => strings.T("language-recovery.row.label");

    /// <summary>The caption of one choice: its endonym, marked when it is the selection.</summary>
    public string Caption(UiLanguage language) =>
        (language == Selected ? "✓ " : "") + UiLanguageTokens.Endonym(language);

    /// <summary>The note under the row while a choice is pending, in the CHOSEN language (the
    /// ticket); null when nothing is pending.</summary>
    public string? Note(StringTable strings)
    {
        if (!IsPending) return null;
        // Resolve the target first: the lookup's own "System" means "this table's language", but
        // the caretaker's System means "whatever Windows resolves to" (T04's F2).
        var target = StringTable.ResolveLanguage(Selected, _culture);
        return strings.T(target, "language-recovery.supervisor.pending");
    }

    /// <summary>The setup status line for the delivery's state; null when it has nothing to say.</summary>
    public string? StatusText(StringTable strings) => _delivery.State switch
    {
        SettingDeliveryState.Sending => strings.T("language-recovery.supervisor.sending"),
        SettingDeliveryState.Saved => _delivery.LastSavedStatus == Core.Models.SettingRequestStatus.RestartInProgress
            ? strings.T("language-recovery.supervisor.saved-restarting")
            : strings.T("language-recovery.supervisor.saved"),
        // The note under the row carries the pending explanation; the status line only says
        // that the choice is waiting, so the caretaker does not read the same sentence twice.
        SettingDeliveryState.Parked => strings.T("language-recovery.supervisor.waiting"),
        SettingDeliveryState.Refused => strings.T("language-recovery.supervisor.refused"),
        SettingDeliveryState.Superseded => strings.T("language-recovery.supervisor.superseded"),
        _ => null,
    };

    private void OnDeliveryChanged()
    {
        // A save (confirmed by the re-read) means the choice is on disk; a superseded choice means
        // the disk holds a newer one. Either way the row marks that value once the choice clears.
        if (_delivery.State == SettingDeliveryState.Saved && _delivery.LastSaved is { } saved)
            _onDisk = saved;
        else if (_delivery.State == SettingDeliveryState.Superseded && _delivery.SupersededBy is { } newer)
            _onDisk = newer;
        Changed?.Invoke();
    }

    public void Dispose() => _delivery.Changed -= OnDeliveryChanged;
}
