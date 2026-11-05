namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Windows;

public class WindowsAppLogProbeTests
{
    /// <summary>
    /// The line the first real install wrote to <c>%APPDATA%\EyeCtrl\logs\eyectrl-20261105.log</c>
    /// (S8 sitting, 05.11), verbatim. Serilog's file sink renders the bool with <c>{Message:lj}</c>,
    /// so it is lowercase <c>true</c> — not the <c>True</c> the plan's dummy App wrote.
    /// </summary>
    private const string RealSittingLine =
        "2026-11-05 00:56:05.479 [INF] [] Token: IL=High UIAccess=true Elevated=false " +
        @"exe=C:\Program Files\EyeCtrl\EyeCtrl.App.exe";

    /// <summary>A launch time well before the real line's timestamp, whatever the test PC's zone.</summary>
    private static readonly DateTimeOffset BeforeTheRealLine =
        new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheRealSittingLine_PassesAsHighAndUiAccess()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            File.WriteAllText(
                Path.Combine(paths.LogsRoot, "eyectrl-20261105.log"), RealSittingLine + "\n");

            var found = new WindowsAppLogProbe(paths).ReadStartupTokenLine(app, BeforeTheRealLine);

            Check.That(found).IsEqualTo(AppStartupTokenLine.UiAccessTrue);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void TheRealLineShapeWithLowercaseFalse_IsTheDegradedCandidate()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            File.WriteAllText(
                Path.Combine(paths.LogsRoot, "eyectrl-20261105.log"),
                RealSittingLine.Replace("UIAccess=true", "UIAccess=false", StringComparison.Ordinal) + "\n");

            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(app, BeforeTheRealLine))
                .IsEqualTo(AppStartupTokenLine.UiAccessFalse);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("True", AppStartupTokenLine.UiAccessTrue)]
    [InlineData("true", AppStartupTokenLine.UiAccessTrue)]
    [InlineData("TRUE", AppStartupTokenLine.UiAccessTrue)]
    [InlineData("False", AppStartupTokenLine.UiAccessFalse)]
    [InlineData("false", AppStartupTokenLine.UiAccessFalse)]
    [InlineData("FALSE", AppStartupTokenLine.UiAccessFalse)]
    public void TheUiAccessValue_IsReadAsABool_WhateverTheCasing(
        string uiAccess, AppStartupTokenLine expected)
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            WriteLog(paths, app, "High", uiAccess, DateTime.Now.AddMinutes(-1));

            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-2))).IsEqualTo(expected);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void AValueThatIsNotABool_IsNoEvidence()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new SetupPaths(@"C:\Program Files", root, root);
            Directory.CreateDirectory(paths.LogsRoot);
            var app = Path.Combine(paths.InstallRoot, "EyeCtrl.App.exe");
            WriteLog(paths, app, "High", "yes", DateTime.Now.AddMinutes(-1));

            Check.That(new WindowsAppLogProbe(paths).ReadStartupTokenLine(
                app, DateTimeOffset.UtcNow.AddMinutes(-2))).IsEqualTo(AppStartupTokenLine.None);
        }
        finally
        {
            TryDelete(root);
        }
    }

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
