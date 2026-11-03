namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;
using Microsoft.Win32;

public class ElevationPolicyGateTests
{
    [Fact]
    public void Zero_AllowsSilentElevation()
    {
        var registry = new FakeRegistryStore();
        registry.SeedDword(
            RegistryHive.LocalMachine, SetupConstants.PolicyKeyPath,
            SetupConstants.ConsentPromptBehaviorAdmin, 0);

        var result = new ElevationPolicyGate(registry).Read();

        Check.That(result.Allowed).IsTrue();
        Check.That(result.ConsentPromptBehaviorAdmin).IsEqualTo(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void AnyOtherValue_Refuses(int value)
    {
        var registry = new FakeRegistryStore();
        registry.SeedDword(
            RegistryHive.LocalMachine, SetupConstants.PolicyKeyPath,
            SetupConstants.ConsentPromptBehaviorAdmin, value);

        var result = new ElevationPolicyGate(registry).Read();

        Check.That(result.Allowed).IsFalse();
        Check.That(result.ConsentPromptBehaviorAdmin).IsEqualTo(value);
    }

    [Fact]
    public void MissingValue_Refuses()
    {
        var result = new ElevationPolicyGate(new FakeRegistryStore()).Read();

        Check.That(result.Allowed).IsFalse();
        Check.That(result.ConsentPromptBehaviorAdmin).IsNull();
    }
}
