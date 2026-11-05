namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// C9 F2 fixup-02 (review-02 N1): the two production composition wires and the real order-repair
/// list had no teeth — removing either App assignment, or one window from
/// <see cref="OverlayManager.ReassertOverlayOrder"/>, left the whole App suite green. These gates
/// drive the real manager and the extracted startup wire; they show nothing except the existing
/// DWM-cloaked recipe, and the backdrop runs off-screen through its own seam.
/// </summary>
[Collection("WpfApplication")]
public class OverlayBandWiringTests
{
    /// <summary>
    /// N1(a): the REAL manager's repair list, not a test lambda. Visible windows are raised in the
    /// exact bottom-to-top order; a hidden window is skipped; a visible backdrop is never raised;
    /// the live banner slot is reasserted after the strip.
    /// </summary>
    [StaFact]
    public void TheRealRepair_WalksOnlyTheVisibleOrderCriticalWindows_InOrder()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var magnifier = new ManualSelectionOverlayWindow
        {
            Capture = (_, _) => null,
            ScreenSize = () => (1920, 1080),
        };
        var actionMenu = new ActionMenuWindow { PreviewCapture = (_, _) => null };
        var modifierBar = new EdgeButtonBarWindow();
        var edgeBar = new EdgeButtonBarWindow();
        var indicator = new ManualSelectionGazeIndicatorWindow();
        var strip = new PauseIndicatorWindow();
        var blockedBanner = new BlockedInputBannerWindow();
        var backdrop = InvisibleTestWindow.PrepareOffScreen(new GlassBackdropWindow());

        InvisibleTestWindow.PrepareCloaked(magnifier);
        InvisibleTestWindow.PrepareCloaked(actionMenu);
        InvisibleTestWindow.PrepareCloaked(indicator);
        InvisibleTestWindow.PrepareCloaked(strip);
        InvisibleTestWindow.PrepareCloaked(blockedBanner);
        // The two hidden windows are prepared but never shown; they need only an HWND-free preparation.
        InvisibleTestWindow.Prepare(modifierBar);
        InvisibleTestWindow.Prepare(edgeBar);
        // Hidden, but with a real HWND: "visible only" must be a real conjunct, not an artefact
        // of RaiseToTop's HWND==0 early-out (review-ring-03 F2 / RM20).
        _ = new WindowInteropHelper(modifierBar).EnsureHandle();
        _ = new WindowInteropHelper(edgeBar).EnsureHandle();

        if (new OverlayWindow[] { magnifier, actionMenu, indicator, strip, blockedBanner }
            .Any(w => InvisibleTestWindow.IsCloaked(w) != true))
        {
            CloseAll();
            Assert.Skip("DWM cannot cloak a window here; this test never shows an uncloaked window.");
        }

        var sequence = new List<string>();
        var backdropCalls = new List<string>();
        void Record(OverlayWindow window, string name) =>
            window.SetWindowPosForBand = (_, _, _) => sequence.Add(name);

        Record(magnifier, "magnifier");
        Record(actionMenu, "actionMenu");
        Record(modifierBar, "modifierBar");
        Record(edgeBar, "edgeBar");
        Record(indicator, "indicator");
        Record(strip, "strip");
        Record(blockedBanner, "blockedBanner");

        var slot = new BannerSlot();
        var occupant = new RecordingOccupant(() => sequence.Add("bannerSlot"));
        slot.Enqueue(occupant);

        try
        {
            // The production order from OverlayVmBindingCoordinator: backdrop first, then the
            // magnifier / action menu. The two bars are intentionally left hidden.
            backdrop.ShowBackdrop(
                new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.3 },
                new PhysicalPoint(100, 100));
            magnifier.Show(new PhysicalPoint(900, 500), radius: 100, magnification: 2.0);
            actionMenu.Show(
                new PhysicalPoint(900, 500), new List<InteractionAction>(),
                showBackButton: false, dwellTimeMs: 600, ringRadius: 100, previewCircleSize: 80,
                borderColor: "#FFFFFF", positionIndicatorStyle: PositionIndicatorStyle.Crosshair);
            indicator.Show();
            strip.Show();
            blockedBanner.Show();

