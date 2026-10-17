namespace EyeCtrl.App.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Ticket 8's App-side half of the activation offer. Three jobs:
///
/// <list type="number">
/// <item><b>Geometry.</b> Screen bounds and DPI scale are App knowledge (Core owns no screen API),
/// so this coordinator hands them to <c>GazeEngine.ConfigureActivationOffers</c> at startup and again
/// after a display change. Core rebuilds the engine on the gaze thread and drops a live offer, which
/// is right: its B was placed for the old geometry.</item>
/// <item><b>Chrome zones.</b> No offer may be made over EyeCtrl's own controls (decision 8), so the
/// zones are re-published on every trigger the plan lists: keyboard show, hide and layout; toolbar
/// slide-in, slide-out and auto-hide; the prediction bar's relayout; the action and modes menus'
/// show and hide. The push goes through <c>InteractionManager.SetActivationChromeZones</c> as plain
/// data, the <c>SetRepeatSuppressionZones</c> pattern; Core re-validates a live offer against them,
/// so chrome that appears over one takes it away (review A F3).</item>
/// <item><b>Suspension.</b> While EyeCtrl's Settings window, the task switcher, the quick-actions
/// grid, the eye-care banner, the adaptation preview panel (D16) or the restart notice card is
/// open, NO offer is made and a live unfired offer is cleared (the user's decision of 2026-10-02,
/// review B F3). A single suspension flag carries that rule, because these surfaces are not
/// chrome to avoid but interactions to yield to entirely.</item>
/// </list>
///
/// <para>
/// It also requests the offer's teardown when a cooperating app takes the dwell over
/// (<see cref="ExternalDwellActiveMessage"/>), so a linger cannot complete behind that app's own
/// overlay.
/// </para>
///
/// <para>
/// The zone collection is injected (<see cref="Func{T, TResult}"/>) so the coordinator's trigger
/// wiring is testable without standing up every overlay window; production uses
/// <see cref="ActivationChromeZones.CollectFromProduction"/>.
/// </para>
/// </summary>
internal sealed class ActivationOfferCoordinator : CoordinatorBase
{
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly ModalInteractionState _modal;
    private readonly Func<EventCoordinationContext, PhysicalRect[]> _collectZones;
    private bool _publishScheduled;
    private bool _bannerActive;
    private bool _previewVisible;
    private bool _restartNoticeActive;
    private bool _suspended;
    private PhysicalRect? _lagMarkZone;

    public ActivationOfferCoordinator(
        ToolbarLifecycleCoordinator toolbar,
        ModalInteractionState modal,
        Func<EventCoordinationContext, PhysicalRect[]>? collectZones = null)
    {
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _collectZones = collectZones ?? ActivationChromeZones.CollectFromProduction;
    }

    /// <summary>
    /// The combined modal-suspension state <see cref="RecomputeSuspension"/> last computed (P2 D1):
    /// the note gate's suspender input, read through the composed predicate EventCoordinator wires.
    /// True while ANY of the collected reasons holds — Settings, probe, task switcher, quick-actions
    /// grid, eye-care banner, adaptation preview, restart notice. When T4 adds the "note move"
    /// reason it is excluded HERE, because a note Move must suspend offers but not the note gate
    /// itself. Null-safe by construction: the flag exists from construction, so a read before the
    /// coordinator is initialized (the note predicate is wired first) simply reads false.
    /// </summary>
    public bool ModalSuspended => _suspended;

