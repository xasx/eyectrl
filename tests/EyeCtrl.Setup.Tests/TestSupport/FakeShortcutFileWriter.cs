namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Setup.Abstractions;

/// <summary>Records every shortcut write and mirrors it into the fake file system, so a written
/// .lnk exists afterwards. A test scripts a failure through <see cref="OnWrite"/>; a scripted
/// throw happens before the mirror, so a failed write leaves the file absent.</summary>
public sealed class FakeShortcutFileWriter : IShortcutFileWriter
{
    private readonly InMemoryFileSystem _files;

    /// <summary>Creates the fake over the harness's fake file system.</summary>
    public FakeShortcutFileWriter(InMemoryFileSystem files) => _files = files;

    /// <summary>The writes made, as <c>linkPath|targetExePath</c>.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>When set, every write throws (the failure the test needs).</summary>
    public Action? OnWrite { get; set; }

    /// <inheritdoc />
    public void Write(string linkPath, string targetExePath)
    {
        Calls.Add($"{linkPath}|{targetExePath}");
        OnWrite?.Invoke();
        _files.AddFile(linkPath);
    }
}
