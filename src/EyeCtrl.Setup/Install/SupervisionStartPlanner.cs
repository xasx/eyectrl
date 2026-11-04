namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>
/// Plan §10's "Supervision × start-on-boot" answer: after every swap Setup starts ONLY the App,
/// once. It never starts the Supervisor (the App does that itself when supervision is on) and it
/// never writes the HKCU Run value (the App owns start-on-boot). All four on/off combinations
/// therefore produce the same actions; the flags exist so tests pin the independence.
/// </summary>
public sealed record SupervisionStartPlan(
    bool StartAppOnce,
    bool StartSupervisor,
    bool WriteStartOnBootRunKey,
    bool AppWillStartSupervisor,
    bool StartOnBootEnabled)
{
    /// <summary>The one plan for every combination.</summary>
    public static SupervisionStartPlan For(SupervisionBootState state) => new(
        StartAppOnce: true,
        StartSupervisor: false,
        WriteStartOnBootRunKey: false,
        AppWillStartSupervisor: state.SupervisionEnabled,
        StartOnBootEnabled: state.StartOnBoot);
}

/// <summary>Builds the post-swap plan from the install-time settings snapshot.</summary>
public sealed class SupervisionStartPlanner
{
    private readonly ISettingsSnapshotReader _settings;

    /// <summary>Creates the planner.</summary>
    public SupervisionStartPlanner(ISettingsSnapshotReader settings) => _settings = settings;

    /// <summary>Reads the two switches and returns the plan.</summary>
    public SupervisionStartPlan Plan() => SupervisionStartPlan.For(_settings.Read());
}
