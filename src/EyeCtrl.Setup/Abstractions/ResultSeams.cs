namespace EyeCtrl.Setup.Abstractions;

/// <summary>Reads and writes the one result record, <c>%APPDATA%\EyeCtrl\setup-last.json</c>.</summary>
public interface IResultRecorder
{
    /// <summary>Writes the result atomically.</summary>
    void Write(SetupResult result);

    /// <summary>Reads the last record, or null when none exists or it is unreadable.</summary>
    SetupResult? TryReadLast();
}
