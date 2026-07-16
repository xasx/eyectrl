namespace EyeCtrl.Supervisor;

using System;
using System.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // SupervisorLoggingInitializer.Initialize(...) wired in Task 20.
        var app = new SupervisorApp();
        app.Initialize();
        return app.Run();
    }
}

internal sealed class SupervisorApp : Application
{
    public void Initialize()
    {
        // Wired in later tasks (state machine, pipe server, tray, supervision loop).
    }
}
