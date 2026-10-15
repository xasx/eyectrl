namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.Logging;

/// <summary>
/// The D17 gaze-consumer inventory (Task 11): the surfaces whose rects a preview panel must never
/// cover, as plain data. <see cref="Live"/> is the one call the collision gate needs; the seven
/// named sources are:
///
/// <list type="bullet">
/// <item><b>Keyboard</b> — the whole window (key grid, prediction bar, category bar) via
/// <see cref="KeyboardWindow.TryGetChromeBounds"/>, gated on <c>KeyboardVM.IsVisible</c> AND a
/// successful bounds read — the same visible-and-sourced rule
/// <see cref="ActivationChromeZones.CollectFromProduction"/> applies to the offer's chrome
/// zones.</item>
/// <item><b>Edge-button bars</b> — BOTH <c>EdgeButtonBarViewModel</c> instances (the shared bar
/// that shows with the keyboard/drag/scroll contexts, and the keyed action-menu modifier bar):
/// the union of the buttons' dwell hit areas (<see cref="EdgeButtonBarBounds"/>), the same
/// over-covering union <see cref="ActivationChromeZones.ModesMenuBounds"/> builds from row
/// positions.</item>
/// <item><b>Action menu / modes menu</b> — the ticket-8 derived rects, REUSED from
/// <see cref="ActivationChromeZones"/> rather than duplicated here.</item>
/// <item><b>External dwell</b> — the cooperating apps' target rects via
/// <c>ExternalDwellService.LiveTargetRects</c> (read under its state lock), injected as a
/// delegate so this class stays constructible headlessly.</item>
/// <item><b>Notes</b> (review F1) — <c>NoteManager.LiveTargetRects</c>: the dwell rects this
/// process itself feeds on its own private detector (a note's close/edit/move buttons, its
/// <c>:place</c> target mid-move). Notes are non-modal and freely positioned, so a note parked
/// beyond the strip's end is the same R4-H1 harm as a keyboard there; the detector snapshot (not
/// re-derived button math) is read so the inventory can never disagree with what gaze is
/// actually evaluated against. Delegate-injected like external dwell.</item>
/// <item><b>Restart notice</b> (T15 merge) — <c>RestartNoticeBridge.LiveTargetRects</c>: the
/// card's close-button dwell rect while the card shows. The card (main's
/// <c>RestartNoticeWindow</c>) runs its own detector like the notes do, is click-through and
/// non-modal, and can appear over the desktop anywhere the placement puts it — a panel covering
/// its close button is the same R4-H1 harm. The bridge is the late-bound holder (the
/// <see cref="ToolbarLifecycleBridge"/> pattern): App.xaml.cs news the card after the container
/// is built, so the delegate reaches it through a mutable singleton rather than DI.</item>
/// </list>
/// <para>
/// The class also implements <see cref="IOverlayCollision"/> (the seam Task 10's service already
/// gates on) and raises <see cref="IOverlayCollision.Changed"/> on a SUPERSET of the trigger set
/// <c>ActivationOfferCoordinator.SchedulePublish</c> listens to — keyboard/menus VM
/// <c>PropertyChanged</c>, toolbar visibility and toolbar settings, the display-geometry,
/// keyboard-edge and external-dwell messages — PLUS the bars' own <c>ButtonSetChanged</c>
/// (buttons are added/removed while a bar is up, which moves its rect without touching
/// <c>IsVisible</c>), <c>NoteTargetsChangedMessage</c>,
/// <c>ExternalDwellTargetsChangedMessage</c> (fix-wave F5) and
/// <c>RestartNoticeActiveMessage</c> (T15 merge: the card's show/hide moves a source in and out).
/// It is a superset, not an exact copy:
/// the offer coordinator's <c>ExternalDwellActiveMessage</c> handler CLEARS offers rather than
/// publishing zones, so that message serves a different purpose there — and it is NOT this
/// inventory's external-dwell structural trigger: it fires only once gaze has ENTERED a target
/// (the service's <c>SetCursorRingHidden</c>), never on register/update/unregister, so the
/// load-bearing external signal is <c>ExternalDwellTargetsChangedMessage</c>, raised
/// dispatcher-marshalled from the service's three pipe arms (a client registering or moving a
/// target under a visible panel must re-check before the reader dwells it). The service
/// re-checks on every raise — delivered one dispatcher hop after the trigger
/// (Background: after layout), plus the keyboard window's own SizeChanged/Loaded (final review
/// F2: a keyboard relayout raises no VM flip).
/// </para>
/// <para>
/// The toolbar itself is NOT a source: the panel's placement (§3.3) puts it beyond the strip's
/// outer end by construction. Toolbar changes still raise <see cref="IOverlayCollision.Changed"/>
/// because they MOVE the anchor the re-check's consumer set is judged against — the offer
/// coordinator's parity, kept deliberately.
/// </para>
/// <para>
/// Overlays outside the seven sources are classified <c>exempt:&lt;reason&gt;</c> in
/// <c>docs/design/overlay-unification-spec.md</c> §3.1, and
/// <c>OverlayInventoryDriftTests</c> fails the build when a row's classification drifts from the
/// inventory's actual source list.
/// </para>
/// </summary>
public interface IGazeConsumerInventory
{
    /// <summary>The physical rects of every gaze-consuming surface that is currently up.</summary>
    IReadOnlyList<PhysicalRect> Live();
}

