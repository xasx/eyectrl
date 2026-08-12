namespace EyeCtrl.App.Views.Settings;

/// <summary>One settings tab's page content, as a declarative spec list. Re-invoked on every rebuild.</summary>
internal interface ISettingsPageSource
{
    IReadOnlyList<PageSpec> Build();
}
