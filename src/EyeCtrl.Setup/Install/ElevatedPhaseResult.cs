namespace EyeCtrl.Setup.Install;

using System.Text.Json;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The elevated child's own result, written to <c>EyeCtrl.run\phase-last.json</c> before it exits.
/// S6c D3: an exit code says only <c>NotApplied</c>, so the medium instance read back
/// <c>childNotApplied</c> and the record lost the real reason (for example
/// <c>image:EyeCtrl.DryRunBlocker.exe</c>).
/// </summary>
public sealed record ElevatedPhaseReport(
    ElevatedJob Job, string? RunId, ElevatedPhaseStatus Status, string? Detail);

/// <summary>Writes and reads the one phase report the elevated child leaves in the run root.</summary>
public interface IElevatedPhaseResultStore
{
    /// <summary>Records one phase's result (the elevated side). Never throws.</summary>
    void Write(ElevatedRequest request, ElevatedPhaseResult result);

    /// <summary>The report for exactly this job and run id, or null when absent or stale.</summary>
    ElevatedPhaseResult? Read(ElevatedRequest request);
}

/// <summary>
/// The file-backed store. It sits beside the journal in <c>EyeCtrl.run</c> — admin-only, never
/// renamed — so the medium process can read it after the elevated child exits. A report is only
/// accepted when both the job and the run id match the request, so a crashed phase's stale line
/// cannot be attributed to the next one.
/// </summary>
public sealed class ElevatedPhaseResultStore : IElevatedPhaseResultStore
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly ISetupLog _log;

    /// <summary>Creates the store.</summary>
    public ElevatedPhaseResultStore(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard, ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _log = log;
    }

    /// <summary>The report's fixed path in the run root.</summary>
    public string ReportPath => Path.Combine(_paths.RunRoot, "phase-last.json");

    /// <inheritdoc />
    public void Write(ElevatedRequest request, ElevatedPhaseResult result)
    {
        if (request.RunId is not { } runId)
            return;

        _guard.RequireProgramFilesPath(ReportPath);
        try
        {
            _fileSystem.CreateDirectory(_paths.RunRoot);
            _fileSystem.WriteAllText(ReportPath, JsonSerializer.Serialize(
                new ElevatedPhaseReport(request.Job, runId, result.Status, result.Detail),
                SetupJson.Options));
        }
        catch (Exception ex)
        {
            // The exit code still carries the status; only the diagnosis is lost.
            _log.Warn($"Could not write the elevated phase result: {ex.GetType().Name}");
        }
    }

    /// <inheritdoc />
    public ElevatedPhaseResult? Read(ElevatedRequest request)
    {
        try
        {
            if (!_fileSystem.FileExists(ReportPath))
                return null;

            var report = JsonSerializer.Deserialize<ElevatedPhaseReport>(
                _fileSystem.ReadAllText(ReportPath), SetupJson.Options);
            if (report is null
                || report.Job != request.Job
                || !string.Equals(report.RunId, request.RunId, StringComparison.Ordinal))
            {
                return null;
            }

            return new ElevatedPhaseResult(report.Status, report.Detail);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
