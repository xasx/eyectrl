namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
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
            WriteLog(paths, app, "High", "True", DateTime.Now.AddMinutes(-1));

            var found = new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-2));

            Check.That(found).IsEqualTo(AppStartupTokenLine.UiAccessTrue);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ALineSayingUiAccessFalse_IsReportedAsSuch()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            WriteLog(paths, app, "Medium", "False", DateTime.Now.AddMinutes(-1));

            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-2))).IsEqualTo(AppStartupTokenLine.UiAccessFalse);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ALineBeforeTheLaunch_OrForAnotherExe_IsNoEvidence()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            WriteLog(paths, app, "High", "True", DateTime.Now.AddMinutes(-30));

            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-1))).IsEqualTo(AppStartupTokenLine.None);
            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow)).IsEqualTo(AppStartupTokenLine.None);
            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                @"C:\Program Files\EyeCtrl\Other.exe", DateTimeOffset.UtcNow.AddHours(-1)))
                .IsEqualTo(AppStartupTokenLine.None);
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

            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow.AddHours(-1))).IsEqualTo(AppStartupTokenLine.None);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void NoLogsFolder_IsNone()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                @"C:\Program Files\EyeCtrl\EyeCtrl.App.exe", DateTimeOffset.UtcNow))
                .IsEqualTo(AppStartupTokenLine.None);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void WriteLog(
        SetupPaths paths, string app, string integrity, string uiAccess, DateTime timestamp)
    {
        File.WriteAllText(Path.Combine(paths.LogsRoot, "eyectrl-20261103.log"),
            $"{timestamp:yyyy-MM-dd HH:mm:ss.fff} [INF] [App] " +
            $"Token: IL={integrity} UIAccess={uiAccess} Elevated=False exe={app}\n");
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
