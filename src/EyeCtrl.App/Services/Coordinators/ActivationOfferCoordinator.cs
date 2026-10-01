namespace EyeCtrl.App.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Ticket 8's App-side half of the activation offer. Two jobs:
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
/// data, the <c>SetRepeatSuppressionZones</c> pattern.</item>
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
    private readonly Func<EventCoordinationContext, PhysicalRect[]> _collectZones;
    private bool _publishScheduled;

    public ActivationOfferCoordinator(
        ToolbarLifecycleCoordinator toolbar,
        Func<EventCoordinationContext, PhysicalRect[]>? collectZones = null)
    {
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _collectZones = collectZones ?? ActivationChromeZones.CollectFromProduction;
    }

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
            Context.InteractionManager.SetActivationChromeZones(_collectZones(Context));
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
    }
}
