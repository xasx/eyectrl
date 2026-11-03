namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// C9 S3: the two Win32 failures that only a uiAccess build adds to the App launch. They are
/// deliberately just classification + prose — the launch-failure path itself is unchanged, so a
/// 8235/740 launch books the same strike, retries with the same backoff and escalates to
/// <c>FallbackAt</c> through <c>SupervisionStateMachine.GoRecovering</c> as any other failure.
/// </summary>
public static class UiAccessLaunchFailure
{
    /// <summary>
    /// <c>ERROR_DS_REFERRAL</c> (0x202B, "A referral was returned from the server"). Measured in
    /// S1a as the hard failure of an unsigned or untrusted uiAccess exe in Program Files: the
    /// signature or its trust chain is broken, so the App cannot start at all.
    /// </summary>
    public const int SignatureOrTrustFailure = 8235;

    /// <summary>
    /// <c>ERROR_ELEVATION_REQUIRED</c>. Measured in S1a: a plain CreateProcess of a uiAccess exe
    /// fails with this even from a UIAccess parent. Seeing it here means a CreateProcess path
    /// slipped through somewhere — the launch must go through ShellExecute.
    /// </summary>
    public const int ElevationRequired = 740;

    public static bool IsUiAccessSpecific(int nativeErrorCode) =>
        nativeErrorCode is SignatureOrTrustFailure or ElevationRequired;

    /// <summary>The distinct log reason for a launch failure, or null for every other error.</summary>
    public static string? Describe(int nativeErrorCode) => nativeErrorCode switch
    {
        SignatureOrTrustFailure =>
            "Win32 8235 (ERROR_DS_REFERRAL): the App's signature or trust chain is broken, so a uiAccess exe cannot start",
        ElevationRequired =>
            "Win32 740 (ERROR_ELEVATION_REQUIRED): a CreateProcess of the uiAccess App slipped through; the App must be started through ShellExecute",
        _ => null,
    };
}
