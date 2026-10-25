namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// B2a's placement acceptance — fully on-screen, mid-edge, clear of the out slot and of the band —
/// on the pure layout for every edge, and on the real window at DPI 1 and 2: the circle the window
/// DRAWS is the circle it registers as the dwell target, converted through the same scale.
/// <para>
/// The windows here are constructed, never shown (InvisibleTestWindow.Prepare all the same, the
/// repo's rule A). The DpiHelper collection because the window tests write the process-global scale.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ResumeStripPlacementTests : IDisposable
{
    public void Dispose() => DpiHelper.Initialize(1.0, 1.0);

    // The owner's display: 3840×2400 at 200 % is 1920×1200 DIP; a 48 DIP taskbar at the bottom.
    private static readonly Rect OwnerWorkArea = new(0, 0, 1920, 1152);

    // A docked top band at the owner's size-112 toolbar: centred, ~144 DIP deep.
    private static readonly Rect OwnerTopBand = new(510, 0, 900, 144);

    private static IReadOnlyList<Rect> KeepOuts(Rect screen) =>
        ResumeStripLayout.OutSlotKeepOuts(screen.Width, screen.Height);

    private static Rect Inflate(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static void CheckClear(ResumeStripLayout.Result layout, Rect workArea, Rect? band, IReadOnlyList<Rect> keepOuts)
    {
        var disc = layout.Disc;
        Check.WithCustomMessage($"disc {disc} must lie fully inside the work area {workArea}")
            .That(workArea.Contains(disc)).IsTrue();
        Check.WithCustomMessage($"label {layout.Label} must lie fully inside the work area {workArea}")
            .That(workArea.Contains(layout.Label)).IsTrue();

        if (band is { } b)
        {
            Check.WithCustomMessage($"disc {disc} overlaps the band {b}")
                .That(disc.IntersectsWith(b)).IsFalse();
            Check.WithCustomMessage($"label {layout.Label} overlaps the band {b}")
                .That(layout.Label.IntersectsWith(b)).IsFalse();
        }

        foreach (var k in keepOuts)
            Check.WithCustomMessage($"disc {disc} touches the out slot {k}")
                .That(Inflate(disc, 1).IntersectsWith(k)).IsFalse();

        Check.WithCustomMessage("the disc and the label must not overlap")
            .That(disc.IntersectsWith(layout.Label)).IsFalse();
        Check.That(layout.Strip.Height >= ResumeStripLayout.StripThicknessDip
            || layout.Strip.Width >= ResumeStripLayout.StripThicknessDip).IsTrue();
    }

    [Fact]
    public void TheOwnersTopEdge_PutsTheDiscMidEdge_BelowTheBand_AndOnScreen()
    {
        var layout = ResumeStripLayout.Compute(OwnerWorkArea, "Top", OwnerTopBand, KeepOuts(new Rect(0, 0, 1920, 1200)));

        CheckClear(layout, OwnerWorkArea, OwnerTopBand, KeepOuts(new Rect(0, 0, 1920, 1200)));
        Check.WithCustomMessage("mid-edge: the disc is centred on the edge, never in a corner")
            .That(layout.DiscCentre.X).IsEqualTo(960.0);
        // Inset past the band by the clearance — the band's own Play is never covered.
        Check.That(layout.Disc.Top).IsEqualTo(OwnerTopBand.Bottom + ResumeStripLayout.ClearanceDip);
        Check.That(layout.DiscCentre.X - ResumeStripLayout.DiscRadiusDip).IsEqualTo(910.0);
        Check.That(layout.Strip).IsEqualTo(new Rect(0, 0, 1920, ResumeStripLayout.StripThicknessDip));
        Check.WithCustomMessage("r ≥ 50 DIP").That(ResumeStripLayout.DiscRadiusDip).IsStrictlyGreaterThan(49.9);
    }

    public static TheoryData<string, double, double> EveryEdgeAndScreen => new()
    {
        { "Top", 1920, 1040 }, { "Bottom", 1920, 1040 }, { "Left", 1920, 1040 }, { "Right", 1920, 1040 },
        { "Top", 1920, 1152 }, { "Bottom", 1920, 1152 }, { "Left", 1920, 1152 }, { "Right", 1920, 1152 },
        { "Top", 1280, 672 }, { "Right", 1280, 672 },
    };

    [Theory]
    [MemberData(nameof(EveryEdgeAndScreen))]
    public void EveryEdge_IsOnScreen_MidEdge_AndClearOfTheBandAndTheOutSlot(string edge, double width, double height)
    {
        var workArea = new Rect(0, 0, width, height);
        const double depth = 144;
        Rect band = edge switch
        {
            "Bottom" => new Rect(width / 2 - 450, height - depth, 900, depth),
            "Left" => new Rect(0, height / 2 - 300, depth, 600),
            "Right" => new Rect(width - depth, height / 2 - 300, depth, 600),
            _ => new Rect(width / 2 - 450, 0, 900, depth),
        };
        var keepOuts = KeepOuts(new Rect(0, 0, width, height + 48));

        var layout = ResumeStripLayout.Compute(workArea, edge, band, keepOuts);

        CheckClear(layout, workArea, band, keepOuts);
        if (edge is "Top" or "Bottom")
            Check.That(layout.DiscCentre.X).IsEqualTo(width / 2);
        else
            Check.That(layout.DiscCentre.Y).IsEqualTo(height / 2);
    }

    [Fact]
    public void TheRightEdge_PushesTheDiscInwardPastTheOutSlot()
    {
        var workArea = new Rect(0, 0, 1920, 1152);
        var keepOuts = KeepOuts(new Rect(0, 0, 1920, 1200));
        var withoutKeepOuts = ResumeStripLayout.Compute(workArea, "Right", band: null, keepOuts: []);
        var withKeepOuts = ResumeStripLayout.Compute(workArea, "Right", band: null, keepOuts);

        // Without the push the disc would sit on the out slot; with it, it clears.
        Check.That(keepOuts.Any(k => k.IntersectsWith(withoutKeepOuts.Disc))).IsTrue();
        CheckClear(withKeepOuts, workArea, band: null, keepOuts);
    }

    [Fact]
    public void ABandThatIsNotAgainstTheEdge_IsIgnored()
    {
        // A floating toolbar mid-screen says nothing about the docked edge's depth.
        var floating = new Rect(600, 500, 700, 144);
        var layout = ResumeStripLayout.Compute(OwnerWorkArea, "Top", floating, []);
        var bare = ResumeStripLayout.Compute(OwnerWorkArea, "Top", band: null, []);

        Check.That(layout.DiscCentre).IsEqualTo(bare.DiscCentre);
    }

    /// <summary>
    /// The real window at the owner's 200 % and at 100 %: the band rect arrives in physical pixels,
    /// the disc is laid out in DIP, and the registered dwell target is that same circle in physical
    /// pixels — the drawn disc is the target.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheWindow_RegistersTheDrawnDisc_AsTheDwellTarget_AtThisScale(double scale)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(scale, scale);
        var settings = new EyeCtrlSettings();
        var vm = new ResumeStripViewModel(settings, new WeakReferenceMessenger());
        var window = InvisibleTestWindow.Prepare(new PauseIndicatorWindow());
        try
        {
            window.SetViewModel(vm);
            window.SetEdge("Top");
            var workArea = SystemParameters.WorkArea;
            var bandLogical = new Rect(workArea.Left + workArea.Width / 2 - 450, workArea.Top, 900, 144);
            window.SetBandBounds(new PhysicalRect(
                bandLogical.X * scale, bandLogical.Y * scale, bandLogical.Width * scale, bandLogical.Height * scale));

            var layout = window.LastLayout!.Value;
            CheckClear(layout, workArea, bandLogical,
                KeepOuts(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)));
            Check.That(layout.DiscCentre.X).IsEqualTo(workArea.Left + workArea.Width / 2);

            // The registered target: the layout's circle, in physical pixels at this scale.
            var target = vm.DetectorForTest.Targets.Single();
            Check.That(target.Center.X).IsCloseTo(layout.DiscCentre.X * scale, 1e-9);
            Check.That(target.Center.Y).IsCloseTo(layout.DiscCentre.Y * scale, 1e-9);
            Check.That(target.Radius).IsCloseTo(ResumeStripLayout.DiscRadiusDip * scale, 1e-9);

            // The drawn disc: centred on the same point, in window coordinates.
            var disc = window.ResumeDisc!;
            var drawnCentreX = window.Left + System.Windows.Controls.Canvas.GetLeft(disc.Background) + disc.Background.Width / 2;
            var drawnCentreY = window.Top + System.Windows.Controls.Canvas.GetTop(disc.Background) + disc.Background.Height / 2;
            Check.That(drawnCentreX).IsCloseTo(layout.DiscCentre.X, 1e-9);
            Check.That(drawnCentreY).IsCloseTo(layout.DiscCentre.Y, 1e-9);
            Check.That(disc.Background.Width).IsEqualTo(2 * ResumeStripLayout.DiscRadiusDip);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheDisc_IsIconOnly_WithTheResumeName()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new PauseIndicatorWindow());
        try
        {
            var disc = window.ResumeDisc!;
            Check.That(disc.Symbol).IsNull();
            Check.That(disc.Label).IsNull();
            Check.That(disc.IconFill ?? disc.IconStroke).IsNotNull();
            Check.That(System.Windows.Automation.AutomationProperties.GetName(disc.AutomationAnchor!))
                .IsEqualTo(StringTable.Current.T("overlays.pause.resume"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The window draws what the view model says: disc and label up while paused with the band gone,
    /// collapsed while the band shows. Driven through the window's own band seam (what the adapter
    /// calls). Mutation: drop the band term from the view model's visibility → red.
    /// </summary>
    [StaFact]
    public void TheDiscAndLabel_FollowTheBand()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var vm = new ResumeStripViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = InvisibleTestWindow.Prepare(new PauseIndicatorWindow());
        try
        {
            window.SetViewModel(vm);
            vm.SetStripShown(true); // what the window's IPauseIndicatorOverlay.Show does after Show()

            Check.WithCustomMessage("the band starts visible (the adapter's seed), so no Resume yet")
                .That(window.ResumeLayer.Visibility).IsEqualTo(Visibility.Collapsed);

            window.SetBandVisible(false);
            Check.That(window.ResumeLayer.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(window.StateLabelCard.Visibility).IsEqualTo(Visibility.Visible);

            window.SetBandVisible(true);
            Check.That(window.ResumeLayer.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(window.StateLabelCard.Visibility).IsEqualTo(Visibility.Collapsed);
            // The strip itself stays: it is the pause feedback the ring relies on.
            Check.That(window.StripBar.Visibility).IsEqualTo(Visibility.Visible);
        }
        finally
        {
            window.Close();
        }
    }
}
