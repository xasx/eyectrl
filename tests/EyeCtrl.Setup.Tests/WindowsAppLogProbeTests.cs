namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Windows;

public class WindowsAppLogProbeTests
{
    [Fact]
    public void TheUiAccessStartupLine_IsFoundForTheAppAfterTheLaunchTime()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            File.WriteAllText(Path.Combine(paths.LogsRoot, "eyectrl-20261103.log"),
                $"{DateTime.Now.AddMinutes(-1):yyyy-MM-dd HH:mm:ss.fff} [INF] [App] " +
                $"Token: IL=High UIAccess=True Elevated=False exe={app}\n");

            var found = new WindowsAppLogProbe(paths).HasUiAccessStartupLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-2));

            Check.That(found).IsTrue();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ALineBeforeTheLaunch_OrForAnotherExe_DoesNotPass()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            File.WriteAllText(Path.Combine(paths.LogsRoot, "eyectrl-20261103.log"),
                $"{DateTime.Now.AddMinutes(-30):yyyy-MM-dd HH:mm:ss.fff} [INF] [App] " +
                $"Token: IL=High UIAccess=True Elevated=False exe={app}\n");

            Check.That(new WindowsAppLogProbe(paths).HasUiAccessStartupLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-1))).IsFalse();
            Check.That(new WindowsAppLogProbe(paths).HasUiAccessStartupLine(
                app, DateTimeOffset.UtcNow)).IsFalse();
            Check.That(new WindowsAppLogProbe(paths).HasUiAccessStartupLine(
                @"C:\Program Files\EyeCtrl\Other.exe", DateTimeOffset.UtcNow.AddHours(-1))).IsFalse();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void AnUnparseableTimestamp_FailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            File.WriteAllText(Path.Combine(paths.LogsRoot, "eyectrl-20261103.log"),
                $"no-timestamp [INF] [App] Token: IL=High UIAccess=True Elevated=False exe={app}\n");

            Check.That(new WindowsAppLogProbe(paths).HasUiAccessStartupLine(
                app, DateTimeOffset.UtcNow.AddHours(-1))).IsFalse();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void NoLogsFolder_IsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Check.That(new WindowsAppLogProbe(paths).HasUiAccessStartupLine(
                @"C:\Program Files\EyeCtrl\EyeCtrl.App.exe", DateTimeOffset.UtcNow)).IsFalse();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "eyectrl-setup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
