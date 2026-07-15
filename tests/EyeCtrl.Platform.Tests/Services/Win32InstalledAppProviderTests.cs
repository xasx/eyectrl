namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;

public class Win32InstalledAppProviderTests
{
    [Fact]
    public void GetInstalledApps_NeverThrows_AndEntriesAreWellFormed()
    {
        var provider = new Win32InstalledAppProvider();

        // Must not throw even where the shell namespace is unavailable (headless CI).
        var apps = provider.GetInstalledApps();

        Check.That(apps).IsNotNull();
        // Empty is a valid result (minimal images). Assert shape only when present.
        foreach (var app in apps)
        {
            Check.That(app.DisplayName).IsNotEmpty();
            Check.That(app.Aumid).Contains("!");
        }
    }

    [Fact]
    public void GetInstalledApps_IsCached_ReturnsSameInstanceOnRepeatCalls()
    {
        var provider = new Win32InstalledAppProvider();
        Check.That(provider.GetInstalledApps()).IsSameReferenceAs(provider.GetInstalledApps());
    }
}
