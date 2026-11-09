namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Setup.Abstractions;

/// <summary>A recording clock: sleeping advances the fake time instantly.</summary>
public sealed class FakeClock : ISetupClock
{
    /// <summary>Creates the clock at a fixed instant.</summary>
    public FakeClock() => UtcNow = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Every requested sleep, for assertions.</summary>
    public List<TimeSpan> Sleeps { get; } = [];

    /// <inheritdoc />
    public void Sleep(TimeSpan duration)
    {
        Sleeps.Add(duration);
        UtcNow += duration;
    }
}

/// <summary>A recording log.</summary>
public sealed class RecordingSetupLog : ISetupLog
{
    /// <summary>All lines.</summary>
    public List<string> Lines { get; } = [];

    /// <inheritdoc />
    public void Info(string message) => Lines.Add("INF " + message);

    /// <inheritdoc />
    public void Warn(string message) => Lines.Add("WRN " + message);

    /// <inheritdoc />
    public void Error(string message) => Lines.Add("ERR " + message);
}

/// <summary>A fake log probe.</summary>
public sealed class FakeAppLogProbe : IAppLogProbe
{
    /// <summary>What the probe answers when <see cref="Results"/> is empty.</summary>
    public AppStartupTokenLine Result { get; set; } = AppStartupTokenLine.UiAccessTrue;

    /// <summary>Per-call answers; consumed before <see cref="Result"/>.</summary>
    public Queue<AppStartupTokenLine> Results { get; } = new();

    /// <summary>The calls made.</summary>
    public List<string> Calls { get; } = [];

    /// <inheritdoc />
    public AppStartupTokenLine ReadStartupTokenLine(string appExePath, DateTimeOffset sinceUtc)
    {
        Calls.Add($"{appExePath}|{sinceUtc:O}");
        return Results.Count > 0 ? Results.Dequeue() : Result;
    }
}

/// <summary>A fake settings snapshot.</summary>
public sealed class FakeSettingsReader : ISettingsSnapshotReader
{
    /// <summary>The switches the next read returns.</summary>
    public SupervisionBootState State { get; set; }

    /// <inheritdoc />
    public SupervisionBootState Read() => State;
}

/// <summary>A fake held-button recovery.</summary>
public sealed class FakeHeldButtonRecovery : IHeldButtonRecovery
{
    /// <summary>The result returned.</summary>
    public EyeCtrl.Core.Services.HeldButtonReleaseResult Result { get; set; } =
        EyeCtrl.Core.Services.HeldButtonReleaseResult.NoMarker;

    /// <summary>The calls made.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public EyeCtrl.Core.Services.HeldButtonReleaseResult ReleaseIfMarked()
    {
        Calls++;
        return Result;
    }
}

/// <summary>A fake Q8 choice.</summary>
public sealed class FakeUninstallChoice : IUninstallChoice
{
    /// <summary>The answer.</summary>
    public UninstallChoice Choice { get; set; } = UninstallChoice.Keep;

    /// <summary>O6a: the route a <see cref="UninstallChoice.Cancel"/> answer reports.</summary>
    public UninstallCancelPath CancelPath { get; set; } = UninstallCancelPath.Button;

    /// <summary>The calls made.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public UninstallChoiceAnswer Ask()
    {
        Calls++;
        return new(Choice, Choice == UninstallChoice.Cancel ? CancelPath : UninstallCancelPath.None);
    }
}

/// <summary>A fake app-data deletion seam.</summary>
public sealed class FakeAppDataDeletion : IAppDataDeletion
{
    /// <summary>The result.</summary>
    public AppDataDeletionResult Result { get; set; } = new(true, []);

    /// <summary>The calls made.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public AppDataDeletionResult Delete()
    {
        Calls++;
        return Result;
    }
}

/// <summary>A fake self path.</summary>
public sealed class FakeSetupEnvironment : ISetupEnvironment
{
    /// <summary>The path returned.</summary>
    public string? ProcessPath { get; set; }
}

/// <summary>A fixed run id.</summary>
public sealed class FakeRunIdGenerator : IRunIdGenerator
{
    /// <summary>The id.</summary>
    public string Id { get; set; } = "run1";

    /// <inheritdoc />
    public string NewId() => Id;
}

/// <summary>Captures the status JSON.</summary>
public sealed class FakeStatusWriter : IStatusWriter
{
    /// <summary>The last document.</summary>
    public string? Json { get; private set; }

    /// <inheritdoc />
    public void Write(string json) => Json = json;
}
