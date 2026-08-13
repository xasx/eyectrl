namespace EyeCtrl.Sdk.Abstractions.Tests;

using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;

/// <summary>
/// DwellHead is process-wide static state, so these tests reset it between cases via the
/// test-only hook and must not run in parallel with each other. They are in one class, which xUnit
/// serialises by default — and the assembly disables cross-class parallelism too, because
/// <see cref="DwellHeadCoreTests"/> latches without resetting.
/// </summary>
public class DwellHeadTests : IDisposable
{
    public DwellHeadTests() => DwellHead.ResetForTests();
    public void Dispose() => DwellHead.ResetForTests();

    [Fact]
    public void DefaultsToTheProductionPipeName()
    {
        Check.That(DwellHead.PipeName).IsEqualTo(DwellPipe.Name);
        Check.That(DwellHead.IsLatched).IsFalse();
    }

    [Fact]
    public void AcceptsASetBeforeTheLatch()
    {
        DwellHead.PipeName = "eyectrl_dwell_run7";

        Check.That(DwellHead.PipeName).IsEqualTo("eyectrl_dwell_run7");
    }

    [Fact]
    public void LatchReturnsTheCurrentValue_AndMarksItLatched()
    {
        DwellHead.PipeName = "eyectrl_dwell_run7";

        Check.That(DwellHead.Latch()).IsEqualTo("eyectrl_dwell_run7");
        Check.That(DwellHead.IsLatched).IsTrue();
    }

    [Fact]
    public void SecondLatchReturnsTheSameValue_SoWpfAndWinUiCannotDisagree()
    {
        DwellHead.PipeName = "eyectrl_dwell_run7";
        var first = DwellHead.Latch();
        var second = DwellHead.Latch();

        Check.That(second).IsEqualTo(first);
    }

    [Fact]
    public void SetAfterTheLatch_Throws_RatherThanBeingSilentlyIgnored()
    {
        // The failure this replaces: "set before first use, ignored afterwards" made a late set
        // indistinguishable from success, and Dwell.Enabled="True" in XAML latches during
        // InitializeComponent — early enough that almost any startup hook is too late.
        DwellHead.Latch();

        InvalidOperationException? ex = null;
        try { DwellHead.PipeName = "eyectrl_dwell_toolate"; }
        catch (InvalidOperationException e) { ex = e; }

        Check.That(ex).IsNotNull();
        Check.That(ex!.Message).Contains("latched");
    }

    [Fact]
    public void SettingTheSameValueAfterTheLatch_IsAllowed()
    {
        // Idempotent sets are not a bug and throwing on them would punish a host that configures
        // defensively in two places.
        DwellHead.PipeName = "eyectrl_dwell_run7";
        DwellHead.Latch();

        DwellHead.PipeName = "eyectrl_dwell_run7";   // must not throw

        Check.That(DwellHead.PipeName).IsEqualTo("eyectrl_dwell_run7");
    }

    [Fact]
    public void RejectsABlankName()
    {
        ArgumentException? ex = null;
        try { DwellHead.PipeName = "  "; } catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
    }

    [Fact]
    public void DoesNotReadTheEnvironment()
    {
        // Deliberate: a host that inherited a valid-but-stale EYECTRL_INSTANCE_ID would silently
        // target another instance's pipe, and DwellClient swallows every connection failure — so
        // the symptom would be dwell simply not working, with nothing logged anywhere.
        var original = Environment.GetEnvironmentVariable(InstanceScope.EnvVar);
        Environment.SetEnvironmentVariable(InstanceScope.EnvVar, "run7");
        try
        {
            DwellHead.ResetForTests();

            Check.That(DwellHead.PipeName).IsEqualTo(DwellPipe.Name);
        }
        finally { Environment.SetEnvironmentVariable(InstanceScope.EnvVar, original); }
    }

    [Fact]
    public void LatchWithoutAnySet_YieldsTheProductionName()
    {
        // The default-instance path: no host configuration at all must still reach eyectrl_dwell.
        Check.That(DwellHead.Latch()).IsEqualTo(DwellPipe.Name);
    }
}
