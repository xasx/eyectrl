namespace EyeCtrl.Supervisor.Tests;

/// <summary>
/// Every test class that loads Supervisor XAML (<c>FallenBackDialog</c>, <c>SupervisorSetupWindow</c>)
/// belongs here. Each such class builds its windows on its own STA thread, and
/// <c>Application.LoadComponent</c> reads the assembly's resource package, whose list of open part
/// streams is not thread-safe: two classes loading in parallel once threw a
/// <c>NullReferenceException</c> in <c>PackagePart.CleanUpRequestedStreamsList</c> and turned six
/// fit tests red (T09 review-03, F1). A named collection runs its members one after the other
/// without stopping the rest of the suite (unlike <c>DisableParallelization</c>, which in xUnit v3
/// runs alongside nothing at all); no other class in this project loads XAML.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SupervisorXamlCollection
{
    public const string Name = "Supervisor XAML";
}
