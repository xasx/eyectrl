namespace EyeCtrl.Setup.Display;

using System.Windows;

/// <summary>
/// The pure, DPI-independent geometry of Setup's screens, shared by the windows and their tests.
/// The owner's display is 3840 x 2400 at 200 %, so WPF's work area is already 1920 x 1152 DIP; the
/// figures here are DIPs and the fit harness lays them out at scale 2.0.
/// </summary>
public static class SetupWindowMetrics
{
    /// <summary>A choice target's minimum width (accessibility floor).</summary>
    public const double ChoiceButtonWidth = 420;

    /// <summary>A choice target's minimum height (accessibility floor).</summary>
    public const double ChoiceButtonHeight = 160;

    /// <summary>The minimum clear space between two choice targets.</summary>
    public const double ChoiceGap = 120;

    /// <summary>The choice buttons' text size (the floor is 28 DIP).</summary>
    public const double ChoiceFontSize = 28;

    /// <summary>The prose size on every Setup screen (the floor is 36 DIP).</summary>
    public const double ProseFontSize = 36;

    /// <summary>The prose box's width; the fit class measures every text here.</summary>
    public const double ProseWidth = 1280;

    /// <summary>The prose box's height; four lines at 36 DIP fit with room.</summary>
    public const double ProseHeight = 384;

    /// <summary>The window's outer margin.</summary>
    public const double WindowMargin = 32;

    /// <summary>The clear space under the choice row.</summary>
    public const double BottomMargin = 48;

    /// <summary>"About 70 % of the work area", the plan's window proportion.</summary>
    public const double WorkAreaFraction = 0.70;

    /// <summary>The total width of the three-target choice row (three buttons, two gaps).</summary>
    public static double ChoiceRowWidth => (3 * ChoiceButtonWidth) + (2 * ChoiceGap);

    /// <summary>
    /// The choice window's bounds. Width is the larger of 70 % of the work area and the row's own
    /// width plus both margins: the three 420 DIP targets and their 120 DIP gaps need 1500 DIP,
    /// which is more than 70 % of the owner's 1920 DIP work area (1344). The accessibility floors
    /// win over the proportion (a recorded deviation), and the window stays centred.
    /// </summary>
    public static Rect ChoiceBounds(Rect workArea)
    {
        var width = Math.Min(
            workArea.Width,
            Math.Max(workArea.Width * WorkAreaFraction, ChoiceRowWidth + (2 * WindowMargin)));
        var height = Math.Min(
            workArea.Height,
            Math.Max(workArea.Height * WorkAreaFraction, ChoiceButtonHeight + BottomMargin + (3 * WindowMargin)));
        return Centred(workArea, width, height);
    }

    /// <summary>The progress/result window's bounds: about 70 % of the work area, never smaller
    /// than the prose box plus its margin.</summary>
    public static Rect ProgressBounds(Rect workArea)
    {
        var width = Math.Min(
            workArea.Width,
            Math.Max(workArea.Width * WorkAreaFraction, ProseWidth + (2 * WindowMargin)));
        var height = Math.Min(
            workArea.Height,
            Math.Max(workArea.Height * WorkAreaFraction, ProseHeight + (2 * WindowMargin)));
        return Centred(workArea, width, height);
    }

    private static Rect Centred(Rect workArea, double width, double height) =>
        new(
            workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2),
            width,
            height);
}
