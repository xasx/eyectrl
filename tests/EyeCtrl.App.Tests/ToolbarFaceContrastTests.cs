namespace EyeCtrl.App.Tests;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The toolbar face now has three states — rest (<see cref="ITheme.SurfaceMedium"/>), acquired
/// (<see cref="ITheme.SurfaceLight"/>) and a 250 ms activation flash
/// (<see cref="ITheme.SurfacePrediction"/>; see <c>ToolbarWindow.xaml.cs</c>'s
/// <c>PaintFace</c>/<c>FillFor</c>, lines 534 and 692-696) — each shipped individually defensible,
/// none ever measured against the other two. That is how the banned progress wash (Task 6's
/// <c>DwellWashCensusTests</c>) reached 1.08:1: nobody was looking at the number. That census bans
/// a MECHANISM; this bounds an OUTCOME — the icon colour (<see cref="ITheme.TextPrimary"/>, the
/// default <c>IconBrushFor</c> for every button but Reset) against each of the three fills, in
/// every registered theme.
///
/// <para>
/// Two floors, not one: rest and acquired are states gaze can sit in indefinitely, so they are held
/// to WCAG AAA (7:1). The flash is transient — 250 ms, never dwelt on, existing only to confirm a
/// dwell fired — so it is held to the ordinary AA body-text floor (4.5:1) rather than AAA.
/// </para>
///
/// <para>
/// Reads each <see cref="ITheme"/> directly via <see cref="ThemeRegistry.All"/> rather than through
/// <c>Tokens.Current</c>/<c>Tokens.Activate</c> — the convention every cross-theme contrast gate in
/// this suite follows (<c>SliderPlateContrastTests</c>, <c>SliderStepperContrastTests</c>,
/// <c>SettingsPageHeaderContrastTests</c>, <c>HaloContrastTests</c>): these tests can run in
/// parallel with everything else, and <c>Tokens.Activate</c> mutates process-wide static state, so
/// activating and restoring a theme here would race any other test reading <c>Tokens.Current</c>
/// concurrently. Reading the theme objects directly needs no activation and therefore no restore.
/// </para>
///
/// <para>
/// Contrast is computed here from the token colours by the WCAG relative-luminance formula (the
/// same helper duplicated across every file named above), not pasted in as expected numbers — a
/// palette edit that regresses one of these pairings must fail this test, not silently invalidate a
/// hardcoded table.
/// </para>
/// </summary>
public class ToolbarFaceContrastTests
{
    /// <summary>AAA floor for the two persistent face states (rest, acquired).</summary>
    private const double PersistentStateFloor = 7.0;

    /// <summary>AA floor for the transient 250 ms activation flash.</summary>
    private const double TransientFlashFloor = 4.5;

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Every registered theme.</summary>
    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheIcon_ClearsAAA_AtRest(ITheme theme)
    {
        var ratio = Contrast(theme.TextPrimary, theme.SurfaceMedium);

        Check.WithCustomMessage(
                $"{theme.Name}: the toolbar icon reaches only {ratio:F2}:1 against the at-rest "
                + $"fill (SurfaceMedium), below the {PersistentStateFloor:F1}:1 AAA floor this "
                + "persistent state is held to.")
            .That(ratio).IsGreaterOrEqualThan(PersistentStateFloor);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheIcon_ClearsAAA_WhenAcquired(ITheme theme)
    {
        var ratio = Contrast(theme.TextPrimary, theme.SurfaceLight);

        Check.WithCustomMessage(
                $"{theme.Name}: the toolbar icon reaches only {ratio:F2}:1 against the acquired "
                + $"fill (SurfaceLight), below the {PersistentStateFloor:F1}:1 AAA floor this "
                + "persistent state is held to.")
            .That(ratio).IsGreaterOrEqualThan(PersistentStateFloor);
    }

    /// <summary>
    /// The lower of the two floors, deliberately: the flash lasts 250 ms and is never dwelt on, so
    /// it is held to the ordinary AA body-text floor rather than the AAA floor the two persistent
    /// states above owe. This is also the state where the margin is thinnest — Midnight's flash
    /// measures 6.42:1, below AAA but comfortably clear of this 4.5:1 floor — which is exactly the
    /// number a per-state review would have caught before the progress wash ever shipped at 1.08:1.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheIcon_ClearsAA_DuringTheActivationFlash(ITheme theme)
    {
        var ratio = Contrast(theme.TextPrimary, theme.SurfacePrediction);

        Check.WithCustomMessage(
                $"{theme.Name}: the toolbar icon reaches only {ratio:F2}:1 against the activation "
                + $"flash fill (SurfacePrediction), below the {TransientFlashFloor:F1}:1 AA floor "
                + "this transient state is held to.")
            .That(ratio).IsGreaterOrEqualThan(TransientFlashFloor);
    }
}
