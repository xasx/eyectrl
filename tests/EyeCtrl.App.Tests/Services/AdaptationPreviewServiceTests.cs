namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The adaptation preview panel's service layer (Task 10): placement geometry read by the service
/// itself (P10), bounds publishes per P2/P11, the named-holder auto-hide suspension (P8), the
/// activation-participant lifecycle (P9/R2-2) and the D17 collision stub's show-time gate.
/// Headless throughout — the window is behind <see cref="IPreviewPanel"/> (a fake here; the real
/// window is construction-tested in AdaptationPreviewWindowTests).
/// </summary>
/// <remarks>
/// <see cref="Composition_DisabledStart_ConstructsNoPreviewWindow"/> builds the real DI graph, so
/// this class sits in the <c>WpfApplication</c> collection: that collection is
/// <c>DisableParallelization</c>, which keeps the window-construction counter it reads from being
/// bumped concurrently by AdaptationPreviewWindowTests.
/// </remarks>
[Collection("WpfApplication")]
public class AdaptationPreviewServiceTests
{
    private const int Engagement = 7;

    private sealed class ManualClock
    {
        public long Now = 10_000;
        public long Tick() => Now;
    }

    private sealed class StubCollision : IOverlayCollision
    {
        private readonly bool _clear;
        public int Calls;

        public StubCollision(bool clear) => _clear = clear;

        public bool IsClear(PhysicalRect panelRect)
        {
            Calls++;
            return _clear;
        }

        public event Action? Changed { add { } remove { } }
    }

    /// <summary>
    /// Task 11's mutable collision fake: a single blocked consumer rect (null ⇒ clear) plus the
    /// raisable <see cref="IOverlayCollision.Changed"/> trigger the real inventory fires on chrome
    /// changes. Records every rect it was asked about so tests can pin WHICH rect the service fed
    /// the gate — the T10-review carry: the REAL measured bounds, never the DIP-numbered nominal.
    /// </summary>
    private sealed class RectCollision : IOverlayCollision
    {
        public PhysicalRect? Blocked;
        public List<PhysicalRect> Queried { get; } = new();
        public PhysicalRect LastQueried => Queried[^1];
        public event Action? Changed;

        public bool IsClear(PhysicalRect panelRect)
        {
            Queried.Add(panelRect);
            return Blocked is null || !Blocked.Value.IntersectsWith(panelRect);
        }

        public void Flip() => Changed?.Invoke();
    }

    private sealed class FakePreviewPanel : IPreviewPanel
    {
        public List<AdaptationPreviewSnapshot> Snapshots { get; } = new();
        public List<PhysicalPoint> Positions { get; } = new();
        public List<string> Edges { get; } = new();
        public int ShowCount;
        public int HideCount;
        public bool IsShown;
        public bool ThrowOnShow;

        /// <summary>The fake's DIP size — the service's collision estimate and the VM's union read
        /// it back through <see cref="BoundsChanged"/> converted at <see cref="Scale"/>, exactly the
        /// way the real window converts at its DPI.</summary>
        public (double W, double H) DipSize = (180, 120);
        public double Scale = 2.0;

        public event Action<PhysicalRect?>? BoundsChanged;

        /// <summary>Test seam: the REAL window reports its bounds asynchronously out of layout,
        /// not synchronously out of ShowPanel — set this to open that window in a test and close
        /// it with <see cref="ReportBounds"/>.</summary>
        public bool DeferBoundsReport;

        public void ShowPanel(AdaptationPreviewSnapshot snapshot, PhysicalPoint positionPhysical, string dockedEdge)
        {
            if (ThrowOnShow)
                throw new InvalidOperationException("panel boom");
            Snapshots.Add(snapshot);
            Positions.Add(positionPhysical);
            Edges.Add(dockedEdge);
            ShowCount++;
            IsShown = true;
            if (!DeferBoundsReport)
                PublishBounds();
        }

        /// <summary>Test seam: the async layout report.</summary>
        public void ReportBounds() => PublishBounds();

        public void HidePanel()
        {
            HideCount++;
            IsShown = false;
            BoundsChanged?.Invoke(null);
        }

        /// <summary>Test seam: the window's SizeChanged path — re-reports the live bounds.</summary>
        public void Resize((double W, double H) dipSize)
        {
            DipSize = dipSize;
            if (IsShown)
                PublishBounds();
        }

        private void PublishBounds()
        {
            var p = Positions[^1];
            BoundsChanged?.Invoke(new PhysicalRect(p.X, p.Y, DipSize.W * Scale, DipSize.H * Scale));
        }
    }

    private sealed class FakeToolbarOverlay : IToolbarOverlay
    {
        public bool IsOnScreen { get; set; } = true;
        public PhysicalRect Bounds { get; set; } = new(100, 0, 800, 140);

        public bool IsVisible => true;
        public bool TargetsInitialized => true;
        public bool IsSliding => false;
        public double ExtentDip => 140;

