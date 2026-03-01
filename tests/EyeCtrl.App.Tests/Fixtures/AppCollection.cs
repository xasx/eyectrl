namespace EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// xUnit collection that shares a single AppFixture across all test classes.
/// Only one app instance runs for all UI tests.
/// </summary>
[CollectionDefinition("App")]
public class AppCollection : ICollectionFixture<AppFixture>
{
}