            // Capture the backdrop AFTER its own creation raise, so only the repair would show up.
            backdrop.SetWindowPosForBand = (_, _, _) => backdropCalls.Add("backdrop");

            var manager = new OverlayManager(
                magnifier, actionMenu, modifierBar, edgeBar, indicator, strip,
                bannerSlot: () => slot,
                blockedInputBanner: () => blockedBanner,
                glassBackdrop: backdrop);

            var names = manager.RepairSteps.Select(step => step.Name).ToList();
            var expectedOrder = new[]
            {
                "magnifier", "actionMenu", "modifierBar", "edgeBar",
                "gazeDot", "resumeStrip", "bannerSlot", "blockedBanner",
            };
            Check.WithCustomMessage($"production repair list was: {string.Join(" > ", names)}")
                .That(names).HasSize(expectedOrder.Length);
            for (var i = 0; i < expectedOrder.Length; i++)
            {
                Check.WithCustomMessage(
                        $"repair step {i} must be {expectedOrder[i]}; list was: {string.Join(" > ", names)}")
                    .That(names[i]).IsEqualTo(expectedOrder[i]);
            }
            Check.WithCustomMessage("the repair must not include the backdrop or the ring")
                .That(names.Contains("backdrop") || names.Contains("ring")).IsFalse();

            manager.ReassertOverlayOrder();

            Check.WithCustomMessage($"repair order was: {string.Join(" > ", sequence)}")
                .That(sequence).HasSize(6);
            Check.That(sequence[0]).IsEqualTo("magnifier");
            Check.That(sequence[1]).IsEqualTo("actionMenu");
            Check.That(sequence[2]).IsEqualTo("indicator");
            Check.That(sequence[3]).IsEqualTo("strip");
            Check.That(sequence[4]).IsEqualTo("bannerSlot");
            Check.That(sequence[5]).IsEqualTo("blockedBanner");
            Check.WithCustomMessage("the live banner slot must be reasserted after the strip")
                .That(occupant.Reasserts).IsEqualTo(1);
            Check.WithCustomMessage("the hidden bars must be skipped")
                .That(sequence.Contains("modifierBar") || sequence.Contains("edgeBar")).IsFalse();
            Check.WithCustomMessage("the backdrop must never be raised by the repair").That(backdropCalls).IsEmpty();
        }
        finally
        {
            CloseAll();
        }

        void CloseAll()
        {
            backdrop.Close();
            blockedBanner.Close();
            strip.Close();
            indicator.Close();
            edgeBar.Close();
            modifierBar.Close();
            actionMenu.Close();
            magnifier.Close();
        }
    }

    /// <summary>N1(b): the App really calls the one wire (and no longer assigns the statics itself).</summary>
    [Fact]
    public void TheApp_CallsTheBandStartupWireExactlyOnce()
    {
        var source = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs")));

        Check.WithCustomMessage("App.OnStartup must call OverlayBandStartup.WireOverlayBand exactly once")
            .That(Regex.Matches(source, @"OverlayBandStartup\.WireOverlayBand\(").Count).IsEqualTo(1);
        Check.WithCustomMessage("the band statics must be assigned only by the extracted wire")
            .That(source).Not.Contains("OverlayWindow.ProcessHasUiAccess =");
        Check.WithCustomMessage("the band statics must be assigned only by the extracted wire")
            .That(source).Not.Contains("OverlayWindow.ProcessReassertOverlayOrder =");
    }

    /// <summary>
    /// N1(a) second half: the repair is a no-op until Initialize has run; dropping the latch makes
    /// every production fallback silently skip its order repair.
    /// </summary>
    [Fact]
    public void OverlayManager_MarksItselfInitialized_InsideInitialize()
    {
        var source = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Services", "OverlayManager.cs")));

        // Anchored on the completion log INSIDE Initialize, not a bare Contains: the internal test
        // constructor also contains "_initialized = true;", which made the old scan vacuous
        // (review-03 L1 / review-ring-03 F1 / RM21).
        const string completionLog = "Overlay windows initialized successfully";
        var logAt = source.IndexOf(completionLog, StringComparison.Ordinal);
        Check.WithCustomMessage("OverlayManager.Initialize's completion log was not found")
            .That(logAt).IsStrictlyGreaterThan(-1);

        var latchAt = source.IndexOf("_initialized = true;", logAt, StringComparison.Ordinal);
        Check.WithCustomMessage(
                "OverlayManager.Initialize must set _initialized = true after it has created every window, "
                + "or every production order repair becomes a silent no-op")
            .That(latchAt).IsStrictlyGreaterThan(logAt);
    }

    private sealed class RecordingOccupant : IBannerSlotOccupant
    {
        private readonly Action? _onReassert;

        public RecordingOccupant(Action? onReassert = null) => _onReassert = onReassert;

        public int Reasserts { get; private set; }

        public event EventHandler? Closed;

        public void ShowInSlot() { }

        public void HideFromSlot() => Closed?.Invoke(this, EventArgs.Empty);

        public bool TryGetVisibleBounds(out PhysicalRect bounds)
        {
            bounds = default;
            return false;
        }

        public void ReassertForOrder()
        {
            Reasserts++;
            _onReassert?.Invoke();
        }
    }
}

