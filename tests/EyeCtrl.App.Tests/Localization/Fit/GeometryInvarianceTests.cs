namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// T09 PR 3: geometry invariance — translated text never decides where a gaze target is. Each
/// overlay is built in English and in a longer table, hosted at the user's 2.0 in
/// <see cref="FitHost"/>, and every target's id, centre and radius are compared.
///
/// <para>
/// <b>Two kinds of target, both compared</b> (review-04 R1). The DRAWN target — the painted disc, in
/// PHYSICAL pixels through <c>PointToScreen</c> — and the REGISTERED hit target, the
/// <see cref="DwellTarget"/> the view model added to its <see cref="DwellDetector"/>, which is what
/// gaze is actually evaluated against (read through <see cref="DwellDetector.Targets"/>). The
/// registered targets are compared English against translation like the drawn ones, and each is
/// also compared against its own disc in the same build: a hit target that drifts from its drawing
/// is the same failure, even in one language.
/// </para>
///
/// <para>
/// <b>Four tables.</b> T10's German draft (<see cref="GermanDraft"/>, every surface on under a test
/// manifest; non-vacuous when some drawn text differs from the English), the SHIPPED German (T13:
/// the production manifest and <c>strings.de.json</c> beside the exe, so the binding is the real
/// switch-on, not the test manifest), the pseudo-locale (every word +40 %, like German), and the
/// STRESS table (every word doubled, review-04 D2): a fixed box whose text happens to stay just
/// inside it looks fixed under the pseudo-locale without proving it — "snooze" is 63.8 DIP against
/// its 64 DIP ring. Doubled, every caption overruns its box, so a box that follows its text moves.
/// </para>
///
/// <para>
/// <b>Covered:</b> the action-menu ring (labels on and off), the edge bars (action menu, keyboard,
/// manual selection, drag, scroll and repeat), the modifier bar (latched and not), the modes menu,
/// the toolbar, the calibration target, the eye-care banner's two rings, the glass backdrop's hide
/// button (with its Esc hint shown), the scroll overlay's anchor marks (it registers no dwell
/// target; its stop button is the "scroll and repeat" edge bar), and the Settings toggle's switch.
/// The Supervisor setup window is compared in EyeCtrl.Supervisor.Tests.
/// </para>
///
/// <para>
/// <b>Recorded exceptions</b> — the only places a translation may move a target, each pinned to its
/// one allowed movement by its own test:
/// <list type="bullet">
/// <item>Settings pages re-paginate per language (the ticket's invariant), and in the stacked form a
/// longer row name moves the control slot down: <see cref="SettingsToggle"/> compares the switch
/// WITHIN its slot.</item>
/// <item>The Settings modal: a wrapping message grows the centred card, so its buttons drop by half
/// the extra height — laid out once per show, never under a live dwell
/// (<see cref="SettingsModalExceptionTests"/>).</item>
/// <item>Supervisor setup, a scrolling form, re-flows: its dwell controls may move down, never
/// sideways, never resized, in order (a lower control by at least as much as a higher one) — laid
/// out once per language
/// (<c>SupervisorFitTests.Setup_TheRecordedException_ControlsOnlyReflowDown_InOrder_NeverSideways</c>).</item>
/// </list>
/// </para>
///
/// <para>
/// The overlays resolve their text from <see cref="StringTable.Current"/>, so the table is activated
/// globally — the exclusive "StringTable Tests (App)" collection, with English restored after every
/// test. Each snapshot also proves it is not vacuous: the translated build must carry bracketed text
/// somewhere, so a surface that stopped reading the table cannot pass by drawing English twice.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class GeometryInvarianceTests
{
    /// <summary>A sub-pixel allowance for float noise; a moved target moves by whole DIPs.</summary>
    private const double TolerancePx = 0.01;

    /// <summary>
    /// How far a registered hit target may sit from its drawn disc's centre, in physical px. The two
    /// come from different arithmetic (a view model's formula against WPF's layout of the disc), and
    /// the toolbar's both go through <c>PointToScreen</c>, which snaps to whole pixels; so this allows
    /// rounding, not a moved target (a label-length offset is 4 px or more).
    /// </summary>
    private const double DriftTolerancePx = 1.5;

    private readonly ITestOutputHelper _output;

    public GeometryInvarianceTests(ITestOutputHelper output) => _output = output;

    /// <summary>One target: its id, centre and radius in physical pixels.</summary>
    internal sealed record Target(string Id, double X, double Y, double Radius);

    /// <summary>
    /// A surface's targets (drawn, and registered with a " (hit)" suffix), the texts it drew (for the
    /// non-vacuity check), and each registered target's drift from its own drawing.
    /// </summary>
    internal sealed record Snapshot(IReadOnlyList<Target> Targets, IReadOnlyList<string> Texts, IReadOnlyList<string> Drift);

    private static readonly string[] SurfaceNames =
    [
        "ring, labels on", "ring, labels off",
        "edge bar: action menu", "edge bar: keyboard", "edge bar: manual selection", "edge bar: drag",
        "edge bar: scroll and repeat", "modifier bar", "modifier bar, latched",
        "modes menu", "toolbar", "calibration target", "eye-care banner", "glass backdrop", "scroll overlay",
        "settings toggle, two-column", "settings toggle, stacked",
    ];

    /// <summary>Every surface, against the shipped German, the German draft, the pseudo-locale and the stress table.</summary>
    public static TheoryData<string, string> Surfaces()
    {
        var data = new TheoryData<string, string>();
        foreach (var table in new[] { "shipped", "german", "pseudo", "stress" })
            foreach (var surface in SurfaceNames)
                data.Add(surface, table);
        return data;
    }

    private static StringTable TableNamed(string table) => table switch
    {
        "shipped" => ShippedGerman.Load(),
        "pseudo" => PseudoLocale.Table,
        "stress" => PseudoLocale.StressTable,
        "german" => GermanDraft.Table,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown table."),
    };

    [StaTheory]
    [MemberData(nameof(Surfaces))]
    public void EveryTarget_StaysPut_UnderALongerTable(string surface, string table)
    {
        var english = In(StringTable.EmbeddedEnglish, () => Build(surface));
        var translated = In(TableNamed(table), () => Build(surface));

        _output.WriteLine($"{surface}: {english.Targets.Count} targets");
        foreach (var t in english.Targets)
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} ({1:0.##}, {2:0.##}) r {3:0.##}", t.Id, t.X, t.Y, t.Radius));

        Check.WithCustomMessage($"{surface}: no targets — the snapshot measured nothing.").That(english.Targets).Not.IsEmpty();
        Check.WithCustomMessage($"{surface} ({table}): the translated build drew no translated text — it is not reading the "
                                + $"table, so the comparison would be English against English. Texts: {string.Join(" | ", translated.Texts.Take(8))}")
            .That(table is "german" or "shipped"
                ? translated.Texts.Except(english.Texts, StringComparer.Ordinal).Any()
                : translated.Texts.Any(t => t.Contains('[', StringComparison.Ordinal))).IsTrue();

        Check.WithCustomMessage($"{surface}: a registered hit target is not where its disc is drawn (English):\n  " + string.Join("\n  ", english.Drift))
            .That(english.Drift).IsEmpty();
        Check.WithCustomMessage($"{surface} ({table}): a registered hit target is not where its disc is drawn:\n  " + string.Join("\n  ", translated.Drift))
            .That(translated.Drift).IsEmpty();

        var moved = Compare(english, translated);
        Check.WithCustomMessage($"{surface} ({table}): translated text moved a gaze target:\n  " + string.Join("\n  ", moved))
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

    /// <summary>An element's centre in <paramref name="root"/>'s DIP coordinates (exact; no pixel snapping).</summary>
    private static Point CentreIn(FrameworkElement element, Visual root)
    {
        var box = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        return new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    private static PhysicalPoint ToPhysical(Point logical) =>
        new(DpiHelper.LogicalToPhysicalX(logical.X), DpiHelper.LogicalToPhysicalY(logical.Y));

    private static readonly PropertyInfo DetectorProperty =
        typeof(DwellOverlayViewModelBase).GetProperty("DwellDetector", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DwellOverlayViewModelBase.DwellDetector is gone; repoint the registered-target read.");

    /// <summary>The hit targets a view model has registered: what gaze is evaluated against.</summary>
    private static IReadOnlyList<DwellTarget> Registered(DwellOverlayViewModelBase vm) =>
        ((DwellDetector)DetectorProperty.GetValue(vm)!).Targets;

    /// <summary>Registered targets as snapshot entries, " (hit)"-suffixed, in the view model's physical px.</summary>
    private static IEnumerable<Target> HitTargets(IReadOnlyList<DwellTarget> hits) =>
        hits.Select(t => t.Bounds is { } b
            ? new Target(t.Id + " (hit)", t.Center.X, t.Center.Y, Math.Max(b.Width, b.Height) / 2)
            : new Target(t.Id + " (hit)", t.Center.X, t.Center.Y, t.Radius));

    /// <summary>Each registered target against its drawn disc's centre (both in the same physical frame).</summary>
    private static List<string> DriftOf(IReadOnlyList<DwellTarget> hits, IReadOnlyDictionary<string, PhysicalPoint> drawn)
    {
        var problems = new List<string>();
        if (hits.Count == 0) problems.Add("the view model registered no hit target at all");
        foreach (var hit in hits)
        {
            if (!drawn.TryGetValue(hit.Id, out var disc))
            {
                problems.Add($"{hit.Id}: registered, but no disc is drawn for it");
                continue;
            }

            if (Math.Abs(hit.Center.X - disc.X) > DriftTolerancePx || Math.Abs(hit.Center.Y - disc.Y) > DriftTolerancePx)
                problems.Add(string.Format(CultureInfo.InvariantCulture, "{0}: hit target at ({1:0.##}, {2:0.##}), disc drawn at ({3:0.##}, {4:0.##})",
                    hit.Id, hit.Center.X, hit.Center.Y, disc.X, disc.Y));
        }

        problems.AddRange(drawn.Keys.Except(hits.Select(h => h.Id)).Select(id => $"{id}: drawn, but no hit target is registered for it"));
        return problems;
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
            "glass backdrop" => GlassBackdrop(),
            "scroll overlay" => ScrollOverlay(),
            "settings toggle, two-column" => SettingsToggle(HandChunkedColumn()),
            "settings toggle, stacked" => SettingsToggle(416),
            _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
        };
    }

    /// <summary>
    /// The runtime ring, composed the way <c>ActionMenuWindow.Show</c> composes it: the view model's
    /// items (their positions and labels, which it resolves from the active table) painted through
    /// <see cref="ActionMenuRingPainter.Paint"/> at <c>ButtonCanvasCenter</c>. Compared: the view
    /// model's item positions, its registered hit targets, and the painted discs.
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
            targets.Add(new Target(item.Id + " (item)", item.Position.X, item.Position.Y, ActionMenuGeometry.ButtonRadius));
            targets.Add(Of(item.Id, buttons[item.Id].Background));
        }

        // The disc back in the view model's screen frame: the inverse of ButtonCanvasCenter.
        var drawn = buttons.ToDictionary(pair => pair.Key, pair =>
        {
            var c = CentreIn(pair.Value.Background, canvas);
            var offset = ToPhysical(new Point(c.X - canvasCentre.X, c.Y - canvasCentre.Y));
            return new PhysicalPoint(centre.X + offset.X, centre.Y + offset.Y);
        });
        var hits = Registered(vm);
        targets.AddRange(HitTargets(hits));

        // The labels the items carry, drawn or not (labels off draws none, but the slots still
        // carry them as accessible names): the non-vacuity check reads these.
        return new Snapshot(targets, vm.Items.Select(i => i.Label).Concat(TextsIn(canvas)).ToList(), DriftOf(hits, drawn));
    }

    /// <summary>One edge bar, through the production window (<c>ShowButtons</c> + <c>SyncButtons</c>).</summary>
    private static Snapshot EdgeBar(IReadOnlyList<EdgeButtonDefinition> definitions)
    {
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new EdgeButtonBarWindow { DataContext = vm };
        vm.ShowButtons(definitions, new PhysicalPoint(1000 * DpiHelper.ScaleX, 500 * DpiHelper.ScaleY));
        window.SyncButtons();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);

        var targets = window.DrawnButtons.Select(pair => Of(pair.Key, pair.Value.Background)).ToList();
        // SyncButtons places the window at (Left, Top) and each disc relative to it.
        var drawn = window.DrawnButtons.ToDictionary(pair => pair.Key, pair =>
        {
            var c = CentreIn(pair.Value.Background, root);
            return ToPhysical(new Point(window.Left + c.X, window.Top + c.Y));
        });
        var hits = Registered(vm);
        targets.AddRange(HitTargets(hits));

        var names = definitions.Select(d => d.Label ?? d.AccessibleName ?? string.Empty);
        var anchors = window.DrawnButtons.Values
            .Select(b => b.AutomationAnchor is { } a ? System.Windows.Automation.AutomationProperties.GetName(a) : string.Empty);
        return new Snapshot(targets, names.Concat(anchors).Concat(TextsIn(root)).ToList(), DriftOf(hits, drawn));
    }

    /// <summary>
    /// The modes menu at the user's toolbar size, through <c>BuildButtonsAt</c>, with the row targets
    /// it computed published to the view model as <c>ShowAtTopCentre</c> publishes them.
    /// </summary>
    private static Snapshot ModesMenu()
    {
        const double size = 112;
        var vm = new ModesMenuViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new ModesMenuWindow { Labels = RingLabelSource.Overlays };
        window.Configure(vm, size);
        var registered = window.BuildButtonsAt(size, 400, 300);
        vm.SetRowScreenPositions(registered);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);

        var targets = registered.Select(r => new Target(r.Id + " (reported)", r.Centre.X, r.Centre.Y, r.Radius))
            .Concat(window.DrawnButtons.Select(pair => Of(pair.Key, pair.Value.Background)))
            .ToList();
        var drawn = window.DrawnButtons.ToDictionary(pair => pair.Key, pair =>
        {
            var c = CentreIn(pair.Value.Background, root);
            return ToPhysical(new Point(window.Left + c.X, window.Top + c.Y));
        });
        var hits = Registered(vm);
        targets.AddRange(HitTargets(hits));

        var names = window.DrawnButtons.Values
            .Select(b => b.AutomationAnchor is { } a ? System.Windows.Automation.AutomationProperties.GetName(a) : b.Label?.Text ?? string.Empty);
        return new Snapshot(targets, names.Concat(TextsIn(root)).ToList(), DriftOf(hits, drawn));
    }

    /// <summary>
    /// The toolbar band, bound the way production binds it; every face. Its hit targets are published
    /// by the window itself on the first gaze sample after layout (<c>UpdateGaze</c> →
    /// <c>UpdateTargetPositions</c>, from each face's <c>PointToScreen</c>), so they share the drawn
    /// discs' frame.
    /// </summary>
    private static Snapshot Toolbar()
    {
        var (window, vm) = ToolbarTestHelpers.NewBoundToolbar();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);
        window.UpdateGaze(new PhysicalPoint(0, 0), 0);
        Check.WithCustomMessage("The toolbar published no hit targets after layout.").That(window.TargetsInitialized).IsTrue();

        // Every face: the known ones, and any the view model registered (a hard-coded list once
        // missed "Modes" — the drift check below found it).
        var hits = Registered(vm);
        var ids = new[] { "Pause", "SelectionMode", "Keyboard", "Settings", "TaskSwitcher", "ResetAdaptation", "Modes" }
            .Union(hits.Select(h => h.Id)).ToList();
        var targets = ids.Select(id => Of(id, window.ButtonForTest(id).Background)).ToList();
        var drawn = targets.ToDictionary(t => t.Id, t => new PhysicalPoint(t.X, t.Y));
        targets.AddRange(HitTargets(hits));

        // The band's names ride invisible anchor TextBlocks (AutomationId Toolbar_Button_{id}).
        var names = FitText.Descendants<TextBlock>(root)
            .Where(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t).StartsWith("Toolbar_Button_", StringComparison.Ordinal))
            .Select(t => System.Windows.Automation.AutomationProperties.GetName(t));
        return new Snapshot(targets, names.Concat(TextsIn(root)).ToList(), DriftOf(hits, drawn));
    }

    /// <summary>
    /// The calibration target, placed by the window's own <c>PositionTarget</c> (reached by
    /// reflection: the window drives it from its view model) with the progress line and the cancel
    /// hint drawn beside it in the active language. Not a dwell target, so nothing registered.
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
        return new Snapshot([Of("target", window.TargetDot)], TextsIn(root), []);
    }

    /// <summary>The eye-care banner's Start and Snooze rings, with their captions in the active language.</summary>
    private static Snapshot EyeCareBanner()
    {
        var window = new EyeCareBannerWindow();
        window.MessageText.Text = StringTable.Current.T("eye-care.banner.message");
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: window.Height, inheritFrom: window);
        return new Snapshot([Of("start", window.StartRing), Of("snooze", window.SnoozeRing)], TextsIn(root), []);
    }

    /// <summary>
    /// The glass backdrop's hide button, drawn by <c>DrawHideButton</c> and registered by
    /// <see cref="GlassBackdropViewModel"/> from the same <see cref="ShowBackdropMessage"/> centre, as
    /// <c>OverlayVmBindingCoordinator</c> does; with the Esc hint shown in the active language. The
    /// backdrop covers the screen from its origin, so the canvas's DIP are screen DIP.
    /// </summary>
    private static Snapshot GlassBackdrop()
    {
        var centre = new PhysicalPoint(DpiHelper.LogicalToPhysicalX(1920 - UIConstants.BackdropHideButtonOffset),
            DpiHelper.LogicalToPhysicalY(UIConstants.BackdropHideButtonOffset));
        var vm = new GlassBackdropViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new GlassBackdropWindow();
        window.ShowEscapeHint();
        window.DrawHideButton(centre);
        vm.Receive(new ShowBackdropMessage(centre));
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: 1200, inheritFrom: window);

        var button = (DwellArcButton?)typeof(GlassBackdropWindow).GetField("_hideButton", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("GlassBackdropWindow._hideButton is gone or was not drawn; repoint this read.");
        var disc = button.Background;
        var drawn = new Dictionary<string, PhysicalPoint>(StringComparer.Ordinal) { ["backdrop_hide"] = ToPhysical(CentreIn(disc, root)) };
        var hits = Registered(vm);
        var targets = new List<Target> { Of("backdrop_hide", disc) };
        targets.AddRange(HitTargets(hits));
        return new Snapshot(targets, TextsIn(root).Concat([window.EscapeHint.Text]).ToList(), DriftOf(hits, drawn));
    }

    /// <summary>
    /// The scroll overlay through <c>Place</c> (the headless half of <c>Show</c>), with its Esc hint
    /// in the active language: the anchor ring and dot. It registers no dwell target — that is
    /// asserted, so a target added later joins this gate — and its stop button is an edge bar.
    /// </summary>
    private static Snapshot ScrollOverlay()
    {
        var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings());
        var anchor = new PhysicalPoint(DpiHelper.LogicalToPhysicalX(900), DpiHelper.LogicalToPhysicalY(600));
        vm.EnterScrollMode(anchor, new PhysicalPoint(DpiHelper.LogicalToPhysicalX(1800), DpiHelper.LogicalToPhysicalY(600)));
        var window = new ScrollOverlayWindow();
        window.Place(vm);
        Check.WithCustomMessage("The scroll overlay registered a dwell target: compare it here.").That(Registered(vm)).IsEmpty();

        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: window.Width, height: window.Height, inheritFrom: window);
        return new Snapshot([Of("anchor ring", window.CursorCircle), Of("anchor dot", window.AnchorDot)], TextsIn(root), []);
    }

    private static double HandChunkedColumn() =>
        EyeCtrl.App.Tests.Settings.HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml());

    /// <summary>
    /// A Settings toggle row through <see cref="SettingsWindow.CreateControl"/> — the row's name and
    /// state words from the active table — and its switch's box inside the row's CONTROL SLOT, in
    /// physical px at 2.0. Settings is the one surface allowed to re-lay out per language: a page
    /// re-paginates, and in the stacked (narrow) form a longer name pushes the control slot down
    /// (measured: 24 DIP under the pseudo-locale). What must not move is the target within its
    /// slot — its size and its place — and that is what is compared. (Settings registers its hit
    /// targets per page through its registrar, from these same elements.)
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
            TextsIn(panel),
            []);
    }
}
