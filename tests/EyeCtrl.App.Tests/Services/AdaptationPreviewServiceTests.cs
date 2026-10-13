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

        public void ShowPanel(AdaptationPreviewSnapshot snapshot, PhysicalPoint positionPhysical, string dockedEdge)
        {
            if (ThrowOnShow)
                throw new InvalidOperationException("panel boom");
            Snapshots.Add(snapshot);
            Positions.Add(positionPhysical);
            Edges.Add(dockedEdge);
            ShowCount++;
            IsShown = true;
            PublishBounds();
        }

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
        public readonly WeakReferenceMessenger Messenger = new();
        public readonly ManualClock Clock = new();
        public readonly PhysicalRect WorkArea = new(0, 0, 1920, 1080);
        public readonly StubCollision Collision = new(clear: true);
        public readonly Recorder Recorder = new();
        public readonly AdaptationPreviewService Service;

        public Harness(bool adaptationEnabled = true)
        {
            Settings.Adaptation.Enabled = adaptationEnabled;
            Settings.Toolbar.IsDocked = true;
            Settings.Toolbar.DockedEdge = "Top";
            Provider = new EffectiveSettingsProvider(Settings.Adaptation);
            Coordinator.InjectAutoHideForTesting(NewAutoHide());
            Bridge.Attach(Coordinator);
            Service = new AdaptationPreviewService(
                Settings,
                Provider,
                toolbar: () => Toolbar,
                panel: () => Panel,
                collision: Collision,
                suspension: Bridge,
                Messenger,
                workArea: () => WorkArea,
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
            () => new PhysicalRect(0, 0, 1920, 1080), new ManualClock().Tick, null);
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

    private static ToolbarAutoHideController NewAutoHide() => new(
        enabled: true, edge: "top", screenW: 1920, screenH: 1080,
        toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
        hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
}
