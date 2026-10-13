namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// SP-4d L3 — the legibility floors of the probe's buttons, in EVERY theme registered at this base,
/// read off the REAL headless <see cref="SubPixelProbeWindow"/>'s visual tree (SP-7 review 01 O1:
/// the Accent fill put the label at 1.08:1 under Midnight — blank rectangles — and made the Accent
/// cue invisible on it at 1.00:1).
///
/// <para>
/// The window is constructed once per theme (it reads <c>Tokens</c> at construction), never shown,
/// and each button's own brushes are read back: the <see cref="Border.Background"/> fill, the label
/// <see cref="TextBlock.Foreground"/>, the cue's <see cref="DwellOutlineOverlay.Outline"/> stroke,
/// and End test's rest border. Assertions: the label clears the WCAG AA floor (4.5:1) against its
/// fill, the cue outline and End test's identity border clear the SC 1.4.11 non-text floor (3:1).
/// The fills are also pinned to the L1 tokens (five at <c>SurfaceLight</c>, "Later" at
/// <c>SurfaceMedium</c>) and the six non-End-test buttons to having no rest border, so a mutant
/// that restores a filled pairing fails on the fill itself, not only through its ratio.
/// </para>
///
/// <para>
/// The themes are enumerated through <see cref="ThemeRegistry.All"/>, never a local list; the count
/// is pinned ≥ 3, and (fixup 01 F2) the names the loop actually rendered are compared with
/// <see cref="ThemeRegistry.Names"/>, so a narrowed loop cannot pass on a separate count. Daylight
/// arrives from <c>main</c> at the merge, and its row must be covered by this same test then.
/// <see cref="Tokens.Activate"/> is process-global, so this class sits in
/// the <c>ActiveTheme</c> collection (exclusive) and restores Midnight afterwards; the contrast
/// maths is the WCAG relative-luminance helper the other cross-theme gates in this suite carry.
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class SubPixelProbeContrastTests
{
    /// <summary>WCAG 2.1 AA body-text floor, for every button label.</summary>
    private const double LabelFloor = 4.5;

    /// <summary>WCAG 2.1 SC 1.4.11 non-text floor, for the cue outline and End test's identity.</summary>
    private const double NonTextFloor = 3.0;

    /// <summary>The L2 rest border, in DIP, as a literal — a production constant change must fail here.</summary>
    private const double EndButtonDangerBorderDip = 3;

    /// <summary>
    /// Every probe button, with the fill L1 assigns it and whether it carries the L2 danger
    /// identity. The fill is a token selector, not a colour copy, so the pins stay theme-exact.
    /// </summary>
    private static readonly (string Id, Func<ITheme, Color> Fill, bool Danger)[] Buttons =
    [
        (SubPixelProbeViewModel.AnswerOneTargetId, t => t.SurfaceLight, false),
        (SubPixelProbeViewModel.AnswerTwoTargetId, t => t.SurfaceLight, false),
        (SubPixelProbeViewModel.EndTestTargetId, t => t.SurfaceLight, true),
        (SubPixelProbeViewModel.IntroStartTargetId, t => t.SurfaceLight, false),
        (SubPixelProbeViewModel.AskYesTargetId, t => t.SurfaceLight, false),
        (SubPixelProbeViewModel.AskLaterTargetId, t => t.SurfaceMedium, false),
        (SubPixelProbeViewModel.ResultDoneTargetId, t => t.SurfaceLight, false),
    ];

    [StaFact]
    public void EveryRegisteredTheme_KeepsEveryProbeLabelAndTheCueLegible()
    {
        // The coverage pin: hard-coding the enumeration to one theme (or a registry losing
        // entries) must fail here rather than silently shrink what this gate covers.
        Check.WithCustomMessage(
                "Fewer than 3 themes are registered at this base; the probe's contrast gate must "
                + "cover every theme the app can present (SP-4d L3).")
            .That(ThemeRegistry.All.Count).IsGreaterOrEqualThan(3);

        var rendered = new List<string>();
        try
        {
            foreach (var theme in ThemeRegistry.All)
            {
                Tokens.Activate(theme);
                rendered.Add(theme.Name);
                AssertTheme(theme);
            }
        }
        finally
        {
            Tokens.Activate(MidnightTheme.Instance);
        }

        // Fixup 01 (review 01 F2): the coverage pin must bind the names the LOOP rendered, not a
        // separate count expression. Mutant T1 — the loop narrowed to Midnight only — left the
        // count at 3 and survived this gate; this equality kills it. It also catches a registry
        // gaining a theme (Daylight from main) that the loop stops short of.
        Check.WithCustomMessage(
                $"The loop rendered [{string.Join(", ", rendered)}]; the registry holds "
                + $"[{string.Join(", ", ThemeRegistry.Names)}]. A theme the app can present is not "
                + "covered by this gate (SP-4d fixup 01 F2).")
            .That(rendered).ContainsExactly(ThemeRegistry.Names.ToArray());
    }

    // Mutation (m1): an answer's fill back to Tokens.BrushAccent -> the fill pin and the label
    //   ratio (1.08:1 under Midnight) go red.
    // Mutation (m2): End test's fill back to Tokens.BrushAccentDanger -> the fill pin and the
    //   label ratio (2.21:1) go red.
    // Mutation (m3): ThemeRegistry.All hard-coded to Midnight only -> the >= 3 coverage pin goes
    //   red.
    // Mutation (m5): End test's rest border removed (or not AccentDanger/3 DIP) -> the identity
    //   pin goes red.

    private static void AssertTheme(ITheme theme)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        using var vm = NewViewModel();
        vm.Start();
        var window = NewWindow(vm);
        try
        {
            foreach (var (id, fillToken, danger) in Buttons)
            {
                var button = (Border)window.TargetElementForTest(id)!;
                var fill = ((SolidColorBrush)button.Background).Color;

                Check.WithCustomMessage(
                        $"{theme.Name}/{id}: the fill is {fill} but L1 assigns {fillToken(theme)}.")
                    .That(fill).IsEqualTo(fillToken(theme));

                var label = LabelFor(button);
                var labelColor = ((SolidColorBrush)label.Foreground).Color;
                Check.WithCustomMessage(
                        $"{theme.Name}/{id}: the label colour is {labelColor}, not the theme's TextPrimary.")
                    .That(labelColor).IsEqualTo(theme.TextPrimary);

                var labelRatio = Contrast(labelColor, fill);
                Check.WithCustomMessage(
                        $"{theme.Name}/{id}: the label reaches only {labelRatio:F2}:1 on its fill; "
                        + $"the SP-4d L3 floor is {LabelFloor:F1}:1.")
                    .That(labelRatio).IsGreaterOrEqualThan(LabelFloor);

                var cueColor = ((SolidColorBrush)window.CueForTest(id)!.Outline.Stroke).Color;
                Check.WithCustomMessage(
                        $"{theme.Name}/{id}: the cue outline is {cueColor}, not the theme's Accent.")
                    .That(cueColor).IsEqualTo(theme.Accent);

                var cueRatio = Contrast(cueColor, fill);
                Check.WithCustomMessage(
                        $"{theme.Name}/{id}: the cue reaches only {cueRatio:F2}:1 on its fill; "
                        + $"the SP-4d L3 floor is {NonTextFloor:F1}:1.")
                    .That(cueRatio).IsGreaterOrEqualThan(NonTextFloor);

                if (danger)
                {
                    var identity = button.BorderBrush as SolidColorBrush;
                    Check.WithCustomMessage(
                            $"{theme.Name}/{id}: End test lost its AccentDanger rest border "
                            + "(brush is not the theme's AccentDanger).")
                        .That(identity?.Color).IsEqualTo(theme.AccentDanger);
                    Check.WithCustomMessage(
                            $"{theme.Name}/{id}: End test's rest border is {button.BorderThickness}, "
                            + $"not {EndButtonDangerBorderDip} DIP on every side.")
                        .That(button.BorderThickness)
                        .IsEqualTo(new Thickness(EndButtonDangerBorderDip));

                    var identityRatio = Contrast(identity!.Color, fill);
                    Check.WithCustomMessage(
                            $"{theme.Name}/{id}: the identity border reaches only {identityRatio:F2}:1 "
                            + $"on the fill; the SP-4d L3 floor is {NonTextFloor:F1}:1.")
                        .That(identityRatio).IsGreaterOrEqualThan(NonTextFloor);
                }
                else
                {
                    Check.WithCustomMessage(
                            $"{theme.Name}/{id}: a non-End-test button carries a {button.BorderThickness} "
                            + "rest border; at rest only End test may carry one, the cue is the only "
                            + "Accent edge (SP-4d L1).")
                        .That(button.BorderThickness).IsEqualTo(new Thickness(0));
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------------------------- helpers

    private static SubPixelProbeViewModel NewViewModel() => new(
        new WeakReferenceMessenger(),
        new DispatcherTimeProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher),
        SubPixelAbSchedule.Create(seed: 7, new PhysicalRect(0, 0, 1920, 1080)),
        new EyeCtrlSettings().Dwell,
        () => true);

    /// <summary>The owner's configuration (2.0, 1920×1200 DIP), the one the colours were picked for.</summary>
    private static SubPixelProbeWindow NewWindow(SubPixelProbeViewModel vm)
    {
        var window = new SubPixelProbeWindow(
            null, vm, new CursorSettings(), () => { }, viewFault: () => { }, scale: 2.0);
        window.Width = 1920;
        window.Height = 1200;
        new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    /// <summary>The label the button draws: the TextBlock inside its cue overlay's content.</summary>
    private static TextBlock LabelFor(DependencyObject button)
    {
        var found = new List<TextBlock>();
        Collect(button, found);
        return found.First();
    }

    private static void Collect(DependencyObject node, List<TextBlock> into)
    {
        if (node is TextBlock tb) into.Add(tb);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Collect(VisualTreeHelper.GetChild(node, i), into);
    }

    // The WCAG relative-luminance block, verbatim as HaloContrastTests / ToolbarFaceContrastTests
    // carry it: a fix to the maths must be made in each copy or they disagree under one name.

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
