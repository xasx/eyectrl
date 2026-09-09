namespace EyeCtrl.ViewModels.Tests.Adaptation;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class OverlayTelemetryWiringTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    // Minimal concrete overlay that exposes target creation, to test the base-class (context, sink) threading.
    // internal + partial (not private): DwellOverlayViewModelBase implements IRecipient<GazeCursorMovedMessage>,
    // so the IMessengerRegisterAllGenerator emits a partial for this type; the generator's helper must be able
    // to reference it, which requires at least internal visibility. Mirrors TestOverlayVm in the sibling suite.
    internal sealed partial class ProbeOverlayViewModel : DwellOverlayViewModelBase
    {
        public ProbeOverlayViewModel(IMessenger messenger, IStruggleTelemetrySink sink)
            : base(messenger, dwellCooldownMs: 0, context: DwellContext.QuickActions, sink: sink)
        {
        }

        public void AddProbeTarget() =>
            DwellDetector.AddTarget(new DwellTarget("probe", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        protected override void OnTargetActivated(string targetId) { }
        protected override void OnTargetProgress(string targetId, double progress) { }
    }

    [Fact]
    public void BaseClass_ThreadsContextAndSink_IntoDwellDetector()
    {
        var sink = new RecordingSink();
        var vm = new ProbeOverlayViewModel(new WeakReferenceMessenger(), sink) { IsVisible = true };
        vm.AddProbeTarget();

        long t = 0;
        for (int i = 0; i < 9; i++) // 200 ms dwell at 30 ms/sample
        {
            vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), t));
            t += 30_000;
        }

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.QuickActions);
    }

    [Fact]
    public void EveryDwellOverlaySubclass_HasAnAssignedContext()
    {
        // Intended context for every concrete DwellOverlayViewModelBase subclass. ScrollOverlay's
        // ctor wiring lands in plan 1a-③; its entry here documents intent and keeps this guard green.
        var expected = new Dictionary<string, DwellContext>
        {
            [nameof(ActionMenuViewModel)] = DwellContext.ActionMenu,
            [nameof(ActivationButtonViewModel)] = DwellContext.ActivationButton,
            [nameof(DragTrailViewModel)] = DwellContext.DragTrail,
            [nameof(EdgeButtonBarViewModel)] = DwellContext.EdgeButtonBar,
            [nameof(GlassBackdropViewModel)] = DwellContext.Backdrop,
            [nameof(ManualSelectionOverlayViewModel)] = DwellContext.ManualSelection,
            [nameof(ModesMenuViewModel)] = DwellContext.Toolbar,
            [nameof(QuickActionsGridViewModel)] = DwellContext.QuickActions,
            [nameof(ScrollOverlayViewModel)] = DwellContext.Scroll,
            [nameof(TaskSwitcherViewModel)] = DwellContext.TaskSwitcher,
            [nameof(ToolbarViewModel)] = DwellContext.Toolbar,
        };

        var subclasses = typeof(DwellOverlayViewModelBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DwellOverlayViewModelBase).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        Check.That(subclasses).Not.IsEmpty();
        foreach (var name in subclasses)
            Check.That(expected.ContainsKey(name)).IsTrue();
    }

    [Fact]
    public void EveryDwellOverlaySubclass_TakesTheSinkOnItsSingleConstructor()
    {
        // Companion to the App-side TelemetrySinkRegistrationTests. Microsoft.Extensions.DependencyInjection
        // picks the greediest constructor it can satisfy — so "exactly one public constructor" + "that
        // constructor declares IStruggleTelemetrySink" + "the sink is registered" together guarantee
        // injection. Without this guard, adding a second constructor or dropping the parameter would
        // silently fall back to the `sink = null` default: telemetry stops with a fully green suite.
        // The same guarantee covers IDwellDetectorRegistry (dwell routing): dropping that parameter
        // would silently fall back to `dwellRegistry = null` and the surface would never be routed.
        //
        // ScrollOverlayViewModel is exempt by design: it overrides Receive without ever calling
        // DwellDetector.Update, so it cannot emit (scroll-stop dwell lives on EdgeButtonBarViewModel).
        // DwellContext.Scroll is reserved-but-silent. Listing it explicitly means un-exempting it is a
        // deliberate edit rather than an oversight.
        var exempt = new HashSet<string> { nameof(ScrollOverlayViewModel) };

        var subclasses = typeof(DwellOverlayViewModelBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(DwellOverlayViewModelBase).IsAssignableFrom(t))
            .Where(t => !exempt.Contains(t.Name))
            .ToList();

        Check.That(subclasses).Not.IsEmpty();
        foreach (var type in subclasses)
        {
            var ctors = type.GetConstructors();
            Check.WithCustomMessage($"{type.Name} must have exactly one public constructor for DI to "
                    + "unambiguously select the sink-taking overload")
                .That(ctors).HasSize(1);
            Check.WithCustomMessage($"{type.Name} must accept IStruggleTelemetrySink so DI can inject it")
                .That(ctors[0].GetParameters().Any(p => p.ParameterType == typeof(IStruggleTelemetrySink)))
                .IsTrue();
            Check.WithCustomMessage($"{type.Name} must accept IDwellDetectorRegistry so DI can inject it")
                .That(ctors[0].GetParameters().Any(p => p.ParameterType == typeof(IDwellDetectorRegistry)))
                .IsTrue();
        }
    }

    [Fact]
    public void ActivationButtonViewModel_EmitsOutcome_TaggedActivationButtonContext()
    {
        // A real subclass, end-to-end: proves the wired context literal is correct. The drift guard
        // above only checks that every subclass has *an* entry, not that its `: base(...)` literal
        // matches — this closes that gap for one representative surface (L3).
        var sink = new RecordingSink();
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 200;
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(settings, messenger, sink);

        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        Check.That(vm.IsVisible).IsTrue();
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 300), 0));
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 300), 200_000));

        Check.That(sink.Outcomes).Not.IsEmpty();
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(sink.Outcomes[0].Context).IsEqualTo(DwellContext.ActivationButton);
    }
}