    protected override void OnInitialize()
    {
        var context = Context!;
        ConfigureEngine();

        // Toolbar slide-in/out and auto-hide (the adapter's event fires for both).
        _toolbar.ToolbarVisibilityChanged += OnToolbarVisibilityChanged;
        // Keyboard show/hide, action-menu and modes-menu show/hide.
        context.KeyboardVM.PropertyChanged += OnKeyboardVMPropertyChanged;
        context.ActionMenuVM.PropertyChanged += OnActionMenuVMPropertyChanged;
        context.ModesMenuVM.PropertyChanged += OnModesMenuVMPropertyChanged;
        // A toolbar size/edge edit moves its rect; the prediction bar's relayout shows up as a
        // keyboard SizeChanged, which the deferred publish reads as fresh bounds.
        context.Settings.Toolbar.PropertyChanged += OnToolbarSettingsChanged;
        // The modal surfaces that suspend offers entirely.
        _modal.SettingsOpenChanged += OnSettingsOpenChanged;
        _modal.ProbeOpenChanged += OnProbeOpenChanged;
        context.TaskSwitcherVM.PropertyChanged += OnTaskSwitcherVMPropertyChanged;
        context.QuickActionsGridVM.PropertyChanged += OnQuickActionsGridVMPropertyChanged;

        context.Messenger.Register<ActivationOfferCoordinator, DisplayGeometryChangedMessage>(this, (_, _) =>
        {
            ConfigureEngine();
            SchedulePublish();
        });
        context.Messenger.Register<ActivationOfferCoordinator, KeyboardEdgeToggledMessage>(this, (_, _) =>
            SchedulePublish());
        context.Messenger.Register<ActivationOfferCoordinator, ExternalDwellActiveMessage>(this, (_, message) =>
        {
            if (message.Active)
                context.InteractionManager.RequestActivationOfferClear();
        });
        context.Messenger.Register<ActivationOfferCoordinator, EyeCareBannerActiveMessage>(this, (_, message) =>
        {
            _bannerActive = message.Active;
            RecomputeSuspension();
        });
        // D16: the adaptation preview panel is a suspension input. The service publishes its
        // visibility; it never writes the offers flag itself — this coordinator stays the single
        // writer that recomputes from every input.
        context.Messenger.Register<ActivationOfferCoordinator, PreviewPanelActiveMessage>(this, (_, message) =>
        {
            _previewVisible = message.Active;
            RecomputeSuspension();
        });
        // The restart notice card is the banner's twin: a transient dwell card over the desktop.
        context.Messenger.Register<ActivationOfferCoordinator, RestartNoticeActiveMessage>(this, (_, message) =>
        {
            _restartNoticeActive = message.Active;
            RecomputeSuspension();
        });
        // The T0(a) lag-mark button is a ZONE, not a suspension: it carries its own dwell target
        // while it is up, so no offer may be placed over it, but the session must stay normal use.
        context.Messenger.Register<ActivationOfferCoordinator, LagMarkZoneMessage>(this, (_, message) =>
        {
            _lagMarkZone = message.Zone;
            SchedulePublish();
        });
        // P2 D7: a structural note change (create/remove/state flip) moves the note chrome zones —
        // the coalesced publish re-collects them, so no offer is ever placed over a note.
        context.Messenger.Register<ActivationOfferCoordinator, NoteTargetsChangedMessage>(this, (_, _) =>
            SchedulePublish());

        RecomputeSuspension();
        SchedulePublish();
    }

