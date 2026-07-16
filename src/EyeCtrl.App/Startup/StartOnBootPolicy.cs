namespace EyeCtrl.App.Startup;

public static class StartOnBootPolicy
{
    /// <summary>In supervised mode the supervisor owns launch-at-logon; the app never self-registers.</summary>
    public static bool ShouldSelfRegister(bool supervised, bool startOnBoot) => !supervised && startOnBoot;
}
