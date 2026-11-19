namespace EyeCtrl.Setup.Tests.TestSupport;

using EyeCtrl.Core.Services;
using EyeCtrl.Setup;

/// <summary>
/// The fixup-01 D3 contract, in one place: for every production entry point that starts Setup,
/// the Setup it starts must compute exactly the four per-user facts the INSTALLED App owns —
/// the profile folder (which is also the uninstall <c>--delete</c> target), the pass rule's log
/// root, <c>setup-last.json</c>, and the Setup session mutex name.
/// <para>
/// Every fact is compared against a fresh resolution of the installed App
/// (<c>%ProgramFiles%\EyeCtrl\EyeCtrl.App.exe</c>, Release), never against a constant: mutant MF
/// (the installed check always false) turns the App's own answer into <c>EyeCtrl.Dev</c> and
/// these assertions go RED outside the resolver tests.
/// </para>
/// </summary>
internal static class InstalledProductProfile
{
    public const string ProgramFiles = @"C:\Program Files";
    public const string Roaming = @"C:\Users\owner\AppData\Roaming";
    public const string LocalAppData = @"C:\Users\owner\AppData\Local";

    /// <summary>The installed App's profile, under the APP rule (the reference answer).</summary>
    public static ProfileLocation InstalledApp => ProfileLocation.Resolve(
        Path.Combine(ProgramFiles, "EyeCtrl", "EyeCtrl.App.exe"),
        isDebugBuild: false,
        args: [],
        ProgramFiles,
        Roaming,
        LocalAppData);

    /// <summary>A Release Setup's resolution for one entry point's real exe path and argv.</summary>
    public static ProfileLocation ResolveSetup(string setupExePath, IReadOnlyList<string> args) =>
        ProfileLocation.Resolve(
            setupExePath, isDebugBuild: false, args, ProgramFiles, Roaming, LocalAppData,
            isSetupProcess: true);

    /// <summary>Asserts that <paramref name="setup"/> lays every per-user path and name on the
    /// installed App's profile.</summary>
    public static void AssertResolvesTheInstalledProduct(string entryPoint, ProfileLocation setup)
    {
        var installed = InstalledApp;
        var paths = SetupPaths.FromProfile(setup);
        var detail = $"Setup resolved {setup.ModeToken} dir={setup.Directory} reason={setup.Reason}";

        Check.WithCustomMessage(
                $"{entryPoint}: a Release Setup must use the installed App's profile folder (the " +
                $"uninstall --delete target). {detail}")
            .That(paths.AppDataRoot).IsEqualTo(installed.Directory);
        Check.WithCustomMessage(
                $"{entryPoint}: the pass rule must read the installed App's log root. {detail}")
            .That(paths.LogsRoot).IsEqualTo(Path.Combine(installed.Directory, "logs"));
        Check.WithCustomMessage(
                $"{entryPoint}: setup-last.json must land in the installed App's profile. {detail}")
            .That(paths.RecordPath).IsEqualTo(Path.Combine(installed.Directory, "setup-last.json"));
        Check.WithCustomMessage(
                $"{entryPoint}: the Setup session mutex must be the installed App's session name. {detail}")
            .That(setup.SetupSessionMutexName).IsEqualTo(installed.SetupSessionMutexName);
    }

    /// <summary>Resolves and asserts in one call.</summary>
    public static void AssertEntryPoint(string entryPoint, string setupExePath, IReadOnlyList<string> args) =>
        AssertResolvesTheInstalledProduct(entryPoint, ResolveSetup(setupExePath, args));

    /// <summary>
    /// Splits one of these entry points' command lines into tokens the way Windows would: a
    /// double-quoted run is one token, whitespace separates the rest. Enough for Setup's own
    /// command lines (the package directory is the only quoted part).
    /// </summary>
    public static string[] SplitArguments(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens.ToArray();
    }

    /// <summary>
    /// The program path and arguments out of the HKCU <c>RunOnce\EyeCtrlRepair</c> value Setup
    /// writes (<c>"&lt;run copy&gt;" repair</c>) — the logon repair entry point's real argv.
    /// </summary>
    public static (string ExePath, string[] Arguments) ParseRunOnceValue(string value)
    {
        if (!value.StartsWith('"'))
            throw new InvalidOperationException($"Not a quoted RunOnce command: {value}");

        var end = value.IndexOf('"', 1);
        if (end < 0)
            throw new InvalidOperationException($"Unterminated RunOnce command: {value}");

        var exe = value.Substring(1, end - 1);
        var rest = value.Substring(end + 1).Trim();
        return (exe, rest.Length == 0 ? [] : SplitArguments(rest));
    }

    /// <summary>Finds the repository root above the test binary.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (EyeCtrl.slnx) above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Reads <c>Install-EyeCtrl.ps1</c> and returns the ACTUAL argument list its
    /// <c>$setupArguments</c> line builds for <paramref name="packageDirectory"/>. Comments are
    /// stripped first, so a doc mention of <c>--real-profile</c> can never satisfy the check.
    /// </summary>
    public static string[] ReadInstallScriptArguments(string packageDirectory)
    {
        var script = Path.Combine(FindRepoRoot(), "tools", "release", "Install-EyeCtrl.ps1");
        Check.WithCustomMessage($"script not found at {script}").That(File.Exists(script)).IsTrue();

        var code = StripPowerShellComments(File.ReadAllText(script));
        var line = code
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.TrimStart().StartsWith("$setupArguments", StringComparison.Ordinal));
        Check.WithCustomMessage(
                "Install-EyeCtrl.ps1 must build its Setup arguments in a $setupArguments line")
            .That(line).IsNotNull();

        // The line is '$setupArguments = '{0} "{1}" --real-profile' -f $SetupVerb, $PackageDir'.
        // The format string is the single-quoted literal on the right-hand side.
        var match = System.Text.RegularExpressions.Regex.Match(line!, @"'(?<format>[^']*)'");
        Check.WithCustomMessage($"$setupArguments has no format string: {line}").That(match.Success).IsTrue();

        var rendered = string.Format(match.Groups["format"].Value, "update", packageDirectory);
        return SplitArguments(rendered);
    }

    /// <summary>Removes PowerShell block comments (&lt;# #&gt;) and full-line # comments. No
    /// string in these scripts contains a #, so this never eats an argument.</summary>
    public static string StripPowerShellComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"<#.*?#>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(
            withoutBlocks, @"(?m)^\s*#.*$", string.Empty);
    }
}
