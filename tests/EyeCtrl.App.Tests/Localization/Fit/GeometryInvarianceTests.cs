namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// T09 PR 3: geometry invariance — translated text never decides where a gaze target is. Each
/// overlay is built in English and in the pseudo-locale (later in German: <see cref="Snapshot"/>
/// takes the table), hosted at the user's 2.0 in <see cref="FitHost"/>, and every target's id,
/// centre and radius are compared in PHYSICAL pixels through <c>PointToScreen</c>. Where the
/// overlay also computes the target it registers (the ring's item positions, the modes row's
/// centres), that registered geometry is compared too.
///
/// <para>
/// <b>Covered:</b> the action-menu ring (labels on and off), the edge bars (action menu, keyboard,
/// manual selection, drag, scroll and repeat), the modifier bar (latched and not), the modes menu,
/// the toolbar, the calibration target, the eye-care banner's two rings, and the Settings toggle's
/// switch. The Supervisor setup window's switches are compared in EyeCtrl.Supervisor.Tests.
/// </para>
///
/// <para>
/// The overlays resolve their text from <see cref="StringTable.Current"/>, so the pseudo-locale is
/// activated globally — the exclusive "StringTable Tests (App)" collection, with English restored
/// after every test. Each snapshot also proves it is not vacuous: the pseudo-locale build must
/// carry bracketed text somewhere, so a surface that stopped reading the table cannot pass by
/// drawing English twice.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class GeometryInvarianceTests
{
    /// <summary>A sub-pixel allowance for float noise; a moved target moves by whole DIPs.</summary>
    private const double TolerancePx = 0.01;

    private readonly ITestOutputHelper _output;

    public GeometryInvarianceTests(ITestOutputHelper output) => _output = output;

    /// <summary>One target: its id, centre and radius in physical pixels at 2.0.</summary>
    internal sealed record Target(string Id, double X, double Y, double Radius);

    /// <summary>A surface's targets, and the texts it drew (for the non-vacuity check).</summary>
    internal sealed record Snapshot(IReadOnlyList<Target> Targets, IReadOnlyList<string> Texts);

    /// <summary>The surfaces, each a builder that runs under whatever table is active.</summary>
    public static TheoryData<string> Surfaces() =>
    [
        "ring, labels on", "ring, labels off",
        "edge bar: action menu", "edge bar: keyboard", "edge bar: manual selection", "edge bar: drag",
        "edge bar: scroll and repeat", "modifier bar", "modifier bar, latched",
        "modes menu", "toolbar", "calibration target", "eye-care banner",
        "settings toggle, two-column", "settings toggle, stacked",
    ];

    [StaTheory]
    [MemberData(nameof(Surfaces))]
    public void EveryTarget_StaysPut_UnderThePseudoLocale(string surface)
    {
        var english = In(StringTable.EmbeddedEnglish, () => Build(surface));
        var pseudo = In(PseudoLocale.Table, () => Build(surface));

        _output.WriteLine($"{surface}: {english.Targets.Count} targets");
        foreach (var t in english.Targets)
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} ({1:0.##}, {2:0.##}) r {3:0.##}", t.Id, t.X, t.Y, t.Radius));

        Check.WithCustomMessage($"{surface}: no targets — the snapshot measured nothing.").That(english.Targets).Not.IsEmpty();
        Check.WithCustomMessage($"{surface}: the pseudo-locale build drew no pseudo-locale text — it is not reading the table, "
                                + $"so the comparison would be English against English. Texts: {string.Join(" | ", pseudo.Texts.Take(8))}")
            .That(pseudo.Texts.Any(t => t.Contains('[', StringComparison.Ordinal))).IsTrue();

        var moved = Compare(english, pseudo);
        Check.WithCustomMessage($"{surface}: translated text moved a gaze target:\n  " + string.Join("\n  ", moved))
            .That(moved).IsEmpty();
    }

    /// <summary>The differences between two snapshots: missing or extra ids, moved centres, changed radii.</summary>
    internal static List<string> Compare(Snapshot english, Snapshot other)
    {
        var problems = new List<string>();
        var a = english.Targets.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var b = other.Targets.ToDictionary(t => t.Id, StringComparer.Ordinal);
        problems.AddRange(a.Keys.Except(b.Keys).Select(id => $"{id}: missing in the translation"));
        problems.AddRange(b.Keys.Except(a.Keys).Select(id => $"{id}: only in the translation"));
        foreach (var id in a.Keys.Intersect(b.Keys))
        {
            var (x, y) = (a[id], b[id]);
            if (Math.Abs(x.X - y.X) > TolerancePx || Math.Abs(x.Y - y.Y) > TolerancePx || Math.Abs(x.Radius - y.Radius) > TolerancePx)
                problems.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}: ({1:0.##}, {2:0.##}) r {3:0.##} → ({4:0.##}, {5:0.##}) r {6:0.##}",
                    id, x.X, x.Y, x.Radius, y.X, y.Y, y.Radius));
        }

        return problems;
    }

    private static Snapshot In(StringTable table, Func<Snapshot> build)
    {
        StringTable.Activate(table);
        try
        {
            return build();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>A hosted element's box as a target: centre and half-width in physical px.</summary>
    private static Target Of(string id, FrameworkElement circle)
    {
        var topLeft = circle.PointToScreen(new Point(0, 0));
        var bottomRight = circle.PointToScreen(new Point(circle.ActualWidth, circle.ActualHeight));
        return new Target(id, (topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2, (bottomRight.X - topLeft.X) / 2);
    }

    private static IReadOnlyList<string> TextsIn(DependencyObject root) =>
        FitText.Descendants<TextBlock>(root).Select(t => t.Text).Where(t => t.Length > 0).ToList();

    private static Snapshot Build(string surface)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return surface switch
        {
            "ring, labels on" => Ring(showLabels: true),
            "ring, labels off" => Ring(showLabels: false),
            "edge bar: action menu" => EdgeBar(ActionMenuEdgeButtons.Definitions(600)),
            "edge bar: keyboard" => EdgeBar(KeyboardEdgeButtons.Definitions(600)),
            "edge bar: manual selection" => EdgeBar(ManualSelectionEdgeButtons.Definitions(600)),
            "edge bar: drag" => EdgeBar([DragCoordinator.CreateCancelButtonDefinition(600), DragCoordinator.CreateBackButtonDefinition(600)]),
            "edge bar: scroll and repeat" => EdgeBar([
                ScrollModeController.CreateScrollStopButtonDefinition(600, verticalOffset: 0),
                RepeatModeController.CreateRepeatStopButtonDefinition(600, verticalOffset: 220)]),
            "modifier bar" => EdgeBar(ActionMenuModifierButtons.Definitions(600, new HashSet<ModifierKey>())),
            "modifier bar, latched" => EdgeBar(ActionMenuModifierButtons.Definitions(600,
                new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Ctrl, ModifierKey.Alt })),
            "modes menu" => ModesMenu(),
            "toolbar" => Toolbar(),
            "calibration target" => CalibrationTarget(),
            "eye-care banner" => EyeCareBanner(),
            "settings toggle, two-column" => SettingsToggle(HandChunkedColumn()),
            "settings toggle, stacked" => SettingsToggle(416),
            _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
        };
    }

    /// <summary>
    /// The runtime ring, composed the way <c>ActionMenuWindow.Show</c> composes it: the view model's
    /// items (their REGISTERED positions and labels, which it resolves from the active table) painted
    /// through <see cref="ActionMenuRingPainter.Paint"/> at <c>ButtonCanvasCenter</c>. Both the
    /// registered position and the painted disc are compared.
    /// </summary>
    private static Snapshot Ring(bool showLabels)
    {
        var settings = new EyeCtrlSettings();
        settings.ActionMenu.PrimaryActions = [InteractionAction.LeftClick, InteractionAction.RightClick,
            InteractionAction.DoubleClick, InteractionAction.Drag];
        using var vm = new ActionMenuViewModel(settings, new WeakReferenceMessenger());
        var centre = new PhysicalPoint(1800, 1100);
        vm.Receive(new ShowActionMenuMessage(centre, Enum.GetValues<InteractionAction>().ToList(),
            ShowBack: true, DwellTimeMs: 600, centre, CircleSize: 250));

        var canvas = new Canvas { Width = 1400, Height = 1400 };
        var canvasCentre = new Point(700, 700);
        var slots = vm.Items.Select(i => new RingPaintSlot(i.Id,
            ActionMenuGeometry.ButtonCanvasCenter(i.Position, centre, canvasCentre), i.Icon, i.Label, i.Kind, i.IsDisabled)).ToList();
        var buttons = ActionMenuRingPainter.Paint(canvas, slots, RingPaintOptions.Runtime(showLabels));
        using var host = FitHost.Open(canvas, family: Tokens.FontStandard);

        var targets = new List<Target>();
        foreach (var item in vm.Items)
        {
            targets.Add(new Target(item.Id + " (registered)", item.Position.X, item.Position.Y, ActionMenuGeometry.ButtonRadius));
            targets.Add(Of(item.Id, buttons[item.Id].Background));
        }

        // The labels the items carry, drawn or not (labels off draws none, but the slots still
        // carry them as accessible names): the non-vacuity check reads these.
        return new Snapshot(targets, vm.Items.Select(i => i.Label).Concat(TextsIn(canvas)).ToList());
    }

    /// <summary>One edge bar, through the production window (<c>ShowButtons</c> + <c>SyncButtons</c>).</summary>
    private static Snapshot EdgeBar(IReadOnlyList<EdgeButtonDefinition> definitions)
    {
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new EdgeButtonBarWindow { DataContext = vm };
        vm.ShowButtons(definitions, new PhysicalPoint(1000 * EyeCtrl.Core.Services.DpiHelper.ScaleX, 500 * EyeCtrl.Core.Services.DpiHelper.ScaleY));
        window.SyncButtons();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);

        var targets = window.DrawnButtons.Select(pair => Of(pair.Key, pair.Value.Background)).ToList();
        var names = definitions.Select(d => d.Label ?? d.AccessibleName ?? string.Empty);
        var anchors = window.DrawnButtons.Values
            .Select(b => b.AutomationAnchor is { } a ? System.Windows.Automation.AutomationProperties.GetName(a) : string.Empty);
        return new Snapshot(targets, names.Concat(anchors).Concat(TextsIn(root)).ToList());
    }

    /// <summary>The modes menu at the user's toolbar size, through <c>BuildButtonsAt</c>.</summary>
    private static Snapshot ModesMenu()
    {
        const double size = 112;
        var vm = new ModesMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new ModesMenuWindow { Labels = RingLabelSource.Overlays };
        window.Configure(vm, size);
        var registered = window.BuildButtonsAt(size, 400, 300);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);

        var targets = registered.Select(r => new Target(r.Id + " (registered)", r.Centre.X, r.Centre.Y, r.Radius))
            .Concat(window.DrawnButtons.Select(pair => Of(pair.Key, pair.Value.Background)))
            .ToList();
        var names = window.DrawnButtons.Values
            .Select(b => b.AutomationAnchor is { } a ? System.Windows.Automation.AutomationProperties.GetName(a) : b.Label?.Text ?? string.Empty);
        return new Snapshot(targets, names.Concat(TextsIn(root)).ToList());
    }

    /// <summary>The toolbar band, bound the way production binds it; every face.</summary>
    private static Snapshot Toolbar()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);

        string[] ids = ["Pause", "SelectionMode", "Keyboard", "Settings", "TaskSwitcher", "ResetAdaptation"];
        var targets = ids.Select(id => Of(id, window.ButtonForTest(id).Background)).ToList();
        // The band's names ride invisible anchor TextBlocks (AutomationId Toolbar_Button_{id}).
        var names = FitText.Descendants<TextBlock>(root)
            .Where(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t).StartsWith("Toolbar_Button_", StringComparison.Ordinal))
            .Select(t => System.Windows.Automation.AutomationProperties.GetName(t));
        return new Snapshot(targets, names.Concat(TextsIn(root)).ToList());
    }

    /// <summary>
    /// The calibration target, placed by the window's own <c>PositionTarget</c> (reached by
    /// reflection: the window drives it from its view model) with the progress line and the cancel
    /// hint drawn beside it in the active language.
    /// </summary>
    private static Snapshot CalibrationTarget()
    {
        var window = new CalibrationWindow();
        window.ProgressText.Text = CalibrationWindow.ProgressTextFor(
            EyeCtrl.Core.Calibration.CalibrationState.Collecting, index: 2, total: 9, measureOnly: false);
        window.ProgressText.Visibility = Visibility.Visible;
        window.CancelHint.Visibility = Visibility.Visible;
        window.TargetCanvas.Visibility = Visibility.Visible;
        typeof(CalibrationWindow).GetMethod("PositionTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new PhysicalPoint(1200, 700), false]);

        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: 1200, inheritFrom: window);
        return new Snapshot([Of("target", window.TargetDot)], TextsIn(root));
    }

    /// <summary>The eye-care banner's Start and Snooze rings, with their captions in the active language.</summary>
    private static Snapshot EyeCareBanner()
    {
        var window = new EyeCareBannerWindow();
        window.MessageText.Text = StringTable.Current.T("eye-care.banner.message");
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: window.Height, inheritFrom: window);
        return new Snapshot([Of("start", window.StartRing), Of("snooze", window.SnoozeRing)], TextsIn(root));
    }

    private static double HandChunkedColumn() =>
        EyeCtrl.App.Tests.Settings.HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());

    /// <summary>
    /// A Settings toggle row through <see cref="SettingsWindow.CreateControl"/> — the row's name and
    /// state words from the active table — and its switch's box inside the row's CONTROL SLOT, in
    /// physical px at 2.0. Settings is the one surface allowed to re-lay out per language: a page
    /// re-paginates, and in the stacked (narrow) form a longer name pushes the control slot down
    /// (measured: 24 DIP under the pseudo-locale). What must not move is the target within its
    /// slot — its size and its place — and that is what is compared.
    /// </summary>
    private static Snapshot SettingsToggle(double columnWidth)
    {
        var spec = new ToggleSpec("t_probe", StringTable.Current.T("settings.general.pause.label"), () => false, _ => { })
        {
            Description = StringTable.Current.T("settings.general.pause.description"),
        };
        var row = (IDwellComposite)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), SettingsWindow.ControlBtnRadius, 800, columnWidth);
        var panel = new StackPanel { Width = columnWidth };
        row.Attach(panel);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);

        var switchRoot = row.Children.Single().Root;
        var slot = (FrameworkElement)switchRoot.Parent;
        var box = switchRoot.TransformToAncestor(slot).TransformBounds(new Rect(switchRoot.RenderSize));
        var scale = FitHost.Scale;
        return new Snapshot(
            [
                new Target("switch, within its slot", (box.Left + box.Width / 2) * scale, (box.Top + box.Height / 2) * scale, box.Width / 2 * scale),
                new Target("the control slot", slot.ActualWidth * scale, slot.ActualHeight * scale, 0),
            ],
            TextsIn(panel));
    }
}
