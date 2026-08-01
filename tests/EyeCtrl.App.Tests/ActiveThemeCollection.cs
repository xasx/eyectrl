namespace EyeCtrl.App.Tests;

using Xunit;

/// <summary>
/// Serialises the tests that mutate Tokens.Current, which is process-global.
/// Everything else reads an ITheme directly and needs no such coordination.
/// </summary>
[CollectionDefinition("ActiveTheme", DisableParallelization = true)]
public class ActiveThemeCollection
{
}
