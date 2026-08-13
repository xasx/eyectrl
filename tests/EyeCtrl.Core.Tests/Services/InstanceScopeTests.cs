namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Contracts;

public class InstanceScopeTests
{
    [Fact]
    public void EnvVar_IsTheDocumentedName()
    {
        Check.That(InstanceScope.EnvVar).IsEqualTo("EYECTRL_INSTANCE_ID");
    }

    [Theory]
    [InlineData("e2e")]
    [InlineData("abc123")]
    [InlineData("with-dash")]
    [InlineData("with_underscore")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void TryParseId_AcceptsLegalIds(string raw)
    {
        Check.That(InstanceScope.TryParseId(raw, out var id)).IsTrue();
        Check.That(id).IsEqualTo(raw);
    }

    [Fact]
    public void TryParseId_TrimsSurroundingWhitespace()
    {
        Check.That(InstanceScope.TryParseId("  run7  ", out var id)).IsTrue();
        Check.That(id).IsEqualTo("run7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseId_RejectsAbsent(string? raw)
    {
        Check.That(InstanceScope.TryParseId(raw, out var id)).IsFalse();
        Check.That(id).IsEqualTo(string.Empty);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("back\\slash")]      // would smuggle a segment into Local\...
    [InlineData("for/ward")]         // would smuggle a path segment
    [InlineData("dot.dot")]          // not in the allowed set
    [InlineData("semi;colon")]
    [InlineData("pipe|char")]
    [InlineData("colon:char")]
    public void TryParseId_RejectsCharactersIllegalInPipeOrMutexNames(string raw)
    {
        Check.That(InstanceScope.TryParseId(raw, out _)).IsFalse();
    }

    [Fact]
    public void TryParseId_RejectsOverLongIds()
    {
        Check.That(InstanceScope.TryParseId(new string('a', 64), out _)).IsTrue();
        Check.That(InstanceScope.TryParseId(new string('a', 65), out _)).IsFalse();
    }

    [Fact]
    public void Scope_AppendsTheSuffix()
    {
        Check.That(InstanceScope.Scope("eyectrl_dwell", "run7")).IsEqualTo("eyectrl_dwell_run7");
        Check.That(InstanceScope.Scope(@"Local\EyeCtrl.App.Singleton", "run7"))
             .IsEqualTo(@"Local\EyeCtrl.App.Singleton_run7");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Scope_ReturnsTheDefaultWhenThereIsNoId(string? id)
    {
        Check.That(InstanceScope.Scope("eyectrl_dwell", id)).IsEqualTo("eyectrl_dwell");
    }

    [Fact]
    public void Scope_RejectsAMalformedIdRatherThanBuildingAnUnbindableName()
    {
        // A caller that skipped TryParseId must not get a name with a backslash in the middle.
        ArgumentException? ex = null;
        try { InstanceScope.Scope("eyectrl_dwell", "bad\\id"); }
        catch (ArgumentException e) { ex = e; }

        Check.That(ex).IsNotNull();
    }
}
