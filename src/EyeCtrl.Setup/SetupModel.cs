namespace EyeCtrl.Setup;

/// <summary>
/// The public jobs EyeCtrl.Setup accepts. <see cref="Help"/> is a parser-only answer; the
/// <c>Internal*</c> jobs are never typed by a person — the medium instance re-launches the same
/// exe with one of them through ShellExecute <c>runas</c>, and only those phases touch Program
/// Files or the machine-wide uninstall key.
/// </summary>
public enum SetupJob
{
    Update,
    Rollback,
    Uninstall,
    Repair,
    Status,
    Help,

    InternalPrepareUpdate,
    InternalApplyUpdate,
    InternalPrepareRunCopy,
    InternalApplyRollback,
    InternalRevert,
    InternalUninstall,
    InternalRepair,
}

/// <summary>What happened, in the vocabulary the result record and the S6f screens use.</summary>
public enum SetupOutcome
{
    Ok,
    RolledBack,
    AllFailed,
    VerifyFailed,
    NotApplied,
    Blocked,
    Uninstalled,
    Repaired,
    Cancelled,
    Failed,
}

/// <summary>One verb's terminal result. <see cref="Version"/> is the version the verb acted on
/// (the new one for an update, the restored one for a rollback); <see cref="PreviousVersion"/> is
/// the other side of the record, when there is one.</summary>
public sealed record SetupResult
{
    /// <summary>The verb that produced this result.</summary>
    public required SetupVerb Verb { get; init; }

    /// <summary>The outcome.</summary>
    public required SetupOutcome Outcome { get; init; }

    /// <summary>The version the verb acted on, when known.</summary>
    public string? Version { get; init; }

    /// <summary>The previous version, when known.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>When the verb finished.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>One machine-readable sentence for the log and the status report; never user text.</summary>
    public string? Detail { get; init; }
}

/// <summary>The small public verb set, for the result record (the internal jobs are not results).</summary>
public enum SetupVerb
{
    Update,
    Rollback,
    Uninstall,
    Repair,
    Status,
}

/// <summary>Exit codes shared by the medium and elevated instances.</summary>
public static class SetupExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int Blocked = 2;
    public const int VerifyFailed = 3;
    public const int NotApplied = 4;
    public const int Failed = 5;
    public const int AllFailed = 6;

    /// <summary>Maps a result outcome to the process exit code the parent maps back.</summary>
    public static int For(SetupOutcome outcome) => outcome switch
    {
        SetupOutcome.Ok or SetupOutcome.Uninstalled or SetupOutcome.Repaired or SetupOutcome.RolledBack => Ok,
        SetupOutcome.Blocked => Blocked,
        SetupOutcome.VerifyFailed => VerifyFailed,
        SetupOutcome.NotApplied => NotApplied,
        SetupOutcome.AllFailed => AllFailed,
        SetupOutcome.Cancelled => Ok,
        _ => Failed,
    };
}

/// <summary>The pinned code-signing thumbprint from the C9 plan (§8, S1a). Every file Setup runs
/// or stages must chain to a certificate with this thumbprint; it is a ratchet, not a preference.</summary>
public static class SetupConstants
{
    public const string PinnedThumbprint = "C1039769A2D9ED01F93CEBC78B61176EE6B12F91";

    public const string SetupExeName = "EyeCtrl.Setup.exe";
    public const string AppExeName = "EyeCtrl.App.exe";
    public const string SupervisorExeName = "EyeCtrl.Supervisor.exe";
    public const string CatalogName = "EyeCtrl.cat";
    public const string PackageMetadataName = "package.json";

    public const string InstallFolderName = "EyeCtrl";
    public const string PreviousFolderName = "EyeCtrl.previous";
    public const string StagingFolderName = "EyeCtrl.staging";
    public const string FailedFolderName = "EyeCtrl.failed";
    public const string RunFolderName = "EyeCtrl.run";

    public const string RepairRunOnceName = "EyeCtrlRepair";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EyeCtrl";
    public const string PolicyKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ConsentPromptBehaviorAdmin = "ConsentPromptBehaviorAdmin";

    /// <summary>The integrity level the pass rule requires (High, 0x3000).</summary>
    public const int HighIntegrityLevel = 0x3000;

    public static readonly TimeSpan TokenCheckTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AliveAfter = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CleanExitGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TokenPollInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ProcessExitWait = TimeSpan.FromSeconds(5);
}
