namespace EyeCtrl.Setup.Install;

using System.Text.Json;
using EyeCtrl.Setup.Abstractions;

/// <summary>Which transition was in flight when the machine last touched the folders.</summary>
public enum InstallOperation
{
    Update,
    OwnerRollback,
    Revert,
}

/// <summary>The durable intent: what the state machine was doing and which rename it reached.</summary>
public sealed record InstallJournal(InstallOperation Operation, int Step);

/// <summary>
/// The journal store. It lives at <c>C:\Program Files\EyeCtrl.run\journal.json</c> (admin-only and
/// never renamed), so a repair process finds it regardless of which run copy is current. It is
/// written before each rename and cleared after the transition (or its clean undo).
/// </summary>
public interface IInstallJournalStore
{
    /// <summary>Writes/replaces the in-flight intent.</summary>
    void Write(InstallJournal journal);

    /// <summary>The in-flight intent, or null when there is none (or it is unreadable).</summary>
    InstallJournal? Read();

    /// <summary>Removes the intent after a completed transition or a clean undo.</summary>
    void Clear();
}

/// <summary>The file-backed journal over the run root.</summary>
public sealed class InstallJournalStore : IInstallJournalStore
{
    private readonly SetupPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly PathGuard _guard;
    private readonly ISetupLog _log;

    /// <summary>Creates the store.</summary>
    public InstallJournalStore(
        SetupPaths paths, IFileSystem fileSystem, PathGuard guard, ISetupLog log)
    {
        _paths = paths;
        _fileSystem = fileSystem;
        _guard = guard;
        _log = log;
    }

    /// <summary>The journal's path (also used by status to report a pending repair).</summary>
    public string JournalPath => Path.Combine(_paths.RunRoot, "journal.json");

    /// <inheritdoc />
    public void Write(InstallJournal journal)
    {
        _guard.RequireProgramFilesPath(JournalPath);
        try
        {
            _fileSystem.CreateDirectory(_paths.RunRoot);
            _fileSystem.WriteAllText(JournalPath, JsonSerializer.Serialize(journal, SetupJson.Options));
        }
        catch (Exception ex)
        {
            // A missing journal costs the safe repair decision, not the install; the RunOnce value
            // still exists and repair falls back to the folder heuristics.
            _log.Warn($"Could not write the install journal: {ex.GetType().Name}");
        }
    }

    /// <inheritdoc />
    public InstallJournal? Read()
    {
        try
        {
            if (!_fileSystem.FileExists(JournalPath))
                return null;
            return JsonSerializer.Deserialize<InstallJournal>(
                _fileSystem.ReadAllText(JournalPath), SetupJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _guard.RequireProgramFilesPath(JournalPath);
        try
        {
            _fileSystem.DeleteFile(JournalPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not clear the install journal: {ex.GetType().Name}");
        }
    }
}