/// <summary>
/// Exclusive (review-ring-03 F3): the startup-wire gate below flips process-wide band statics, so
/// no other collection may run while it does. xUnit v3 runs a DisableParallelization collection
/// exclusively; the gate itself is show-free, so the window is only the few synchronous asserts.
/// </summary>
[CollectionDefinition("OverlayBandProcessStatics", DisableParallelization = true)]
public sealed class OverlayBandProcessStaticsCollection
{
}

/// <summary>
/// N1(b): the extracted startup wire sets both process-wide values. Show-free: it asserts the flag
/// and the repair delegate's target/method, then invokes the delegate through a recording fake, so
/// no window is shown and no band read is performed (review-03 L2 / review-ring-03 F3).
/// </summary>
[Collection("OverlayBandProcessStatics")]
public class OverlayBandStartupTests
{
    [Fact]
    public void WireOverlayBand_SetsTheUiAccessFlagAndTheManagersRepair()
    {
        var previousAccess = OverlayWindow.ProcessHasUiAccess;
        var previousRepair = OverlayWindow.ProcessReassertOverlayOrder;
        var repairs = 0;
        try
        {
            // Deterministic W1/RM17: the flag starts false, so only the wire can turn it on.
            OverlayWindow.ProcessHasUiAccess = false;
            OverlayWindow.ProcessReassertOverlayOrder = null;
            var manager = TestFakes.MakeOverlayManager(onReassertOverlayOrder: () => repairs++);

            OverlayBandStartup.WireOverlayBand(
                new SelfTokenFacts(IntegrityLevel: 0x3000, Elevated: false, HasUIAccess: true), manager);

            Check.WithCustomMessage("WireOverlayBand must set the process UIAccess flag")
                .That(OverlayWindow.ProcessHasUiAccess).IsTrue();

            var repair = OverlayWindow.ProcessReassertOverlayOrder;
            Check.WithCustomMessage("WireOverlayBand must point the repair at the resolved manager")
                .That(repair).IsNotNull();
            Check.WithCustomMessage("the repair delegate targets a different manager")
                .That(ReferenceEquals(repair!.Target, manager)).IsTrue();
            Check.That(repair.Method.Name).IsEqualTo(nameof(IOverlayManager.ReassertOverlayOrder));

            repair();
            Check.WithCustomMessage("the wired delegate must reach the manager's repair")
                .That(repairs).IsEqualTo(1);

            OverlayBandStartup.WireOverlayBand(
                new SelfTokenFacts(IntegrityLevel: 0x2000, Elevated: false, HasUIAccess: false), manager);
            Check.WithCustomMessage("WireOverlayBand must propagate UIAccess=false too")
                .That(OverlayWindow.ProcessHasUiAccess).IsFalse();
        }
        finally
        {
            OverlayWindow.ProcessHasUiAccess = previousAccess;
            OverlayWindow.ProcessReassertOverlayOrder = previousRepair;
        }
    }
}
