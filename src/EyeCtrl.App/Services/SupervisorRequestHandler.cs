namespace EyeCtrl.App.Services;

using System.Text.Json;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

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

    /// <param name="settings">The App's live settings.</param>
    /// <param name="save">The App writer's synchronous save (<see cref="SettingsService.Save"/>).</param>
    /// <param name="logger">Where outcomes are logged.</param>
    public SupervisorRequestHandler(EyeCtrlSettings settings, Action<EyeCtrlSettings> save, ILogger logger)
    {
        _settings = settings;
        _save = save;
        _logger = logger;
    }

    /// <summary>Applies and saves one request; returns the status to acknowledge with.</summary>
    public SettingRequestStatus Handle(SettingRequest request)
    {
        switch (request.Kind)
        {
            case SettingRequestKinds.SetStartOnBoot:
                if (request.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    _logger.LogWarning(
                        "Supervisor request {Id}: {Kind} needs a boolean value, got {ValueKind}; rejected.",
                        request.Id, request.Kind, request.Value.ValueKind);
                    return SettingRequestStatus.Rejected;
                }

                // Setting the property raises PropertyChanged, which re-runs the live Run-key
                // reconciler (SupervisionComposition.ReconcileRunKeyOnStartOnBootChange) — the
                // same path the Settings row takes. The Supervisor never touches the Run key: its
                // registrar would register the Supervisor's own exe.
                _settings.General.StartOnBoot = request.Value.GetBoolean();
                return Save(request);

            default:
                _logger.LogWarning("Supervisor request {Id}: unknown kind {Kind}; rejected.", request.Id, request.Kind);
                return SettingRequestStatus.Rejected;
        }
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
