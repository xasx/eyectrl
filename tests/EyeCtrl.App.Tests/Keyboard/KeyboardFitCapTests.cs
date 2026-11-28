namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// F21's runtime fit cap on the REAL keyboard window: the effective key size is
/// <c>min(setting, the largest size at which the widest row of ANY page fits the work area minus
/// the window chrome)</c>. The QWERTZ letters page grew to 14 units, so at the 2.0x setting its
/// 2038-DIP window no longer fits a 1920-DIP work area; at the owner's 1.75x the cap must be a
/// no-op and the page measures 1793 DIP. The window is never shown: its content is lifted into an
/// off-screen WS_POPUP HwndSource, like the neighbouring keyboard probes.
/// </summary>
[Collection("WpfApplication")]
public class KeyboardFitCapTests
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const double BaseKeySize = 70;
    private const double OwnerWorkAreaWidthDip = 1920;
    // KeyboardWindow.WindowChromeWidthDip: Padding 8 + BorderThickness 3 per side.
    private const double WindowChromeWidthDip = 2 * (8 + 3);

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static double KeySize(KeyboardWindow window) =>
        (double)typeof(KeyboardWindow)
            .GetField("_keySize", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    private static KeyboardLayout ShippedQwertz() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));

    private static Probe Open(KeyboardLayout layout, double keySizeMultiplier, double? workAreaWidthDip)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        var window = new KeyboardWindow();
        window.SetViewModel(vm);
        window.SetSettings(settings);
        window.LoadLayout(layout, keySizeMultiplier,
            keyDwellMs: 450, predictionDwellMs: 800, cooldownMs: 0,
            workAreaWidthDip: workAreaWidthDip);

        var content = (FrameworkElement)window.Content;
        window.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-fit-cap-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });
        source.RootVisual = content;
        Measure(content);

        return new Probe(window, vm, content, source);
    }

    private static void Measure(FrameworkElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
    }

    private static KeyboardLayout SyntheticLayout(int lettersKeys, int functionKeys)
    {
        static SubLayout Page(int keyCount) => new()
        {
            Label = "page",
            Rows =
            [
                Enumerable.Range(0, keyCount)
                    .Select(i => new KeyDefinition { Label = $"k{i}", Output = "k" })
                    .ToList()
            ]
        };

        return new KeyboardLayout
        {
            Name = "synthetic",
            DefaultSubLayout = "letters",
            SubLayouts = new Dictionary<string, SubLayout>
            {
                ["letters"] = Page(lettersKeys),
                ["function"] = Page(functionKeys),
            }
        };
    }

    [StaFact]
    public void Qwertz_At1_75x_OnTheOwnersWorkArea_CapIsANoOp_AndThePageMeasures1793x736()
    {
        // The owner's setting (F17): the cap must not touch it. F20 measured the option C page at
        // 1793 DIP wide (1771 grid + 22 chrome) and the same heights as before (letters 736, Fn
        // 862.5): the row grew sideways only.
        using var probe = Open(ShippedQwertz(), 1.75, OwnerWorkAreaWidthDip);
        try
        {
            Check.WithCustomMessage("the cap reduced the owner's 1.75x key size — it must be a no-op")
                .That(KeySize(probe.Window)).IsCloseTo(BaseKeySize * 1.75, 1e-9);
            Check.That(probe.Content.ActualWidth).IsCloseTo(1793.0, 0.5);
            Check.That(probe.Content.ActualHeight).IsCloseTo(736.0, 0.5);
            Check.WithCustomMessage(
                    "the F17 bar must follow the 14-unit letters grid (1771 DIP), so its four 1.6x cells are wider")
                .That(probe.Window.PredictionBar.Width).IsCloseTo(1771.0, 0.5);

            probe.Vm.SwitchSubLayout("function");
            Measure(probe.Content);

            Check.WithCustomMessage("the Fn page's width must be unchanged (only the letters page grew)")
                .That(probe.Content.ActualWidth).IsCloseTo(1641.2, 0.5);
            Check.That(probe.Content.ActualHeight).IsCloseTo(862.5, 0.5);
            Check.WithCustomMessage("a page switch must not change the effective key size")
                .That(KeySize(probe.Window)).IsCloseTo(BaseKeySize * 1.75, 1e-9);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void Qwertz_At2_0x_OnTheOwnersWorkArea_IsCapped_SoEveryPageFits()
    {
        var layout = ShippedQwertz();
        using var probe = Open(layout, 2.0, OwnerWorkAreaWidthDip);
        try
        {
            // The exact largest size: (1920 - 22 chrome - 56 fixed) / 14 units = 131.571 DIP.
            var expected = KeyboardFitCap.MaxKeySizeDip(layout, OwnerWorkAreaWidthDip - WindowChromeWidthDip);
            Check.WithCustomMessage(
                    $"the 14-unit letters row at 2.0x must be capped below 140 DIP (effective {expected:0.###})")
                .That(expected).IsStrictlyLessThan(BaseKeySize * 2.0);
            Check.That(KeySize(probe.Window)).IsCloseTo(expected, 1e-9);
            Check.WithCustomMessage("the capped letters page must fill the work area exactly")
                .That(probe.Content.ActualWidth).IsCloseTo(OwnerWorkAreaWidthDip, 0.5);

            foreach (var page in layout.SubLayouts.Keys)
            {
                probe.Vm.SwitchSubLayout(page);
                Measure(probe.Content);

                Check.WithCustomMessage(
                        $"{page} at 2.0x (effective key {KeySize(probe.Window):0.###} DIP) measures " +
                        $"{probe.Content.ActualWidth:0.#} DIP, over the {OwnerWorkAreaWidthDip} DIP work area")
                    .That(probe.Content.ActualWidth).IsLessOrEqualThan(OwnerWorkAreaWidthDip + 0.5);
                Check.WithCustomMessage($"the {page} page changed the effective key size")
                    .That(KeySize(probe.Window)).IsCloseTo(expected, 1e-9);
            }
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void FitCap_UsesTheWidestRowOfEveryPage_NotOnlyTheCurrentOne()
    {
        // The default page is deliberately the narrower one, so a cap computed from the current
        // (or default) page alone would keep the 96 DIP size and run the 14-key function page
        // 391 DIP off this 1000-DIP work area at the page switch.
        var layout = SyntheticLayout(lettersKeys: 10, functionKeys: 14);
        const double workArea = 1000;
        using var probe = Open(layout, 2.0, workArea);
        try
        {
            var cap = (workArea - WindowChromeWidthDip - 56) / 14.0;
            Check.WithCustomMessage("arrange: the function page must be the wider one")
                .That(cap).IsStrictlyLessThan((workArea - WindowChromeWidthDip - 40) / 10.0);
            // The default page fits even under a default-page-only cap, so the page switch below
            // is what exposes it.
            Check.That(probe.Content.ActualWidth).IsLessOrEqualThan(workArea + 0.5);

            probe.Vm.SwitchSubLayout("function");
            Measure(probe.Content);

            Check.WithCustomMessage(
                    "a cap that used only the current/default page would leave the function page at " +
                    "the larger size and run it off the 1000-DIP work area")
                .That(probe.Content.ActualWidth).IsLessOrEqualThan(workArea + 0.5);
            Check.WithCustomMessage("the page switch must keep the layout-wide cap")
                .That(KeySize(probe.Window)).IsCloseTo(cap, 1e-9);
        }
        finally { probe.Dispose(); }
    }

    [Fact]
    public void LoadKeyboard_PassesTheLiveWorkAreaWidth_ToTheFitCap()
    {
        // Source pin (the KeyboardE1WiringTests model): KeyboardCoordinator.LoadKeyboard is the
        // only production caller of LoadLayout, and the cap only exists if that call hands the
        // live work area over. Without it the 2.0x QWERTZ page would run off a 1920-DIP screen.
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Services", "Coordinators", "KeyboardCoordinator.cs"));
        var start = source.IndexOf("public void LoadKeyboard(", StringComparison.Ordinal);
        var end = source.IndexOf("internal bool ReloadIfChanged(", StringComparison.Ordinal);
        Check.WithCustomMessage("The pin's own anchors moved: fix the pin.")
            .That(start >= 0 && end > start).IsTrue();

        var body = source[start..end];
        Check.WithCustomMessage(
                "LoadKeyboard must pass SystemParameters.WorkArea.Width to LoadLayout: the fit cap is " +
                "the only thing that keeps the 2.0x QWERTZ page on a 1920-DIP screen.")
            .That(body.Contains("keyboard.LoadLayout(") && body.Contains("SystemParameters.WorkArea.Width"))
            .IsTrue();
    }
}
