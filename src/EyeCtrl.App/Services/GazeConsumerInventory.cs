namespace EyeCtrl.App.Services;

using System;
using System.Collections.Generic;
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
/// cover, as plain data. <see cref="Live"/> is the one call the collision gate needs; the six
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
/// </list>
/// <para>
/// The class also implements <see cref="IOverlayCollision"/> (the seam Task 10's service already
/// gates on) and raises <see cref="IOverlayCollision.Changed"/> on a SUPERSET of the trigger set
/// <c>ActivationOfferCoordinator.SchedulePublish</c> listens to — keyboard/menus VM
/// <c>PropertyChanged</c>, toolbar visibility and toolbar settings, the display-geometry,
/// keyboard-edge and external-dwell messages — PLUS the bars' own <c>ButtonSetChanged</c>
/// (buttons are added/removed while a bar is up, which moves its rect without touching
/// <c>IsVisible</c>) and <c>NoteTargetsChangedMessage</c>. It is a superset, not an exact copy:
/// the offer coordinator's <c>ExternalDwellActiveMessage</c> handler CLEARS offers rather than
/// publishing zones, so that trigger serves a different purpose there — for this inventory an
/// external target appearing is a consumer change like any other, and the re-check must run.
/// The service re-checks on every raise.
/// </para>
/// <para>
/// The toolbar itself is NOT a source: the panel's placement (§3.3) puts it beyond the strip's
/// outer end by construction. Toolbar changes still raise <see cref="IOverlayCollision.Changed"/>
/// because they MOVE the anchor the re-check's consumer set is judged against — the offer
/// coordinator's parity, kept deliberately.
/// </para>
/// <para>
/// Overlays outside the five sources are classified <c>exempt:&lt;reason&gt;</c> in
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
    private readonly ToolbarLifecycleBridge _bridge;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private readonly ToolbarLifecycleCoordinator? _toolbarCoordinator;

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
    /// <param name="bridge">The late-bound toolbar lifecycle bridge: the attached coordinator's
    /// <c>ToolbarVisibilityChanged</c> is the slide/auto-hide trigger. Null-coordinator is
    /// tolerated (D14: a comfort feature's trigger set degrades, it does not crash the enable
    /// edge).</param>
    /// <param name="messenger">The app messenger: the three change messages.</param>
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
        _messenger.Register<GazeConsumerInventory, NoteTargetsChangedMessage>(this, OnNoteTargetsChanged);
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
        if (_keyboardVm.IsVisible && _overlays().Keyboard.TryGetChromeBounds(out var keyboard))
            rects.Add(keyboard);

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

    private void OnNoteTargetsChanged(GazeConsumerInventory recipient, NoteTargetsChangedMessage message)
        => OnConsumersChanged();

    private void OnConsumersChanged() => Changed?.Invoke();

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
        _messenger.UnregisterAll(this);
    }
}
