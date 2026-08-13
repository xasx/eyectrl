namespace EyeCtrl.Core.Services;

using System.IO;

/// <summary>
/// Single source of truth for the per-user data directory holding <c>settings.json</c>,
/// <c>calibration.json</c>, <c>actions.json</c>, <c>recent_emojis.json</c>, the user dictionary,
/// downloaded models and the Serilog logs.
/// <para>
/// A scoped instance nests under <c>instances\&lt;id&gt;</c> rather than sitting in a temp directory,
/// so a scoped profile is provably <em>inside</em> the live profile. That is deliberate: the
/// end-to-end harness deletes its own profile on teardown, and "below the live one" makes a delete
/// of the wrong level impossible to write by accident.
/// </para>
/// <para>
/// The id arrives as a parameter, not from the environment, because Core cannot reference
/// <c>EyeCtrl.Contracts</c> where the naming rule lives — and because a pure function is testable
/// without mutating process state, which would race sibling test classes in the same xUnit host.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Explicit path override. Wins over both the default and the instance id, for a caller that
    /// needs the profile at a specific location.
    /// </summary>
    public const string OverrideEnvVar = "EYECTRL_APPDATA_DIR";

    /// <summary>Directory name under the roaming profile.</summary>
    private const string RootFolder = "EyeCtrl";

    /// <summary>Subdirectory holding scoped instances.</summary>
    private const string InstancesFolder = "instances";

    /// <summary>Maximum instance id length. Mirrors <c>EyeCtrl.Contracts.InstanceScope</c>.</summary>
    private const int MaxIdLength = 64;

    /// <summary>
    /// Resolves the profile directory. Precedence: explicit override, then instance id, then the
    /// live profile.
    /// </summary>
    /// <param name="overrideDir">Value of <see cref="OverrideEnvVar"/>, or <see langword="null"/>.</param>
    /// <param name="roamingAppData">Resolved <c>ApplicationData</c> special folder.</param>
    /// <param name="instanceId">
    /// Instance id, or <see langword="null"/>/blank for the default instance. Blank is treated as
    /// absent rather than invalid, matching <c>InstanceScope.Scope</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="instanceId"/> is non-blank but malformed.</exception>
    public static string Resolve(string? overrideDir, string roamingAppData, string? instanceId)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir.Trim();

        var root = Path.Combine(roamingAppData, RootFolder);
        if (string.IsNullOrWhiteSpace(instanceId)) return root;

        // Validated the same way InstanceScope does. Duplicated rather than referenced because Core
        // cannot see Contracts; the drift guard asserts the two rules agree.
        var id = instanceId.Trim();
        if (id.Length > MaxIdLength || !IsLegalId(id))
            throw new ArgumentException(
                $"'{instanceId}' is not a well-formed instance id.", nameof(instanceId));

        return Path.Combine(root, InstancesFolder, id);
    }

    private static bool IsLegalId(string id)
    {
        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '-' || c == '_';
            if (!ok) return false;
        }

        return id.Length > 0;
    }
}
