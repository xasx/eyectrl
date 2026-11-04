namespace EyeCtrl.Setup.Tests.TestSupport;

/// <summary>Stands in for a power loss: the file operation has happened, but no undo runs.</summary>
public sealed class SimulatedCrashException : Exception
{
    /// <summary>Creates the crash marker.</summary>
    public SimulatedCrashException(string message) : base(message)
    {
    }
}