        public void Show() { }
        public void Hide() { }
        public void Initialize(double buttonSize, int dwellTimeMs, bool includeReset) { }
        public void SetMode(bool isDocked, string edge) { }
        public void SetSelectionModeState(SelectionMode mode) { }
        public void SetPausedState(bool isPaused) { }
        public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds) { }
        public void BindToViewModel() { }
        public void ShowAtRest() { }
        public void ShowThenSlideIn() { }
        public void SlideOutThenHide(Action onHidden) => onHidden();
        public PhysicalRect GetPhysicalBounds() => Bounds;
    }

    // internal (not private): the MVVM toolkit's RegisterAll source generator emits its extension
    // outside the nested type, so a private recipient does not compile.
    internal sealed class Recorder :
        IRecipient<AdaptationPreviewBoundsMessage>,
        IRecipient<PreviewPanelActiveMessage>
    {
        public List<AdaptationPreviewBoundsMessage> Bounds { get; } = new();
        public List<PreviewPanelActiveMessage> Active { get; } = new();

        public void Receive(AdaptationPreviewBoundsMessage message) => Bounds.Add(message);
        public void Receive(PreviewPanelActiveMessage message) => Active.Add(message);
    }

    private sealed class Harness
    {
        public readonly EyeCtrlSettings Settings = new();
        public readonly EffectiveSettingsProvider Provider;
        public readonly FakePreviewPanel Panel = new();
        public readonly FakeToolbarOverlay Toolbar = new();
        public readonly ToolbarLifecycleCoordinator Coordinator = new(new ModalInteractionState());
        public readonly ToolbarLifecycleBridge Bridge = new();
        public readonly WeakReferenceMessenger Messenger;
        public readonly ManualClock Clock = new();
        public readonly PhysicalRect WorkArea = new(0, 0, 1920, 1080);
        public readonly IOverlayCollision Collision;
        public readonly Recorder Recorder = new();
        public readonly AdaptationPreviewService Service;

        /// <param name="adaptationEnabled">Drives the settings flag (not the activation wiring).</param>
        /// <param name="collision">Task 11: the collision seam under test — defaults to the
        /// always-clear stub, exactly the shape production registered before Task 11.</param>
        /// <param name="messenger">Task 11: share a messenger with other participants (the
        /// coordinator-path auto-show test); defaults to a fresh one.</param>
        public Harness(
            bool adaptationEnabled = true,
            IOverlayCollision? collision = null,
            WeakReferenceMessenger? messenger = null)
        {
            Settings.Adaptation.Enabled = adaptationEnabled;
            Settings.Toolbar.IsDocked = true;
            Settings.Toolbar.DockedEdge = "Top";
            Provider = new EffectiveSettingsProvider(Settings.Adaptation);
            Coordinator.InjectAutoHideForTesting(NewAutoHide());
            Bridge.Attach(Coordinator);
            Messenger = messenger ?? new WeakReferenceMessenger();
            Collision = collision ?? new StubCollision(clear: true);
            Service = new AdaptationPreviewService(
                Settings,
                Provider,
                toolbar: () => Toolbar,
                panel: () => Panel,
                collision: Collision,
                suspension: Bridge,
                Messenger,
                workArea: () => WorkArea,
                // The service's DPI-scale seam tracks the fake panel's own scale, exactly the way
                // the real window's DPI drives both (Task 10 review carry (a)'s test shape).
                dpiScale: () => Panel.Scale,
                nowMs: Clock.Tick,
                logger: null);
            Messenger.RegisterAll(Recorder);
            Service.Start();
        }

        public void ShowPreview(int engagement = Engagement) =>
            Messenger.Send(new AdaptationPreviewVisibilityMessage(
                true, engagement, new PhysicalPoint(700, 70)));

        public void HidePreview(int engagement = Engagement) =>
            Messenger.Send(new AdaptationPreviewVisibilityMessage(
                false, engagement, new PhysicalPoint(700, 70)));
    }

    // ── rev 1: content, bounds, clean end, throttle, contained failure ──

    [Fact]
    public void Show_BuildsSnapshotFromProvider_PublishesRect_SuspendsAutoHide()
    {
        var h = new Harness();
        h.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.3, tighteningFill: 0);

        h.ShowPreview();

        Check.That(h.Panel.ShowCount).IsEqualTo(1);
        Check.That(h.Panel.Snapshots).HasSize(1);
        Check.That(h.Panel.Snapshots[0].Rows[0].TotalFill).IsCloseTo(0.3, 1e-9);
        Check.That(h.Panel.Edges[0]).IsEqualTo("Top");
        // The rect publish is the panel's BoundsChanged (P2: the live bounds, not the estimate).
        Check.That(h.Recorder.Bounds).HasSize(1);
        Check.That(h.Recorder.Bounds[0].EngagementId).IsEqualTo(Engagement);
        Check.That(h.Recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.Rect);
        // The fake converts its DIP size at its scale — 180x120 DIP at 2.0 ⇒ 360x240 px.
        Check.That(h.Recorder.Bounds[0].PanelBounds!.Value.Width).IsEqualTo(360);
        Check.That(h.Recorder.Bounds[0].PanelBounds!.Value.Height).IsEqualTo(240);
        Check.That(h.Recorder.Active).HasSize(1);
        Check.That(h.Recorder.Active[0].Active).IsTrue();
        Check.That(h.Coordinator.IsAutoHideSuspended).IsTrue();
    }

    [Fact]
    public void Show_WithNoFills_PassesAnythingActiveFalse()
    {
        var h = new Harness();

        h.ShowPreview();

        Check.That(h.Panel.Snapshots[0].AnythingActive).IsFalse();
        Check.That(h.Panel.Snapshots[0].Rows).HasSize(4);
    }

    [Fact]
    public void VisibilityFalse_HidesPanel_ReleasesSuspension_PublishesNoBounds()
    {
        var h = new Harness();
        h.ShowPreview();
        var boundsBefore = h.Recorder.Bounds.Count;

        h.HidePreview();

        Check.That(h.Panel.IsShown).IsFalse();
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        Check.That(h.Recorder.Active[^1].Active).IsFalse();
        // P2: the engine hid the panel itself; the service must not publish any bounds for the leave.
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore);
    }

    [Fact]
    public void StaleSizeChange_AfterTheHide_IsNotRepublished()
    {
        var h = new Harness();
        h.ShowPreview();
        h.HidePreview();
        var boundsBefore = h.Recorder.Bounds.Count;

        h.Panel.Resize((200, 140)); // the async SizeChanged that lands after the hide

        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore);
    }

    [Fact]
    public void ProviderChanged_TwiceWithinThrottle_RefreshesOnce()
    {
        var h = new Harness();
        h.ShowPreview();
        Check.That(h.Panel.ShowCount).IsEqualTo(1);

        h.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.35, 0);
        h.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.40, 0);

        Check.That(h.Panel.ShowCount).IsEqualTo(2); // show + ONE throttled refresh

        h.Clock.Now += AdaptationPreviewService.RefreshThrottleMs;
        h.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.45, 0);
        Check.That(h.Panel.ShowCount).IsEqualTo(3);
    }

    [Fact]
    public void ProviderChanged_WhilePanelDown_DoesNothing()
    {
        var h = new Harness();
        h.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.3, 0);

        Check.That(h.Panel.ShowCount).IsEqualTo(0);
    }

    [Fact]
    public void ThrowingPanel_PublishesNoPanel_DoesNotCrash_DoesNotHoldSuspension()
    {
        var h = new Harness();
        h.Panel.ThrowOnShow = true;

        Check.ThatCode(() => h.ShowPreview()).Not.ThrowsAny();

        Check.That(h.Panel.IsShown).IsFalse();
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        Check.That(h.Recorder.Bounds).HasSize(1);
        Check.That(h.Recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
        Check.That(h.Recorder.Bounds[0].PanelBounds).IsNull();
    }

    // ── P8: named-holder suspension, both orders ──

    [Fact]
    public void Suspension_SettingsThenPreview_PreviewRelease_KeepsSuspended()
    {
        var c = new ToolbarLifecycleCoordinator(new ModalInteractionState());
        c.InjectAutoHideForTesting(NewAutoHide());

        c.SetAutoHideSuspended("settings", true);
        c.SetAutoHideSuspended(AdaptationPreviewService.AutoHideHolder, true);
        c.SetAutoHideSuspended(AdaptationPreviewService.AutoHideHolder, false);

        Check.That(c.IsAutoHideSuspended).IsTrue(); // settings still holds its own suspension

        c.SetAutoHideSuspended("settings", false);
        Check.That(c.IsAutoHideSuspended).IsFalse();
    }

    [Fact]
    public void Suspension_PanelUp_SettingsOpensAndCloses_StillSuspended()
    {
        var h = new Harness();
        h.ShowPreview();

        h.Coordinator.SetAutoHideSuspended("settings", true);
        h.Coordinator.SetAutoHideSuspended("settings", false);

        Check.That(h.Coordinator.IsAutoHideSuspended).IsTrue(); // the panel's holder survives the settings round-trip

        h.HidePreview();
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
    }

    // ── P9: nothing is constructed while adaptation is disabled ──

    [Fact]
    public void Composition_DisabledStart_ConstructsNoPreviewWindow()
    {
        var appData = TestAppDataDir.Create("adaptation-preview-p9");
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = false;

        var before = AdaptationPreviewWindow.ConstructionCount;
        using (var sp = AppBootstrapper.BuildServiceProvider(
                   settings,
                   settingsFilePath: Path.Combine(appData, "settings.json"),
                   dispatcher: Dispatcher.CurrentDispatcher,
                   isTestMode: true,
                   appDataDir: appData))
        {
            AppBootstrapper.ResolveEagerServices(sp);
            sp.GetRequiredService<AdaptationActivation>().StartCore(null);

            Check.WithCustomMessage(
                    "Resolving + starting the app graph with adaptation disabled must not construct "
                    + "the preview window (P9: no overlay HWNDs while the feature is off).")
                .That(AdaptationPreviewWindow.ConstructionCount - before).IsEqualTo(0);

            // The service's own resolution stays cheap too: the window hangs behind a lazy factory
            // resolved only at Start (the first enable edge).
            _ = sp.GetRequiredService<AdaptationPreviewService>();
            Check.That(AdaptationPreviewWindow.ConstructionCount - before).IsEqualTo(0);
        }
    }

    // ── R2-2: self-contained teardown on the disable edge, restartable after ──

    [Fact]
    public void DisableMidPreview_TeardownIsSelfContained_ThenEnableShowsAgain()
    {
        var h = new Harness();
        // The REAL PropertyChanged path drives the service's Start/Dispose pair (the production
        // AdaptationActivation wiring resolves the service on the enable edge and disposes it on
        // the disable edge; the internal testing ctor drives those two effects without a container).
        var activation = new AdaptationActivation(
            h.Settings.Adaptation, _ => h.Service.Start(), () => h.Service.Dispose());
        activation.StartCore(null); // already enabled ⇒ Start (idempotent over the harness's own)

        h.ShowPreview(Engagement);
        Check.That(h.Panel.IsShown).IsTrue();

        h.Settings.Adaptation.Enabled = false; // the disable edge — NO visibility-false is ever sent

        Check.That(h.Panel.IsShown).IsFalse();
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        Check.That(h.Recorder.Bounds).HasElementThatMatches(
            m => m.EngagementId == Engagement && m.Kind == PreviewBoundsKind.NoPanel);
        Check.That(h.Recorder.Active[^1].Active).IsFalse();

        // A late VM hide for the dead engagement must be a harmless no-op.
        Check.ThatCode(() => h.HidePreview(Engagement)).Not.ThrowsAny();

        // Restartable: enable → disable → enable, and the panel shows again.
        h.Settings.Adaptation.Enabled = true;
        h.Settings.Adaptation.Enabled = false;
        h.Settings.Adaptation.Enabled = true;
        var showsBefore = h.Panel.ShowCount;

        h.ShowPreview(Engagement + 1);

        Check.That(h.Panel.ShowCount).IsEqualTo(showsBefore + 1);
        Check.That(h.Panel.IsShown).IsTrue();
        Check.That(h.Coordinator.IsAutoHideSuspended).IsTrue();
        activation.Dispose();
        h.Service.Dispose();
    }

    // ── P10: the service reads geometry itself; floating ⇒ no panel ──

    [Fact]
    public void FloatingToolbar_NoPanel_NoShow()
    {
        var h = new Harness();
        h.Settings.Toolbar.IsDocked = false;

        h.ShowPreview();

        Check.That(h.Panel.ShowCount).IsEqualTo(0);
        Check.That(h.Recorder.Bounds).HasSize(1);
        Check.That(h.Recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
    }

    [Fact]
    public void OffscreenToolbar_NoPanel()
    {
        var h = new Harness();
        h.Toolbar.IsOnScreen = false;

        h.ShowPreview();

        Check.That(h.Panel.ShowCount).IsEqualTo(0);
        Check.That(h.Recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
    }

    // ── P11: show-time collision ⇒ NoPanel immediately ──

    [Fact]
    public void ShowTimeCollision_NoPanelImmediately_NoShow()
    {
        // Not the Harness: this one needs the not-clear stub (P11), and the stub's IsClear must be
        // proven to have actually run at show time — Task 11 replaces it with the real inventory.
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var panel = new FakePreviewPanel();
        var coordinator = new ToolbarLifecycleCoordinator(new ModalInteractionState());
        coordinator.InjectAutoHideForTesting(NewAutoHide());
        var bridge = new ToolbarLifecycleBridge();
        bridge.Attach(coordinator);
        var messenger = new WeakReferenceMessenger();
        var recorder = new Recorder();
        var collision = new StubCollision(clear: false);
        using var service = new AdaptationPreviewService(
            settings, provider, () => new FakeToolbarOverlay(), () => panel, collision, bridge, messenger,
            () => new PhysicalRect(0, 0, 1920, 1080), dpiScale: null, new ManualClock().Tick, null);
        messenger.RegisterAll(recorder);
        service.Start();

        messenger.Send(new AdaptationPreviewVisibilityMessage(true, 5, new PhysicalPoint(700, 70)));

        Check.That(panel.ShowCount).IsEqualTo(0);
        Check.That(recorder.Bounds).HasSize(1);
        Check.That(recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
        Check.That(collision.Calls).IsEqualTo(1); // the check actually ran at show time
        Check.That(coordinator.IsAutoHideSuspended).IsFalse();
    }

    // ── placement (§3.3): pure math, physical px ──

    [Fact]
    public void Placement_TopDock_BeyondTheStripEnd_IntoTheScreen()
    {
        var toolbar = new PhysicalRect(100, 8, 800, 140); // Reset is the last (rightmost) button
        var rect = AdaptationPreviewService.ComputePanelPlacement(
            new PhysicalPoint(820, 78), "Top", toolbar, 360, 240, new PhysicalRect(0, 0, 1920, 1040));

        Check.That(rect.X).IsEqualTo(toolbar.X + toolbar.Width + AdaptationPreviewService.AnchorGapPx);
        Check.That(rect.Y).IsEqualTo(toolbar.Y);
    }

    [Fact]
    public void Placement_LeftDock_BelowTheStripEnd_RightOfIt()
    {
        // A short vertical strip, so the panel below its end still fits the work area (the clamp
        // test below covers the does-not-fit case).
        var toolbar = new PhysicalRect(0, 100, 140, 300); // vertical strip; Reset is the last button
        var rect = AdaptationPreviewService.ComputePanelPlacement(
            new PhysicalPoint(70, 320), "Left", toolbar, 360, 240, new PhysicalRect(0, 0, 1920, 1040));

        Check.That(rect.X).IsEqualTo(toolbar.X);
        Check.That(rect.Y).IsEqualTo(toolbar.Y + toolbar.Height + AdaptationPreviewService.AnchorGapPx);
    }

    [Fact]
    public void Placement_ClampsIntoTheWorkArea()
    {
        var toolbar = new PhysicalRect(1600, 8, 300, 140); // near the right edge: 1600+300+16 overflows 1920
        var rect = AdaptationPreviewService.ComputePanelPlacement(
            new PhysicalPoint(1850, 78), "Top", toolbar, 360, 240, new PhysicalRect(0, 0, 1920, 1040));

        Check.That(rect.X + rect.Width).IsLessOrEqualThan(1920);
        Check.That(rect.X).IsGreaterOrEqualThan(0);
    }

    [Fact]
    public void Placement_DegenerateToolbar_FallsBackToResetCenter()
    {
        var rect = AdaptationPreviewService.ComputePanelPlacement(
            new PhysicalPoint(700, 70), "Top", new PhysicalRect(700, 70, 0, 0), 360, 240,
            new PhysicalRect(0, 0, 1920, 1040));

        Check.That(rect.X).IsStrictlyGreaterThan(700); // beyond Reset's centre, not snapped to the origin
        Check.That(rect.Y).IsGreaterOrEqualThan(0);
    }

    // ── Task 11: D17 — physical collision sizes, continuous re-check, HideAsPolicy pinned ──

    [Fact]
    public void ShowTimeCollision_FirstShowEstimate_IsPhysicalDipTimesScale()
    {
        // T10-review carry (a): before the first layout the candidate's size is the nominal DIP
        // size × the DPI scale, never the bare DIP numbers (which under-cover at scale > 1: a
        // 200-DIP-wide panel is 400 physical px at 2.0, and a consumer sitting past nominal-360
        // from the anchor would be cleared by the DIP-numbered estimate). Panel: 200×150 DIP at
        // scale 2.0 ⇒ the first-show candidate is 400×300 px; the consumer starts where the
        // nominal 360-wide rect ends (touching, hence clear pre-fix) — the physical 400-wide
        // rect overlaps it.
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        // Toolbar (100,0,800,140) Top-docked ⇒ the candidate anchors at x = 916. Nominal-as-DIP
        // ends at 1276; the consumer occupies [1276, 1376).
        collision.Blocked = new PhysicalRect(1276, 0, 100, 100);

        h.ShowPreview();

        Check.WithCustomMessage(
                "The show-time gate must clear the PHYSICAL candidate (360 DIP × 2.0 = 720 px " +
                "wide), so a consumer at the DIP-numbered estimate's edge is a collision.")
            .That(h.Panel.ShowCount).IsEqualTo(0);
        Check.That(h.Recorder.Bounds).HasSize(1);
        Check.That(h.Recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
        Check.That(collision.Queried[0].Width).IsEqualTo(AdaptationPreviewService.NominalWidthDip * 2.0);
        Check.That(collision.Queried[0].Height).IsEqualTo(AdaptationPreviewService.NominalHeightDip * 2.0);
    }

    [Fact]
    public void ChromeChanged_NewCollisionOnTheMeasuredPanelRect_HidesKeepLast()
    {
        // The continuous re-check's core: a consumer appears over the panel AFTER the show, the
        // trigger fires, and the service intersects the REAL measured rect (BoundsChanged's
        // 400×300, position included) — not the nominal estimate.
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;

        h.ShowPreview();
        Check.That(h.Panel.IsShown).IsTrue();
        var boundsBefore = h.Recorder.Bounds.Count;

        // The keyboard pops up over the panel's right side.
        collision.Blocked = new PhysicalRect(1200, 0, 200, 200);
        collision.Flip();

        Check.That(h.Panel.IsShown).IsFalse();
        Check.That(h.Panel.HideCount).IsEqualTo(1);
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        Check.That(h.Recorder.Active[^1].Active).IsFalse();
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore + 1);
        Check.That(h.Recorder.Bounds[^1].EngagementId).IsEqualTo(Engagement);
        Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.HiddenKeepLast);
        Check.That(h.Recorder.Bounds[^1].PanelBounds).IsNull();
        // T10-review carry (a), re-check half: the rect handed to the gate is the last MEASURED
        // one (position 916,0 — the placement — with the panel's real 400×300), not a re-derived
        // nominal candidate.
        Check.That(collision.LastQueried.X).IsEqualTo(916);
        Check.That(collision.LastQueried.Y).IsEqualTo(0);
        Check.That(collision.LastQueried.Width).IsEqualTo(400);
        Check.That(collision.LastQueried.Height).IsEqualTo(300);
    }

    [Fact]
    public void ChromeChanged_StillClear_PanelStaysUp_NoBoundsRepublish()
    {
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        h.ShowPreview();
        var boundsBefore = h.Recorder.Bounds.Count;

        collision.Flip(); // a chrome change that lands nowhere near the panel

        Check.That(h.Panel.IsShown).IsTrue();
        Check.That(h.Panel.HideCount).IsEqualTo(0);
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore);
    }

    [Fact]
    public void ChromeChanged_WhilePanelDown_IsANoOp()
    {
        var collision = new RectCollision { Blocked = new PhysicalRect(0, 0, 4000, 4000) };
        var h = new Harness(collision: collision);

        collision.Flip(); // no engagement, nothing shown — the trigger must not publish

        Check.That(h.Panel.ShowCount).IsEqualTo(0);
        Check.That(h.Recorder.Bounds).IsEmpty();
    }

    [Fact]
    public void ProviderChanged_CollisionFlippedSinceShow_HidesKeepLast()
    {
        // T10-review carry (b): the implemented-but-untested refresh-path HideAsPolicy. A
        // collision source flips between the show and a provider.Changed: the throttled refresh
        // re-runs the gate on its re-derived candidate, finds the consumer, and the panel comes
        // down as a POLICY hide — HiddenKeepLast published with the LIVE engagement id (the VM
        // keeps the last rect in its union; T5's rule) — suspension released, offers input freed.
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        h.ShowPreview();
        var showsBefore = h.Panel.ShowCount;
        var boundsBefore = h.Recorder.Bounds.Count;

        collision.Blocked = new PhysicalRect(1200, 0, 200, 200);
        h.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.35, 0);

        Check.That(h.Panel.IsShown).IsFalse();
        Check.That(h.Panel.ShowCount).IsEqualTo(showsBefore); // hidden, not re-shown elsewhere
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        Check.That(h.Recorder.Active[^1].Active).IsFalse();
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore + 1);
        Check.That(h.Recorder.Bounds[^1].EngagementId).IsEqualTo(Engagement);
        Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.HiddenKeepLast);
        Check.That(h.Recorder.Bounds[^1].PanelBounds).IsNull();
    }

    [Fact]
    public void PanelResize_GrowsIntoAConsumerZone_HidesKeepLast()
    {
        // The re-check's second trigger (the panel's own SizeChanged, T10's BoundsChanged): a
        // refresh that grows the panel over a consumer is caught against the NEW measured rect.
        // Rect publishes as Rect first, then the hide publishes HiddenKeepLast — the union keeps
        // the last rect (T5).
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        h.ShowPreview();
        var boundsBefore = h.Recorder.Bounds.Count;
        // Current panel [916..1316)×[0..300); the growth reaches [916..1416). A consumer inside
        // ONLY the grown region: [1320..1370).
        collision.Blocked = new PhysicalRect(1320, 0, 50, 50);

        h.Panel.Resize((250, 160)); // 500×320 px at scale 2.0 — Rect published, then the hide

        Check.That(h.Panel.IsShown).IsFalse();
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore + 2);
        Check.That(h.Recorder.Bounds[^2].Kind).IsEqualTo(PreviewBoundsKind.Rect);
        Check.That(h.Recorder.Bounds[^2].PanelBounds!.Value.Width).IsEqualTo(500);
        Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.HiddenKeepLast);
        Check.That(h.Recorder.Bounds[^1].EngagementId).IsEqualTo(Engagement);
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
    }

    [Fact]
    public void KeyboardOnEdgeAtShow_SecondEngagement_GatesOnTheMeasuredSize_NoPanel()
    {
        // The rev-1 "keyboard-on-edge ⇒ no panel" pin, on the engagement that HAS a measured size
        // (the half the estimate test above cannot reach): engagement 1 shows and measures the
        // panel (200×150 DIP at 2.0 ⇒ 400×300 px); the keyboard then docks onto the toolbar's own
        // Top edge, its strip covering the anchor slot; the show-time gate on engagement 2 must
        // run on the MEASURED 400×300 — never a re-derived nominal candidate (carry (a)'s show
        // half: last-known measured bounds are the collision size once they exist). With a fake
        // inventory rect standing in for the keyboard's chrome (the real-inventory mapping — a
        // visible, laid-out keyboard contributes exactly its TryGetChromeBounds rect — is pinned
        // in GazeConsumerInventoryTests).
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        h.ShowPreview();
        Check.That(h.Panel.IsShown).IsTrue();
        h.HidePreview();

        // The keyboard docks onto the Top edge: its strip starts right of the toolbar's end and
        // covers the panel's anchor slot [916..1316)×[0..300).
        collision.Blocked = new PhysicalRect(1000, 0, 600, 300);

        h.ShowPreview(Engagement + 1);

        Check.That(h.Panel.ShowCount).IsEqualTo(1); // engagement 1 only — no second show
        Check.That(h.Recorder.Bounds[^1].EngagementId).IsEqualTo(Engagement + 1);
        Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
        // THE discriminator: the candidate handed to the gate was the measured 400×300, not the
        // 720×520 nominal×scale estimate engagement 1 used.
        Check.That(collision.Queried[^1].Width).IsEqualTo(400);
        Check.That(collision.Queried[^1].Height).IsEqualTo(300);
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
    }

    [Fact]
    public void SideDockWithBottomKeyboard_ClampedCornerPanel_NoPanelImmediately()
    {
        // P16's show-time half: a toolbar docked on a SIDE near the screen bottom anchors the
        // panel below the strip's end, where the work-area clamp pushes it into the BOTTOM CORNER
        // — exactly the strip a bottom-docked keyboard occupies. A fake inventory rect modelling
        // that keyboard (the real-inventory half lives in GazeConsumerInventoryTests) makes the
        // clamped candidate a collision ⇒ NoPanel immediately: no show, no suspension held, no
        // 500 ms unknown wait. The queried-rect assertions pin that the CLAMPED corner rect is
        // what the gate judged — the clamp is part of the collision input, not an afterthought.
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Settings.Toolbar.DockedEdge = "Left";
        h.Toolbar.Bounds = new PhysicalRect(0, 700, 140, 200); // vertical strip, ends at y=900
        // First-show estimate 360×260 DIP × 2.0 ⇒ 720×520 px; anchored at y=916, clamped to
        // y = 1080-520 = 560 ⇒ the panel is the bottom-left corner [0..720)×[560..1080).
        collision.Blocked = new PhysicalRect(0, 880, 1920, 200); // the bottom-docked keyboard

        h.ShowPreview();

        Check.That(h.Panel.ShowCount).IsEqualTo(0);
        Check.That(h.Recorder.Bounds).HasSize(1);
        Check.That(h.Recorder.Bounds[0].EngagementId).IsEqualTo(Engagement);
        Check.That(h.Recorder.Bounds[0].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);
        Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        Check.That(collision.Queried[0].X).IsEqualTo(0);
        Check.That(collision.Queried[0].Y).IsEqualTo(560);
        Check.That(collision.Queried[0].Width).IsEqualTo(720);
        Check.That(collision.Queried[0].Height).IsEqualTo(520);
    }

    [Fact]
    public void ChromeChanged_BetweenShowAndFirstBoundsReport_IgnoresTheStaleRect()
    {
        // The prior-rect trap: the real window reports bounds ASYNC out of layout, so a chrome
        // Changed can fire after ShowPanel but before the fresh rect exists. The re-check's
        // contract (its own guard comment): "shown but not yet measured — the show-time candidate
        // gate owns this window" — it must NOT judge the new show by the DEAD engagement's rect.
        // Here engagement 1 measures the panel at [916..1316)×[0..300); the toolbar then moves and
        // engagement 2 re-shows at [916..1316)×[400..700), with a consumer that collides ONLY
        // with the stale engagement-1 rect flipping Changed inside the window.
        var collision = new RectCollision();
        var h = new Harness(collision: collision);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        h.ShowPreview();
        Check.That(h.Panel.IsShown).IsTrue();
        h.HidePreview();

        h.Toolbar.Bounds = new PhysicalRect(100, 400, 800, 140); // engagement 2 anchors at y=400
        collision.Blocked = new PhysicalRect(1000, 0, 100, 100); // overlaps ONLY the dead rect
        h.Panel.DeferBoundsReport = true; // open the async-layout window
        var boundsBefore = h.Recorder.Bounds.Count;

        h.ShowPreview(Engagement + 1);
        Check.That(h.Panel.IsShown).IsTrue(); // shown, not yet measured
        collision.Flip(); // the chrome change lands INSIDE the window

        Check.WithCustomMessage(
                "A Changed inside the show-to-first-report window must not re-check the DEAD " +
                "engagement's rect — the show-time candidate gate owns that window.")
            .That(h.Panel.IsShown).IsTrue();
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore); // nothing published, no hide

        h.Panel.ReportBounds(); // layout lands: the fresh [916,400] rect is clear of the consumer

        Check.That(h.Panel.IsShown).IsTrue();
        Check.That(h.Recorder.Bounds.Count).IsEqualTo(boundsBefore + 1);
        Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.Rect);
        Check.That(h.Recorder.Bounds[^1].PanelBounds!.Value.Y).IsEqualTo(400);
    }

    [StaFact]
    public void KeyboardAutoShow_MidReadThroughTheRealCoordinator_HidesKeepLast()
    {
        // P16's auto-show: the user is mid-read on the panel and a text field takes focus, so the
        // keyboard AUTO-SHOWS over it. Drives the REAL KeyboardCoordinator's ShowKeyboardRequest
        // path (message → SetKeyboardVisible → real KeyboardWindow.Show + edge bar + VM
        // IsVisible=true) and the real VM the coordinator raises; the collision seam is a fake
        // playing the inventory's two roles for this scenario (the trigger on the VM flip, and the
        // keyboard rect now covering the panel). Stated plainly — the halves this headless test
        // does NOT cover: the keyboard window here never lays out a real key grid, so the rect is
        // the fake's, and the "zero keyboard activations while gaze is held where the panel was"
        // half is asserted only at the service level (the panel hid before any key could dwell);
        // the live-keyboard activation count belongs to the owner-gated UI pass, not this suite.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var keyboardVm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
        var keyboard = new KeyboardWindow();
        keyboard.SetViewModel(keyboardVm);

        var collision = new AutoShowCollision(keyboardVm)
        {
            // The panel anchors at (916, 0) sized 400×300 (200×150 DIP at 2.0); the keyboard —
            // bottom-anchored on the toolbar's Top edge in production, top strip here — covers it.
            ConsumerRect = new PhysicalRect(0, 0, 1500, 300),
        };
        // Fixture discipline: this constructs a real KeyboardWindow, so the harness is built with
        // AdaptationSettings.Enabled LEFT FALSE — nothing in this scenario reads the flag (the
        // service is message-driven; the snapshot's disabled path is all-zero rows), and a window
        // test must never light up real adaptation wiring.
        var h = new Harness(adaptationEnabled: false, collision: collision, messenger: messenger);
        h.Panel.DipSize = (200, 150);
        h.Panel.Scale = 2.0;
        h.ShowPreview();
        Check.That(h.Panel.IsShown).IsTrue();

        var coordinator = new KeyboardCoordinator(
            new ModalInteractionState(), new QuickActionsCoordinator(),
            new ToolbarLifecycleCoordinator(new ModalInteractionState()));
        try
        {
            var context = EyeCtrl.App.Tests.Services.TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                OverlayManager = new AutoShowOverlays(keyboard, h.Toolbar),
                Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher,
                Messenger = messenger,
                Settings = h.Settings,
                KeyboardVM = keyboardVm,
            };
            coordinator.Initialize(context);

            messenger.Send(new ShowKeyboardRequestMessage());
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);

            Check.WithCustomMessage(
                    "The real coordinator path did not raise the VM visibility flip this test " +
                    "is about (its trigger).")
                .That(keyboardVm.IsVisible).IsTrue();
            Check.That(h.Panel.IsShown).IsFalse();
            Check.That(h.Recorder.Bounds).HasElementThatMatches(
                m => m.EngagementId == Engagement && m.Kind == PreviewBoundsKind.HiddenKeepLast);
            Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();
        }
        finally
        {
            coordinator.Dispose();
            keyboard.Close();
            h.Service.Dispose();
        }
    }

    [StaFact]
    public void NoteOverThePanel_ThroughTheRealInventory_HidesKeepLast_ThenBlocksShow_Drains()
    {
        // Review F1's end-to-end pin: the REAL inventory (all six sources wired, nothing else up)
        // with a REAL NoteManager. A note pinned where the panel already sits must behave exactly
        // like the keyboard does: mid-read it is a POLICY hide (HiddenKeepLast, live engagement
        // id), at the next show it is NoPanel, and removing the note drains the zone so the panel
        // returns. Note geometry is the real RegisterDwellTargets math (DPI-robust: the close
        // rect lands inside the panel for any scale this process can hold here). Fixture
        // discipline: real NoteWindow constructed, so AdaptationSettings.Enabled stays false.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        using var noteManager = new NoteManager(messenger);

        // Fresh VMs; the keyboard seam is never reached (IsVisible false short-circuits before
        // the overlays factory is invoked — the delegate throws to prove it).
        var invSettings = new EyeCtrlSettings();
        using var inventory = new GazeConsumerInventory(
            invSettings,
            overlays: () => throw new NotImplementedException("the keyboard gate must short-circuit"),
            new KeyboardViewModel(invSettings, messenger),
            new ViewModels.ActionMenuViewModel(invSettings, messenger),
            new ModesMenuViewModel(invSettings, messenger),
            new EdgeButtonBarViewModel(invSettings, messenger),
            new EdgeButtonBarViewModel(invSettings, messenger),
            externalTargetRects: () => Array.Empty<PhysicalRect>(),
            noteTargetRects: () => noteManager.LiveTargetRects(),
            new ToolbarLifecycleBridge(),
            messenger);

        var h = new Harness(adaptationEnabled: false, collision: inventory, messenger: messenger);
        try
        {
            h.Toolbar.Bounds = new PhysicalRect(100, 0, 1000, 140); // panel anchors at x = 1116
            h.Panel.DipSize = (200, 150);
            h.Panel.Scale = 2.0;

            h.ShowPreview();
            Check.That(h.Panel.IsShown).IsTrue();

            // A note is pinned right where the panel sits (Edit state ⇒ the close button's dwell
            // rect lands inside [1116..1516)x[0..300)) — the exact R4-H1 harm: buttons fed under
            // the panel. The structural message is the inventory's trigger; no manual flip.
            noteManager.CreateNoteAt(new PhysicalPoint(1300, 150));

            Check.That(h.Panel.IsShown).IsFalse();
            Check.That(h.Recorder.Bounds[^1].EngagementId).IsEqualTo(Engagement);
            Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.HiddenKeepLast);
            Check.That(h.Coordinator.IsAutoHideSuspended).IsFalse();

            // The note still up blocks the NEXT show too — NoPanel at show time, no window.
            h.HidePreview();
            h.ShowPreview(Engagement + 1);

            Check.That(h.Panel.ShowCount).IsEqualTo(1); // the first show only
            Check.That(h.Recorder.Bounds[^1].EngagementId).IsEqualTo(Engagement + 1);
            Check.That(h.Recorder.Bounds[^1].Kind).IsEqualTo(PreviewBoundsKind.NoPanel);

            // Removing the note drains the zone: the panel returns on the show after that.
            noteManager.RemoveNote(noteManager.ActiveNotes[0].Id);
            h.ShowPreview(Engagement + 2);

            Check.That(h.Panel.ShowCount).IsEqualTo(2);
            Check.That(h.Panel.IsShown).IsTrue();
        }
        finally
        {
            h.Service.Dispose();
        }
    }

    /// <summary>
    /// The auto-show scenario's inventory stand-in: raises <see cref="IOverlayCollision.Changed"/>
    /// exactly when the real inventory would (the keyboard VM's visibility flip) and answers
    /// IsClear from the keyboard's covering rect — the real window here never lays out a grid, so
    /// its chrome bounds cannot be the source.
    /// </summary>
    private sealed class AutoShowCollision : IOverlayCollision
    {
        private readonly KeyboardViewModel _keyboardVm;
        public PhysicalRect ConsumerRect;

        public AutoShowCollision(KeyboardViewModel keyboardVm)
        {
            _keyboardVm = keyboardVm;
            _keyboardVm.PropertyChanged += OnKeyboardPropertyChanged;
        }

        public event Action? Changed;

        public bool IsClear(PhysicalRect panelRect)
        {
            if (!_keyboardVm.IsVisible)
                return true;
            return !ConsumerRect.IntersectsWith(panelRect);
        }

        private void OnKeyboardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(KeyboardViewModel.IsVisible))
                Changed?.Invoke();
        }
    }

    /// <summary>The coordinator path's overlay manager: the real keyboard window plus the
    /// harness's fake toolbar; everything else throws (the PartialOverlayManager discipline).</summary>
    private sealed class AutoShowOverlays : IOverlayManager
    {
        private readonly KeyboardWindow _keyboard;
        private readonly IToolbarOverlay _toolbar;

        public AutoShowOverlays(KeyboardWindow keyboard, IToolbarOverlay toolbar)
        {
            _keyboard = keyboard;
            _toolbar = toolbar;
        }

        public KeyboardWindow Keyboard => _keyboard;
        public IToolbarOverlay Toolbar => _toolbar;
        public ICursorCircleOverlay CursorCircle => throw new NotImplementedException();
        public IActivationButtonOverlay ActivationButton => throw new NotImplementedException();
        public IManualSelectionOverlay ManualSelectionOverlay => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator => throw new NotImplementedException();
        public IGlassBackdropOverlay GlassBackdrop => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();
        public IEdgeButtonBar EdgeButtonBar { get; } =
            new EyeCtrl.App.Tests.Services.TestFakes.FakeEdgeButtonBar();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            Core.Enums.GazeProviderType providerType) => throw new NotImplementedException();
        public void CloseAll() => throw new NotImplementedException();
    }

    private static ToolbarAutoHideController NewAutoHide() => new(
        enabled: true, edge: "top", screenW: 1920, screenH: 1080,
        toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
        hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
}
