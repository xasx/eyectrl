namespace EyeCtrl.Setup;

/// <summary>
/// The complete command line: five public verbs, the internal elevated jobs, and the flags that
/// are legal for each. Parsing is deliberately strict — an unknown flag must fail before any
/// mutation, because a typo that silently installs something is worse than a usage error.
/// </summary>
public sealed record ParsedSetupCommand(
    SetupJob Job,
    string? PackageDirectory,
    bool Quiet,
    bool? KeepData,
    string? RunId,
    string? SourceSetupExe,
    string? Error)
{
    /// <summary>True for a job only the elevated re-launch may use.</summary>
    public bool IsInternal => Job is SetupJob.InternalPrepareUpdate
        or SetupJob.InternalApplyUpdate
        or SetupJob.InternalPrepareRunCopy
        or SetupJob.InternalApplyRollback
        or SetupJob.InternalRevert
        or SetupJob.InternalUninstall
        or SetupJob.InternalRepair;
}

/// <summary>The parser for <see cref="ParsedSetupCommand"/>. Pure: no file, registry or process access.</summary>
public static class SetupCommandLine
{
    /// <summary>The text printed for <c>--help</c> and for a usage error.</summary>
    public const string Usage =
        """
        EyeCtrl.Setup <verb> [options]

          update <packageDir>     Install the package in <packageDir>.
          rollback                Restore the previous version.
          uninstall [--quiet]     Remove EyeCtrl. --quiet always keeps %APPDATA%\EyeCtrl.
          repair                  Finish an interrupted swap and restore a startable version.
          status                  Print the read-only install state as JSON.

        Options:
          --keep | --delete       Pass the uninstall data choice to the elevated step.
          --help                  Show this text.
        """;

    /// <summary>Parses one command line. A malformed line returns an <see cref="ParsedSetupCommand.Error"/>.</summary>
    public static ParsedSetupCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return Fail("a verb is required");

        var verb = args[0];
        if (verb is "--help" or "-h" or "/?")
            return new ParsedSetupCommand(SetupJob.Help, null, false, null, null, null, null);

        if (verb == "--internal")
            return ParseInternal(args);

        return verb.ToLowerInvariant() switch
        {
            "update" => ParseUpdate(args),
            "rollback" => ParseSimple(SetupJob.Rollback, args),
            "repair" => ParseSimple(SetupJob.Repair, args),
            "status" => ParseSimple(SetupJob.Status, args),
            "uninstall" => ParseUninstall(args),
            _ => Fail($"unknown verb '{verb}'"),
        };
    }

    private static ParsedSetupCommand ParseUpdate(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            return Fail("update requires a package directory");
        if (args.Count > 2)
            return Fail("update takes exactly one package directory");

        // F3: Setup's cwd is neutral, so a relative directory would resolve somewhere else and
        // surface later as a misleading "package damaged". Refuse it before anything starts.
        if (!Path.IsPathFullyQualified(args[1]))
            return Fail("update requires an absolute package directory");
        return new ParsedSetupCommand(SetupJob.Update, args[1], false, null, null, null, null);
    }

    private static ParsedSetupCommand ParseSimple(SetupJob job, IReadOnlyList<string> args) =>
        args.Count > 1
            ? Fail($"{job.ToString().ToLowerInvariant()} takes no arguments")
            : new ParsedSetupCommand(job, null, false, null, null, null, null);

    private static ParsedSetupCommand ParseUninstall(IReadOnlyList<string> args)
    {
        var quiet = false;
        bool? keep = null;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--quiet":
                    quiet = true;
                    break;
                case "--keep":
                    if (keep is not null) return Fail("--keep and --delete are mutually exclusive");
                    keep = true;
                    break;
                case "--delete":
                    if (keep is not null) return Fail("--keep and --delete are mutually exclusive");
                    keep = false;
                    break;
                default:
                    return Fail($"unknown option '{args[i]}'");
            }
        }

        return new ParsedSetupCommand(SetupJob.Uninstall, null, quiet, keep, null, null, null);
    }

    private static ParsedSetupCommand ParseInternal(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return Fail("--internal requires a job");

        var jobName = args[1].ToLowerInvariant();
        string? packageDir = null;
        string? runId = null;
        string? source = null;
        bool? keep = null;
        var quiet = false;

        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--run-id":
                    if (++i >= args.Count) return Fail("--run-id requires a value");
                    runId = args[i];
                    break;
                case "--source":
                    if (++i >= args.Count) return Fail("--source requires a value");
                    source = args[i];
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "--delete":
                    keep = false;
                    break;
                default:
                    if (!arg.StartsWith("--", StringComparison.Ordinal) && packageDir is null)
                    {
                        packageDir = arg;
                        break;
                    }
                    return Fail($"unknown option '{arg}'");
            }
        }

        if (string.IsNullOrWhiteSpace(runId))
            return Fail("an internal job requires --run-id");

        var job = jobName switch
        {
            "prepare-update" => SetupJob.InternalPrepareUpdate,
            "apply-update" => SetupJob.InternalApplyUpdate,
            "prepare-run-copy" => SetupJob.InternalPrepareRunCopy,
            "apply-rollback" => SetupJob.InternalApplyRollback,
            "revert" => SetupJob.InternalRevert,
            "uninstall" => SetupJob.InternalUninstall,
            "repair" => SetupJob.InternalRepair,
            _ => (SetupJob?)null,
        };

        if (job is null)
            return Fail($"unknown internal job '{jobName}'");

        if (job is SetupJob.InternalPrepareUpdate)
        {
            if (string.IsNullOrWhiteSpace(packageDir))
                return Fail($"{jobName} requires a package directory");
            if (!Path.IsPathFullyQualified(packageDir))
                return Fail($"{jobName} requires an absolute package directory");
        }

        if (job is SetupJob.InternalPrepareRunCopy && string.IsNullOrWhiteSpace(source))
            return Fail("prepare-run-copy requires --source");

        return new ParsedSetupCommand(job.Value, packageDir, quiet, keep, runId, source, null);
    }

    private static ParsedSetupCommand Fail(string error) =>
        new(SetupJob.Help, null, false, null, null, null, error);
}