/// <summary>
/// The production inventory (D17). Constructed lazily inside the DI container exactly like the
/// preview service itself: nothing resolves it until the first adaptation enable edge, so no
/// overlay HWND or subscription exists while the feature is off (P9's discipline). All reads
/// happen on the UI thread (the only thread the preview service calls from).
/// </summary>
internal sealed class GazeConsumerInventory : IOverlayCollision, IGazeConsumerInventory, IDisposable
{
    private readonly EyeCtrlSettings _settings;
    private readonly Func<IOverlayManager> _overlays;
    private readonly KeyboardViewModel _keyboardVm;
    private readonly ActionMenuViewModel _actionMenuVm;
    private readonly ModesMenuViewModel _modesMenuVm;
    private readonly EdgeButtonBarViewModel _edgeBarVm;
    private readonly EdgeButtonBarViewModel _modBarVm;
    private readonly Func<IReadOnlyList<PhysicalRect>> _externalTargetRects;
    private readonly Func<IReadOnlyList<PhysicalRect>> _noteTargetRects;
    private readonly Func<IReadOnlyList<PhysicalRect>> _restartNoticeTargetRects;
    private readonly ToolbarLifecycleBridge _bridge;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private readonly ToolbarLifecycleCoordinator? _toolbarCoordinator;

    // Final review F2: the re-check's timing discipline and the keyboard's layout-event hook.
    // Captured at construction — the inventory is built on the UI thread (the lazy enable edge),
    // and every trigger arrives there too.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private KeyboardWindow? _hookedKeyboard;

