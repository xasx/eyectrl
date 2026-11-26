namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// SP-4d L5 (SP-7 review 01 F1): the two middle hops of the Settings → probe entry, pinned as
/// source. The existing pins cover the two ends only — <c>EventCoordinatorTests</c> pins
/// <c>SetProbeEntry</c> and <c>DiagnosticsPageSourceTests</c> pins the page source's behaviour —
/// and the review's mutant M1 (the three callbacks replaced with <c>null</c> in
/// <c>SettingsWindow</c>) survived the whole App suite: the Diagnostics button still renders, still
/// shows the live label, and does nothing when the owner dwells it.
///
/// <para>
/// The hop into <c>SettingsWindow</c> needs a running <c>EventCoordinator</c> plus model services,
/// and the hop into <c>DiagnosticsPageSource</c> a constructed <c>SettingsWindow</c> — neither is
/// reachable headlessly — so these are source pins in the style of
/// <c>EventCoordinatorTests.Initialize_WiresTheProbeCoordinator_AndItsEntryThreading</c>:
/// comment-stripped, whitespace-collapsed <c>Contains</c> over the exact argument lists. The
/// named-argument shape is load-bearing: it is what makes the collapsed string distinguish a real
/// pass-through from a <c>null</c> or a swapped argument.
/// </para>
/// </summary>
public class SettingsProbeWiringTests
{
    [Fact]
    public void OpenSettingsWindow_PassesTheProbeEntry_IntoTheSettingsWindow()
    {
        var source = Collapsed("src", "EyeCtrl.App", "Services", "Coordinators", "SettingsUiCoordinator.cs");

        Check.WithCustomMessage(
                "SettingsUiCoordinator.OpenSettingsWindow no longer passes the probe entry into "
                + "SettingsWindow: the Diagnostics button would render but do nothing (SP-4d L5).")
            .That(source.Contains(
                "context.UserDictionary,probeBlockReason:_probeBlockReason,"
                + "requestProbe:_probeRequest,canResumeSession2:_probeCanResume);"))
            .IsTrue();
    }

    [Fact]
    public void SettingsWindow_PassesTheProbeEntry_IntoTheDiagnosticsPageSource()
    {
        var source = Collapsed("src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs");

        Check.WithCustomMessage(
                "SettingsWindow no longer passes the probe callbacks into DiagnosticsPageSource: "
                + "the Diagnostics button would render but do nothing (SP-4d L5, review 01 F1/M1).")
            .That(source.Contains(
                "_diagnosticsSource=newDiagnosticsPageSource(_ctx,"
                + "onOpenRecordingFolder:OpenGazeRecordingFolder,probeBlockReason:probeBlockReason,"
                + "requestProbe:requestProbe,canResumeSession2:canResumeSession2);"))
            .IsTrue();
    }

    /// <summary>The file's code with comments blanked and every whitespace run collapsed.</summary>
    private static string Collapsed(params string[] parts) =>
        Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(
                Path.Combine(SourceScanner.RepoRoot().FullName, Path.Combine(parts)))),
            @"\s+", string.Empty);
}
