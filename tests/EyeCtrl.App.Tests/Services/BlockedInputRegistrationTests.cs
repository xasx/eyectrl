namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using EyeCtrl.Platform.Services;
using EyeCtrl.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// C10's composition root, resolved through the real bootstrapper (the
/// <see cref="WindowManagerRegistrationTests"/> pattern): the input sink every caller resolves must
/// be the R2 decorator, the escape path must be the same non-injecting window manager, and the
/// startup token line must be logged from the resolved probe. A registration that compiles but
/// silently bypasses the decorator is the exact defect these pins exist for.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class BlockedInputRegistrationTests
{
    private static readonly string AppData = TestAppDataDir.Create("blocked-input");

    private static ServiceProvider BuildProvider()
        => AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    [Fact]
    public void TheInputSink_IsTheVerifyingDecorator()
    {
        using var sp = BuildProvider();

        Check.That(sp.GetRequiredService<IInputSimulator>()).IsInstanceOf<VerifiedInputSimulator>();
        Check.That(sp.GetRequiredService<BlockedInputDetector>()).IsNotNull();
        Check.That(sp.GetRequiredService<ICursorPositionProbe>()).IsInstanceOf<Win32CursorPositionProbe>();
        Check.That(sp.GetRequiredService<IInputIntegrityProbe>()).IsInstanceOf<Win32InputIntegrityProbe>();
    }

    [Fact]
    public void TheEscapeService_IsTheSameNonInjectingWindowManager()
    {
        using var sp = BuildProvider();

        var windowManager = sp.GetRequiredService<IWindowManager>();
        var escape = sp.GetRequiredService<IEscapeWindowService>();

        Check.That(escape).IsInstanceOf<Win32WindowManager>();
        Check.That(escape).IsSameReferenceAs(windowManager);
    }

    [Fact]
    public void TheStartupTokenLine_AndTheCoordinator_AreWiredInAppStartup()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs"));

        Check.WithCustomMessage("the one startup token line is gone.")
            .That(source.Contains("AppStartupTokenLog.TokenLineTemplate", StringComparison.Ordinal))
            .IsTrue();
        Check.WithCustomMessage("the token line no longer reads the resolved probe.")
            .That(source.Contains("IInputIntegrityProbe>().ReadSelf()", StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("the token line no longer names the level with R1's vocabulary.")
            .That(source.Contains("InputIntegrityRules.IntegrityName(selfToken.IntegrityLevel)", StringComparison.Ordinal))
            .IsTrue();
        Check.WithCustomMessage("the blocked-input coordinator is not constructed at startup.")
            .That(source.Contains("new BlockedInputCoordinator(", StringComparison.Ordinal)).IsTrue();
    }
}
