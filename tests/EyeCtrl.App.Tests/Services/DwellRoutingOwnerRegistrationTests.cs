namespace EyeCtrl.App.Tests.Services;

using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Direct-construction companions to <see cref="DwellRoutingRegistrationWiringTests"/>: each
/// XAML-free dwell owner registers itself on a <see cref="DwellDetectorRegistry"/> with the right
/// context and validated range the moment it is constructed — no DI, no container, just the owner
/// and a fresh registry. The implementation review found all five owner Register calls deletable
/// with the suite green; these facts pin three of them, and the DI wiring test pins the factory
/// lambdas that feed them.
/// </summary>
/// <remarks>
/// The ManualSelectionDwellService fact doubles as the applier-identity fold-in: it drives a
/// halving map through the registered entry's <c>Apply</c> and observes THE OWNER's dwell complete
/// at the halved threshold through its public surface (the completion message it sends), proving
/// the entry routes to that owner's live detector rather than to any delegate.
///
/// EmojiGridManager needs [StaFact] for the two StackPanels its constructor receives (WPF
/// DependencyObjects), but no Application resources: the constructor touches no XAML — the
/// category tabs it would build are created later, on Enter().
///
/// ExternalDwellService is covered by the DI guard instead of a direct-construction fact: its
/// constructor accepts a pipe name precisely so tests never bind the production one
/// (see ExternalDwellServiceTests), and resolving it through DI lands the same registration
/// without any pipe involvement at all.
/// </remarks>
[Collection("DpiHelper Tests (App)")]
public class DwellRoutingOwnerRegistrationTests
{
    [Fact]
    public void NoteManager_DirectConstruction_RegistersNotesContext_WithStandardRange()
    {
        var registry = new DwellDetectorRegistry();
        using var manager = new NoteManager(new WeakReferenceMessenger(), sink: null, dwellRegistry: registry);

        Check.That(registry.Registrations).HasSize(1);
        var entry = registry.Registrations[0];
        Check.That(entry.Context).IsEqualTo(DwellContext.Notes);
        Check.That(entry.MinDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMinDwellMs);
        Check.That(entry.MaxDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMaxDwellMs);
    }

    [StaFact]
    public void EmojiGridManager_DirectConstruction_RegistersEmojiContext_WithKeyboardRange()
    {
        var registry = new DwellDetectorRegistry();
        var manager = new EmojiGridManager(
            new StackPanel(), new StackPanel(),
            registerKeyVisual: (_, _, _, _) => { },
            getKeySize: () => 40.0,
            clearKeyState: () => { },
            sink: null,
            dwellRegistry: registry);

        Check.That(registry.Registrations).HasSize(1);
        var entry = registry.Registrations[0];
        Check.That(entry.Context).IsEqualTo(DwellContext.Emoji);
        // Keyboard range, not Standard: the category tabs read KeyboardKeyDwellMs, so the emoji
        // grid's validated span is the keyboard guard family [100, 1500].
        Check.That(entry.MinDwellMs).IsEqualTo(AdaptationDwellRanges.KeyboardMinDwellMs);
        Check.That(entry.MaxDwellMs).IsEqualTo(AdaptationDwellRanges.KeyboardMaxDwellMs);
    }

    [Fact]
    public void ManualSelectionDwellService_RegisteredApplier_DrivesTheOwningDetector()
    {
        DpiHelper.Initialize(1.0, 1.0); // the start handler scales radius/bounds by the global scale
        var settings = new EyeCtrlSettings
        {
            ManualSelection = new ManualSelectionSettings
            {
                CircleRadiusPixels = 200,
                MagnificationLevel = 2.0,
                ManualSelectionGracePeriodMs = 300
            },
            Dwell = new DwellSettings
            {
                FixationRadiusPixels = 60,
                ManualSelectionActionMenuDwellMs = 800 // the baseline the halving map composes against
            }
        };
        var messenger = new WeakReferenceMessenger();
        var interactionManager = new InteractionManager();
        interactionManager.SelectionMode = EyeCtrl.Core.Models.SelectionMode.ManualSelection;
        var registry = new DwellDetectorRegistry();
        using var service = new ManualSelectionDwellService(
            messenger,
            NullLogger<ManualSelectionDwellService>.Instance,
            settings,
            Dispatcher.CurrentDispatcher,
            interactionManager,
            sink: null,
            dwellRegistry: registry);

        // Per-session registration rides the start message...
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 200, 2.0, new PhysicalPoint(500, 500)));

        Check.That(registry.Registrations).HasSize(1);
        var entry = registry.Registrations[0];
        Check.That(entry.Context).IsEqualTo(DwellContext.ManualSelection);
        Check.That(entry.MinDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMinDwellMs);
        Check.That(entry.MaxDwellMs).IsEqualTo(AdaptationDwellRanges.StandardMaxDwellMs);

        // ...and the entry's applier must drive THIS service's detector: a halving map takes the
        // 800 ms baseline to 400 ms. If Apply were wired to anything but this detector, dwell
        // would still wait for the full 800 ms.
        entry.Apply(t => t / 2);

        ManualSelectionDwellCompletedMessage? completed = null;
        messenger.Register<ManualSelectionDwellCompletedMessage>(this, (r, m) => completed = m);

        // Enter ManualSelection-active state, then start the grace period (300 ms) with one sample.
        interactionManager.OnFixationStarted(new PhysicalPoint(500, 500));
        interactionManager.OnActivationButtonDwelled();
        var dwellPoint = new PhysicalPoint(510, 510); // well inside the 200 px bounds, on the target
        messenger.Send(new GazeCursorMovedMessage(dwellPoint, 100_000));
        // Grace over (350 ms elapsed); this sample enters the dwell target at t = 450 ms.
        messenger.Send(new GazeCursorMovedMessage(dwellPoint, 450_000));
        // 399 ms of dwell < halved 400 ms — must not complete. Against the 800 ms baseline this
        // would be less than half done; the check below is what proves the map landed at all.
        messenger.Send(new GazeCursorMovedMessage(dwellPoint, 849_000));
        Check.That(completed).IsNull();
        // Exactly 400 ms of dwell → completes.
        messenger.Send(new GazeCursorMovedMessage(dwellPoint, 850_000));
        Check.That(completed).IsNotNull();
        Check.That(completed!.PrecisePoint.X).IsCloseTo(dwellPoint.X, 0.005);
        Check.That(completed!.PrecisePoint.Y).IsCloseTo(dwellPoint.Y, 0.005);

        messenger.Unregister<ManualSelectionDwellCompletedMessage>(this);

        // End message removes the per-session entry (also pinned by the DI wiring test).
        messenger.Send(new EndManualSelectionMessage());
        Check.That(registry.Registrations).IsEmpty();
    }
}
