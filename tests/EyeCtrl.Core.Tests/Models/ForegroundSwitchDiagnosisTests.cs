namespace EyeCtrl.Core.Tests.Models;

using EyeCtrl.Core.Models;

/// <summary>
/// The switcher's only failure signal is a bool from SetForegroundWindow plus the count
/// SendInput returned. This classifier turns that pair into the three states worth logging,
/// so the next "switching does nothing" field report arrives with a cause attached instead
/// of needing another round of guessing (2026-08-18 and 2026-08-25 both cost a round).
/// </summary>
public class ForegroundSwitchDiagnosisTests
{
    private static ForegroundSwitchOutcome Classify(uint injected, bool ok)
        => ForegroundSwitchDiagnosis.Classify(
            new ForegroundSwitchAttempt(injected, ExpectedInputs: 2, SetForegroundSucceeded: ok));

    [Fact]
    public void GrantAccepted_AndSwitchSucceeded_IsSwitched()
        => Check.That(Classify(injected: 2, ok: true)).IsEqualTo(ForegroundSwitchOutcome.Switched);

    [Fact]
    public void SwitchSucceeded_WinsEvenIfTheGrantWasBlocked()
        // The grant is not always needed — an unlocked foreground accepts the bare call.
        // A successful switch is a success regardless of how the tap fared.
        => Check.That(Classify(injected: 0, ok: true)).IsEqualTo(ForegroundSwitchOutcome.Switched);

    [Fact]
    public void NoInputsInjected_AndSwitchFailed_IsGrantBlocked()
        // UIPI: an elevated foreground app makes SendInput a silent no-op for us, so the
        // grant was never earned. Distinct cause, distinct fix (run elevated / different path).
        => Check.That(Classify(injected: 0, ok: false)).IsEqualTo(ForegroundSwitchOutcome.GrantBlocked);

    [Fact]
    public void PartialInjection_AndSwitchFailed_IsGrantBlocked()
        // A truncated pair is the stuck-ALT shape: down delivered, up dropped.
        => Check.That(Classify(injected: 1, ok: false)).IsEqualTo(ForegroundSwitchOutcome.GrantBlocked);

    [Fact]
    public void FullGrant_ButSwitchFailed_IsSwitchRefused()
        // The tap landed and Windows still said no: the grant is not the problem, so the
        // minimize/restore fallback is the thing to look at.
        => Check.That(Classify(injected: 2, ok: false)).IsEqualTo(ForegroundSwitchOutcome.SwitchRefused);

    [Fact]
    public void MoreInputsThanExpected_IsNotTreatedAsBlocked()
        => Check.That(Classify(injected: 3, ok: false)).IsEqualTo(ForegroundSwitchOutcome.SwitchRefused);
}
