namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The P5 composition test (Task 6, R2-1): <c>ToolbarViewModel</c> resolved from the PRODUCTION
/// container must arrive fully wired. Its three optional constructor parameters —
/// <c>IStruggleTelemetrySink</c>, <c>IDwellDetectorRegistry</c>, <c>Func&lt;bool&gt;</c>
/// (the feedback-gate poll) — all default to null, so the plain
/// <c>services.AddSingleton&lt;ToolbarViewModel&gt;()</c> registration (or a factory that drops an
/// argument) compiles, resolves, and silently switches off toolbar telemetry, adaptation dwell/
/// geometry routing, and sensitive-context suppression of the preview. This fact makes each of
/// the three loud against the real bootstrapper, not a hand-built container.
/// </summary>
/// <remarks>
/// Mirrors <see cref="DwellRoutingRegistrationWiringTests"/>: builds the real container, and —
/// unlike that suite — DOES resolve a ViewModel. The VM registers its handlers on the
/// process-wide <c>WeakReferenceMessenger.Default</c> the bootstrapper binds, so the provider is
/// disposed at the end of the fact to tear those registrations (and the registry entry) down;
/// nothing leaks into sibling tests. No windows are constructed, so no STA or
/// <c>WpfApplicationGate</c> is needed — but the class joins the same exclusive collection so it
/// can never race another gate caller.
/// </remarks>
[Collection("DpiHelper Tests (App)")]
public class ToolbarPreviewWiringTests
{
    private static readonly string AppData = TestAppDataDir.Create("toolbar-preview-wiring");

    private static ServiceProvider BuildProvider()
    {
        var settings = new EyeCtrlSettings();
        // DispatcherTelemetrySink drops outcomes while adaptation is off (the §9.1 gate — see
        // TelemetrySinkRegistrationTests), so the sink-reach leg below opts in.
        settings.Adaptation.Enabled = true;
        return AppBootstrapper.BuildServiceProvider(
            settings,
            settingsFilePath: Path.Combine(AppData, "settings.json"),
            dispatcher: Dispatcher.CurrentDispatcher,
            isTestMode: true,
            appDataDir: AppData);
    }

    /// <summary>
    /// The production gate reads its detector field per poll, so substituting a fake detector
    /// (reflectively, the same one-level-removed technique DwellRoutingRegistrationWiringTests
    /// uses for OverlayManager) turns the resolved gate into a controllable fake without
    /// touching the registration — which is the whole point of asserting against the real container.
    /// </summary>
    private sealed class FakeSensitiveDetector : ISensitiveContextDetector
    {
        public bool Active { get; set; }
        public bool IsSensitiveActive => Active;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public void ToolbarViewModel_Factory_ReceivesRegistrySinkAndSuppressionGate()
    {
        DpiHelper.Initialize(1.0, 1.0); // UpdateButtonScreenPosition scales the hit radius
        using var sp = BuildProvider();

        var settings = sp.GetRequiredService<EyeCtrlSettings>();
        var messenger = sp.GetRequiredService<IMessenger>();
        var registry = sp.GetRequiredService<IDwellDetectorRegistry>();
        var vm = sp.GetRequiredService<ToolbarViewModel>();

        // (1) Registry entry: the factory passed the registry singleton — dropping the argument
        // lands no entry and the toolbar silently stops receiving adaptation's dwell/geometry routing.
        var entries = registry.Registrations;
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Context).IsEqualTo(DwellContext.Toolbar);
        Check.That(entries[0].MinDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMinDwellMs);
        Check.That(entries[0].MaxDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMaxDwellMs);

        // (2) Sink reach: a real dwell driven through the resolved VM surfaces as a Toolbar
        // Activated outcome in the registered buffer.
        int pauses = 0;
        messenger.Register<PauseToggledMessage>(this, (_, _) => pauses++);
        vm.UpdateButtonScreenPosition("Pause", new PhysicalPoint(200, 200));
        long dwellUs = (settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        for (var t = 0L; t <= dwellUs; t += 30_000)
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(200, 200), t));

        Check.That(pauses).IsEqualTo(1);
        var buffer = sp.GetRequiredService<BufferingTelemetrySink>();
        var outcomes = buffer.Snapshot();
        Check.That(outcomes).HasSize(1);
        Check.That(outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(outcomes[0].Context).IsEqualTo(DwellContext.Toolbar);
        Check.That(outcomes[0].TargetId).IsEqualTo("Pause");

        // (3) Suppression delegate wired: with the gate forced suppressed, driving Reset past the
        // 0.40 freeze threshold engages SILENTLY — no visibility-true is ever sent and the rim-cue
        // property comes on. With the delegate dropped (null), the engine cannot consult the gate,
        // the panel is published, and these assertions fail.
        var gate = (SensitiveContextFeedbackGate)sp.GetRequiredService<IFeedbackGate>();
        var fakeDetector = new FakeSensitiveDetector { Active = true };
        typeof(SensitiveContextFeedbackGate).GetField("_detector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(gate, fakeDetector);
        Check.That(sp.GetRequiredService<IFeedbackGate>().IsFeedbackSuppressed).IsTrue();

        var visibility = new List<bool>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m.Visible));
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);

        // Timestamps continue past the Pause activation's cooldown (400 ms after dwellUs).
        vm.UpdateButtonScreenPosition("ResetAdaptation", new PhysicalPoint(900, 500));
        var resetCenter = new PhysicalPoint(900, 500);
        for (var t = 2_000_000L; t <= 2_350_000L; t += 30_000) // 350 ms = 0.5 × the 700 ms dwell
            vm.Receive(new GazeCursorMovedMessage(resetCenter, t));

        Check.That(visibility).IsEmpty();                 // suppressed at engage: the panel never existed
        Check.That(vm.ResetFrozenSilent).IsTrue();        // the rim cue is the only evidence
        Check.That(resets).IsEqualTo(0);                  // the freeze held: nothing fired

        // Provider disposal must unregister the VM from the messenger AND the registry — the
        // leak discipline this suite's remarks promise.
        sp.Dispose();
        Check.That(registry.Registrations).IsEmpty();
    }
}
