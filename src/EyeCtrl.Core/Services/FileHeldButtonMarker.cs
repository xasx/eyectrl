namespace EyeCtrl.Core.Services;

/// <summary>
/// The production <see cref="IHeldButtonMarker"/>: one small file under the app-data folder,
/// written atomically (temp file in the same directory, then a replace-move) so a reader in
/// another process never sees a half-written marker.
/// <para>
/// Presence is the whole signal — the file's contents are irrelevant — so a torn write can only
/// ever read as "absent", never as a false positive. A crash between the temp write and the move
/// leaves a stray temp file, which nothing reads; <see cref="IsSet"/> and <see cref="Clear"/> only
/// ever look at the real name.
/// </para>
/// </summary>
public sealed class FileHeldButtonMarker : IHeldButtonMarker
{
    /// <summary>The marker's file name inside the app-data folder.</summary>
    public const string FileName = "drag-left-button-held";

    /// <summary>
    /// <c>%APPDATA%\EyeCtrl\drag-left-button-held</c> — the folder the App and the Supervisor
    /// already share (settings.json, logs).
    /// </summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", FileName);

    private readonly string _path;

    /// <summary>Creates a marker over the given path, or the production path when null.</summary>
    /// <param name="path">Marker file path; null selects <see cref="DefaultPath"/>. Tests pass a
    /// temp path so the real app-data file is never touched.</param>
    public FileHeldButtonMarker(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>The file this marker reads and writes (for logging and tests).</summary>
    public string Path => _path;

    /// <inheritdoc />
    public bool IsSet => File.Exists(_path);

    /// <inheritdoc />
    public void Set()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Atomic replace: a concurrent reader sees the old state or the new one, never a partial
        // file. Same-directory temp keeps the move on one volume (where it is a rename).
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, "1");
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best effort: a stray temp is unreadable by the marker and harmless */ }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (DirectoryNotFoundException)
        {
            // No directory means no marker: clearing is already done. (The production folder
            // always exists; this is the temp-path/tests and first-run case.)
        }
    }
}
