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
/// Why a collection and not just per-test pinning: tests within one collection never run in
/// parallel with each other, which is the guarantee the readers here need. Do not rely on
/// <c>DisableParallelization</c> to serialise this collection against *other* collections — see
/// the note in EyeCtrl.App.Tests/Fixtures/WpfApplicationGate.cs, which documents that residual
/// as accepted. A mutator placed outside this collection is therefore still a hazard; put it here.
/// </para>
///
/// <para>
/// Controls.Tests has no <c>Tokens.Activate</c> caller today. This exists so the first one added
/// has an obvious home, and so the readers do not go back to being correct only by that absence.
/// </para>
/// </summary>
[CollectionDefinition("ActiveTheme", DisableParallelization = true)]
public class ActiveThemeCollection
{
}
