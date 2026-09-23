namespace EyeCtrl.App.Tests;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Xml.Linq;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
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

    /// <summary>
    /// Hit radius and dwell duration for the row-level probes below, matching what the settings
    /// render path registers: <see cref="SettingsWindow.ControlBtnRadius"/> and the 800 ms every
    /// dwell control in this file is probed at (HandChunkedPageFitTests, SettingsRowLayoutTests).
    /// </summary>
    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;

    /// <inheritdoc cref="RadiusPhysical"/>
    private const int DwellMs = 800;

    /// <summary>
    /// The content column on the 1920x1080 display this application is actually used on, the
    /// same width SettingsRowLayoutTests probes its rows at: roomy, so a label measurement here
    /// is the label's own fit inside the button, not a squeeze by the column.
    /// </summary>
    private const double WideColumnWidth = 1424.0;

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

    // ────── XAML readers ──────
    //
    // The eight readers below are `internal static`, not private, and that is deliberate:
    // HandChunkedPageFitTests derives the content column's WIDTH and HEIGHT from the same four
    // XAML elements (HeaderPanel, ContentArea, ContentCard, PaginationPanel) and must read them
    // through these exact helpers rather than parse the XAML a second time. Each one already
    // FAILS on a missing attribute rather than returning default — see Margin's remarks for the
    // bug that discipline exists to prevent — and a second copy of the parsing would be a second
    // place for that discipline to rot. Nothing else about them changed.

    /// <summary>The parsed SettingsWindow.xaml document.</summary>
    /// <returns>The root element.</returns>
    internal static XElement Xml()
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
    internal static XElement Named(XElement root, string name)
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
    internal static double SidebarWidth(XElement root)
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

    /// <summary>
    /// Reads a named element's <c>Margin</c> through WPF's own converter.
    ///
    /// A missing attribute FAILS rather than returning <c>default</c>, and that is not
    /// pedantry — it is the one thing standing between this suite and a silently wrong column.
    /// This helper returned <c>default</c> until <c>ContentPanel</c>'s <c>Margin="16"</c> was
    /// replaced by <c>ContentCard</c>'s <c>Padding</c>: a derivation site left pointing at the
    /// vanished margin then subtracted zero, computed a column 32 DIP WIDER than the real one,
    /// and every assertion in this file kept passing while measuring a column that does not
    /// exist. Verified by reverting one such site: the whole headless App suite stayed green.
    /// (Deliberately no test count here — the number rots with every test added, and the point is
    /// that NOTHING failed, not how many things passed.)
    /// Enumerating the sites was the only protection; this makes it a guard.
    /// </summary>
    /// <param name="root">The XAML root.</param>
    /// <param name="name">The element's x:Name.</param>
    /// <returns>The declared margin.</returns>
    internal static Thickness Margin(XElement root, string name)
    {
        var attr = Named(root, name).Attribute("Margin");
        Check.WithCustomMessage(
                $"SettingsWindow.xaml's \"{name}\" declares no Margin. If this element's inset "
                + "moved to another element or another property, repoint the caller at it — "
                + "reading a margin that is not there subtracts nothing, widens the derived "
                + "content column, and leaves every fit assertion in this file green against a "
                + "column the application never has.")
            .That(attr).IsNotNull();
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(attr!.Value)!;
    }

    /// <summary>
    /// Reads a named element's <c>Padding</c> through WPF's own converter.
    ///
    /// The content column's inner inset used to be <c>ContentPanel</c>'s own <c>Margin</c>. The
    /// rows now sit on a card (<c>ContentCard</c>) whose <c>Padding</c> replaced it, and
    /// <c>ContentPanel</c> carries no margin at all — which is why <see cref="Margin"/> fails on a
    /// missing attribute rather than returning <c>default</c>: a site left reading the vanished
    /// margin would otherwise compute a column 32 DIP WIDER than the real one with nothing
    /// failing. Hence a second reader rather than a second inline parse.
    /// </summary>
    /// <param name="root">The XAML root.</param>
    /// <param name="name">The element's x:Name.</param>
    /// <returns>The declared padding.</returns>
    internal static Thickness Padding(XElement root, string name)
    {
        var attr = Named(root, name).Attribute("Padding");
        Check.WithCustomMessage(
                $"SettingsWindow.xaml's \"{name}\" no longer declares a Padding. That padding is "
                + "the inner inset of the content-column arithmetic, and "
                + "SettingsWindow.ContentAvailableWidth reads it at runtime, so a missing "
                + "attribute would make every derivation here 32 DIP too wide and nothing would "
                + "fail. Repoint this reader at whatever inset replaced it.")
            .That(attr).IsNotNull();
        return (Thickness)new ThicknessConverter().ConvertFromInvariantString(attr!.Value)!;
    }

    /// <summary>
    /// Reads a named element's fixed <c>Height</c> out of the XAML.
    ///
    /// Same discipline as <see cref="Margin"/> and <see cref="Padding"/>: a missing attribute
    /// FAILS rather than returning zero. <c>HeaderPanel</c>'s Height is a term of
    /// <c>SettingsWindow.ContentAvailableHeight</c>, so a reader that silently saw 0 would
    /// derive a budget the header's whole block too generous and every remaining-room
    /// assertion here would pass against a budget the application never has.
    /// </summary>
    /// <param name="root">The XAML root.</param>
    /// <param name="name">The element's x:Name.</param>
    /// <returns>The declared height in DIP.</returns>
    internal static double FixedHeight(XElement root, string name)
    {
        var attr = Named(root, name).Attribute("Height");
        Check.WithCustomMessage(
                $"SettingsWindow.xaml's \"{name}\" declares no fixed Height. "
                + "SettingsWindow.ContentAvailableHeight subtracts that Height before the first "
                + "layout pass (ActualHeight is still 0 while the content is built in the ctor), "
                + "so an Auto-height header would make the budget wrong in production AND make "
                + "every derivation here read zero. Repoint this reader, do not delete it.")
            .That(attr).IsNotNull();
        var parsed = double.TryParse(attr!.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var height);
        Check.WithCustomMessage(
                $"\"{name}\" declares Height=\"{attr.Value}\", which is not an absolute number.")
            .That(parsed).IsTrue();
        return height;
    }

    /// <summary>
    /// Rebuilds the settings content area at the given window width and drops one real picker
    /// button into it, returning where everything landed.
    ///
    /// Mirrors SettingsWindow.xaml element for element: the two-column root Grid, the content
    /// Grid at column 1 with its margin and its Star/Auto rows, and the <c>ContentCard</c> Border
    /// carrying the padding that replaced <c>ContentPanel</c>'s margin, holding
    /// <c>ContentPanel</c>. Every one of those decides where the button's right edge falls, so
    /// none may be dropped.
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

        var card = new Border
        {
            Padding = Padding(root, "ContentCard"),
            VerticalAlignment = VerticalAlignment.Top
        };
        var panel = new StackPanel();
        card.Child = panel;
        Grid.SetRow(card, 0);
        contentArea.Children.Add(card);

        // A real DwellButton, built the way QuickActionsPageSource's ChunkPresets builds one. Its
        // Root.Margin and its width plumbing then come from production rather than from a copy of
        // them here.
        var button = new DwellButton(
            new NullRegistrar(), "fit_probe", "Some Preset Label", () => { },
            default, DwellCategory.Control, radiusPhysical: 50, dwellMs: 500,
            width: SettingsWindow.PickerButtonWidth, height: 80);

        // The one line under test. availableWidth is recomputed here exactly as
        // ContentAvailableWidth computes it inside the window, from the same three XAML numbers.
        var available = windowWidth
            - SidebarWidth(root)
            - Margin(root, "ContentArea").Left - Margin(root, "ContentArea").Right
            - Padding(root, "ContentCard").Left - Padding(root, "ContentCard").Right;
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
    internal static double WindowWidthFor(double screenWidthDip) =>
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
    public void TheSidebarAndTheContentColumn_AreStillJointlySatisfiable()
    {
        // What the two fit suites do NOT say on their own. SettingsTabLabelFitTests pushes the
        // sidebar wider; this file's cap gives the content column whatever is left. Both read the
        // same live ColumnDefinitions attribute, so a widening moves both at once and
        // ThePickerButton_FitsTheContentColumn_OnANarrowDisplay does fail once the sidebar eats
        // enough — but it fails reporting an overrun, which reads as "the button is too wide" when
        // the cause is "the sidebar is". This says which, and how much room is actually left.
        //
        // The mechanism: ConstrainToContentWidth's floor is MinimumDwellableWidth, which exists so
        // a control is never drawn narrower than the dwell circle it registers. Once the content
        // column falls below that floor the floor wins, the cap stops capping, and controls hang
        // off the window again — the exact failure the cap was added to fix, reintroduced from the
        // other side.
        var root = Xml();
        var available = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - Margin(root, "ContentArea").Left - Margin(root, "ContentArea").Right
            - Padding(root, "ContentCard").Left - Padding(root, "ContentCard").Right;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The {0:F0} DIP sidebar leaves only {1:F2} DIP of content column on a {2:F0} DIP "
                + "display, at or below the {3:F2} DIP MinimumDwellableWidth floor. Below that "
                + "floor ConstrainToContentWidth stops constraining — it returns the floor instead "
                + "of the available width — so every fixed-width control hangs off the window "
                + "again. Widen SettingsWindow.WindowSizeRatio, narrow the margins, or shorten the "
                + "tab labels so SettingsTabLabelFitTests needs less sidebar. Do not lower the "
                + "floor: it is hit geometry, not aesthetics.",
                SidebarWidth(root), available, NarrowDisplayWidthDip,
                SettingsWindow.MinimumDwellableWidth))
            .That(available).IsStrictlyGreaterThan(SettingsWindow.MinimumDwellableWidth);
    }

    [StaFact]
    public void TheWidestSeparatorLabel_FitsTheContentColumn()
    {
        // DwellLabel is attached by its own Add overload, which does NOT cap it: a label is a bare
        // TextBlock with no Border root, and it registers no dwell target, so nothing about it can
        // move hit geometry out from under a visual. That makes an overrun cosmetic — but cosmetic
        // on a settings page the user reads with their eyes, and this branch raised the label from
        // FontSize 22 to Tokens.FontSizeTitle (24), so it is a shape this work made worse and
        // should therefore measure rather than assume.
        //
        // The widest label in the file is a run of box-drawing characters used as a rule. Its
        // length is read out of the source, not restated, so lengthening the rule fails here
        // instead of silently overflowing. The rules moved out of SettingsWindow.xaml.cs into
        // ModelsPageSource as part of the settings-tab decomposition, so the source-read now
        // targets that file (the measurement logic below is unchanged).
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "Sources", "ModelsPageSource.cs"));
        var runs = Regex.Matches(source, @"new string\('\\u2500',\s*(\d+)\)")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

        Check.WithCustomMessage(
                "ModelsPageSource.cs no longer builds a box-drawing separator label. If the "
                + "rules were replaced by a Border or removed, delete this test; if they merely "
                + "moved or changed character, repoint the pattern.")
            .That(runs).Not.IsEmpty();

        var root = Xml();
        var available = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - Margin(root, "ContentArea").Left - Margin(root, "ContentArea").Right
            - Padding(root, "ContentCard").Left - Padding(root, "ContentCard").Right;

        var longest = runs.Max();
        var label = new DwellLabel(new string('\u2500', longest));

        // SettingsWindow is an OverlayWindow, whose constructor sets FontFamily = Tokens.FontStandard
        // and lets it inherit down. Without replicating that the measurement runs in the system
        // font and reports a width the application never produces.
        var host = new StackPanel();
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        label.Attach(host);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = label.Root.DesiredSize.Width;

        // Measured: 850.17 DIP wanted against a 416.00 DIP column \u2014 the rule is more than twice
        // the room. It is also NOT a regression: at the FontSize 22 this branch replaced it wanted
        // 779 DIP, so it has overrun a narrow column all along, and on the 1920 DIP display this
        // application is actually used on the column is 1424 DIP and it fits outright.
        //
        // It is nonetheless benign, and this is the part worth pinning rather than the number.
        // U+2500 draws a horizontal stroke across its whole advance width, so a run of them cut at
        // the column edge renders as a rule that spans the column \u2014 which is what a rule is for.
        // The one thing that would NOT be benign is the oversized child dragging the layout with
        // it, so that is what is asserted: the Grid's star column bounds the StackPanel, the panel
        // keeps the column's width, and a layout clip stops the ink at that edge.
        var panel = BuildContentTree(WindowWidthFor(NarrowDisplayWidthDip), out var outer);
        var probe = new DwellLabel(new string('\u2500', longest));
        probe.Attach(panel);
        outer.Measure(new Size(outer.Width, outer.Height));
        outer.Arrange(new Rect(0, 0, outer.Width, outer.Height));
        outer.UpdateLayout();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The {0}-character separator label wants {1:F2} DIP and has widened the content "
                + "panel to {2:F2} DIP, past the {3:F2} DIP column. An over-wide decorative label "
                + "must not drag the layout: everything else on the page is positioned relative to "
                + "this panel, so a panel that grows to fit its widest child pushes real controls "
                + "off the window. If the panel stopped being bounded by a star column, cap the "
                + "label in Add(DwellLabel) the way Add(DwellControl) caps its controls.",
                longest, wanted, panel.ActualWidth, available))
            .That(panel.ActualWidth).IsLessOrEqualThan(available);

        // The clip is what turns "850 DIP of text in a 416 DIP column" into a drawn rule rather
        // than ink over the sidebar. WPF installs it automatically once RenderSize exceeds the
        // arrange slot \u2014 no ClipToBounds required, the same mechanism TrackStatusFitTests measures.
        // Asserted on whichever element actually overflows its slot, because which one that is
        // depends on how StackPanel arranges an over-wide child, and that is WPF's business.
        var clipped = LayoutInformation.GetLayoutClip(probe.Root)
            ?? LayoutInformation.GetLayoutClip(panel);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "Nothing clips the {0:F2} DIP separator label down to the {1:F2} DIP content "
                + "column, so its ink now runs past the panel \u2014 over the sidebar, or off the "
                + "window. WPF installs this layout clip on its own when RenderSize exceeds the "
                + "arrange slot; losing it means something above gained an unbounded width.",
                wanted, available))
            .That(clipped).IsNotNull();

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The layout clip is {0:F2} DIP wide, wider than the {1:F2} DIP content column, so "
                + "it is not what is holding the label inside the column.",
                clipped!.Bounds.Width, available))
            .That(clipped.Bounds.Width).IsLessOrEqualThan(available);
    }

    /// <summary>
    /// Rebuilds SettingsWindow.xaml's content area \u2014 the two-column root Grid, the content Grid at
    /// column 1, the <c>ContentCard</c> Border carrying the padding that replaced
    /// <c>ContentPanel</c>'s margin, and <c>ContentPanel</c> itself \u2014 at a given window width, from the live XAML.
    /// </summary>
    /// <param name="windowWidth">The window's width in DIP.</param>
    /// <param name="outer">The root Grid, for measuring and arranging.</param>
    /// <returns>The content panel that settings controls are dropped into.</returns>
    private static StackPanel BuildContentTree(double windowWidth, out Grid outer)
    {
        const double windowHeight = 900;
        var root = Xml();

        outer = new Grid { Width = windowWidth, Height = windowHeight };
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

        var card = new Border
        {
            Padding = Padding(root, "ContentCard"),
            VerticalAlignment = VerticalAlignment.Top
        };
        var panel = new StackPanel();
        TextElement.SetFontFamily(panel, Tokens.FontStandard);
        card.Child = panel;
        Grid.SetRow(card, 0);
        contentArea.Children.Add(card);
        return panel;
    }

    /// <summary>
    /// The slider row is a composite, and composites are deliberately NOT capped by
    /// ConstrainToContentWidth — DwellSlider owns a horizontal StackPanel, which clips rather
    /// than wraps. So its natural width has to fit the narrowest column outright. The plate that
    /// widened this row from 356 to 404 DIP is the reason this guard exists.
    /// </summary>
    [StaFact]
    public void TheSliderRow_FitsTheContentColumn_OnANarrowDisplay()
    {
        var root = Xml();
        var contentArea = Margin(root, "ContentArea");
        var cardPadding = Padding(root, "ContentCard");
        var available = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - contentArea.Left - contentArea.Right
            - cardPadding.Left - cardPadding.Right;

        var scratch = new StackPanel();
        TextElement.SetFontFamily(scratch, Tokens.FontStandard);
        var slider = new DwellSlider(
            new NullRegistrar(), "sl", "Smoothing Factor", 0, 1, 0.1, 1,
            () => 0.5, _ => { }, DwellCategory.Control, 50, 800);
        slider.Attach(scratch);
        scratch.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Attach adds a DwellLabel heading above the row, so the panel's width is the wider of
        // the two. Both are reported: the row dominates today (404.00 against the heading's 188.11),
        // but a long enough heading could become the maximum, and "narrow PlateWidth" would then
        // be the wrong advice.
        var heading = scratch.Children[0].DesiredSize.Width;
        var row = scratch.Children[1].DesiredSize.Width;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The slider composite wants {0:F2} DIP against a {1:F2} DIP content column on a "
                + "{2:F0} DIP display. It is a composite, so nothing caps it and its StackPanel "
                + "clips rather than wraps — the plus button would be drawn off the panel. The "
                + "heading measures {3:F2} and the stepper row {4:F2}: if the ROW is the larger, "
                + "narrow DwellSlider's PlateWidth; if the HEADING is, shorten that setting's "
                + "label. Either way do not narrow the sidebar, which SettingsTabLabelFitTests "
                + "needs.",
                scratch.DesiredSize.Width, available, NarrowDisplayWidthDip, heading, row))
            .That(scratch.DesiredSize.Width).IsLessOrEqualThan(available);
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

    /// <summary>
    /// Every label a toggle row is built with in the running application, read out of the page
    /// sources rather than restated, so a new setting with a long name fails HERE instead of on
    /// screen.
    ///
    /// <para>Three sources, because the app has three:</para>
    /// <list type="bullet">
    /// <item>the literal <c>ToggleSpec</c> labels in <c>Views/Settings/Sources</c>;</item>
    /// <item><c>ActionMenuPageSource.FriendlyName</c>'s arms, which that page builds one toggle
    /// per <c>InteractionAction</c> from;</item>
    /// <item><c>QuickActionsPageSource</c>'s glyph-plus-label caption, whose label is DATA. The
    /// bound used is a glyph plus two spaces plus the longest <see cref="QuickActionPresets"/>
    /// label — the longest caption the application itself can generate. A label the user types is
    /// unbounded and no enumeration can cover it, which is why
    /// <see cref="AnUnboundedToggleLabel_CannotWidenTheRow"/> pins the MECHANISM that keeps an
    /// arbitrary one safe instead of trying to bound it.</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<string> RealToggleLabels()
    {
        var sources = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "Settings", "Sources");
        var labels = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(
                         text, @"new ToggleSpec\(\s*""[^""]*""\s*,\s*""([^""]*)"""))
                labels.Add(m.Groups[1].Value);
        }

        var actionMenu = File.ReadAllText(Path.Combine(sources, "ActionMenuPageSource.cs"));
        foreach (Match m in Regex.Matches(
                     actionMenu, @"InteractionAction\.\w+\s*=>\s*""([^""]*)"""))
            labels.Add(m.Groups[1].Value);

        // A box-drawing glyph plus the two spaces the caption interpolates, over every preset the
        // app can create an action from. Referenced, not regexed: QuickActionPresets is public.
        var presets = QuickActionPresets.KeyboardShortcuts.Select(p => p.Label)
            .Concat(QuickActionPresets.MediaControls.Select(p => p.Label))
            .Concat(QuickActionPresets.WindowActions.Select(p => p.Label));
        foreach (var preset in presets)
            labels.Add("▦  " + preset);

        return labels;
    }

    /// <summary>The row's label <see cref="TextBlock"/>, found by the text it was built with.</summary>
    private static TextBlock? LabelBlockIn(DependencyObject row, string text)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(row))
        {
            if (child is TextBlock tb && tb.Text == text) return tb;
            if (child is DependencyObject d && LabelBlockIn(d, text) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// <c>DwellToggle</c> stopped being a <c>DwellControl</c> and became an
    /// <c>IDwellComposite</c>, which means <c>SettingsWindow.Add</c> no longer caps it with
    /// <c>ConstrainToContentWidth</c> — composites are deliberately uncapped. The slider row
    /// already needed this guard for the same reason; the toggle row now needs its own, and it is
    /// the row kind that carries arbitrary-length text.
    ///
    /// <para>
    /// TWO things are asserted, and the second is the one a reader is likely to miss. The row must
    /// fit the 416 DIP narrowest content column. And its measured HEIGHT must stay 124 — 100 for
    /// the card plus its 24 DIP bottom margin, exactly what the control measured before this
    /// change: <c>SettingsAutoPagination</c> decides page breaks from row heights, so a label
    /// wrapping to two lines and growing the row would silently re-paginate every page holding a
    /// toggle. The label's own height is checked against the card's 100 as well, since a row that
    /// keeps its height by CLIPPING its label is not a fit either.
    /// </para>
    /// </summary>
    [StaFact]
    public void TheLongestRealToggleLabel_FitsTheContentColumn_OnANarrowDisplay()
    {
        var root = Xml();
        var contentArea = Margin(root, "ContentArea");
        var cardPadding = Padding(root, "ContentCard");
        var available = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - contentArea.Left - contentArea.Right
            - cardPadding.Left - cardPadding.Right;

        var labels = RealToggleLabels();
        Check.WithCustomMessage(
                "No toggle labels were found. Either the page sources stopped declaring "
                + "ToggleSpec rows, or the patterns above went stale — a guard over an empty set "
                + "proves nothing.")
            .That(labels).Not.IsEmpty();

        foreach (var label in labels)
        {
            var scratch = new StackPanel { Width = available };
            TextElement.SetFontFamily(scratch, Tokens.FontStandard);
            var toggle = new DwellToggle(
                new NullRegistrar(), "fit_probe", label, () => true, _ => { },
                DwellCategory.Control, 50, 800);
            toggle.Attach(scratch);
            scratch.Measure(new Size(available, double.PositiveInfinity));

            var row = (FrameworkElement)scratch.Children[0];

            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "The toggle row for \"{0}\" wants {1:F2} DIP against a {2:F2} DIP content "
                    + "column on a {3:F0} DIP display. The toggle is a composite, so nothing caps "
                    + "it — the switch would be drawn off the panel. Shorten the label, or narrow "
                    + "DwellToggle's switch; do not narrow the sidebar, which "
                    + "SettingsTabLabelFitTests needs.",
                    label, row.DesiredSize.Width, available, NarrowDisplayWidthDip))
                .That(row.DesiredSize.Width).IsLessOrEqualThan(available);

            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "The toggle row for \"{0}\" measures {1:F2} DIP tall, not the 124.00 it "
                    + "measured before DwellToggle became a composite (a 100 DIP card plus a 24 "
                    + "DIP bottom margin). SettingsAutoPagination decides page breaks from row "
                    + "heights, so this re-paginates every page holding a toggle.",
                    label, scratch.DesiredSize.Height))
                .That(scratch.DesiredSize.Height).IsEqualTo(124.0);

            var text = LabelBlockIn(row, label);
            Check.WithCustomMessage($"No TextBlock in the row carries the label \"{label}\".")
                .That(text).IsNotNull();

            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "The label \"{0}\" wraps to {1:F2} DIP of height inside a 100 DIP card, so it "
                    + "is clipped. The row keeping its height by cutting its own text is not a "
                    + "fit — shorten the label or widen the column.",
                    label, text!.DesiredSize.Height))
                .That(text.DesiredSize.Height).IsLessOrEqualThan(100.0);
        }
    }

    /// <summary>
    /// A Quick Action's caption carries a label the USER typed, so no enumeration can bound it —
    /// which makes the MECHANISM the thing to pin, not any particular length.
    ///
    /// <para>
    /// That mechanism is the row's STAR label column, and the first draft of this test named the
    /// wrong one. It claimed <c>TextWrapping.Wrap</c> was what kept the switch on the panel;
    /// measured, switching the label to <c>WrapWithOverflow</c> leaves the row's width and the
    /// switch's position completely unchanged, because a star column is bounded by the room the
    /// Grid actually has and WPF clips an over-wide child to its own arrange slot. Wrap earns its
    /// place for a different reason (a long name is laid out over further lines instead of being
    /// cut off mid-word), and this test would have been green through that mutation while claiming
    /// to catch it.
    /// </para>
    /// <para>
    /// So three things are asserted, all falsifiable by turning the label column from star to
    /// auto: the row's width, the row's height, and — the one that is actually about
    /// accessibility — the switch's ARRANGED right edge, since a switch pushed past the column is
    /// a hit circle over pixels the user cannot see. Such a label does wrap to more lines than the
    /// 100 DIP card can show and is clipped vertically; that is a cosmetic loss on text the user
    /// typed themselves and moves no hit geometry, so it is not asserted here.
    /// </para>
    /// </summary>
    [StaFact]
    public void AnUnboundedToggleLabel_CannotWidenTheRow_NorMoveTheSwitch()
    {
        var root = Xml();
        var contentArea = Margin(root, "ContentArea");
        var cardPadding = Padding(root, "ContentCard");
        var available = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - contentArea.Left - contentArea.Right
            - cardPadding.Left - cardPadding.Right;

        // 200 characters with no break opportunity anywhere.
        var label = new string('M', 200);

        var scratch = new StackPanel { Width = available };
        TextElement.SetFontFamily(scratch, Tokens.FontStandard);
        var toggle = new DwellToggle(
            new NullRegistrar(), "fit_probe", label, () => true, _ => { },
            DwellCategory.Control, 50, 800);
        toggle.Attach(scratch);
        scratch.Measure(new Size(available, double.PositiveInfinity));

        var row = (Border)scratch.Children[0];

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "A 200-character unbroken label widened the toggle row to {0:F2} DIP against a "
                + "{1:F2} DIP column. A composite is not capped by ConstrainToContentWidth, so the "
                + "label column has to be a STAR column — an auto one sizes to its child and the "
                + "row grows without limit.",
                row.DesiredSize.Width, available))
            .That(row.DesiredSize.Width).IsLessOrEqualThan(available);

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "The row grew to {0:F2} DIP tall around an over-long label. The card's height is "
                + "explicit precisely so it cannot: SettingsAutoPagination paginates on these "
                + "numbers.",
                scratch.DesiredSize.Height))
            .That(scratch.DesiredSize.Height).IsEqualTo(124.0);

        scratch.Arrange(new Rect(0, 0, available, scratch.DesiredSize.Height));
        scratch.UpdateLayout();

        var switchRoot = SwitchRootIn(row);
        var origin = switchRoot.TransformToAncestor(scratch).Transform(new Point(0, 0));
        var right = origin.X + switchRoot.ActualWidth;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "An over-long label pushed the switch's right edge to {0:F2} DIP, past the {1:F2} "
                + "DIP column. This is the assertion that is about accessibility rather than "
                + "tidiness: the switch registers a hit circle about its own centre, so a switch "
                + "drawn off the panel is a dwell target over pixels the user cannot see.",
                right, available))
            .That(right).IsLessOrEqualThan(available);
    }

    // ────── The page header, and what it takes off every content page ──────

    /// <summary>
    /// A plausible narrow display's HEIGHT in DIP, paired with <see cref="NarrowDisplayWidthDip"/>.
    /// 800 x 600 DIP is the 4:3 panel that width describes (1600x1200 at 150%); a 16:9 one at the
    /// same width is shorter still in absolute DIP only if it is physically shorter, and 4:3 is
    /// the conservative pairing for a height budget because the window is a fixed FRACTION of the
    /// screen either way.
    /// </summary>
    private const double NarrowDisplayHeightDip = 600;

    /// <summary>The window height production would pick for a given primary-screen height.</summary>
    /// <param name="screenHeightDip">The primary screen's height in DIP.</param>
    /// <returns>The settings window's height in DIP.</returns>
    internal static double WindowHeightFor(double screenHeightDip) =>
        screenHeightDip * SettingsWindow.WindowSizeRatio;

    /// <summary>
    /// The page header takes its whole block off EVERY content page at once — exactly as the
    /// sidebar's 220 -> 240 widening took 20 DIP off every content column at once, which is the
    /// failure this file exists to prevent, one axis over.
    ///
    /// What goes wrong if it is too tall is not cosmetic. <c>SettingsAutoPagination</c> chunks
    /// rows against <c>ContentAvailableHeight</c>, so a header leaving less than one labelled
    /// row's worth of room paginates every tab down to one row per page — and for a gaze-only
    /// user each page turn is another dwell, on tabs that already run to ten rows.
    ///
    /// <para>
    /// HONESTY NOTE — this guard is deliberately OPTIMISTIC, and says so rather than pretending
    /// otherwise. It subtracts every term of <c>ContentAvailableHeight</c> that is reachable from
    /// outside the window: the header's Height and Margin, <c>ContentArea</c>'s Margin and
    /// <c>ContentCard</c>'s Padding, all read out of the XAML. It CANNOT subtract the whole of
    /// <c>PaginationReserveHeight</c>: that is an instance property, and two of its four terms
    /// (<c>PaginationButtonHeight</c>, <c>PaginationButtonMargin</c>) are private consts on a
    /// window with eight non-optional dependencies that cannot be constructed here. Only
    /// <c>PaginationPanel</c>'s own Margin is reachable, so the derived budget below is larger
    /// than production's by that button block. The margin against
    /// <c>LabelledRowMinHeight</c> is wide enough that the omission does not decide the outcome —
    /// the failure message prints both numbers so a future reader can check that claim rather
    /// than take it.
    /// </para>
    /// </summary>
    [StaFact]
    public void ThePageHeader_LeavesRoomForALabelledRow()
    {
        var root = Xml();
        var header = Margin(root, "HeaderPanel");
        var contentArea = Margin(root, "ContentArea");
        var cardPadding = Padding(root, "ContentCard");
        var pagination = Margin(root, "PaginationPanel");

        var windowHeight = WindowHeightFor(NarrowDisplayHeightDip);
        var budget = windowHeight
            - FixedHeight(root, "HeaderPanel") - header.Top - header.Bottom
            - contentArea.Top - contentArea.Bottom
            - cardPadding.Top - cardPadding.Bottom
            - pagination.Top - pagination.Bottom;

        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "On a {0:F0} DIP tall display the settings window is {1:F2} DIP tall, and after "
                + "the page header ({2:F2} + {3:F2} margin), ContentArea's margin, ContentCard's "
                + "padding and PaginationPanel's margin only {4:F2} DIP of content column is "
                + "left — less than the {5:F2} DIP a single labelled row occupies. Every tab "
                + "would paginate to one row per page. Production's real budget is SMALLER still "
                + "by the pagination BUTTON block (private consts, unreachable from here), so "
                + "this number is the optimistic one. Shorten the header, do not widen this "
                + "assertion.",
                NarrowDisplayHeightDip, windowHeight,
                FixedHeight(root, "HeaderPanel"), header.Top + header.Bottom,
                budget, SettingsWindow.LabelledRowMinHeight))
            .That(budget).IsStrictlyGreaterThan(SettingsWindow.LabelledRowMinHeight);
    }

    /// <summary>
    /// The header's Height is FIXED (the budget has to subtract it before the first layout pass),
    /// so the two real strings have to fit inside it — a StackPanel with an explicit Height clips
    /// what overflows rather than growing, silently and with no assertion anywhere else noticing.
    ///
    /// Measured against the narrowest supported header width, which is wider than the content
    /// column: the header sits OUTSIDE <c>ContentCard</c>, so it pays <c>ContentArea</c>'s margin
    /// but not the card's padding. Every string comes from the live <c>_tabDefs</c> initializer
    /// via <see cref="SettingsTabDriftTests.RenderedTabHeaders"/>, so editing a description to
    /// something longer is re-measured here automatically.
    /// </summary>
    [StaFact]
    public void EveryHeaderStringFits_TheHeadersFixedHeight()
    {
        var root = Xml();
        var contentArea = Margin(root, "ContentArea");
        var headerWidth = WindowWidthFor(NarrowDisplayWidthDip)
            - SidebarWidth(root)
            - contentArea.Left - contentArea.Right;
        var declaredHeight = FixedHeight(root, "HeaderPanel");
        var descriptionMargin = Margin(root, "HeaderDescription");

        var headers = SettingsTabDriftTests.RenderedTabHeaders();
        Check.WithCustomMessage(
                "Parsed no tab headers, so this measured nothing at all.")
            .That(headers).Not.IsEmpty();

        foreach (var (label, description) in headers)
        {
            // Mirrors HeaderPanel element for element, minus the explicit Height — measuring a
            // panel that already has one would just return that Height and prove nothing.
            var panel = new StackPanel();
            TextElement.SetFontFamily(panel, Tokens.FontStandard);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = Tokens.FontSizeDisplay,
                FontWeight = Tokens.FontWeightStrong,
            });
            panel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Tokens.FontSizeSubtitle,
                TextWrapping = TextWrapping.Wrap,
                Margin = descriptionMargin,
            });

            panel.Measure(new Size(headerWidth, double.PositiveInfinity));

            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "The \"{0}\" tab's header wants {1:F2} DIP of height inside a {2:F2} DIP box, "
                    + "measured against the {3:F2} DIP header width of the narrowest supported "
                    + "display. HeaderPanel's Height is fixed and a StackPanel CLIPS what does "
                    + "not fit, so the description would lose a line with nothing else failing. "
                    + "Shorten the description, or raise HeaderPanel's Height — and if you raise "
                    + "it, ThePageHeader_LeavesRoomForALabelledRow is the assertion that says "
                    + "whether the content column can still afford it.",
                    label, panel.DesiredSize.Height, declaredHeight, headerWidth))
                .That(panel.DesiredSize.Height).IsLessOrEqualThan(declaredHeight);
        }
    }

    /// <summary>
    /// Adding the header pushed every row of <c>ContentArea</c> down by one, and one of the
    /// elements that had to move is invisible: the hidden <c>Settings_Panel_Content</c> TextBlock
    /// the FlaUI suite anchors on. Left on row 0 it would sit INSIDE the header, and the only
    /// suite that reads it is <c>Category=UI</c> — skipped unless <c>EYECTRL_UI_TESTS=1</c>, so
    /// CI could not catch the slip. This makes the row indices checkable headlessly instead: the
    /// anchor must share the card's row, and the header must own the row above it.
    ///
    /// Read as declared attributes, not as layout, so nothing has to be rendered.
    /// </summary>
    [Fact]
    public void ThePageHeader_OwnsTheRowAboveTheCard_AndTheUiaAnchorSitsWithTheCard()
    {
        var root = Xml();

        // The card's row lives on the WRAPPER Grid that holds ContentCard and its zero-layout
        // outline sibling, not on ContentCard itself — reading the card's own Grid.Row would find
        // nothing and compare against null.
        var wrapper = Named(root, "ContentCard").Parent;
        Check.WithCustomMessage(
                "ContentCard's parent is gone. It is expected to be the wrapping Grid that also "
                + "holds ContentCardOutline, and that wrapper is what carries Grid.Row.")
            .That(wrapper).IsNotNull();

        var cardRow = (string?)wrapper!.Attribute("Grid.Row");
        var headerRow = (string?)Named(root, "HeaderPanel").Attribute("Grid.Row");
        var pagerRow = (string?)Named(root, "PaginationPanel").Attribute("Grid.Row");

        var anchor = root.Descendants()
            .SingleOrDefault(e =>
                (string?)e.Attribute("AutomationProperties.AutomationId") == "Settings_Panel_Content");
        Check.WithCustomMessage(
                "SettingsWindow.xaml no longer holds the hidden Settings_Panel_Content TextBlock. "
                + "The Category=UI settings suite anchors on it; if it moved, repoint this check "
                + "at wherever it lives rather than deleting it.")
            .That(anchor).IsNotNull();
        var anchorRow = (string?)anchor!.Attribute("Grid.Row");

        Check.WithCustomMessage(
                $"HeaderPanel is on Grid.Row=\"{headerRow}\" and the content card's wrapper on "
                + $"\"{cardRow}\". The header has to own the row ABOVE the card: sharing a row "
                + "would stack the two on top of each other, and ContentAvailableHeight would be "
                + "subtracting a header that takes no room of its own.")
            .That(headerRow).IsEqualTo("0");

        Check.WithCustomMessage(
                $"The content card's wrapper is on Grid.Row=\"{cardRow}\", expected \"1\" — the "
                + "row below the header.")
            .That(cardRow).IsEqualTo("1");

        Check.WithCustomMessage(
                $"PaginationPanel is on Grid.Row=\"{pagerRow}\", expected \"2\" — below the card. "
                + "ContentArea declares three rows (Auto, *, Auto); a pagination bar left on row 1 "
                + "would render on top of the content card.")
            .That(pagerRow).IsEqualTo("2");

        Check.WithCustomMessage(
                $"The hidden Settings_Panel_Content anchor is on Grid.Row=\"{anchorRow}\" while "
                + $"the content card is on \"{cardRow}\". On the header's row it lands inside the "
                + "page header instead of over the content, and because the ONLY suite that reads "
                + "this anchor is Category=UI, nothing else — headless or in CI — would notice.")
            .That(anchorRow).IsEqualTo(cardRow);
    }

    /// <summary>
    /// <c>ContentAvailableHeight</c> must still SUBTRACT the header, and must do it by reading the
    /// live element rather than restating 112 + 12.
    ///
    /// A source-text guard because the property is private on a window that cannot be
    /// constructed here. It earns its place: with the three header terms deleted from that
    /// expression, the budget over-reports by the header's whole block and — verified by making
    /// exactly that edit — NOTHING else in the headless suites fails. Pagination would simply
    /// pack one row too many onto every page, below the fold, on a screen whose only input
    /// channel is gaze.
    /// </summary>
    [Fact]
    public void TheHeightBudget_StillCountsThePageHeader()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));
        var start = source.IndexOf("private double ContentAvailableHeight", StringComparison.Ordinal);
        Check.WithCustomMessage(
                "SettingsWindow no longer declares ContentAvailableHeight. That property is the "
                + "content column's height budget; repoint this guard at whatever replaced it.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.That(end).IsStrictlyGreaterThan(start);
        var body = source[start..end];

        foreach (var term in new[]
                 { "HeaderPanel.Height", "HeaderPanel.Margin.Top", "HeaderPanel.Margin.Bottom" })
        {
            Check.WithCustomMessage(
                    "ContentAvailableHeight no longer subtracts " + term + ". The page header "
                    + "occupies ContentArea's row 0 above the card, so every DIP of it comes off "
                    + "the content column — a budget that ignores it over-reports by the header's "
                    + "whole block and packs a row per page below the fold. Restore the term, or "
                    + "repoint this guard at the element that carries the header's height now — "
                    + "do not delete it: nothing else in the headless suites notices.")
                .That(body).Contains(term);
        }
    }

    /// <summary>
    /// The Models tab's failed-model repair row carries the longest label any settings button
    /// declares — three clauses, with the "dwell to re-download" call to action at the END, so
    /// a clipped last line would cut off exactly the part that tells the user what to do.
    /// Measured through <see cref="SettingsWindow.CreateControl"/> so production changes to
    /// DwellButton's font, padding or wrap behaviour make THIS test fail, not a probe.
    /// </summary>
    [StaFact]
    public void TheRepairRow_WrapsItsLabel_InsideTheRealDangerButton()
    {
        // M3+N8: measure through SettingsWindow.CreateControl so production changes to
        // DwellButton's font, padding or wrap behaviour make THIS test fail, not a probe.
        // AND re-measure the inner TextBlock unclamped: UIElement.Measure clamps DesiredSize
        // to the available size, so a height read off an arranged child inside a fixed-height
        // button can never exceed that height and the assertion would be vacuous.
        //
        // The repair row is 106 DIP tall, not the 100 DIP floor every other Models button
        // uses: this label wraps to THREE lines (95.77 DIP at FontSizeTitle 24) and the dwell
        // progress bar takes the bottom 6 DIP, so a 100 DIP button leaves the label row 94 and
        // clips the third line — the one whose tail carries the "dwell to re-download"
        // instruction. 106 gives the label its full 100. Controller decision 2026-09-23 when
        // the brief's verbatim Height: 100 failed this very assertion by 1.27 DIP.
        const double repairButtonHeight = 106;
        const string repairLabel = "⚠ Qwen2.5 1.5B (q4f16) — model unusable · predictions fall " +
            "back to dictionary · dwell to re-download";
        var control = (DwellControl)SettingsWindow.CreateControl(
            new ButtonSpec("md_fix_fit", repairLabel, () => { },
                ButtonKind.Danger, Width: 544, Height: repairButtonHeight),
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, WideColumnWidth);

        var host = new System.Windows.Controls.Grid();
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        control.Attach(host);
        host.Measure(new System.Windows.Size(544, double.PositiveInfinity));
        host.Arrange(new System.Windows.Rect(0, 0, 544, host.DesiredSize.Height));
        host.UpdateLayout();

        var label = FindTextBlocks(control.Root)
            .OrderByDescending(t => t.Text?.Length ?? 0).First();

        // Unclamped re-measure at the width the button actually gives the text:
        label.Measure(new System.Windows.Size(label.ActualWidth, double.PositiveInfinity));
        var wrappedHeight = label.DesiredSize.Height;

        // R5: compare against the label's OWN row height (it sits in the layout Grid's label
        // row, above the progress-bar row) — comparing to the button's outer height would
        // pass a label that clips inside its row.
        Check.That(wrappedHeight).IsStrictlyGreaterThan(0);
        if (label.Parent is System.Windows.Controls.Grid layoutGrid
            && System.Windows.Controls.Grid.GetRow(label) < layoutGrid.RowDefinitions.Count)
        {
            var row = System.Windows.Controls.Grid.GetRow(label);
            var rowHeight = layoutGrid.RowDefinitions[row].ActualHeight;
            Check.That(wrappedHeight).IsLessOrEqualThan(rowHeight + 0.5);
        }
        else
        {
            Check.That(wrappedHeight).IsStrictlyLessThan(repairButtonHeight); // fallback: outer height
        }
    }

    private static IEnumerable<System.Windows.Controls.TextBlock> FindTextBlocks(
        System.Windows.Media.Visual root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(root, i)
                is not System.Windows.Media.Visual child)
                continue;
            if (child is System.Windows.Controls.TextBlock tb)
                yield return tb;
            foreach (var nested in FindTextBlocks(child))
                yield return nested;
        }
    }

    /// <summary>
    /// The switch inside a toggle row. The card's child is a wrapper Grid holding the label/switch
    /// row and a non-layout outline Border; inside that row the one <see cref="Border"/> among the
    /// DIRECT children is the switch (the other child is the label <see cref="TextBlock"/>; the
    /// track and knob are Borders too but sit deeper). Reached through the public tree because
    /// <c>EyeCtrl.App.Tests</c> has no <c>InternalsVisibleTo</c> into <c>EyeCtrl.Controls</c>.
    /// </summary>
    private static Border SwitchRootIn(Border row) =>
        ((Grid)row.Child).Children.OfType<Grid>().Single()
        .Children.OfType<Border>().Single();
}
