namespace EyeCtrl.Contracts;

/// <summary>
/// The rule that scopes a process-external name to one app instance.
/// <para>
/// Lives here rather than in <c>EyeCtrl.Core</c> because <c>EyeCtrl.Sdk.Abstractions</c> targets
/// <c>net8.0</c> and references only this assembly — it cannot see Core (<c>net10.0</c>). Server and
/// client deriving the same name from one place is the same rationale <see cref="DwellPipe"/>
/// already documents.
/// </para>
/// <para>
/// Pure string handling, no I/O and no environment reads, so it is testable without touching
/// process state — which matters because <c>Environment.SetEnvironmentVariable</c> races every
/// sibling test class in the same xUnit host.
/// </para>
/// </summary>
public static class InstanceScope
{
    /// <summary>Environment variable naming the instance. Absence means the default instance.</summary>
    public const string EnvVar = "EYECTRL_INSTANCE_ID";

    /// <summary>Maximum id length. Long enough for a GUID in "N" format plus a prefix.</summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// Validates and trims an instance id.
    /// <para>
    /// The allowed set is letters, digits, <c>-</c> and <c>_</c>. It is deliberately narrow: an id
    /// ends up inside a <c>Local\…</c> mutex name, a pipe name and a directory segment, so a
    /// backslash, a forward slash or a colon would either produce an unbindable name or redirect a
    /// path. Rejecting them here is the only place that has to know.
    /// </para>
    /// </summary>
    /// <param name="raw">Raw value, typically from the environment. May be <see langword="null"/>.</param>
    /// <param name="id">Trimmed id when valid; <see cref="string.Empty"/> otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="raw"/> is a well-formed id.</returns>
    public static bool TryParseId(string? raw, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxIdLength) return false;
        if (!IsLegal(trimmed)) return false;

        id = trimmed;
        return true;
    }

    /// <summary>
    /// Returns <paramref name="defaultName"/> scoped to <paramref name="instanceId"/>, or unchanged
    /// when there is no id.
    /// </summary>
    /// <param name="defaultName">The production name.</param>
    /// <param name="instanceId">Instance id, or <see langword="null"/>/blank for the default instance.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="instanceId"/> is non-blank but malformed. Throwing rather than silently
    /// sanitising: a caller that skipped <see cref="TryParseId"/> has a bug, and an unbindable or
    /// path-redirecting name is the worst possible way to find out.
    /// </exception>
    public static string Scope(string defaultName, string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return defaultName;

        if (!TryParseId(instanceId, out var id))
            throw new ArgumentException(
                $"'{instanceId}' is not a well-formed instance id.", nameof(instanceId));

        return defaultName + "_" + id;
    }

    /// <summary>
    /// Whether every character is in the allowed set. Shared by <see cref="TryParseId"/> and
    /// mirrored by <c>EyeCtrl.Core.Services.AppPaths</c>, which cannot reference this assembly.
    /// </summary>
    private static bool IsLegal(string value)
    {
        foreach (var c in value)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '-' || c == '_';
            if (!ok) return false;
        }

        return value.Length > 0;
    }
}
