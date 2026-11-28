namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Wave D D1: the App-side wiring of the stranding ledger — the pause-exit source carried by
/// <see cref="PauseToggledMessage"/> through the real <see cref="GazeEngineAdapter"/> and the
/// duplicate-instance call site. The ledger's line formats and day totals live in its own Core
/// tests.
/// </summary>
public class StrandingLedgerWiringTests
{
    private sealed class StubControlFinder : IControlFinder
    {
        public ControlInfo? FindTarget(PhysicalPoint point) => null;
    }

    private sealed class Harness : IDisposable
    {
        public string Dir { get; } = TempTestDirectory.Create("strand-wiring");
        public List<string> Lines { get; } = new();
        public EyeCtrlSettings Settings { get; } = new();
        public WeakReferenceMessenger Messenger { get; } = new();
        public DateTime Now { get; set; } = new(2026, 11, 28, 9, 0, 0);
        public GazeEngine Engine { get; }
        public StrandingLedger Ledger { get; }
        public GazeEngineAdapter Adapter { get; }

        public Harness()
        {
            Engine = new GazeEngine(new TestFakes.FakeGazeProvider(),
                new TestFakes.FakeInputSimulator(), Settings);
            Ledger = new StrandingLedger(
                StrandingLedger.SessionMarkerPath(Dir),
                StrandingLedger.RefusedLaunchesPath(Dir),
                Lines.Add,
                () => Now);
            Adapter = new GazeEngineAdapter(
                Engine, Settings, Messenger, Dispatcher.CurrentDispatcher,
                NullLogger<GazeEngineAdapter>.Instance, new StubControlFinder(), ledger: Ledger);
        }

        public void Dispose()
        {
            Adapter.Dispose();
            Engine.Dispose();
        }
    }

    [StaTheory]
    [InlineData(StrandingLedger.PauseSources.Toolbar, true)]
    [InlineData(StrandingLedger.PauseSources.Strip, true)]
    [InlineData(StrandingLedger.PauseSources.Tray, false)]
    [InlineData(StrandingLedger.PauseSources.Probe, false)]
    public void PauseToggledMessage_CarriesTheSourceToTheLedger(string source, bool gaze)
    {
        using var h = new Harness();

        h.Messenger.Send(new PauseToggledMessage(source));   // pause
        Check.That(h.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Paused);

        h.Messenger.Send(new PauseToggledMessage(source));   // resume
        Check.That(h.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);

        Check.That(h.Lines).ContainsExactly($"STRAND-E1 pause_exit src={source} paused_ms=0");

        h.Ledger.EndSession();
        Check.That(h.Lines[^1]).Contains(gaze ? "left_gaze=1" : "left_other=1");
    }

    [StaFact]
    public void DirectToggle_WithoutAMessage_IsRecordedAsUnknown_NotGuessedAt()
    {
        using var h = new Harness();

        h.Engine.InteractionManager.TogglePause();
        h.Engine.InteractionManager.TogglePause();

        Check.That(h.Lines).ContainsExactly("STRAND-E1 pause_exit src=unknown paused_ms=0");
    }

    [StaFact]
    public void StartupPauseEntry_KeepsItsOwnSource_AndIsCountedOnce()
    {
        using var h = new Harness();

        // The App records the startup pause before PauseLaunchProvenance toggles; the adapter's
        // own Paused observation then hits an already-open pause and must not count a second one.
        h.Ledger.OnPauseEntered(StrandingLedger.PauseSources.Startup);
        h.Messenger.Send(new PauseToggledMessage(StrandingLedger.PauseSources.Toolbar));
        h.Messenger.Send(new PauseToggledMessage(StrandingLedger.PauseSources.Strip));

        h.Ledger.EndSession();
        Check.That(h.Lines[^1]).Contains("pauses=1");
        Check.That(h.Lines[^1]).Contains("left_gaze=1");
    }

    [Fact]
    public void AppStartup_RecordsTheRefusedSecondLaunch_CallSiteIsWired()
    {
        // The refusal exits before logging exists, so this call is the ONLY record of it; a
        // refactor that drops it would leave the daily summary's refused= stuck at 0 forever.
        string source = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs")));

        Check.That(source.Contains("StrandingLedger.RecordRefusedSecondLaunch(", StringComparison.Ordinal))
            .IsTrue();
        Check.That(source.Contains("_strandingLedger.StartSession()", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void ExitPlan_WritesTheSummaryAndDeletesTheMarker()
    {
        string dir = TempTestDirectory.Create("strand-exit");
        var lines = new List<string>();
        var ledger = new StrandingLedger(
            StrandingLedger.SessionMarkerPath(dir),
            StrandingLedger.RefusedLaunchesPath(dir),
            lines.Add);
        ledger.StartSession();
        Check.That(File.Exists(StrandingLedger.SessionMarkerPath(dir))).IsTrue();

        using var services = new ServiceCollection().BuildServiceProvider();
        ShutdownSequence sequence = ExitSequencePlan.Create(
            new ShutdownDependencies { ServiceProvider = services, StrandingLedger = ledger },
            _ => { });

        // Run the production ledger step exactly as the plan orders it; the rest of the sequence
        // needs the App's real container and has its own order pin.
        sequence.Steps.Single(step => step.Name == "write stranding summary").Run();

        Check.That(File.Exists(StrandingLedger.SessionMarkerPath(dir))).IsFalse();
        Check.That(lines.Any(line =>
            line.StartsWith("STRAND-E1 day_summary reason=shutdown", StringComparison.Ordinal))).IsTrue();
    }
}
