namespace EyeCtrl.App.Tests;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// The settings content column is what is LEFT of the window after an absolute sidebar and two
/// nested margins, and the controls dropped into it carry fixed widths chosen against a wide
/// screen. Nothing guarded the two against each other, so widening the sidebar from 220 to 240 —
/// which <see cref="SettingsTabLabelFitTests"/> required, and still requires — took 20 DIP off
/// every content page at once.
///
/// Measured on the element tree, at a 800 DIP primary display (a 1600x1200 panel at 150%, or a
/// 1920x1080 one at 240% — high scaling is ordinary for users of this application):
/// <list type="bullet">
/// <item>at a 220 sidebar the 460 DIP picker button's right edge landed at 716.00 against a
/// 720.00 DIP window: fitting, but by 4.00 DIP;</item>
/// <item>at 240 it landed at 736.00 — 16.00 DIP off the window edge, and 48.00 DIP past the
/// content panel it is supposed to sit inside.</item>
/// </list>
/// So the widening caused the clip, and the 4.00 DIP that preceded it was not a margin anyone
/// should have been relying on. The fix is <c>SettingsWindow.ConstrainToContentWidth</c>, applied
/// by <c>Add(DwellControl)</c> to every control it places: a <c>MaxWidth</c> cap, which WPF
/// applies over an explicit <c>Width</c>, so a control keeps its designed width wherever there is
/// room and gives ground only where there is not.
///
/// **Nothing here is restated from the layout it checks.** The sidebar column and both margins are
/// parsed out of SettingsWindow.xaml, the window ratio and the picker width are read from
/// <see cref="SettingsWindow"/>'s own consts, and the button is a real
/// <see cref="DwellButton"/> so its margin and root geometry come from production too.
/// <see cref="SettingsWindow"/> cannot be constructed here — eight non-optional dependencies — so
/// the tree is rebuilt element for element, exactly as <see cref="SettingsTabLabelFitTests"/> does.
/// **Nothing is shown and no input is simulated**: a detached element tree renders no window, which
/// matters because this suite must never take over the screen of a user whose only input channel is
/// an eye tracker. No UI category, so CI runs it.
/// </summary>
public class SettingsContentFitTests
{
    /// <summary>The WPF presentation namespace every element in the XAML lives in.</summary>
    private static readonly XNamespace Wpf =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The XAML language namespace, which carries <c>x:Name</c>.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// A narrow-but-real primary display width in DIP. 1600x1200 at 150%, or 1920x1080 at 240%.
    /// Not the narrowest imaginable — the narrowest that a user of an eye-tracking application,
    /// who is likely to be running heavy display scaling, plausibly has.
    /// </summary>
    private const double NarrowDisplayWidthDip = 800;

