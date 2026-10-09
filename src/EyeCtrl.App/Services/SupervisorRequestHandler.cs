namespace EyeCtrl.App.Services;

using System.Text.Json;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// What a handled request answers, and what must happen only once that answer is out.
/// </summary>
/// <param name="Status">The status to acknowledge with.</param>
/// <param name="AfterAck">Runs on the dispatcher after the acknowledgement was written (or the
/// write failed) — a restart, which would otherwise dispose the channel before the ack leaves.</param>
public sealed record SettingRequestOutcome(SettingRequestStatus Status, Action? AfterAck = null);

/// <summary>
/// Applies one Supervisor request to the App's live settings and saves through the App's own
/// writer. Runs on the dispatcher (<see cref="SupervisorRequestService"/> marshals it there), so
/// it sees and changes the same settings object every App surface binds to.
/// </summary>
public sealed class SupervisorRequestHandler
{
    private readonly EyeCtrlSettings _settings;
    private readonly Action<EyeCtrlSettings> _save;
    private readonly ILogger _logger;
    private readonly Func<bool> _isRestartInProgress;
    private readonly Action _requestRestart;
    private readonly Func<UiLanguage, bool> _changesShownLanguage;

    /// <param name="settings">The App's live settings.</param>
    /// <param name="save">The App writer's synchronous save (<see cref="SettingsService.Save"/>).</param>
    /// <param name="logger">Where outcomes are logged.</param>
    /// <param name="isRestartInProgress">Whether a restart is already running
    /// (<see cref="AppRestartCoordinator.IsRestartInProgress"/>). Null: never.</param>
    /// <param name="requestRestart">Starts a restart (<c>HandleRestart(false)</c>); the coordinator
    /// picks the strategy. Null: language requests never restart.</param>
    /// <param name="changesShownLanguage">Whether a saved selection would show a different language
    /// than this process runs — T04's "pending" rule. Default: resolve against the running table.</param>
    public SupervisorRequestHandler(
        EyeCtrlSettings settings,
        Action<EyeCtrlSettings> save,
        ILogger logger,
        Func<bool>? isRestartInProgress = null,
        Action? requestRestart = null,
        Func<UiLanguage, bool>? changesShownLanguage = null)
    {
        _settings = settings;
        _save = save;
        _logger = logger;
        _isRestartInProgress = isRestartInProgress ?? (() => false);
        _requestRestart = requestRestart ?? (() => { });
        _changesShownLanguage = changesShownLanguage ?? (language =>
            ChangesShownLanguage(language, StringTable.Current.Culture, StringTable.Current.Language));
    }

    /// <summary>
    /// T04's "pending" rule: whether saving <paramref name="selection"/> would show a different
    /// language than <paramref name="running"/>. "System" resolves against Windows first, so a
    /// live System on de-DE while German runs changes nothing and needs no restart.
    /// </summary>
    /// <param name="selection">The saved selection.</param>
    /// <param name="culture">The Windows UI culture System resolves against.</param>
    /// <param name="running">The language this process shows.</param>
    public static bool ChangesShownLanguage(UiLanguage selection, System.Globalization.CultureInfo culture, UiLanguage running) =>
        StringTable.ResolveLanguage(selection, culture) != running;

    /// <summary>Applies and saves one request; returns the status to acknowledge with and what
    /// to run after the acknowledgement.</summary>
    public SettingRequestOutcome Handle(SettingRequest request)
    {
        switch (request.Kind)
        {
            case SettingRequestKinds.SetStartOnBoot:
                if (request.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Reject(request, "a boolean value");

                // Setting the property raises PropertyChanged, which re-runs the live Run-key
                // reconciler (SupervisionComposition.ReconcileRunKeyOnStartOnBootChange) — the
                // same path the Settings row takes. The Supervisor never touches the Run key: its
                // registrar would register the Supervisor's own exe.
                _settings.General.StartOnBoot = request.Value.GetBoolean();
                return new SettingRequestOutcome(Save(request));

            case SettingRequestKinds.SetUiLanguage:
                if (request.Value.ValueKind != JsonValueKind.String
                    || !UiLanguageTokens.TryFromJson(request.Value.GetString(), out var language))
                    return Reject(request, "a UiLanguage JSON token");

                _settings.General.UiLanguage = language;
                var saved = Save(request);
                if (saved != SettingRequestStatus.Saved)
                    return new SettingRequestOutcome(saved);

                // restart:false is a parked or pending delivery: saved only — the App's own row
                // shows its pending note, and nothing restarts while the user is working. A live
                // choice restarts only when it changes what this process shows (T04's rule).
                if (!request.Restart || !_changesShownLanguage(language))
                    return new SettingRequestOutcome(SettingRequestStatus.Saved);

                // The coordinator's latch would drop a second restart silently: say so instead.
                if (_isRestartInProgress())
                    return new SettingRequestOutcome(SettingRequestStatus.RestartInProgress);

                // Hook (b): the restart starts only after the acknowledgement is out.
                return new SettingRequestOutcome(SettingRequestStatus.Saved, _requestRestart);

            default:
                _logger.LogWarning("Supervisor request {Id}: unknown kind {Kind}; rejected.", request.Id, request.Kind);
                return new SettingRequestOutcome(SettingRequestStatus.Rejected);
        }
    }

    private SettingRequestOutcome Reject(SettingRequest request, string expected)
    {
        _logger.LogWarning(
            "Supervisor request {Id}: {Kind} needs {Expected}, got {ValueKind}; rejected.",
            request.Id, request.Kind, expected, request.Value.ValueKind);
        return new SettingRequestOutcome(SettingRequestStatus.Rejected);
    }

    private SettingRequestStatus Save(SettingRequest request)
    {
        try
        {
            _save(_settings);
            _logger.LogInformation("Supervisor request {Id}: {Kind} saved.", request.Id, request.Kind);
            return SettingRequestStatus.Saved;
        }
        catch (Exception ex)
        {
            // The live value stays applied (as after a Settings-row change whose save failed); the
            // Supervisor keeps the request parked and re-sends it, and the App's exit save writes
            // the live value too.
            _logger.LogError(ex, "Supervisor request {Id}: {Kind} applied but the save failed.", request.Id, request.Kind);
            return SettingRequestStatus.Failed;
        }
    }
}
