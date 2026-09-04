namespace EyeCtrl.App.Services;

using System;
using System.ComponentModel;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

/// <summary>
/// Starts and stops the adaptation runtime on the <see cref="AdaptationSettings.Enabled"/> edge, so an
/// opt-in feature that is off by default costs nothing while it is off.
/// <para>
/// Previously every piece started unconditionally at app launch: the coordinator's 4 Hz dispatcher timer,
/// its three assessment subscribers, both feedback overlay windows (each a real HWND, created merely by
/// resolving the cue service), and — the one with genuine cross-process cost — a <b>system-wide UI
/// Automation focus hook</b>. "Off by default" was true of adaptation's *effects* but not of its footprint.
/// </para>
/// <para>
/// Services are resolved lazily on first activation (hence <see cref="IServiceProvider"/> rather than
/// constructor-injected instances — resolving <c>AdaptationCueService</c> is what constructs
/// <c>CueOverlayWindow</c>, which must not happen while disabled). Every participant's <c>Start</c> is
/// idempotent and its <c>Dispose</c> leaves it restartable, so toggling the setting repeatedly is safe.
/// </para>
/// <para>
/// <c>AdaptationResetHandler</c> is deliberately NOT gated: it is a single messenger registration with no
/// runtime cost, and §8.6 wants the manual escape reachable rather than conditional.
/// </para>
/// </summary>
public sealed class AdaptationActivation : IDisposable
{
    private readonly AdaptationSettings _adaptation;
    // Nullable engine so the testing seam can drive the enable/disable edges without constructing a real
    // GazeEngine (and its whole input-simulator surface). In production it is always non-null: the public
    // Start(GazeEngine) is the only entry point.
    private readonly Action<GazeEngine?> _activate;
    private readonly Action _deactivate;
    private readonly PropertyChangedEventHandler _settingsChangedHandler;

    private GazeEngine? _gazeEngine;
    private bool _active;
    private bool _started;

    /// <summary>Production constructor: resolves and starts/stops the runtime out of the container.</summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Enabled"/> is the gate.</param>
    /// <param name="services">Container used to resolve the adaptation runtime lazily on first activation.</param>
    public AdaptationActivation(AdaptationSettings adaptation, IServiceProvider services)
        : this(adaptation, engine => StartRuntime(services, engine!), () => StopRuntime(services))
    {
    }

    /// <summary>
    /// Testing constructor: injects the activate/deactivate effects directly, so the edge behaviour is
    /// headless-testable without a container, a WPF overlay, or a UIA hook.
    /// </summary>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Enabled"/> is the gate.</param>
    /// <param name="activate">Invoked when adaptation becomes enabled.</param>
    /// <param name="deactivate">Invoked when adaptation becomes disabled.</param>
    internal AdaptationActivation(AdaptationSettings adaptation, Action<GazeEngine?> activate, Action deactivate)
    {
        _adaptation = adaptation;
        _activate = activate;
        _deactivate = deactivate;
        _settingsChangedHandler = OnSettingsChanged;
    }

    /// <summary>Whether the adaptation runtime is currently started. Exposed for tests/diagnostics.</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Begins watching <see cref="AdaptationSettings.Enabled"/> and activates immediately if it is already
    /// on. Call once on the UI thread after <c>GazeEngine.Start()</c>; idempotent.
    /// </summary>
    /// <param name="gazeEngine">The gaze engine the coordinator's tick clock subscribes to.</param>
    public void Start(GazeEngine gazeEngine) => StartCore(gazeEngine);

    /// <summary>Testing entry point: drives the edges without requiring a real <see cref="GazeEngine"/>.</summary>
    internal void StartCore(GazeEngine? gazeEngine)
    {
        if (_started)
            return;
        _started = true;
        _gazeEngine = gazeEngine;
        _adaptation.PropertyChanged += _settingsChangedHandler;
        if (_adaptation.Enabled)
            Activate();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdaptationSettings.Enabled))
            return;
        if (_adaptation.Enabled)
            Activate();
        else
            Deactivate();
    }

    private void Activate()
    {
        if (_active || !_started)
            return;
        _active = true;
        try
        {
            _activate(_gazeEngine);
        }
        catch (Exception ex)
        {
            // Never let a feedback/overlay construction failure break app startup or the settings toggle.
            Log.Error(ex, "Failed to activate the adaptation runtime; adaptation stays inert.");
            Deactivate();
        }
    }

    private void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        try
        {
            _deactivate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to cleanly deactivate the adaptation runtime.");
        }
    }

    private static void StartRuntime(IServiceProvider services, GazeEngine gazeEngine)
    {
        var coordinator = services.GetRequiredService<AdaptationCoordinator>();
        coordinator.Start(gazeEngine);
        services.GetRequiredService<AdaptationCueService>().Start(coordinator);
        services.GetRequiredService<AdaptationLog>().Start(coordinator);
        services.GetRequiredService<DriftNoticeService>().Start(coordinator);
        // Sensitive-context detection only matters while feedback can be shown, so the system-wide UIA focus
        // hook goes in and out with the rest of the runtime (§8.10).
        services.GetRequiredService<ISensitiveContextDetector>().Start();
        Log.Information("Adaptation runtime activated");
    }

    private static void StopRuntime(IServiceProvider services)
    {
        // Reset BEFORE stopping the timer: the coordinator's own enabled→disabled tick branch would have
        // cleared momentary state, but with the timer stopped that tick never runs — and effective settings
        // must return to baseline the moment adaptation is switched off (§12).
        var coordinator = services.GetRequiredService<AdaptationCoordinator>();
        // Momentary only — the learned offsets survive the disable and resume when it is toggled back on
        // (§7.4/§13.1; they are inert while disabled because Effective gates on Enabled first).
        coordinator.ResetMomentary();
        coordinator.Dispose();
        services.GetRequiredService<AdaptationCueService>().Dispose();
        services.GetRequiredService<AdaptationLog>().Dispose();
        services.GetRequiredService<DriftNoticeService>().Dispose();
        services.GetRequiredService<ISensitiveContextDetector>().Stop();
        Log.Information("Adaptation runtime deactivated; effective settings back at baseline");
    }

    /// <summary>Stops watching the setting and tears the runtime down if active.</summary>
    public void Dispose()
    {
        if (_started)
        {
            _adaptation.PropertyChanged -= _settingsChangedHandler;
            _started = false;
        }
        Deactivate();
        _gazeEngine = null;
    }
}
