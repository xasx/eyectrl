namespace EyeCtrl.App.Tests.Services;

using System.Globalization;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Input;
using EyeCtrl.Tests.TestSupport;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

/// <summary>
/// C10's startup token line is a contract between the App, which writes it, and Setup, which
/// reads it for the pass rule and Q12's degraded detection. The first real install (S8 sitting,
/// 05.11) failed because the two ends disagreed on a bool's casing: Serilog's file sink renders
/// <c>{UIAccess}</c> as lowercase <c>true</c>, while Setup looked for <c>UIAccess=True</c>.
///
/// These tests render the App's REAL message template through the App's REAL output template with
/// the real Serilog formatter, so the exact line the sink writes — casing included — is pinned at
/// the producing end, and compared with the verbatim line the first real install logged. Setup's
/// probe is tested against that same verbatim line in EyeCtrl.Setup.Tests (Setup cannot reference
/// the App, so the shared <see cref="AppStartupTokenLog"/> template is the join).
/// </summary>
public class StartupTokenLineContractTests
{
    /// <summary>
    /// The line the first real install wrote to
    /// <c>%APPDATA%\EyeCtrl\logs\eyectrl-20261105.log</c>, verbatim.
    /// </summary>
    private const string RealSittingLine =
        "2026-11-05 00:56:05.479 [INF] [] Token: IL=High UIAccess=true Elevated=false " +
        @"exe=C:\Program Files\EyeCtrl\EyeCtrl.App.exe";

    [Fact]
    public void TheAppTemplates_RenderExactlyTheLineTheFirstRealInstallLogged()
    {
        Check.That(Render(uiAccess: true, elevated: false)).IsEqualTo(RealSittingLine);
    }

    [Fact]
    public void AUiAccessFalseApp_AlsoRendersALowercaseBool()
    {
        Check.That(Render(uiAccess: false, elevated: false)).IsEqualTo(
            "2026-11-05 00:56:05.479 [INF] [] Token: IL=High UIAccess=false Elevated=false " +
            @"exe=C:\Program Files\EyeCtrl\EyeCtrl.App.exe");
    }

    [Fact]
    public void TheAppWritesTheSharedTemplate_NotAPrivateCopy()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs"));

        Check.WithCustomMessage("the startup token line no longer uses the shared contract template.")
            .That(source.Contains("AppStartupTokenLog.TokenLineTemplate", StringComparison.Ordinal))
            .IsTrue();
    }

    private static string Render(bool uiAccess, bool elevated)
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 11, 5, 0, 56, 5, 479, TimeSpan.Zero),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse(AppStartupTokenLog.TokenLineTemplate),
            new[]
            {
                new LogEventProperty("IntegrityLevel", new ScalarValue("High")),
                new LogEventProperty("UIAccess", new ScalarValue(uiAccess)),
                new LogEventProperty("Elevated", new ScalarValue(elevated)),
                new LogEventProperty(
                    "ExecutablePath", new ScalarValue(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe")),
            });

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new MessageTemplateTextFormatter(LoggingInitializer.OutputTemplate, CultureInfo.InvariantCulture)
            .Format(logEvent, writer);
        return writer.ToString().TrimEnd('\r', '\n');
    }
}
