namespace EyeCtrl.Supervisor.Tests;

/// <summary>
/// Every test class that sets or reads <c>EYECTRL_APP_EXE</c> (<see cref="AppExePathResolver.AppExeEnvVar"/>)
/// belongs here. The variable is process-wide, and xUnit runs different classes in parallel: one
/// class's write (or its <c>finally</c> reset) lands between another's write and its
/// <see cref="AppExePathResolver.Resolve"/>. A named collection runs its members one after the
/// other without stopping the rest of the suite (unlike <c>DisableParallelization</c>, which in
/// xUnit v3 runs alongside nothing at all).
/// </summary>
[CollectionDefinition(Name)]
public sealed class AppExeEnvironmentCollection
{
    public const string Name = "EYECTRL_APP_EXE environment";
}
