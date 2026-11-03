namespace EyeCtrl.Setup.Abstractions;

/// <summary>
/// Every file-system mutation the engine performs. The production implementation is a thin
/// System.IO wrapper; tests use an in-memory tree with fault injection, so no test ever touches
/// Program Files or app data.
/// </summary>
public interface IFileSystem
{
    /// <summary>True when the file exists.</summary>
    bool FileExists(string path);

    /// <summary>True when the directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Creates a directory (and its parents).</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes one file; missing is a no-op.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes a directory.</summary>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>Renames a directory. The destination must not exist.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Copies one file, optionally stripping Mark-of-the-Web from the copy.</summary>
    void CopyFile(string source, string destination, bool stripZoneIdentifier);

    /// <summary>Copies a directory tree, stripping Mark-of-the-Web from every copied file.</summary>
    void CopyDirectory(string source, string destination, bool stripZoneIdentifier);

    /// <summary>All files below a directory (full paths).</summary>
    IReadOnlyList<string> EnumerateFiles(string directory, bool recursive);

    /// <summary>All immediate subdirectories (full paths).</summary>
    IReadOnlyList<string> EnumerateDirectories(string directory);

    /// <summary>Reads a text file.</summary>
    string ReadAllText(string path);

    /// <summary>Writes a text file atomically (temp file in the same directory, then a replace-move).</summary>
    void WriteAllText(string path, string contents);

    /// <summary>Registers a file or directory tree for deletion at the next restart.</summary>
    void MarkForDeletionAtReboot(string path);
}

/// <summary>The registry writes and reads the engine needs, behind one seam.</summary>
public interface IRegistryStore
{
    /// <summary>Reads a DWORD, or null when the key/value does not exist.</summary>
    int? ReadDword(Microsoft.Win32.RegistryHive hive, string subKey, string valueName);

    /// <summary>Reads a string, or null when the key/value does not exist.</summary>
    string? ReadString(Microsoft.Win32.RegistryHive hive, string subKey, string valueName);

    /// <summary>Writes a string value.</summary>
    void WriteString(Microsoft.Win32.RegistryHive hive, string subKey, string valueName, string value);

    /// <summary>Writes a DWORD value.</summary>
    void WriteDword(Microsoft.Win32.RegistryHive hive, string subKey, string valueName, int value);

    /// <summary>Deletes a value; missing is a no-op.</summary>
    void DeleteValue(Microsoft.Win32.RegistryHive hive, string subKey, string valueName);

    /// <summary>Deletes a key; missing is a no-op.</summary>
    void DeleteKey(Microsoft.Win32.RegistryHive hive, string subKey);

    /// <summary>The value names present under a key, or an empty list when the key is missing.</summary>
    IReadOnlyList<string> ReadValueNames(Microsoft.Win32.RegistryHive hive, string subKey);
}
