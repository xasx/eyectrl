namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// An in-memory file tree with fault injection. Every mutation is recorded in a shared log so a
/// test can assert order against registry operations (the RunOnce bracket). Tests never touch the
/// real disk or Program Files.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _zoneIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private int _moveCount;
    private int _copyCount;
    private int _deleteCount;

    /// <summary>Creates the tree with an optional shared operation log.</summary>
    public InMemoryFileSystem(List<string>? log = null) => Log = log ?? [];

    /// <summary>A shared ordering log (also used by the registry fake).</summary>
    public List<string> Log { get; }

    /// <summary>The 1-based MoveDirectory call to fail, or null.</summary>
    public int? FailMoveAt { get; set; }

    /// <summary>A predicate over the 1-based MoveDirectory call index; lets a test fail several.</summary>
    public Func<int, bool>? MoveFailure { get; set; }

    /// <summary>The 1-based CopyFile/CopyDirectory call to fail, or null.</summary>
    public int? FailCopyAt { get; set; }

    /// <summary>A predicate over the 1-based copy call index.</summary>
    public Func<int, bool>? CopyFailure { get; set; }

    /// <summary>The 1-based DeleteDirectory call to fail, or null.</summary>
    public int? FailDeleteAt { get; set; }

    /// <summary>A predicate over the 1-based DeleteDirectory call index.</summary>
    public Func<int, bool>? DeleteFailure { get; set; }

    /// <summary>Throws <see cref="SimulatedCrashException"/> after this 1-based move COMPLETES,
    /// leaving the exact state a power loss would leave (no undo).</summary>
    public int? CrashAfterMoveAt { get; set; }

    /// <summary>When it returns true, the next AppendAllText throws (the log must swallow it).</summary>
    public Func<string, bool>? AppendFailure { get; set; }

    /// <summary>Adds a file (and its parents) without logging, for test setup.</summary>
    public void AddFile(string path, string contents = "")
    {
        var full = SetupPaths.Normalize(path);
        EnsureParents(full);
        _files[full] = contents;
    }

    /// <summary>Adds a directory (and its parents) without logging, for test setup.</summary>
    public void AddDirectory(string path)
    {
        var full = SetupPaths.Normalize(path);
        EnsureParents(full);
        _directories.Add(full);
    }

    /// <summary>Marks a file as carrying a Zone.Identifier stream (the inbox's Mark-of-the-Web).</summary>
    public void AddZoneIdentifier(string path) =>
        _zoneIdentifiers.Add(SetupPaths.Normalize(path));

    /// <summary>True when the file still carries a Zone.Identifier stream.</summary>
    public bool HasZoneIdentifier(string path) =>
        _zoneIdentifiers.Contains(SetupPaths.Normalize(path));

    /// <summary>The text content, or null.</summary>
    public string? Contents(string path) =>
        _files.TryGetValue(SetupPaths.Normalize(path), out var value) ? value : null;

    /// <summary>All files under a folder, for assertions.</summary>
    public IReadOnlyList<string> FilesUnder(string folder)
    {
        var full = SetupPaths.Normalize(folder);
        return _files.Keys.Where(p => SetupPaths.IsUnder(full, p)).ToList();
    }

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(SetupPaths.Normalize(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _directories.Contains(SetupPaths.Normalize(path));

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        var full = SetupPaths.Normalize(path);
        Log.Add("mkdir:" + full);
        EnsureParents(full);
        _directories.Add(full);
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        var full = SetupPaths.Normalize(path);
        Log.Add("deleteFile:" + full);
        _files.Remove(full);
        _zoneIdentifiers.Remove(full);
    }

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive)
    {
        var full = SetupPaths.Normalize(path);
        Log.Add("deleteDir:" + full);
        if (FailDeleteAt == ++_deleteCount || DeleteFailure?.Invoke(_deleteCount) == true)
            throw new IOException("injected delete failure");

        if (!_directories.Contains(full))
            return;

        if (!recursive && _files.Keys.Any(p => SetupPaths.IsUnder(full, p)))
            throw new IOException("directory is not empty");

        foreach (var file in _files.Keys.Where(p => SetupPaths.IsUnder(full, p)).ToList())
        {
            _files.Remove(file);
            _zoneIdentifiers.Remove(file);
        }

        foreach (var directory in _directories.Where(p => SetupPaths.IsUnder(full, p)).ToList())
            _directories.Remove(directory);
    }

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination)
    {
        var from = SetupPaths.Normalize(source);
        var to = SetupPaths.Normalize(destination);
        Log.Add("move:" + from + "->" + to);

        if (FailMoveAt == ++_moveCount || MoveFailure?.Invoke(_moveCount) == true)
            throw new IOException("injected move failure");

        if (!_directories.Contains(from))
            throw new DirectoryNotFoundException(from);
        if (_directories.Contains(to) || _files.ContainsKey(to))
            throw new IOException("destination exists");

        var movingDirectories = _directories.Where(p => SetupPaths.IsUnder(from, p)).ToList();
        var movingFiles = _files.Where(kv => SetupPaths.IsUnder(from, kv.Key)).ToList();
        var movingZones = _zoneIdentifiers.Where(p => SetupPaths.IsUnder(from, p)).ToList();

        foreach (var directory in movingDirectories)
            _directories.Remove(directory);
        foreach (var file in movingFiles)
            _files.Remove(file.Key);
        foreach (var zone in movingZones)
            _zoneIdentifiers.Remove(zone);

        foreach (var directory in movingDirectories)
            _directories.Add(to + directory[from.Length..]);
        foreach (var file in movingFiles)
            _files[to + file.Key[from.Length..]] = file.Value;
        foreach (var zone in movingZones)
            _zoneIdentifiers.Add(to + zone[from.Length..]);

        if (CrashAfterMoveAt == _moveCount)
            throw new SimulatedCrashException($"simulated power loss after move {_moveCount}");
    }

    /// <inheritdoc />
    public void CopyFile(string source, string destination, bool stripZoneIdentifier)
    {
        var from = SetupPaths.Normalize(source);
        var to = SetupPaths.Normalize(destination);
        Log.Add("copy:" + from + "->" + to);

        if (FailCopyAt == ++_copyCount || CopyFailure?.Invoke(_copyCount) == true)
            throw new IOException("injected copy failure");

        if (!_files.TryGetValue(from, out var contents))
            throw new FileNotFoundException(from);

        EnsureParents(to);
        _files[to] = contents;
        if (_zoneIdentifiers.Contains(from) && !stripZoneIdentifier)
            _zoneIdentifiers.Add(to);
    }

    /// <inheritdoc />
    public void CopyDirectory(string source, string destination, bool stripZoneIdentifier)
    {
        var from = SetupPaths.Normalize(source);
        var to = SetupPaths.Normalize(destination);
        Log.Add("copyDir:" + from + "->" + to);

        if (FailCopyAt == ++_copyCount || CopyFailure?.Invoke(_copyCount) == true)
            throw new IOException("injected copy failure");

        AddDirectory(to);
        foreach (var directory in _directories.Where(p => SetupPaths.IsUnder(from, p)).ToList())
            AddDirectory(to + directory[from.Length..]);
        foreach (var file in _files.Keys.Where(p => SetupPaths.IsUnder(from, p)).ToList())
            CopyFile(file, to + file[from.Length..], stripZoneIdentifier);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string directory, bool recursive)
    {
        var full = SetupPaths.Normalize(directory);
        return _files.Keys
            .Where(p => SetupPaths.IsUnder(full, p))
            .Where(p => recursive
                || string.Equals(Path.GetDirectoryName(p), full, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string directory)
    {
        var full = SetupPaths.Normalize(directory);
        return _directories
            .Where(p => string.Equals(Path.GetDirectoryName(p), full, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc />
    public string ReadAllText(string path) =>
        _files.TryGetValue(SetupPaths.Normalize(path), out var contents)
            ? contents
            : throw new FileNotFoundException(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        var full = SetupPaths.Normalize(path);
        Log.Add("write:" + full);
        EnsureParents(full);
        _files[full] = contents;
    }

    /// <inheritdoc />
    public void AppendAllText(string path, string contents)
    {
        var full = SetupPaths.Normalize(path);
        Log.Add("append:" + full);
        if (AppendFailure?.Invoke(full) == true)
            throw new IOException("injected append failure");

        EnsureParents(full);
        _files[full] = _files.TryGetValue(full, out var existing)
            ? existing + contents
            : contents;
    }

    /// <inheritdoc />
    public void MarkForDeletionAtReboot(string path) =>
        Log.Add("reboot:" + SetupPaths.Normalize(path));

    private void EnsureParents(string path)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            _directories.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }
    }
}
