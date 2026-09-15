namespace EyeCtrl.Platform.Tests.Interop;

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using EyeCtrl.Platform.Interop;
using NFluent;
using Xunit;

public class NamedPipeSecurityBuilderTests
{
    [Fact]
    public void BuildCurrentUserOnly_ReturnsPipeSecurityWithSingleRule()
    {
        var security = new NamedPipeSecurityBuilder().BuildCurrentUserOnly();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        Check.That(rules).HasSize(1);
    }

    [Fact]
    public void BuildCurrentUserOnly_RuleGrantsCurrentUserReadWrite()
    {
        var security = new NamedPipeSecurityBuilder().BuildCurrentUserOnly();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var rule = (PipeAccessRule)rules[0]!;

        Check.That(rule.IdentityReference).IsEqualTo(WindowsIdentity.GetCurrent().User!);
        Check.That(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite)).IsTrue();
        Check.That(rule.AccessControlType).IsEqualTo(AccessControlType.Allow);
    }

    [Fact]
    public void BuildCurrentUserOnly_ContainsNoOtherRules()
    {
        var security = new NamedPipeSecurityBuilder().BuildCurrentUserOnly();
        var allRules = security.GetAccessRules(true, true, typeof(NTAccount));

        Check.That(allRules).HasSize(1);
    }

    // The accept loop creates the pipe's SECOND instance after the first client connects (and a
    // reconnect needs another). Creating a subsequent instance requires FILE_CREATE_PIPE_INSTANCE
    // on the first instance's own DACL: a ReadWrite-only rule DENIES it (verified empirically in
    // the branch review), permanently killing the channel after one connection — latent in the
    // supervisor's heartbeat pipe for as long as only one connection per launch ever happened.
    // This test pins the FullControl rule by BEHAVIOUR: both instances must construct.
    [Fact]
    public void BuildCurrentUserOnly_AllowsCreatingASecondPipeInstance()
    {
        var pipeName = "eyectrl_acl_test_" + Guid.NewGuid().ToString("N");
        var security = new NamedPipeSecurityBuilder().BuildCurrentUserOnly();

        using var first = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        using var second = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        Check.That(first.IsConnected).IsFalse();
        Check.That(second.IsConnected).IsFalse();
    }
}
