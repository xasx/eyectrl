namespace EyeCtrl.Setup.Windows;

using System.Runtime.InteropServices;
using EyeCtrl.Setup.Abstractions;

/// <summary>The thin System.IO implementation of <see cref="IFileSystem"/>.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private const uint MoveFileDelayUntilReboot = 0x4;

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
    }

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
            throw new IOException($"The destination already exists: {destination}");
        Directory.Move(source, destination);
    }

    /// <inheritdoc />
    public void CopyFile(string source, string destination, bool stripZoneIdentifier)
    {
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        File.Copy(source, destination, overwrite: true);
        if (stripZoneIdentifier)
            StripZoneIdentifier(destination);
    }

    /// <inheritdoc />
    public void CopyDirectory(string source, string destination, bool stripZoneIdentifier)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            CopyFile(file, Path.Combine(destination, relative), stripZoneIdentifier);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string directory, bool recursive) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(
                    directory, "*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .ToList()
            : [];

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToList()
            : [];

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public long GetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <inheritdoc />
    public void AppendAllText(string path, string contents)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        File.AppendAllText(path, contents);
    }

    /// <inheritdoc />
    public void MarkForDeletionAtReboot(string path)
    {
        if (File.Exists(path))
        {
            MarkFile(path);
            return;
        }

        if (!Directory.Exists(path))
            return;

        // A directory registered with MoveFileEx is removed only when empty, so every file and
        // subdirectory is registered too, deepest first.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length))
        {
            MarkFile(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length))
        {
            MarkDirectory(directory);
        }

        MarkDirectory(path);
    }

    private static void MarkFile(string path) =>
        MoveFileEx(path, null, MoveFileDelayUntilReboot);

    private static void MarkDirectory(string path) =>
        MoveFileEx(path, null, MoveFileDelayUntilReboot);

    private static void StripZoneIdentifier(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch (Exception)
        {
            // No stream, a filesystem without ADS support, or a race: the file itself is intact.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
