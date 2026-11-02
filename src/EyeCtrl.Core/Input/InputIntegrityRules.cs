namespace EyeCtrl.Core.Input;

/// <summary>Why R2 believes Windows is discarding EyeCtrl's injected input.</summary>
public enum BlockedInputReason
{
    /// <summary>No integrity evidence names a higher window: R2 saw the input vanish and nothing
    /// else is known. The banner must say only that clicks are blocked.</summary>
    Unknown = 0,

    /// <summary>R1 found the foreground window's process above EyeCtrl's reach.</summary>
    HigherIntegrityForeground,

    /// <summary>R1 found the process under the last target point above EyeCtrl's reach.</summary>
    HigherIntegrityTarget,
}

/// <summary>
/// R1's pure decision: would Windows' UIPI rules discard an injection aimed at this process? The
/// rule is a comparison of token facts, never a hard-coded "elevated means blocked", because a
/// UIAccess build at High integrity reaches elevated (High) windows and only system-IL UI and the
/// secure desktop stop it (C9's plan, §5).
/// </summary>
public static class InputIntegrityRules
{
    /// <summary>Low mandatory integrity level.</summary>
    public const int LowIntegrity = 0x1000;

    /// <summary>Medium mandatory integrity level (an ordinary user process).</summary>
    public const int MediumIntegrity = 0x2000;

    /// <summary>High mandatory integrity level (an elevated process, or a UIAccess one).</summary>
    public const int HighIntegrity = 0x3000;

    /// <summary>System mandatory integrity level.</summary>
    public const int SystemIntegrity = 0x4000;

    /// <summary>
    /// Whether <paramref name="target"/> sits above what EyeCtrl's own token can inject into.
    /// </summary>
    /// <param name="target">The foreground or target process facts; unknown facts never block.</param>
    /// <param name="self">EyeCtrl's own token facts.</param>
    /// <returns>True when UIPI can be expected to drop injected input aimed at the process.</returns>
    public static bool BlocksInjectedInput(ProcessTokenFacts target, SelfTokenFacts self)
    {
        // A token we cannot even open is, for UIPI purposes, a process above ours: Windows refuses
        // the query at the boundary it would also refuse the injection.
        if (target.AccessDenied)
            return true;

        if (target.IntegrityLevel is not { } targetLevel)
            return false; // no evidence: R2 stays the trigger, R1 only refines the text

        var selfLevel = self.IntegrityLevel ?? MediumIntegrity;

        // UIAccess at High is the measured TD Control shape: it reaches every High/elevated window,
        // and only system-IL UI (the secure desktop included) is out of reach. A UIAccess build on a
        // standard account starts at medium+ and does NOT get this exemption. At the RIDs Windows
        // actually uses this is equivalent to target > self (equal IL is injectable); the clause is
        // kept explicit because C9's plan states it as the rule, and the tests pin its value.
        if (self.HasUIAccess && selfLevel >= HighIntegrity)
            return targetLevel >= SystemIntegrity;

        // Deliberately no "elevated means blocked": a High token (UIAccess or elevated) may inject
        // into another High window; only a strictly higher IL is out of reach.
        return targetLevel > selfLevel;
    }

    /// <summary>
    /// Names the strongest reason R1 can give for a block that R2 has already observed.
    /// </summary>
    /// <param name="foreground">The foreground window's facts.</param>
    /// <param name="target">The last target point's facts.</param>
    /// <param name="self">EyeCtrl's own token facts.</param>
    /// <returns>The foreground when it blocks, else the target, else <see cref="BlockedInputReason.Unknown"/>.</returns>
    public static BlockedInputReason ReasonFor(
        ProcessTokenFacts foreground, ProcessTokenFacts target, SelfTokenFacts self)
    {
        if (BlocksInjectedInput(foreground, self))
            return BlockedInputReason.HigherIntegrityForeground;

        if (BlocksInjectedInput(target, self))
            return BlockedInputReason.HigherIntegrityTarget;

        return BlockedInputReason.Unknown;
    }

    /// <summary>Renders an integrity level RID as its Windows name, for the logs.</summary>
    /// <param name="level">The RID, or null when unknown.</param>
    /// <returns><c>Low</c>, <c>Medium</c>, <c>High</c>, <c>System</c>, <c>Protected</c>, or
    /// <c>Unknown</c>; an unrecognised RID renders as its hex value.</returns>
    public static string IntegrityName(int? level) => level switch
    {
        null => "Unknown",
        LowIntegrity => "Low",
        MediumIntegrity => "Medium",
        HighIntegrity => "High",
        SystemIntegrity => "System",
        0x5000 => "Protected",
        _ => "0x" + level.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
    };
}
