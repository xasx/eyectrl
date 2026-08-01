namespace EyeCtrl.Controls.Tests.Theme;

using Xunit;

/// <summary>
/// Serialises the test classes that load <c>OverlayStyles.xaml</c> through a
/// <c>pack://</c> URI.
///
/// Registering that scheme requires constructing a WPF <see cref="System.Windows.Application"/>,
/// and only one may exist per AppDomain. Both classes guard with
/// <c>if (Application.Current is null) _ = new Application()</c>, which is
/// check-then-act: run concurrently, both can pass the null check before either
/// assigns, and the loser throws <c>InvalidOperationException</c>.
///
/// That was harmless while <c>TokenDictionaryTests</c> was the only caller —
/// xUnit runs a single class's tests sequentially. Adding
/// <c>ThemeResourceApplierTests</c> made it a second collection, and collections
/// run in parallel by default, so the race became reachable and did fire.
///
/// A shared collection is the minimal fix: classes within one collection never
/// run concurrently with each other, while every other collection in the
/// assembly stays parallel. Deliberately no <c>DisableParallelization</c> — these
/// two only need to be serialised against each other, not against the assembly.
/// </summary>
[CollectionDefinition("WpfPackScheme")]
public class WpfPackSchemeCollection
{
}
