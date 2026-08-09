namespace EyeCtrl.Supervisor.Tests.Ipc;

using EyeCtrl.Supervisor.Ipc;
using NFluent;
using Xunit;

public class IdentityChecksTests
{
    [Fact]
    public void CreationTimeWithinTolerance_ExactMatch_ReturnsTrue()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Check.That(IdentityChecks.CreationTimeWithinTolerance(t, t)).IsTrue();
    }

    [Fact]
    public void CreationTimeWithinTolerance_WithinTolerance_ReturnsTrue()
    {
        var expected = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var actual = expected.AddSeconds(1.9);

        Check.That(IdentityChecks.CreationTimeWithinTolerance(actual, expected)).IsTrue();
    }

    [Fact]
    public void CreationTimeWithinTolerance_OutsideTolerance_ReturnsFalse()
    {
        var expected = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var actual = expected.AddSeconds(2.1);

        Check.That(IdentityChecks.CreationTimeWithinTolerance(actual, expected)).IsFalse();
    }

    [Fact]
    public void CreationTimeWithinTolerance_NegativeDelta_OutsideTolerance()
    {
        var expected = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var actual = expected.AddSeconds(-3);

        Check.That(IdentityChecks.CreationTimeWithinTolerance(actual, expected)).IsFalse();
    }

    [Fact]
    public void IsSameSession_Matching_ReturnsTrue()
    {
        Check.That(IdentityChecks.IsSameSession(5, 5)).IsTrue();
    }

    [Fact]
    public void IsSameSession_Mismatched_ReturnsFalse()
    {
        Check.That(IdentityChecks.IsSameSession(5, 10)).IsFalse();
    }

    [Fact]
    public void ExePathMatches_ExactMatch_ReturnsTrue()
    {
        Check.That(IdentityChecks.ExePathMatches(@"C:\App\EyeCtrl.App.exe", @"C:\App\EyeCtrl.App.exe")).IsTrue();
    }

    [Fact]
    public void ExePathMatches_DifferentCase_ReturnsTrue()
    {
        Check.That(IdentityChecks.ExePathMatches(@"C:\APP\eyectrl.app.exe", @"C:\App\EyeCtrl.App.exe")).IsTrue();
    }

    [Fact]
    public void ExePathMatches_DifferentPath_ReturnsFalse()
    {
        Check.That(IdentityChecks.ExePathMatches(@"C:\Other\App.exe", @"C:\App\EyeCtrl.App.exe")).IsFalse();
    }
}
