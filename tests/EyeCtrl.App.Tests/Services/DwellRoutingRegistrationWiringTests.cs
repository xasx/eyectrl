namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Adaptation;
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
/// OverlayManager is the one factory argument NOT covered here: its constructor only stores the
/// registry — the entry lands in KeyboardWindow's constructor, which OverlayManager.Initialize()
/// invokes alongside ~15 other XAML windows. Constructing that set headless is the XAML-resource
/// flake zone, so by the same ruling that parks SetViewModel it stays unpinned.
/// </remarks>
[Collection("DpiHelper Tests (App)")]
public class DwellRoutingRegistrationWiringTests
{
    private static ServiceProvider BuildProvider() =>
        AppBootstrapper.BuildServiceProvider(
            new EyeCtrlSettings(),
            settingsFilePath: Path.Combine(Path.GetTempPath(), "eyectrl-dwell-routing-wiring-test.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true);

    private static void CheckSingleEntry(
        IReadOnlyList<DwellRoutingRegistration> registrations,
        DwellContext context, int minMs, int maxMs)
    {
        Check.That(registrations).HasSize(1);
        Check.That(registrations[0].Context).IsEqualTo(context);
        Check.That(registrations[0].MinDwellMs).IsEqualTo(minMs);
        Check.That(registrations[0].MaxDwellMs).IsEqualTo(maxMs);
    }

    [Fact]
    public void NoteManager_ResolvedFromContainer_LandsItsRegistryEntry()
    {
        using var sp = BuildProvider();

        _ = sp.GetRequiredService<INoteManager>();
        var registry = sp.GetRequiredService<IDwellDetectorRegistry>();

        CheckSingleEntry(registry.Registrations, DwellContext.Notes,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs);
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
}
