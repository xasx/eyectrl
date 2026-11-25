namespace EyeCtrl.App.Tests.Backstage;

using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;

/// <summary>
/// Ticket 02's read-only status strip. The builder is a pure mapping from three plain facts
/// (versions, installed, UIAccess, Supervisor presence) to three localized labels; every state the
/// ticket names must be reachable and distinct, and the text must come from the ACTIVE table at
/// call time so an approved wording change is a strings-file edit, not a code change.
/// </summary>
[Collection("StringTable Tests (App)")]
public class BackstageStatusSnapshotTests
{
    private static string English(string key, params object?[] args) =>
        StringTable.EmbeddedEnglish.T(key, args);

    [Fact]
    public void InstalledWithUIAccess_NamesTheInstalledVersion_AndBothPrivilegeStates()
    {
        StringTable.ResetToEmbeddedEnglish();

        var snapshot = BackstageStatusSnapshot.Build(
            assemblyVersion: "1.4.2.0", installedVersion: "1.5.0.0",
            installed: true, hasUIAccess: true, supervisor: SupervisorPresence.Connected);

        Check.That(snapshot.VersionText)
            .IsEqualTo(English("overlays.backstage.status.version", "1.5.0.0"));
        Check.That(snapshot.InstallText)
            .IsEqualTo(English("overlays.backstage.status.installed.uiAccess"));
        Check.That(snapshot.SupervisorText)
            .IsEqualTo(English("overlays.backstage.status.supervisor.connected"));

        var standard = BackstageStatusSnapshot.Build(
            "1.4.2.0", "1.5.0.0", installed: true, hasUIAccess: false, SupervisorPresence.Connected);
        Check.That(standard.InstallText)
            .IsEqualTo(English("overlays.backstage.status.installed.standard"));
        Check.That(standard.InstallText)
            .IsNotEqualTo(snapshot.InstallText);
    }

    [Fact]
    public void InstalledButVersionUnknown_FallsBackToTheAssemblyVersion()
    {
        StringTable.ResetToEmbeddedEnglish();

        var snapshot = BackstageStatusSnapshot.Build(
            "1.4.2.0", installedVersion: null, installed: true, hasUIAccess: false,
            SupervisorPresence.Off);

        Check.That(snapshot.VersionText)
            .IsEqualTo(English("overlays.backstage.status.version", "1.4.2.0"));
        Check.That(snapshot.SupervisorText)
            .IsEqualTo(English("overlays.backstage.status.supervisor.off"));
    }

    [Fact]
    public void DevBuild_UsesTheAssemblyVersion_IgnoresTheInstalledOne_AndSaysDevBuild()
    {
        StringTable.ResetToEmbeddedEnglish();

        var snapshot = BackstageStatusSnapshot.Build(
            "1.4.2.0", installedVersion: "9.9.9.9", installed: false, hasUIAccess: false,
            SupervisorPresence.NotAnswering);

        Check.That(snapshot.VersionText)
            .IsEqualTo(English("overlays.backstage.status.version", "1.4.2.0"));
        Check.That(snapshot.InstallText)
            .IsEqualTo(English("overlays.backstage.status.devBuild"));
        Check.That(snapshot.SupervisorText)
            .IsEqualTo(English("overlays.backstage.status.supervisor.notAnswering"));
    }

    [Fact]
    public void AllFourSupervisorStates_AreDistinctLabels()
    {
        StringTable.ResetToEmbeddedEnglish();

        var labels = new[] { SupervisorPresence.Connected, SupervisorPresence.Off,
            SupervisorPresence.NotAnswering, SupervisorPresence.Unavailable }
            .Select(state => BackstageStatusSnapshot.Build(
                "1.0", null, installed: true, hasUIAccess: true, state).SupervisorText)
            .ToList();

        Check.That(labels.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(4);
        Check.That(labels[3]).IsEqualTo(English("overlays.backstage.status.supervisor.unavailable"));
    }

    [Fact]
    public void PresenceOf_MapsEveryBranch_IncludingTheOffAndUnavailablePrecedence()
    {
        Check.That(BackstageStatusSnapshot.PresenceOf(supervision: null))
            .IsEqualTo(SupervisorPresence.Unavailable);

        // An absent Supervisor exe outranks a stale enabled flag: "off" would claim a choice.
        Check.That(BackstageStatusSnapshot.PresenceOf(available: false, enabled: true, connected: true))
            .IsEqualTo(SupervisorPresence.Unavailable);

        // A deliberate off outranks a stale process that is still answering.
        Check.That(BackstageStatusSnapshot.PresenceOf(available: true, enabled: false, connected: true))
            .IsEqualTo(SupervisorPresence.Off);

        Check.That(BackstageStatusSnapshot.PresenceOf(available: true, enabled: true, connected: true))
            .IsEqualTo(SupervisorPresence.Connected);
        Check.That(BackstageStatusSnapshot.PresenceOf(available: true, enabled: true, connected: false))
            .IsEqualTo(SupervisorPresence.NotAnswering);
    }

    [Fact]
    public void TheShippedGermanTable_ResolvesTheStrip_NotEnglish()
    {
        var previous = StringTable.Current;
        var german = ShippedGerman.Load();
        StringTable.Activate(german);
        try
        {
            var snapshot = BackstageStatusSnapshot.Build(
                "1.4.2.0", "1.5.0.0", installed: true, hasUIAccess: true,
                SupervisorPresence.Connected);

            Check.That(snapshot.InstallText)
                .IsEqualTo(german.T("overlays.backstage.status.installed.uiAccess"));
            Check.That(snapshot.SupervisorText)
                .IsEqualTo(german.T("overlays.backstage.status.supervisor.connected"));

            // The two labels must genuinely be German — a whole-surface English fallback would
            // still satisfy the equality above if the German file were the English one.
            Check.That(snapshot.InstallText)
                .IsNotEqualTo(English("overlays.backstage.status.installed.uiAccess"));
            Check.That(snapshot.SupervisorText)
                .IsNotEqualTo(English("overlays.backstage.status.supervisor.connected"));
        }
        finally
        {
            StringTable.Activate(previous);
        }
    }
}
