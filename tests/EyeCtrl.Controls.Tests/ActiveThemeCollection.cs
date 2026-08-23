namespace EyeCtrl.Controls.Tests;

using Xunit;

/// <summary>
/// Serialises the tests that read or mutate <c>Tokens.Current</c>, which is process-global
/// mutable state (<c>Tokens.Activate</c>). Mirrors the collection of the same name in
/// EyeCtrl.App.Tests; collection definitions are per-assembly, so each project needs its own.
///
/// <para>
/// The contract for anything joining this collection: if you call <c>Tokens.Activate</c>, restore
/// <c>MidnightTheme.Instance</c> in a <c>finally</c>. <c>TokenValueTests.DefaultTheme_IsMidnight</c>
/// is the canary for that — it asserts the ambient theme IS Midnight, so a mutator that forgets to
/// restore fails it rather than silently skewing a later reader.
/// </para>
///
/// <para>
/// Do not rely on <c>DisableParallelization</c> to serialise this collection against *other*
/// collections — EyeCtrl.App.Tests/Fixtures/WpfApplicationGate.cs documents that residual as
/// accepted. What this leans on is the within-collection guarantee, so a mutator placed outside
/// this collection is still a hazard; put it here.
/// </para>
/// </summary>
[CollectionDefinition("ActiveTheme", DisableParallelization = true)]
public class ActiveThemeCollection
{
}
