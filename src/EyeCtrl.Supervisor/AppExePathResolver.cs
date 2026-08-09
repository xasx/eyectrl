namespace EyeCtrl.Supervisor;

using System.IO;

internal static class AppExePathResolver
{
    public const string AppExeEnvVar = "EYECTRL_APP_EXE";

    /// <summary>
    /// Resolves the EyeCtrl.App.exe path. Env var wins if it points to an existing file;
    /// otherwise assumes the app exe sits next to the supervisor exe.
    /// TODO(installer): the installer will supply the canonical path.
    /// </summary>
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable(AppExeEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        var supervisorDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(supervisorDir, "EyeCtrl.App.exe"));
    }
}