    private void OnToolbarVisibilityChanged(bool _) => SchedulePublish();
    private void OnKeyboardVMPropertyChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // IsVisible: show/hide (including the layout flip's re-show). IsEmojiMode: the prediction
        // bar's show/hide — emoji mode collapses it and shrinks the window, so the zone must follow.
        if (e.PropertyName is nameof(KeyboardViewModel.IsVisible) or nameof(KeyboardViewModel.IsEmojiMode))
            SchedulePublish();
    }

    private void OnActionMenuVMPropertyChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
            SchedulePublish();
    }

    private void OnModesMenuVMPropertyChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModesMenuViewModel.IsVisible))
            SchedulePublish();
    }

    private void OnToolbarSettingsChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        => SchedulePublish();

    private void OnSettingsOpenChanged(bool _) => RecomputeSuspension();

    private void OnProbeOpenChanged(bool _) => RecomputeSuspension();

    private void OnTaskSwitcherVMPropertyChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskSwitcherViewModel.IsVisible))
            RecomputeSuspension();
    }

    private void OnQuickActionsGridVMPropertyChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickActionsGridViewModel.IsVisible))
            RecomputeSuspension();
    }

    // One combined flag: any open modal surface suspends offers. Applied by the gaze thread through
    // the ordinary request path (Core clears a live unfired offer on the rise). Each change is logged
    // — a live session's gap in Offer-shown lines must be explainable (ticket 8 fixup 02, review B).
    private string _lastSuspensionLog = string.Empty;

    private void RecomputeSuspension()
    {
        if (Context is null)
            return;

        var reasons = new List<string>(7);
        if (_modal.SettingsOpen)
            reasons.Add("Settings");
        if (_modal.ProbeOpen)
            reasons.Add("probe");
        if (Context.TaskSwitcherVM.IsVisible)
            reasons.Add("task switcher");
        if (Context.QuickActionsGridVM.IsVisible)
            reasons.Add("quick actions");
        if (_bannerActive)
            reasons.Add("eye-care banner");
        if (_previewVisible)
            reasons.Add("adaptation preview");
        if (_restartNoticeActive)
            reasons.Add("restart notice");

        bool suspended = reasons.Count > 0;
        _suspended = suspended;
        string reason = suspended ? string.Join(", ", reasons) : "none";
        if ($"{suspended}|{reason}" != _lastSuspensionLog)
        {
            _lastSuspensionLog = $"{suspended}|{reason}";
            Serilog.Log.Information("Activation offers suspended: {Suspended} ({Reason})", suspended, reason);
        }

        Context.InteractionManager.SetActivationOffersSuspended(suspended);
    }

    // One layout pass of slack: keyboards and menus are SizeToContent, so on the synchronously
    // delivered show/hide the window may not have measured yet. Coalesced — a burst of triggers
    // costs one publish.
    private void SchedulePublish()
    {
        if (Context is null || _publishScheduled)
            return;
        _publishScheduled = true;
        Context.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _publishScheduled = false;
            var zones = new List<PhysicalRect>(_collectZones(Context));
            // The lag button's hit area joins the zones while it is visible (T0(a) spec §2.2).
            if (_lagMarkZone is { } lagMark)
                zones.Add(lagMark);

            // The live check's proof that the no-offer rule is in force, and when (review B F2).
            Serilog.Log.Information(
                "Activation chrome zones: {Count} {Zones}",
                zones.Count,
                string.Join("; ", zones.Select(z => string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"[{z.X:F0},{z.Y:F0} {z.Width:F0}×{z.Height:F0}]"))));
            Context.InteractionManager.SetActivationChromeZones(zones.ToArray());

            // Corner fix (2026-10-17): the docked toolbar's summon edge is the one off-screen strip a
            // settle is still refused in — the WHOLE edge (fixup 01 R-F1: the summon gate fires at
            // any coordinate past it), so clamping a summon gaze would flash an offer before the
            // toolbar slides in. Pushed with the zones, as plain data; a floating toolbar has no band.
            var summonBand = Context.Settings.Toolbar.IsDocked && _toolbar.AutoHideEdge is { } summonEdge
                ? ActivationSummonBand.FromDockedEdge(summonEdge)
                : null;
            Context.InteractionManager.SetActivationSummonBand(summonBand);
        });
    }

    private void ConfigureEngine()
    {
        if (Context is null)
            return;

        var screenBoundsPx = new PhysicalRect(
            0,
            0,
            System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
        Context.GazeEngine.ConfigureActivationOffers(screenBoundsPx, DpiHelper.ScaleX);
    }

    protected override void OnDispose()
    {
        if (Context is null)
            return;
        _toolbar.ToolbarVisibilityChanged -= OnToolbarVisibilityChanged;
        Context.KeyboardVM.PropertyChanged -= OnKeyboardVMPropertyChanged;
        Context.ActionMenuVM.PropertyChanged -= OnActionMenuVMPropertyChanged;
        Context.ModesMenuVM.PropertyChanged -= OnModesMenuVMPropertyChanged;
        Context.Settings.Toolbar.PropertyChanged -= OnToolbarSettingsChanged;
        _modal.SettingsOpenChanged -= OnSettingsOpenChanged;
        _modal.ProbeOpenChanged -= OnProbeOpenChanged;
        Context.TaskSwitcherVM.PropertyChanged -= OnTaskSwitcherVMPropertyChanged;
        Context.QuickActionsGridVM.PropertyChanged -= OnQuickActionsGridVMPropertyChanged;
    }
}
