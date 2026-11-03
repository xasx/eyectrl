namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.ComponentModel;

/// <summary>
/// C9 S3/fixup-01: the two Win32 failures that only a uiAccess build adds to the App launch, and
/// their classification. Neither can heal on a retry — a broken signature or trust chain (8235)
/// and a leaked CreateProcess of the uiAccess exe (740) fail identically every time — so the
/// supervision clock escalates to <c>FallenBack</c> on the FIRST one instead of booking the
/// ordinary retries. The FallenBack dialog's Retry and its one 60 s auto-retry stay the healing
/// path for a transient case.
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

    /// <summary>
    /// Classifies a failed launch for the supervision clock. Only the two uiAccess codes are
    /// unrecoverable; every other failure keeps the ordinary strike + backoff path.
    /// </summary>
    public static LaunchAttemptResult ClassifyLaunchFailure(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: var code } && IsUiAccessSpecific(code)
            ? LaunchAttemptResult.FailedUnrecoverable
            : LaunchAttemptResult.Failed;

    /// <summary>The distinct log reason for a failed launch, or null when it is an ordinary one.</summary>
    public static string? DescribeFailure(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: var code } ? Describe(code) : null;

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