    /// <summary>A registrar that records nothing: this test measures layout, never dwell.</summary>
    private sealed class NullRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs,
            Action onActivated)
        { }

        public void Unregister(string id, DwellCategory category) { }

        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }

        public double GetProgress(string id, DwellCategory category) => 0;
    }

    /// <summary>The parsed SettingsWindow.xaml document.</summary>
    /// <returns>The root element.</returns>
    private static XElement Xml()
    {
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml");
        var root = XDocument.Parse(File.ReadAllText(path)).Root;
        Check.WithCustomMessage("SettingsWindow.xaml has no root element.").That(root).IsNotNull();
        return root!;
    }

    /// <summary>Finds the single element carrying the given <c>x:Name</c>.</summary>
    /// <param name="root">The XAML root.</param>
    /// <param name="name">The x:Name to look for.</param>
    /// <returns>The named element.</returns>
    private static XElement Named(XElement root, string name)
    {
        var hit = root.DescendantsAndSelf()
            .SingleOrDefault(e => (string?)e.Attribute(Xaml + "Name") == name);
        Check.WithCustomMessage(
                $"SettingsWindow.xaml no longer has an element named \"{name}\". That element is "
                + "one of the three inputs to the content-width arithmetic, and "
                + "SettingsWindow.ContentAvailableWidth reads it by name at runtime — so if it is "
                + "gone, production is broken too. Repoint both, do not delete this assertion.")
            .That(hit).IsNotNull();
        return hit!;
    }

    /// <summary>
    /// The sidebar column's width, off <c>RootGrid</c>'s <c>ColumnDefinitions</c> shorthand.
    /// The same attribute <see cref="SettingsTabLabelFitTests"/> measures the tab labels against
    /// and the same one <c>ContentAvailableWidth</c> subtracts, read here a third time rather than
    /// copied a first.
    /// </summary>
    /// <param name="root">The XAML root.</param>
    /// <returns>The sidebar width in DIP.</returns>
    private static double SidebarWidth(XElement root)
    {
        var columns = Named(root, "RootGrid").Attribute("ColumnDefinitions");
        Check.WithCustomMessage(
                "RootGrid no longer carries a ColumnDefinitions attribute. If the columns moved to "
                + "<Grid.ColumnDefinitions> children, read them from there — do not delete this, or "
                + "the guard measures against a sidebar of zero and everything trivially fits.")
            .That(columns).IsNotNull();

        var first = columns!.Value.Split(',')[0].Trim();
        var parsed = double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var width);
        Check.WithCustomMessage(
                $"The sidebar column is \"{first}\", not an absolute width. A Star or Auto sidebar "
                + "means the content column is no longer \"the window minus a constant\", and this "
                + "arithmetic no longer describes the layout.")
            .That(parsed).IsTrue();
        return width;
    }

    /// <summary>Reads a named element's <c>Margin</c> through WPF's own converter.</summary>
    /// <param name="root">The XAML root.</param>
    /// <param name="name">The element's x:Name.</param>
    /// <returns>The declared margin.</returns>
    private static Thickness Margin(XElement root, string name)
    {
        var attr = Named(root, name).Attribute("Margin");
        if (attr is null) return default;
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(attr.Value)!;
    }

    /// <summary>
    /// Rebuilds the settings content area at the given window width and drops one real picker
    /// button into it, returning where everything landed.
    ///
    /// Mirrors SettingsWindow.xaml element for element: the two-column root Grid, the content
    /// Grid at column 1 with its margin and its Star/Auto rows, and <c>ContentPanel</c> with its
    /// margin. Every one of those decides where the button's right edge falls, so none may be
    /// dropped.
    /// </summary>
    /// <param name="windowWidth">The window's width in DIP.</param>
    /// <param name="constrain">
    /// True to apply production's <c>ConstrainToContentWidth</c> — the fix — before attaching.
    /// False reproduces the unfixed layout, which is what the negative control below needs.
    /// </param>
    /// <returns>The button's right edge, the content panel's right edge, and the drawn width.</returns>
    private static (double ButtonRight, double PanelRight, double ButtonWidth, double MarginRight)
        MeasurePickerButton(double windowWidth, bool constrain)
    {
        const double windowHeight = 900;
        var root = Xml();

        var outer = new Grid { Width = windowWidth, Height = windowHeight };
        outer.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(SidebarWidth(root)) });
        outer.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var contentArea = new Grid { Margin = Margin(root, "ContentArea") };
        contentArea.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(contentArea, 1);
        outer.Children.Add(contentArea);

        var panel = new StackPanel { Margin = Margin(root, "ContentPanel") };
        Grid.SetRow(panel, 0);
        contentArea.Children.Add(panel);

        // A real DwellButton, built the way ChunkPickerPages builds one. Its Root.Margin and its
        // width plumbing then come from production rather than from a copy of them here.
        var button = new DwellButton(
            new NullRegistrar(), "fit_probe", "Some Preset Label", () => { },
            default, DwellCategory.Control, radiusPhysical: 50, dwellMs: 500,
            width: SettingsWindow.PickerButtonWidth, height: 80);

        // The one line under test. availableWidth is recomputed here exactly as
        // ContentAvailableWidth computes it inside the window, from the same three XAML numbers.
        var available = windowWidth
            - SidebarWidth(root)
            - Margin(root, "ContentArea").Left - Margin(root, "ContentArea").Right
            - Margin(root, "ContentPanel").Left - Margin(root, "ContentPanel").Right;
        if (constrain) SettingsWindow.ConstrainToContentWidth(button.Root, available);

        panel.Children.Add(button.Root);

        outer.Measure(new Size(windowWidth, windowHeight));
        outer.Arrange(new Rect(0, 0, windowWidth, windowHeight));
        outer.UpdateLayout();

        double Right(FrameworkElement e) =>
            e.TransformToAncestor(outer).Transform(new Point(e.ActualWidth, 0)).X;

        return (Right(button.Root), Right(panel), button.Root.ActualWidth,
            button.Root.Margin.Right);
    }

    /// <summary>The window width production would pick for a given primary-screen width.</summary>
    /// <param name="screenWidthDip">The primary screen's width in DIP.</param>
    /// <returns>The settings window's width in DIP.</returns>
    private static double WindowWidthFor(double screenWidthDip) =>
        screenWidthDip * SettingsWindow.WindowSizeRatio;

    [StaFact]
    public void ThePickerButton_FitsTheContentColumn_OnANarrowDisplay()
    {
        var windowWidth = WindowWidthFor(NarrowDisplayWidthDip);
        var (buttonRight, panelRight, width, marginRight) =
            MeasurePickerButton(windowWidth, constrain: true);

        // The button's margin sits outside its width and inside the panel, so the panel's edge is
        // cleared only if the margin clears it too.
        var over = buttonRight + marginRight - panelRight;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The {0:F2} DIP quick-action picker button overruns the settings content panel by "
                + "{1:F2} DIP on a {2:F0} DIP display (window {3:F2}, panel right edge {4:F2}, "
                + "button drawn at {5:F2} ending {6:F2}). Reachable at Settings -> Quick Actions "
                + "-> Add action, where it clips against the window edge. The content column is "
                + "the window minus the sidebar minus two nested margins, so anything that widens "
                + "the sidebar or the margins takes it straight off this button. Do NOT fix it by "
                + "narrowing the sidebar — SettingsTabLabelFitTests needs that width for the "
                + "\"Quick Actions\" tab label. Cap the control instead, which is what "
                + "SettingsWindow.ConstrainToContentWidth does.",
                SettingsWindow.PickerButtonWidth, over, NarrowDisplayWidthDip, windowWidth,
                panelRight, width, buttonRight))
            .That(over).IsStrictlyLessThan(0.01);
    }

    [StaFact]
    public void WithoutTheConstraint_ThatSameButtonOverruns()
    {
        // The negative control. Without it the assertion above could be passing because the
        // harness measures nothing — a detached tree that never arranges reports zeroes, and
        // zero fits everything. This pins that the narrow display really is a clipping one, and
        // therefore that the constraint above is doing work rather than decorating a non-problem.
        var windowWidth = WindowWidthFor(NarrowDisplayWidthDip);
        var (buttonRight, panelRight, width, marginRight) =
            MeasurePickerButton(windowWidth, constrain: false);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "An UNCONSTRAINED {0:F2} DIP picker button now fits the {1:F0} DIP display's "
                + "content panel (drawn {2:F2}, ends {3:F2}, panel ends {4:F2}). Either the "
                + "layout got roomier — in which case pick a narrower display for "
                + "NarrowDisplayWidthDip so the fit test above still exercises the clamp — or the "
                + "measurement harness has stopped laying anything out, in which case the test "
                + "above proves nothing.",
                SettingsWindow.PickerButtonWidth, NarrowDisplayWidthDip, width, buttonRight,
                panelRight))
            .That(buttonRight + marginRight - panelRight).IsStrictlyGreaterThan(0);
    }

    [StaFact]
    public void TheConstraint_NeverShrinksAControlInsideItsOwnDwellCircle()
    {
        // The floor matters more than the cap. A dwell target is a centre plus a physical radius,
        // not the rendered rectangle, so a control squeezed narrower than that circle is
        // activatable at points where nothing is drawn — the user dwells on blank surface and the
        // button fires. An absurd width is used deliberately: the floor has to hold where the
        // arithmetic would otherwise go to zero or negative.
        var border = new Border { Margin = new Thickness(4, 0, 4, 0), Width = 460, Height = 80 };
        SettingsWindow.ConstrainToContentWidth(border, availableWidth: 10);

        // A cap has to actually be set. Left at its default the property is +Infinity, which
        // clears the floor below vacuously while capping nothing at all — that is exactly the
        // shape a gutted ConstrainToContentWidth leaves behind.
        Check.WithCustomMessage(
                "ConstrainToContentWidth left MaxWidth unset, so it caps nothing. Every fixed "
                + "width in SettingsWindow then renders at full size however narrow the window is.")
            .That(double.IsPositiveInfinity(border.MaxWidth)).IsFalse();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The width constraint squeezed a control to {0:F2} DIP, inside the "
                + "{1:F2} DIP dwell circle it registers. Below that diameter the gaze-activatable "
                + "region sticks out past the button's own visual.",
                border.MaxWidth, SettingsWindow.MinimumDwellableWidth))
            .That(border.MaxWidth).IsGreaterOrEqualThan(SettingsWindow.MinimumDwellableWidth);
    }

    [StaFact]
    public void TheConstraint_LeavesADesignedWidthAloneWhereThereIsRoom()
    {
        // The cap must be a cap, not a resize. On the developer's own 1920 DIP desktop the picker
        // button has to still be 460 wide, or this "fix" would be a silent redesign of every
        // settings page.
        var windowWidth = WindowWidthFor(1920);
        var (_, _, width, _) = MeasurePickerButton(windowWidth, constrain: true);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a roomy 1920 DIP display the picker button is drawn {0:F2} DIP wide instead of "
                + "its designed {1:F2}. The content-width cap has become an unconditional resize.",
                width, SettingsWindow.PickerButtonWidth))
            .That(width).IsEqualTo(SettingsWindow.PickerButtonWidth);
    }

    [StaFact]
    public void TheContentAdd_StillAppliesTheConstraint()
    {
        // SettingsWindow cannot be constructed (eight non-optional dependencies), so the tests
        // above call ConstrainToContentWidth the way Add does rather than through Add. That leaves
        // exactly one hole: deleting the call from Add would leave all of them green while the
        // running application clipped again. Closed by reading the source, in the same spirit as
        // SettingsTabLabelFitTests parsing _tabDefs out of a file it cannot instantiate.
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

        var start = source.IndexOf("private void Add(DwellControl control)", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "SettingsWindow.xaml.cs no longer declares `private void Add(DwellControl "
                + "control)`, which is the single place every content-area dwell control is "
                + "placed. Repoint this check at wherever that now happens.")
            .That(start).IsStrictlyGreaterThan(-1);

        var end = source.IndexOf('}', start);
        Check.WithCustomMessage("The Add(DwellControl) body is unterminated.")
            .That(end).IsStrictlyGreaterThan(start);

        Check.WithCustomMessage(
                "Add(DwellControl) no longer calls ConstrainToContentWidth. Every fixed width in "
                + "SettingsWindow was chosen against a wide screen, and that call is the only "
                + "thing stopping them from hanging off the window on a narrow one. Body read:\n"
                + source[start..(end + 1)])
            .That(source[start..(end + 1)]).Contains("ConstrainToContentWidth");
    }
}
