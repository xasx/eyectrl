namespace EyeCtrl.Core.Tests.Services;

using System.Text;

/// <summary>
/// The ratchet (ticket scope #4): <c>SpecialFolder.ApplicationData</c>,
/// <c>SpecialFolder.LocalApplicationData</c>, the kernel-object prefixes <c>Local\EyeCtrl</c> /
/// <c>Global\EyeCtrl</c>, and the pipe-name literals (<c>eyectrl_watch</c>, <c>eyectrl_dwell</c>,
/// <c>eyectrl_request</c>) may appear ONLY in the resolver (<c>ProfileLocation.cs</c> — plus the
/// net8 wire contract <c>DwellPipe.cs</c>, which the SDK shares and which must keep the literal)
/// and NOWHERE else under <c>src/</c>.
/// <para>
/// Every one of those tokens is a way to reach around <see cref="ProfileLocation"/> and touch
/// the owner's real profile or collide with the installed build's machine-global names. The
/// scan strips comments first, so a doc mention never satisfies it — only code does.
/// </para>
/// <para>
/// The mutant this must catch: one raw <c>SpecialFolder.ApplicationData</c> added back at
/// <c>KeyboardCoordinator.cs</c> — this test goes RED.
/// </para>
/// </summary>
public class ProfileLocationRatchetTests
{
    private static readonly string[] BannedTokens =
    [
        "SpecialFolder.ApplicationData",
        "SpecialFolder.LocalApplicationData",
        @"Local\EyeCtrl",
        @"Global\EyeCtrl",
        "eyectrl_watch",
        "eyectrl_dwell",
        "eyectrl_request",
    ];

    /// <summary>The only files under src/ that may carry a banned token in code.</summary>
    private static readonly string[] AllowedFiles =
    [
        "ProfileLocation.cs",          // the resolver itself
        @"EyeCtrl.Contracts\DwellPipe.cs", // the net8 wire contract shared with the SDK
        @"EyeCtrl.Core\Models\SettingRequest.cs", // the request channel's base wire name (the resolver adds the suffix)
    ];

    [Fact]
    public void OSFolderAndGlobalNameLiterals_AppearOnlyInTheResolver()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        Check.WithCustomMessage($"src folder not found under {root}").That(Directory.Exists(src)).IsTrue();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (AllowedFiles.Any(allowed => relative.EndsWith(allowed, StringComparison.OrdinalIgnoreCase)))
                continue;

            var code = StripComments(File.ReadAllText(file));
            foreach (var token in BannedTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                    violations.Add($"{relative} contains '{token}' in code — route it through ProfileLocation");
            }
        }

        Check.WithCustomMessage(
                "These files bypass the profile resolver (see ProfileLocation):\n" + string.Join("\n", violations))
            .That(violations).IsEmpty();
    }

    /// <summary>
    /// Removes line (<c>//</c>, <c>///</c>) and block (<c>/* */</c>) comments so a doc-comment
    /// mention of a token does not trip the scan — but leaves string literals (the actual
    /// hazards) untouched.
    /// </summary>
    private static string StripComments(string source)
    {
        var span = source.AsSpan();
        var result = new StringBuilder(source.Length);
        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];
            if (c == '"' || c == '\'')
            {
                // Copy string/char literals verbatim (handles "" and '' escapes).
                var quote = c;
                result.Append(c);
                i++;
                while (i < span.Length)
                {
                    result.Append(span[i]);
                    if (span[i] == '\\' && i + 1 < span.Length)
                    {
                        result.Append(span[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (span[i] == quote)
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < span.Length && span[i + 1] == '/')
            {
                while (i < span.Length && span[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < span.Length && span[i + 1] == '*')
            {
                var end = span.Slice(i + 2).IndexOf("*/");
                i = end < 0 ? span.Length : i + 2 + end + 2;
                continue;
            }

            result.Append(c);
            i++;
        }
        return result.ToString();
    }

    private static string FindRepoRoot()
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
}
