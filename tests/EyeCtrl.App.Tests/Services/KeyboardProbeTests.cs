namespace EyeCtrl.App.Tests.Services;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Fixup M3: the production <see cref="KeyboardProbe"/> — the note gate's real input — had no
/// test at all. All geometry runs at the user's DPI 2.0, where the radius scaling the gate reads
/// must actually be right. The keyboard window is constructed but never shown: TryGetChromeBounds
/// sees ActualWidth 0 and honestly reports no rect, so the window half of <c>Rects()</c> stays
/// deterministically absent and every assertion pins the BAR union.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class KeyboardProbeTests
{
    [StaFact]
    public void Rects_WithVisibleVmAndSeededBar_AtDpi2_ContainsTheBarUnion()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new KeyboardWindow();
            var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
            window.SetViewModel(vm);
            var anchor = new PhysicalPoint(2000, 50);
            var bar = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
            bar.ShowButtons(new List<EdgeButtonDefinition>
            {
                new("probe.close", "✕", 600, _ => { }, VerticalOffset: 40, RadiusLogical: 30),
                new("probe.flip", "⇄", 600, _ => { }, VerticalOffset: 120, RadiusLogical: 30),
            }, anchor);
            Check.That(bar.IsVisible).IsTrue();

            using var probe = new KeyboardProbe(
                vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
                () => window, () => bar);

            var rects = probe.Rects();

            // The expected union through the SAME derivation production reads — the inventory's
            // bounds over the bar's own seeds, at the ×2 scale.
            var seeds = bar.Buttons.ToDictionary(
                b => b.Id,
                b => new PhysicalPoint(anchor.X, anchor.Y + bar.DefinitionFor(b.Id)!.VerticalOffset));
            var expected = GazeConsumerInventory.EdgeButtonBarBounds(
                seeds, GazeConsumerInventory.ButtonRadii(bar), DpiHelper.ScaleX);
            Check.That(expected).IsNotNull();
            Check.WithCustomMessage("the seeded bar's union must reach Rects() at the 2.0 scale")
                .That(rects).HasSize(1);
            Check.That(rects[0]).IsEqualTo(expected!.Value);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    [StaFact]
    public void Rects_WithHiddenVmAndNoBar_IsEmpty()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()); // hidden
        window.SetViewModel(vm);
        var bar = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger()); // never shown
        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => bar);

        Check.That(probe.Rects()).IsEmpty();
    }

    [StaFact]
    public void FlippingIsVisible_RaisesChanged_ExactlyOncePerChange_AfterADispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        vm.IsVisible = true;
        PumpBackgroundHop();
        Check.WithCustomMessage("one show flip, one raise")
            .That(raises).IsEqualTo(1);

        vm.IsVisible = false;
        PumpBackgroundHop();
        Check.That(raises).IsEqualTo(2);
    }

    [StaFact]
    public void AfterDispose_NothingIsRaised()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        probe.Dispose();
        vm.IsVisible = true;
        PumpBackgroundHop();

        Check.That(raises).IsEqualTo(0);
    }

    // ── fixup-01 G1: the four re-place trigger sources (T2–T5) get tests that can fail ──
    // Each triggers exactly one keyboard-relevant change and asserts exactly one Changed
    // after the same one-hop pump the IsVisible test uses. Their proof mutants (dropping
    // IsEmojiMode from the raise condition, the SizeChanged/Loaded subscription, the
    // KeyboardEdgeToggledMessage registration, the ButtonSetChanged subscription) each turn
    // exactly the matching test red.

    [StaFact]
    public void FlippingIsEmojiMode_RaisesChanged_ExactlyOnce_AfterADispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        vm.IsEmojiMode = true;
        PumpBackgroundHop();
        Check.WithCustomMessage("one emoji-mode flip, one raise").That(raises).IsEqualTo(1);
    }

    [StaFact]
    public void RaisingLoaded_RaisesChanged_ExactlyOnce_AfterADispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        // Loaded rides the same handler as SizeChanged (OnKeyboardLayoutChanged); raising it
        // directly pins the layout-changed subscription without a real window resize.
        window.RaiseEvent(new RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent));
        PumpBackgroundHop();
        Check.WithCustomMessage("one layout raise")
            .That(raises).IsEqualTo(1);
    }

    [StaFact]
    public void EdgeToggleMessage_RaisesChanged_ExactlyOnce_AfterADispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        var messenger = new WeakReferenceMessenger();
        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, messenger,
            () => window, () => null);
        int raises = 0;
        probe.Changed += () => raises++;

        messenger.Send(new KeyboardEdgeToggledMessage());
        PumpBackgroundHop();
        Check.WithCustomMessage("one edge toggle, one raise").That(raises).IsEqualTo(1);
    }

    [StaFact]
    public void SeedingButtons_RaisesChanged_ExactlyOnce_AfterADispatcherPump()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.SetViewModel(vm);
        var bar = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        using var probe = new KeyboardProbe(
            vm, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger(),
            () => window, () => bar);
        int raises = 0;
        probe.Changed += () => raises++;

        bar.ShowButtons(new List<EdgeButtonDefinition>
        {
            new("probe.close", "✕", 600, _ => { }, VerticalOffset: 40, RadiusLogical: 30),
        }, new PhysicalPoint(2000, 50));
        PumpBackgroundHop();
        Check.WithCustomMessage("one button-set change, one raise").That(raises).IsEqualTo(1);
    }

    /// <summary>Drains the probe's raise: it is queued at Background, one hop after the property
    /// change, so the assertion pumps at the SAME priority — the frame services the already-queued
    /// raise first, then the empty delegate. Invoke (not InvokeAsync + Wait): on the STA test
    /// thread a blocking Wait would stop the dispatcher from ever running the queued raise, and
    /// the pump would deadlock. Idle priorities would depend on OS idle messages; Background is
    /// deterministic.</summary>
    private static void PumpBackgroundHop() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
