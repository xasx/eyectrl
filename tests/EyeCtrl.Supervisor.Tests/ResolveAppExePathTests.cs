namespace EyeCtrl.Supervisor.Tests;

using System.IO;
using EyeCtrl.Supervisor;
using NFluent;
using Xunit;

public class ResolveAppExePathTests
{
    private const string EnvVar = AppExePathResolver.AppExeEnvVar;

    [Fact]
    public void Resolve_EnvVarSetToExistingFile_ReturnsEnvVarPath()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "EyeCtrl.App_Test.exe");
        File.WriteAllText(tempFile, "test");
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, tempFile);
            var result = AppExePathResolver.Resolve();

            Check.That(result).IsEqualTo(Path.GetFullPath(tempFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Resolve_EnvVarSetToNonExistentFile_FallsBackToSibling()
    {
        Environment.SetEnvironmentVariable(EnvVar, @"C:\NonExistent\path\EyeCtrl.App.exe");
        try
        {
            var result = AppExePathResolver.Resolve();

            Check.That(result).EndsWith("EyeCtrl.App.exe");
            Check.That(result).IsEqualTo(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "EyeCtrl.App.exe")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
        }
    }

    [Fact]
    public void Resolve_EnvVarEmpty_FallsBackToSibling()
    {
        Environment.SetEnvironmentVariable(EnvVar, "");
        try
        {
            var result = AppExePathResolver.Resolve();

            Check.That(result).EndsWith("EyeCtrl.App.exe");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
        }
    }

    [Fact]
    public void Resolve_EnvVarNotSet_FallsBackToSibling()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var result = AppExePathResolver.Resolve();

        Check.That(result).IsEqualTo(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "EyeCtrl.App.exe")));
    }
}
