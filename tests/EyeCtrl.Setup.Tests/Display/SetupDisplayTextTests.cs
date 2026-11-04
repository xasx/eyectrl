namespace EyeCtrl.Setup.Tests.Display;

using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;

/// <summary>
/// The pure stage/result mapping: every outcome resolves its own key (never a raw key or another
/// verb's text), Q12's degraded reads as a failure, and the 10 s / 60 s rule follows the reading.
/// </summary>
public class SetupDisplayTextTests
{
    private static readonly StringTable English = StringTable.EmbeddedEnglish;
    private const string AppData = @"C:\Users\test\AppData\Roaming\EyeCtrl";

    [Fact]
    public void ProgressTitle_AndDetail_AreTheVerbsOwn()
    {
        Check.That(SetupDisplayText.ProgressTitle(English, SetupVerb.Update))
            .IsEqualTo(English.T("update.progress.title"));
        Check.That(SetupDisplayText.ProgressTitle(English, SetupVerb.Rollback))
            .IsEqualTo(English.T("rollback.progress.title"));
        Check.That(SetupDisplayText.ProgressTitle(English, SetupVerb.Uninstall))
            .IsEqualTo(English.T("uninstall.working"));
        Check.That(SetupDisplayText.ProgressTitle(English, SetupVerb.Repair)).IsEqualTo(string.Empty);

        Check.That(SetupDisplayText.ProgressDetail(English, SetupVerb.Update))
            .IsEqualTo(English.T("update.progress.detail"));
        Check.That(SetupDisplayText.ProgressDetail(English, SetupVerb.Rollback))
            .IsEqualTo(English.T("update.progress.detail"));
        Check.That(SetupDisplayText.ProgressDetail(English, SetupVerb.Uninstall)).IsEqualTo(string.Empty);
    }

