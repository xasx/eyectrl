namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Guards the registry arguments of the five factory lambdas in <c>AppBootstrapper</c>
/// (ManualSelectionDwellService, ExternalDwellService, NoteManager, OverlayManager,
/// EyeCareBannerWindow). Every one of them feeds an OPTIONAL constructor parameter
/// (<c>IDwellDetectorRegistry? dwellRegistry = null</c>), so deleting the argument neither fails
/// the build nor throws at resolution — the app just silently stops routing adaptation to that
/// surface. An implementation-review mutation confirmed the suite stays green across all five
/// deletions; these facts make each one loud by resolving the owner from a real provider and
/// asserting the registry entry its factory must land.
/// </summary>
/// <remarks>
/// Mirrors <see cref="TelemetrySinkRegistrationTests"/>: builds the real container and never
/// resolves ViewModels (they would register live handlers on the process-wide
/// <c>WeakReferenceMessenger.Default</c> the bootstrapper binds and leak into sibling tests).
/// NoteManager and ManualSelectionDwellService DO register handlers on that messenger, but every
/// fact disposes its provider, and ServiceProvider disposal disposes both singletons — whose
/// Dispose unregisters them from the messenger — so nothing leaks.
///
/// The two window-constructing facts (ExternalDwellService builds a DwellFeedbackOverlay,
/// EyeCareBannerWindow's XAML reads theme tokens) need [StaFact] plus
/// <see cref="WpfApplicationGate.EnsureApplicationWithTokens"/>. They are therefore gate callers
/// and must never run concurrently with another one: this class sits in
/// "DpiHelper Tests (App)", which is DisableParallelization, and xUnit v3 runs such a collection
/// exclusively — the same reasoning <c>ExternalDwellServiceTests</c> ("ExternalDwellPipe") relies
/// on instead of joining "WpfApplication".
///
/// OverlayManager is guarded one level removed from the other four: its constructor only STORES
/// the registry — the entry lands later, in KeyboardWindow's constructor, which
/// OverlayManager.Initialize() invokes alongside ~15 other XAML windows. Constructing that set
/// headless is the XAML-resource flake zone, so by the same ruling that parks SetViewModel the
/// entry itself is not asserted; instead the <c>_dwellRegistry</c> field is checked reflectively
/// (see <c>OverlayManager_FactoryLambda_ReceivesTheRegistrySingleton</c>).
/// </remarks>
[Collection("DpiHelper Tests (App)")]
public class DwellRoutingRegistrationWiringTests
{
    // A temp app-data folder, not the real one. This test resolves CalibrationService without
    // mentioning it — via ManualSelectionDwellService/ExternalDwellService -> GazeEngine — and that
    // resolution loads, and can rewrite, calibration.json. See TestAppDataDir.
    private static readonly string AppData = TestAppDataDir.Create("dwell-routing");

    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);

    private static void CheckSingleEntry(
        IReadOnlyList<DwellRoutingRegistration> registrations,
        DwellContext context, int minMs, int maxMs)
    {
        Check.That(registrations).HasSize(1);
        Check.That(registrations[0].Context).IsEqualTo(context);
        Check.That(registrations[0].MinDwellMs).IsEqualTo(minMs);
        Check.That(registrations[0].MaxDwellMs).IsEqualTo(maxMs);
        Check.That(registrations[0].ApplyGeometry).IsNotNull();
    }

    [Fact]
    public void NoteManager_ResolvedFromContainer_LandsItsRegistryEntry()
    {
        using var sp = BuildProvider();

        _ = sp.GetRequiredService<INoteManager>();
        var registry = sp.GetRequiredService<IDwellDetectorRegistry>();

        // P2 D6: the container-routed entry carries the explicit 600 ms floor (NoteMinDwellMs),
        // not the standard family's 200 ms minimum.
        CheckSingleEntry(registry.Registrations, DwellContext.Notes,
            NoteManager.NoteMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs);
    }

    [Fact]
    public void NoteManager_ResolvedFromContainer_GetsTheProductionHitTester()
    {
        // Fixup 01 M2: the constructor defaults to the production NoteHitTester and the DI root
        // passes nothing — so a deleted wiring or a swapped default cannot silently disable the
        // own-window conjunct. Mutant: the fallback becomes InactiveNoteHitTester (or the default
        // is dropped and null reaches the field) → this goes red.
        using var sp = BuildProvider();

        var manager = (NoteManager)sp.GetRequiredService<INoteManager>();

        Check.That(manager.HitTesterForTest).IsInstanceOf<NoteHitTester>();
    }

    [Fact]
    public void ManualSelectionDwellService_ResolvedFromContainer_RegistersOnStart_UnregistersOnEnd()
    {
        // ManualSelection registers per-session (on StartManualSelectionMessage), not in its ctor —
        // this also pins the per-session unregister that no other test covers.
        DpiHelper.Initialize(1.0, 1.0); // OnManualSelectionStarted scales the radius by DpiHelper.ScaleX
        using var sp = BuildProvider();

        var messenger = sp.GetRequiredService<IMessenger>(); // the Default messenger the service was built with
        var registry = sp.GetRequiredService<IDwellDetectorRegistry>();
        _ = sp.GetRequiredService<ManualSelectionDwellService>();

        Check.That(registry.Registrations).IsEmpty();

        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 200, 2.0, new PhysicalPoint(500, 500)));

        CheckSingleEntry(registry.Registrations, DwellContext.ManualSelection,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs);

        messenger.Send(new EndManualSelectionMessage());

        Check.That(registry.Registrations).IsEmpty();
    }

    [StaFact]
    public void ExternalDwellService_ResolvedFromContainer_LandsItsRegistryEntry()
    {
        // The constructor builds a DwellFeedbackOverlay (a Window) whose XAML reads theme tokens,
        // hence the gate + STA. It never STARTS the pipe server (NewlineJsonPipeServer binds the
        // stream lazily in Start()), so resolving through DI binds no machine-global pipe name.
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();

        _ = sp.GetRequiredService<ExternalDwellService>();
        var registry = sp.GetRequiredService<IDwellDetectorRegistry>();

        CheckSingleEntry(registry.Registrations, DwellContext.ExternalDwell,
            AdaptationDwellRanges.ExternalMinDwellMs, AdaptationDwellRanges.ExternalMaxDwellMs);

        // Provider disposal disposes the singleton, which must unregister from the registry (the
        // registry itself is not IDisposable, so the held reference survives to observe the empty
        // snapshot). Pins the same teardown contract the per-session owners honor.
        sp.Dispose();
        Check.That(registry.Registrations).IsEmpty();
    }

    [StaFact]
    public void EyeCareBannerWindow_ResolvedFromContainer_LandsItsRegistryEntry()
    {
        // Headless window construction is precedented (GlyphSizeCouplingTests constructs this very
        // window outside the UI category) as long as the token dictionary is merged first.
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var sp = BuildProvider();

        _ = sp.GetRequiredService<EyeCareBannerWindow>();
        var registry = sp.GetRequiredService<IDwellDetectorRegistry>();

        CheckSingleEntry(registry.Registrations, DwellContext.EyeCare,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs);
    }

    [Fact]
    public void OverlayManager_FactoryLambda_ReceivesTheRegistrySingleton()
    {
        // OverlayManager's constructor only STORES the registry — its routing entry lands later,
        // in KeyboardWindow's ctor via Initialize(), out of headless reach (the parked XAML
        // ruling) — so the factory lambda is guarded at the field instead: dropping the registry
        // argument leaves _dwellRegistry null, and passing any other instance fails
        // IsSameReferenceAs. That kills both mutations; asserting the entry itself would require
        // constructing Initialize()'s ~16-window XAML set.
        using var sp = BuildProvider();

        var registrySingleton = sp.GetRequiredService<IDwellDetectorRegistry>();
        var manager = sp.GetRequiredService<IOverlayManager>();
        Check.That(manager).IsInstanceOf<OverlayManager>();

        var field = typeof(OverlayManager).GetField(
            "_dwellRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
        Check.That(field).IsNotNull();

        var fieldValue = field!.GetValue(manager);
        Check.That(fieldValue).IsNotNull();
        Check.That(fieldValue).IsSameReferenceAs(registrySingleton);
    }
}
