namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

public class ModelRegistryServiceVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "eyectrl_verify_test_" + Guid.NewGuid());
    private readonly ModelRegistryService _registry;

    public ModelRegistryServiceVerificationTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new ModelRegistryService(
            _dir, new ModelManifest(), new EyeCtrlSettings());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetVerification_DefaultsToNotYetVerified()
    {
        Check.That(_registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }

    [Fact]
    public void ReportVerification_FirstReport_Raises()
    {
        // NotYetVerified -> Failed is the startup transition the Models page must rebuild on:
        // the first report is a CHANGE, not a silent insert (plan review C2).
        var changes = 0;
        _registry.VerificationChanged += () => changes++;

        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);

        Check.That(changes).IsEqualTo(1);
    }

    [Fact]
    public void ReportVerification_RoundTrips_AndRaisesOnlyOnChange()
    {
        var changes = 0;
        _registry.VerificationChanged += () => changes++;

        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed); // no change
        _registry.ReportVerification("135M", ModelVerificationOutcome.Verified);

        Check.That(changes).IsEqualTo(2);
        Check.That(_registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Verified);
    }

    [Fact]
    public void ReportVerification_IsIndependentPerModel()
    {
        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        Check.That(_registry.GetVerification("360M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }

    [Fact]
    public void ReportVerification_AThrowingSubscriber_IsContained()
    {
        // Issue #166 M6: the raise used to escape into the caller. From the factory's posted
        // background-swap lambda that meant a throwing subscriber died in the supervision
        // wrapper AND StackSwapped never ran — the keyboard stayed bound to the just-disposed
        // stack. The verdict is committed before the raise; a subscriber's failure is its own.
        var raised = false;
        _registry.VerificationChanged += () => raised = true;
        _registry.VerificationChanged += () => throw new InvalidOperationException("subscriber bug");

        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);

        Check.That(raised).IsTrue(); // the earlier subscriber still ran
        // The ledger state is committed despite the throw, and the no-op contract survives:
        // a same-value re-report must still not raise.
        Check.That(_registry.GetVerification("135M")).IsEqualTo(ModelVerificationOutcome.Failed);
        var raisedAgain = false;
        _registry.VerificationChanged += () => raisedAgain = true;
        _registry.ReportVerification("135M", ModelVerificationOutcome.Failed);
        Check.That(raisedAgain).IsFalse();
    }
}
