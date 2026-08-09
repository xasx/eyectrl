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
}
