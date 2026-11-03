namespace EyeCtrl.Core.Tests.Input;

using EyeCtrl.Core.Input;

/// <summary>
/// C10's R1 rule. The point of the table is that "elevated means blocked" is NOT the rule: a medium
/// EyeCtrl is blocked by High, a High UIAccess build (C9's measured TD Control shape) reaches
/// elevated windows and only stops at system-IL UI, and a medium+ UIAccess build on a standard
/// account is blocked again.
/// </summary>
public class InputIntegrityRulesTests
{
    private const int Medium = InputIntegrityRules.MediumIntegrity;
    private const int MediumPlus = 0x2100;
    private const int High = InputIntegrityRules.HighIntegrity;
    private const int System = InputIntegrityRules.SystemIntegrity;

    [Theory]
    [InlineData(Medium, false, High, true)]
    [InlineData(Medium, false, Medium, false)]
    [InlineData(Medium, false, System, true)]
    [InlineData(High, true, High, false)]
    [InlineData(High, true, System, true)]
    [InlineData(High, false, High, false)]
    [InlineData(MediumPlus, true, High, true)]
    [InlineData(MediumPlus, true, MediumPlus, false)]
    public void BlocksInjectedInput_ComparesTokens_NotTheElevationFlag(
        int ownLevel, bool uiAccess, int targetLevel, bool expected)
    {
        var self = new SelfTokenFacts(ownLevel, Elevated: false, HasUIAccess: uiAccess);
        var target = new ProcessTokenFacts("target.exe", targetLevel, Elevated: false, HasUIAccess: false, false);

        Check.That(InputIntegrityRules.BlocksInjectedInput(target, self)).IsEqualTo(expected);
    }

    [Fact]
    public void AnAccessDeniedTarget_CountsAsHigherThanOurs()
    {
        // Windows refuses the query before it would refuse the injection; a medium process cannot
        // open a protected/system token, and that refusal is the evidence.
        var self = new SelfTokenFacts(High, Elevated: false, HasUIAccess: true);
        var denied = new ProcessTokenFacts(null, null, null, null, AccessDenied: true);

        Check.That(InputIntegrityRules.BlocksInjectedInput(denied, self)).IsTrue();
    }

    [Fact]
    public void AnUnknownTarget_IsNoEvidence()
    {
        var self = new SelfTokenFacts(Medium, Elevated: false, HasUIAccess: false);

        Check.That(InputIntegrityRules.BlocksInjectedInput(ProcessTokenFacts.Unknown, self)).IsFalse();
    }

    [Fact]
    public void ReasonFor_NamesTheForegroundBeforeTheTarget_AndFallsBackToUnknown()
    {
        var self = new SelfTokenFacts(Medium, Elevated: false, HasUIAccess: false);
        var high = new ProcessTokenFacts("Taskmgr.exe", High, true, false, false);
        var medium = new ProcessTokenFacts("app.exe", Medium, false, false, false);

        Check.That(InputIntegrityRules.ReasonFor(high, high, self))
            .IsEqualTo(BlockedInputReason.HigherIntegrityForeground);
        Check.That(InputIntegrityRules.ReasonFor(medium, high, self))
            .IsEqualTo(BlockedInputReason.HigherIntegrityTarget);
        Check.That(InputIntegrityRules.ReasonFor(medium, medium, self))
            .IsEqualTo(BlockedInputReason.Unknown);
    }

    [Fact]
    public void IntegrityName_UsesTheWindowsVocabulary()
    {
        Check.That(InputIntegrityRules.IntegrityName(null)).IsEqualTo("Unknown");
        Check.That(InputIntegrityRules.IntegrityName(InputIntegrityRules.LowIntegrity)).IsEqualTo("Low");
        Check.That(InputIntegrityRules.IntegrityName(Medium)).IsEqualTo("Medium");
        Check.That(InputIntegrityRules.IntegrityName(High)).IsEqualTo("High");
        Check.That(InputIntegrityRules.IntegrityName(System)).IsEqualTo("System");
    }
}