    /// <param name="settings">The live settings tree: <c>Settings.Toolbar</c>'s PropertyChanged is
    /// a trigger (a dock/edge/size edit moves the anchor and the consumer geometry with it).</param>
    /// <param name="overlays">Lazy: the overlay manager, reached only when a keyboard rect is
    /// actually needed.</param>
    /// <param name="keyboardVm">The keyboard's visibility gate.</param>
    /// <param name="actionMenuVm">The action menu's centre/radius source.</param>
    /// <param name="modesMenuVm">The modes menu's row source.</param>
    /// <param name="edgeBarVm">The SHARED edge bar's view model (keyboard pair, drag, scroll).</param>
    /// <param name="modBarVm">The keyed action-menu modifier bar's view model — a second
    /// <see cref="EdgeButtonBarWindow"/> that must not escape the inventory.</param>
    /// <param name="externalTargetRects">The external-dwell rect source
    /// (<c>ExternalDwellService.LiveTargetRects</c>); a delegate so tests and this class stay free
    /// of the pipe service's construction weight.</param>
    /// <param name="noteTargetRects">The note dwell-target rect source (review F1,
    /// <c>NoteManager.LiveTargetRects</c>); a delegate for the same headless-construction
    /// reason.</param>
    /// <param name="restartNoticeTargetRects">The restart-notice card's close-target rect source
    /// (<c>RestartNoticeBridge.LiveTargetRects</c>); a delegate for the same reason — the card is
    /// App-owned, built after the container, and reached through the bridge.</param>
    /// <param name="bridge">The late-bound toolbar lifecycle bridge: the attached coordinator's
    /// <c>ToolbarVisibilityChanged</c> is the slide/auto-hide trigger. Null-coordinator is
    /// tolerated (D14: a comfort feature's trigger set degrades, it does not crash the enable
    /// edge).</param>
    /// <param name="messenger">The app messenger: the change messages.</param>
    /// <param name="logger">Optional logger.</param>
    public GazeConsumerInventory(
        EyeCtrlSettings settings,
        Func<IOverlayManager> overlays,
        KeyboardViewModel keyboardVm,
        ActionMenuViewModel actionMenuVm,
        ModesMenuViewModel modesMenuVm,
        EdgeButtonBarViewModel edgeBarVm,
        EdgeButtonBarViewModel modBarVm,
        Func<IReadOnlyList<PhysicalRect>> externalTargetRects,
        Func<IReadOnlyList<PhysicalRect>> noteTargetRects,
        Func<IReadOnlyList<PhysicalRect>> restartNoticeTargetRects,
        ToolbarLifecycleBridge bridge,
        IMessenger messenger,
        ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _keyboardVm = keyboardVm ?? throw new ArgumentNullException(nameof(keyboardVm));
        _actionMenuVm = actionMenuVm ?? throw new ArgumentNullException(nameof(actionMenuVm));
        _modesMenuVm = modesMenuVm ?? throw new ArgumentNullException(nameof(modesMenuVm));
        _edgeBarVm = edgeBarVm ?? throw new ArgumentNullException(nameof(edgeBarVm));
        _modBarVm = modBarVm ?? throw new ArgumentNullException(nameof(modBarVm));
        _externalTargetRects = externalTargetRects ?? throw new ArgumentNullException(nameof(externalTargetRects));
        _noteTargetRects = noteTargetRects ?? throw new ArgumentNullException(nameof(noteTargetRects));
        _restartNoticeTargetRects = restartNoticeTargetRects
            ?? throw new ArgumentNullException(nameof(restartNoticeTargetRects));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _logger = logger;

        // The trigger wiring — the ActivationOfferCoordinator.SchedulePublish set, plus the bars'
        // own button-set mutations. Named handlers throughout: anonymous lambdas would keep a
        // disposed inventory reachable from the singleton VMs and settings forever.
        _keyboardVm.PropertyChanged += OnKeyboardVmPropertyChanged;
        _actionMenuVm.PropertyChanged += OnActionMenuVmPropertyChanged;
        _modesMenuVm.PropertyChanged += OnModesMenuVmPropertyChanged;
        _edgeBarVm.PropertyChanged += OnEdgeBarVmPropertyChanged;
        _modBarVm.PropertyChanged += OnEdgeBarVmPropertyChanged;
        _edgeBarVm.ButtonSetChanged += OnConsumersChanged;
        _modBarVm.ButtonSetChanged += OnConsumersChanged;
        _settings.Toolbar.PropertyChanged += OnToolbarSettingsChanged;

        _toolbarCoordinator = _bridge.Coordinator;
        if (_toolbarCoordinator is null)
        {
            _logger?.LogWarning(
                "Gaze consumer inventory built before the toolbar coordinator was attached; " +
                "toolbar slide/auto-hide will not re-run the panel collision check");
        }
        else
        {
            _toolbarCoordinator.ToolbarVisibilityChanged += OnToolbarVisibilityChanged;
        }

        _messenger.Register<GazeConsumerInventory, DisplayGeometryChangedMessage>(this, OnGeometryChanged);
        _messenger.Register<GazeConsumerInventory, KeyboardEdgeToggledMessage>(this, OnKeyboardEdgeToggled);
        _messenger.Register<GazeConsumerInventory, ExternalDwellActiveMessage>(this, OnExternalDwellActive);
        _messenger.Register<GazeConsumerInventory, ExternalDwellTargetsChangedMessage>(this, OnExternalDwellTargetsChanged);
        _messenger.Register<GazeConsumerInventory, NoteTargetsChangedMessage>(this, OnNoteTargetsChanged);
        _messenger.Register<GazeConsumerInventory, RestartNoticeActiveMessage>(this, OnRestartNoticeActive);
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Strict overlap (Task 1: touching edges are clear) against every live consumer.</summary>
    public bool IsClear(PhysicalRect panelRect)
    {
        foreach (var consumer in Live())
        {
            if (consumer.IntersectsWith(panelRect))
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<PhysicalRect> Live()
    {
        var rects = new List<PhysicalRect>(8);

        // Keyboard: the whole window — grid, prediction bar and category bar together (the same
        // rect the offer's chrome zones use). Visible AND sourced; an unmeasured window
        // contributes nothing rather than a phantom rect at the origin.
        if (_keyboardVm.IsVisible)
        {
            var keyboard = _overlays().Keyboard;
            HookKeyboardLayoutEvents(keyboard);
            if (keyboard.TryGetChromeBounds(out var keyboardRect))
                rects.Add(keyboardRect);
        }

        // The two edge-button bars: union of the dwell hit areas.
        if (_edgeBarVm.IsVisible
            && EdgeButtonBarBounds(_edgeBarVm.SeedCenters, ButtonRadii(_edgeBarVm), DpiHelper.ScaleX) is { } edgeBar)
            rects.Add(edgeBar);
        if (_modBarVm.IsVisible
            && EdgeButtonBarBounds(_modBarVm.SeedCenters, ButtonRadii(_modBarVm), DpiHelper.ScaleX) is { } modBar)
            rects.Add(modBar);

        // The two menus: the ticket-8 derived rects, reused (not duplicated).
        if (_actionMenuVm.IsVisible)
            rects.Add(ActivationChromeZones.ActionMenuBounds(
                _actionMenuVm.Center, _actionMenuVm.EffectiveRingRadius, DpiHelper.ScaleX));
        if (_modesMenuVm.IsVisible
            && ActivationChromeZones.ModesMenuBounds(_modesMenuVm.RowScreenPositions) is { } modesMenu)
            rects.Add(modesMenu);

        // External dwell: the cooperating apps' own rects.
        rects.AddRange(_externalTargetRects());

        // Notes (review F1): this process's own note dwell rects — the detector snapshot, so the
        // inventory and the fed geometry cannot drift.
        rects.AddRange(_noteTargetRects());

        // The restart-notice card (T15 merge): its close button while the card shows — the same
        // own-detector snapshot rule as the notes, reached through the App-attached bridge.
        rects.AddRange(_restartNoticeTargetRects());
        return rects;
    }

    /// <summary>
    /// One edge bar's rect, in physical px (pure — headless-testable): the union of the buttons'
    /// dwell hit squares, seed centres ± the physical radius (<c>RadiusLogical</c> × scale — the
    /// same conversion <c>EdgeButtonBarViewModel.AddTarget</c> uses when it registers the hit
    /// target, so the zone is exactly the interactive surface). Null when the bar holds no
    /// buttons (nothing to protect, and a zero-size rect at the origin would be a phantom zone).
    /// </summary>
    internal static PhysicalRect? EdgeButtonBarBounds(
        IReadOnlyDictionary<string, PhysicalPoint> seedCenters,
        IReadOnlyList<(string Id, double RadiusLogical)> buttons,
        double scale)
    {
        if (buttons.Count == 0)
            return null;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (id, radiusLogical) in buttons)
        {
            if (!seedCenters.TryGetValue(id, out var center))
                continue; // a state without a seed cannot be aimed at; it has no hit area yet
            double radius = radiusLogical * scale;
            minX = Math.Min(minX, center.X - radius);
            minY = Math.Min(minY, center.Y - radius);
            maxX = Math.Max(maxX, center.X + radius);
            maxY = Math.Max(maxY, center.Y + radius);
        }
        if (maxX <= minX)
            return null; // every listed button lacked a seed
        return new PhysicalRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static (string Id, double RadiusLogical)[] ButtonRadii(EdgeButtonBarViewModel vm)
    {
        var buttons = new (string Id, double RadiusLogical)[vm.Buttons.Count];
        for (var i = 0; i < vm.Buttons.Count; i++)
            buttons[i] = (vm.Buttons[i].Id, vm.Buttons[i].RadiusLogical);
        return buttons;
    }

    // ── the trigger set ──

    private void OnKeyboardVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // IsVisible: show/hide (incl. the layout flip's re-show). IsEmojiMode: the prediction bar
        // collapses and the window shrinks. The offer coordinator's exact pair — the keyboard VM
        // raises PropertyChanged continuously while typing, and a re-check per keypress is a
        // per-sample inventory walk.
        if (e.PropertyName is nameof(KeyboardViewModel.IsVisible) or nameof(KeyboardViewModel.IsEmojiMode))
            OnConsumersChanged();
    }

    private void OnActionMenuVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
            OnConsumersChanged();
    }

    private void OnModesMenuVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModesMenuViewModel.IsVisible))
            OnConsumersChanged();
    }

    private void OnEdgeBarVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EdgeButtonBarViewModel.IsVisible))
            OnConsumersChanged();
    }

    private void OnToolbarSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnConsumersChanged();

    private void OnToolbarVisibilityChanged(bool visible) => OnConsumersChanged();

    private void OnGeometryChanged(GazeConsumerInventory recipient, DisplayGeometryChangedMessage message)
        => OnConsumersChanged();

    private void OnKeyboardEdgeToggled(GazeConsumerInventory recipient, KeyboardEdgeToggledMessage message)
        => OnConsumersChanged();

    private void OnExternalDwellActive(GazeConsumerInventory recipient, ExternalDwellActiveMessage message)
        => OnConsumersChanged();

    private void OnExternalDwellTargetsChanged(
        GazeConsumerInventory recipient, ExternalDwellTargetsChangedMessage message)
        => OnConsumersChanged();

    private void OnNoteTargetsChanged(GazeConsumerInventory recipient, NoteTargetsChangedMessage message)
        => OnConsumersChanged();

    private void OnRestartNoticeActive(GazeConsumerInventory recipient, RestartNoticeActiveMessage message)
        => OnConsumersChanged();

    private void OnConsumersChanged()
    {
        // Final review F2 (the D17 first-show gap): KeyboardCoordinator sends ShowKeyboardMessage
        // at the END of the dispatcher callback that called Keyboard.Show(), and a keyboard
        // RELAYOUT (the prediction bar's growth while typing) raises no VM property flip at all —
        // so a raise delivered synchronously with the trigger can re-check against a keyboard
        // that has not reached its final layout yet, or never re-check at all (the relayout case:
        // nothing re-triggers until an unrelated chrome event, and the freshly grown keyboard
        // sits under the panel — the R4-H1 harm class). Deferred one dispatcher hop to
        // Background — below Render (layout) and Loaded, so every consumer is measurable when
        // the re-check runs — mirroring ActivationOfferCoordinator.SchedulePublish's
        // one-layout-pass-of-slack discipline. Deliberately NOT coalesced: each trigger still
        // raises exactly one Changed (the trigger set is low-frequency, a raise is a cheap
        // same-thread re-check for the single subscriber, and the per-trigger count is a pinned
        // test contract). Live()/IsClear stay synchronous — the show-time gate must read truth at
        // show time; only the re-check's SCHEDULING moves.
        _dispatcher.InvokeAsync(() => Changed?.Invoke(), DispatcherPriority.Background);
    }

    /// <summary>
    /// Final review F2's relayout half: the keyboard window's own layout events as
    /// <see cref="Changed"/> triggers. The prediction bar's relayout shows up as a keyboard
    /// SizeChanged with NO VM property flip (the offer coordinator's own comment) — the VM pair
    /// (IsVisible/IsEmojiMode) cannot see it, so a keyboard that grows INTO the panel's rect while
    /// the user reads must re-trigger the re-check from the window itself. Hooked lazily at the
    /// first <see cref="Live"/> read that actually touches the keyboard (the overlays factory is
    /// the lazy seam — nothing resolves it while the keyboard is never visible, so headless
    /// construction stays safe), and once only: the window is app-lifetime (the manager's Keyboard
    /// property is the concrete singleton window; CloseAll closes, it never recreates), so a
    /// second hook could only double-raise.
    /// </summary>
    private void HookKeyboardLayoutEvents(KeyboardWindow keyboard)
    {
        if (_hookedKeyboard is not null)
            return;
        _hookedKeyboard = keyboard;
        keyboard.SizeChanged += OnKeyboardLayoutChanged;
        keyboard.Loaded += OnKeyboardLoaded;
    }

    private void OnKeyboardLayoutChanged(object? sender, SizeChangedEventArgs e) => OnConsumersChanged();

    private void OnKeyboardLoaded(object? sender, RoutedEventArgs e) => OnConsumersChanged();

    /// <summary>
    /// The container-dispose path. The container owns this singleton for the whole session, so in
    /// production this runs only at shutdown; tests use it so no inventory outlives its VMs.
    /// </summary>
    public void Dispose()
    {
        _keyboardVm.PropertyChanged -= OnKeyboardVmPropertyChanged;
        _actionMenuVm.PropertyChanged -= OnActionMenuVmPropertyChanged;
        _modesMenuVm.PropertyChanged -= OnModesMenuVmPropertyChanged;
        _edgeBarVm.PropertyChanged -= OnEdgeBarVmPropertyChanged;
        _modBarVm.PropertyChanged -= OnEdgeBarVmPropertyChanged;
        _edgeBarVm.ButtonSetChanged -= OnConsumersChanged;
        _modBarVm.ButtonSetChanged -= OnConsumersChanged;
        _settings.Toolbar.PropertyChanged -= OnToolbarSettingsChanged;
        _toolbarCoordinator?.ToolbarVisibilityChanged -= OnToolbarVisibilityChanged;
        if (_hookedKeyboard is not null)
        {
            _hookedKeyboard.SizeChanged -= OnKeyboardLayoutChanged;
            _hookedKeyboard.Loaded -= OnKeyboardLoaded;
        }
        _messenger.UnregisterAll(this);
    }
}