    [Fact]
    public void EveryStage_HasItsOwnText()
    {
        var texts = Enum.GetValues<SetupStage>()
            .Select(stage => SetupDisplayText.Stage(English, SetupVerb.Update, stage))
            .ToList();

        Check.That(texts).HasSize(4);
        Check.That(texts).ContainsOnlyElementsThatMatch(text => text.Length > 0);
        Check.That(texts).ContainsOnlyElementsThatMatch(text => !text.StartsWith("update.progress.", StringComparison.Ordinal));
        Check.That(texts.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(4);
    }

    [Fact]
    public void RollbackStage_UsesThePreviousVersionTexts_WhileVerifyAndStopStayShared()
    {
        Check.That(SetupDisplayText.Stage(English, SetupVerb.Rollback, SetupStage.Verify))
            .IsEqualTo(English.T("update.progress.verify"));
        Check.That(SetupDisplayText.Stage(English, SetupVerb.Rollback, SetupStage.Stop))
            .IsEqualTo(English.T("update.progress.stop"));

        Check.That(SetupDisplayText.Stage(English, SetupVerb.Rollback, SetupStage.Swap))
            .IsEqualTo(English.T("rollback.progress.swap"));
        Check.That(SetupDisplayText.Stage(English, SetupVerb.Rollback, SetupStage.Start))
            .IsEqualTo(English.T("rollback.progress.start"));

        // The same stages under an update keep the new-version wording.
        Check.That(SetupDisplayText.Stage(English, SetupVerb.Update, SetupStage.Swap))
            .IsEqualTo(English.T("update.progress.swap"));
        Check.That(SetupDisplayText.Stage(English, SetupVerb.Update, SetupStage.Start))
            .IsEqualTo(English.T("update.progress.start"));
    }

    [Fact]
    public void UpdateResult_MapsEveryOutcome()
    {
        AssertMaps(SetupVerb.Update, SetupOutcome.Ok, "update.result.ok", args: "1.2.3");
        // The failed new version is Version; the restored one is PreviousVersion.
        AssertMaps(SetupVerb.Update, SetupOutcome.RolledBack, "update.result.rolledBack", args: "1.0.0");
        AssertMaps(SetupVerb.Update, SetupOutcome.NotApplied, "update.result.notApplied");
        AssertMaps(SetupVerb.Update, SetupOutcome.VerifyFailed, "update.result.verifyFailed");
        AssertMaps(SetupVerb.Update, SetupOutcome.AllFailed, "update.result.allFailed");
        AssertMaps(SetupVerb.Update, SetupOutcome.Blocked, "uninstall.needsHelp");
        AssertMaps(SetupVerb.Update, SetupOutcome.Failed, "update.result.failed");
        AssertMaps(SetupVerb.Update, SetupOutcome.Failed, "update.result.notApplied", detail: "childStillRunning");
    }

    [Fact]
    public void RolledBack_NamesTheRestoredVersion_NotTheFailedOne()
    {
        var result = new SetupResult
        {
            Verb = SetupVerb.Update,
            Outcome = SetupOutcome.RolledBack,
            Version = "1.5.0",
            PreviousVersion = "1.4.0",
        };

        var mapped = SetupDisplayText.Result(English, result, AppData);

        Check.That(mapped.Text).IsEqualTo(English.T("update.result.rolledBack", "1.4.0"));
        Check.WithCustomMessage("The rolled-back text named the failed new version.").That(mapped.Text).Not.Contains("1.5.0");
    }

    [Fact]
    public void Degraded_NamesTheRunningVersion_NotTheDisplacedOne()
    {
        var result = new SetupResult
        {
            Verb = SetupVerb.Update,
            Outcome = SetupOutcome.Degraded,
            Version = "1.4.0",
            PreviousVersion = "1.5.0",
        };

        var mapped = SetupDisplayText.Result(English, result, AppData);

        Check.That(mapped.Text).IsEqualTo(English.T("update.result.degraded", "1.4.0"));
        Check.That(mapped.Text).Not.Contains("1.5.0");
    }

    [Fact]
    public void RollbackResult_MapsEveryOutcome()
    {
        AssertMaps(SetupVerb.Rollback, SetupOutcome.Ok, "rollback.result.ok", args: "1.2.3");
        AssertMaps(SetupVerb.Rollback, SetupOutcome.NotApplied, "rollback.result.notApplied");
        AssertMaps(SetupVerb.Rollback, SetupOutcome.VerifyFailed, "rollback.result.verifyFailed");
        AssertMaps(SetupVerb.Rollback, SetupOutcome.AllFailed, "rollback.result.allFailed");
        AssertMaps(SetupVerb.Rollback, SetupOutcome.Blocked, "uninstall.needsHelp");
        AssertMaps(SetupVerb.Rollback, SetupOutcome.Failed, "rollback.result.failed");
    }

    [Fact]
    public void UninstallResult_MapsEveryOutcome()
    {
        AssertMaps(SetupVerb.Uninstall, SetupOutcome.Uninstalled, "uninstall.done.kept", detail: "kept");
        AssertMaps(SetupVerb.Uninstall, SetupOutcome.Uninstalled, "uninstall.done.deleted", detail: "deleted");
        AssertMaps(SetupVerb.Uninstall, SetupOutcome.Cancelled, "uninstall.cancelled", detail: "cancelled");
        AssertMaps(SetupVerb.Uninstall, SetupOutcome.Blocked, "uninstall.needsHelp");
        AssertMaps(SetupVerb.Uninstall, SetupOutcome.Failed, "uninstall.failed", detail: "image:App.exe");
        AssertMaps(SetupVerb.Uninstall, SetupOutcome.Failed, "uninstall.done.deleteIncomplete",
            detail: "dataIncomplete:logs/a.log,recordings/b.wav", args: AppData);
    }

    [Fact]
    public void Degraded_IsAFailure_AndRolledBackToo()
    {
        Check.That(SetupDisplayText.IsFailure(SetupOutcome.Degraded)).IsTrue();
        Check.That(SetupDisplayText.IsFailure(SetupOutcome.RolledBack)).IsTrue();
        Check.That(SetupDisplayText.IsFailure(SetupOutcome.Cancelled)).IsFalse();
        Check.That(SetupDisplayText.IsFailure(SetupOutcome.Ok)).IsFalse();
        Check.That(SetupDisplayText.IsFailure(SetupOutcome.Uninstalled)).IsFalse();
    }

    [Fact]
    public void CloseDelays_AreTenSecondsForSuccess_AndSixtyForFailure()
    {
        Check.That(SetupDisplayText.CloseDelayFor(isFailure: false)).IsEqualTo(TimeSpan.FromSeconds(10));
        Check.That(SetupDisplayText.CloseDelayFor(isFailure: true)).IsEqualTo(TimeSpan.FromSeconds(60));
        Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Ok)).IsEqualTo(TimeSpan.FromSeconds(10));
        Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Uninstalled)).IsEqualTo(TimeSpan.FromSeconds(10));
        Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Cancelled)).IsEqualTo(TimeSpan.FromSeconds(10));
        Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Degraded)).IsEqualTo(TimeSpan.FromSeconds(60));
        Check.That(SetupDisplayText.CloseDelay(SetupOutcome.RolledBack)).IsEqualTo(TimeSpan.FromSeconds(60));
        Check.That(SetupDisplayText.CloseDelay(SetupOutcome.Failed)).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void DegradedText_NamesTheRunningVersion_AndIsNotTheSuccessText()
    {
        var result = new SetupResult
        {
            Verb = SetupVerb.Update,
            Outcome = SetupOutcome.Degraded,
            Version = "3.4.5",
            PreviousVersion = "2.0.0",
        };

        var mapped = SetupDisplayText.Result(English, result, AppData);

        Check.That(mapped.IsFailure).IsTrue();
        Check.That(mapped.Text).IsEqualTo(English.T("update.result.degraded", "3.4.5"));
        Check.That(mapped.Text).Contains("3.4.5");
        Check.That(mapped.Text).Not.Contains("2.0.0");
        Check.That(mapped.Text).Not.IsEqualTo(English.T("update.result.ok", "3.4.5"));
    }

    [Fact]
    public void EveryMappedText_IsResolved_NotAKey()
    {
        var outcomes = Enum.GetValues<SetupOutcome>();
        var verbs = new[] { SetupVerb.Update, SetupVerb.Rollback, SetupVerb.Uninstall };

        foreach (var verb in verbs)
        {
            foreach (var outcome in outcomes)
            {
                var result = new SetupResult { Verb = verb, Outcome = outcome, Version = "1.0.0" };
                var text = SetupDisplayText.Result(English, result, AppData).Text;
                Check.WithCustomMessage($"{verb}/{outcome} resolved empty text.").That(text.Length).IsStrictlyGreaterThan(0);
                Check.WithCustomMessage($"{verb}/{outcome} resolved a raw key: {text}.")
                    .That(text.Contains("uninstall.", StringComparison.Ordinal)
                          || text.Contains("update.", StringComparison.Ordinal)
                          || text.Contains("rollback.", StringComparison.Ordinal)).IsFalse();
            }
        }
    }

    private static void AssertMaps(
        SetupVerb verb, SetupOutcome outcome, string expectedKey, string? args = null, string? detail = null)
    {
        var result = new SetupResult
        {
            Verb = verb,
            Outcome = outcome,
            Version = "1.2.3",
            PreviousVersion = "1.0.0",
            Detail = detail,
        };

        var expected = args is null ? English.T(expectedKey) : English.T(expectedKey, args);
        var mapped = SetupDisplayText.Result(English, result, AppData);

        Check.WithCustomMessage($"{verb}/{outcome} ({detail}) mapped to '{mapped.Text}', expected '{expected}'.")
            .That(mapped.Text).IsEqualTo(expected);
        Check.That(mapped.IsFailure).IsEqualTo(SetupDisplayText.IsFailure(outcome));
    }
}
