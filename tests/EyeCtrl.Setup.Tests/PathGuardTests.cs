namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;
using Microsoft.Win32;

public class PathGuardTests
{
    private static PathGuard Guard() => new(new SetupPaths(
        @"C:\Program Files", @"C:\Users\test\AppData\Roaming", @"C:\Users\test\AppData\Local"));

    [Theory]
    [InlineData(@"C:\Program Files\EyeCtrl")]
    [InlineData(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe")]
    [InlineData(@"C:\Program Files\EyeCtrl.previous")]
    [InlineData(@"C:\Program Files\EyeCtrl.staging\EyeCtrl.cat")]
    [InlineData(@"C:\Program Files\EyeCtrl.failed\x.dll")]
    [InlineData(@"C:\Program Files\EyeCtrl.run\abc\EyeCtrl.Setup.exe")]
    public void TheFiveProgramFilesTrees_AreAllowed(string path)
    {
        Guard().RequireProgramFilesPath(path);
    }

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\EyeCtrl2")]
    [InlineData(@"C:\Program Files\Other")]
    [InlineData(@"C:\Program Files\EyeCtrl\..\Other")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void EverythingElse_IsRefused(string path)
    {
        Check.ThatCode(() => Guard().RequireProgramFilesPath(path))
            .Throws<PathGuardException>();
    }

    [Fact]
    public void TheUninstallKey_IsExact()
    {
        Guard().RequireUninstallKey(SetupConstants.UninstallKeyPath);

        Check.ThatCode(() => Guard().RequireUninstallKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Other"))
            .Throws<PathGuardException>();
        Check.ThatCode(() => Guard().RequireUninstallKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
            .Throws<PathGuardException>();
    }

    [Theory]
    [InlineData(SetupConstants.RunKeyPath, "EyeCtrl")]
    [InlineData(SetupConstants.RunKeyPath, "EyeCtrlSupervisor")]
    [InlineData(SetupConstants.RunOnceKeyPath, "EyeCtrlRepair")]
    public void HkcuRunValuesNamedEyeCtrl_AreAllowed(string key, string name)
    {
        Guard().RequireRunValue(RegistryHive.CurrentUser, key, name);
    }

    [Fact]
    public void RunValuesOutsideTheContract_AreRefused()
    {
        Check.ThatCode(() => Guard().RequireRunValue(
                RegistryHive.CurrentUser, SetupConstants.RunKeyPath, "SomethingElse"))
            .Throws<PathGuardException>();
        Check.ThatCode(() => Guard().RequireRunValue(
                RegistryHive.LocalMachine, SetupConstants.RunKeyPath, "EyeCtrl"))
            .Throws<PathGuardException>();
        Check.ThatCode(() => Guard().RequireRunValue(
                RegistryHive.CurrentUser, @"Software\Other", "EyeCtrl"))
            .Throws<PathGuardException>();
    }

    [Fact]
    public void OnlyTheResultRecordAndItsFolder_AreWritableUnderAppData()
    {
        var guard = Guard();
        guard.RequireResultRecordPath(@"C:\Users\test\AppData\Roaming\EyeCtrl\setup-last.json");
        guard.RequireResultRecordDirectory(@"C:\Users\test\AppData\Roaming\EyeCtrl");

        Check.ThatCode(() => guard.RequireResultRecordPath(
                @"C:\Users\test\AppData\Roaming\EyeCtrl\settings.json"))
            .Throws<PathGuardException>();
        Check.ThatCode(() => guard.RequireResultRecordPath(
                @"C:\Users\test\AppData\Roaming\EyeCtrl\logs\x.log"))
            .Throws<PathGuardException>();
        Check.ThatCode(() => guard.RequireResultRecordDirectory(
                @"C:\Users\test\AppData\Roaming"))
            .Throws<PathGuardException>();
    }

    [Fact]
    public void TrailingSeparatorsAndCase_DoNotDefeatTheGuard()
    {
        var guard = Guard();
        guard.RequireProgramFilesPath(@"C:\PROGRAM FILES\EyeCtrl\");
        Check.ThatCode(() => guard.RequireProgramFilesPath(@"C:\PROGRAM FILES\eyectrl2\")
        ).Throws<PathGuardException>();
    }
}
