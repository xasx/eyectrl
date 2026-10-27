namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The paused strip, shown WHENEVER the app is paused (a pure function of the paused state — any
/// toolbar mode, any provider), so the user always has pause feedback even when the toolbar — and
/// its Play button — is off-screen. The ring relies on it as the pause feedback.
///
/// <para>
/// <b>B2a: the Resume disc.</b> While the toolbar band is hidden the strip also carries a dwellable
/// Resume target mid-edge (r50, play glyph, icon-only with a UIA name) and a short state label. The
/// dwell itself lives in <see cref="ResumeStripViewModel"/>; this window only draws it where
/// <see cref="ResumeStripLayout"/> puts it and reports that circle back as the dwell target, so the
/// disc the user sees is the target gaze is evaluated against. The window stays click-through —
/// Resume is a pure gaze dwell, never a mouse target.
/// </para>
/// </summary>
public partial class PauseIndicatorWindow : OverlayWindow, IPauseIndicatorOverlay
{
    private ResumeStripViewModel? _viewModel;
    private string _edge = "Top";
    private Rect? _bandLogical;
    private DwellArcButton? _disc;

    public PauseIndicatorWindow()
    {
        InitializeComponent();
        Relayout();
    }

    /// <summary>The last computed layout, in screen-logical DIP — the placement test seam.</summary>
    internal ResumeStripLayout.Result? LastLayout { get; private set; }

    /// <summary>The drawn Resume disc, or null before the first layout — the visual test seam.</summary>
    internal DwellArcButton? ResumeDisc => _disc;

    /// <summary>
    /// Attaches the Resume view model. The window drives its strip/band inputs and its target
    /// placement; the view model drives the disc's visibility and progress.
    /// </summary>
    public void SetViewModel(ResumeStripViewModel viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        PlaceTarget();
        ApplyResumeVisibility();
    }

    /// <summary>
    /// The paused state from the strip's own command. B2b: the window is also shown for TrackingLost
    /// and NoTracker, so Show/Hide no longer imply it; the adapter sets it before showing and before
    /// hiding, so the Resume target stops dwelling before the window goes.
    /// </summary>
    public void SetPaused(bool paused) => _viewModel?.SetStripShown(paused);

    /// <summary>B1's committed control state (B2b): picks the label; only Paused keeps the disc.</summary>
    public void SetControlState(ControlState state) => _viewModel?.SetControlState(state);

    /// <summary>
    /// The label card's bounds in physical pixels while the window AND the card are shown (B2b: the
    /// note gate's "status strip" occluder). The card only — the window spans the whole edge and
    /// is transparent and click-through everywhere else. Read per gaze sample on the UI thread.
    /// </summary>
    public bool TryGetVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        if (!IsVisible || StateLabelCard.Visibility != Visibility.Visible || LastLayout is not { } layout)
            return false;

        var card = layout.Label;
        bounds = new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(card.X), DpiHelper.LogicalToPhysicalY(card.Y),
            DpiHelper.LogicalToPhysicalX(card.Width), DpiHelper.LogicalToPhysicalY(card.Height));
        return true;
    }

    /// <summary>The label text for each strip state (B2b). Crisis text: the "you have no control"
    /// states.</summary>
    internal static string LabelTextFor(ResumeStripLabel label) => label switch
    {
        ResumeStripLabel.TrackingLost => StringTable.Current.T("overlays.status.trackingLost"),
        ResumeStripLabel.NoTracker => StringTable.Current.T("overlays.status.noTracker"),
        _ => StringTable.Current.T("overlays.pause.label"),
    };

    /// <summary>
    /// The toolbar band became visible or hidden. While it is visible the Resume disc and label are
    /// hidden — the band has its own Play. When it hides, the disc returns and the strip is raised
    /// again, so a NoteWindow shown in between can never cover the Resume target.
    /// </summary>
    public void SetBandVisible(bool visible)
    {
        _viewModel?.SetBandVisible(visible);
        if (!visible && IsVisible)
            RaiseToTop();
    }

    /// <summary>
    /// The toolbar band's rest rectangle in physical pixels (the auto-hide controller's own rect).
    /// The disc is placed past it, so it never overlaps the band.
    /// </summary>
    public void SetBandBounds(PhysicalRect bounds)
    {
        _bandLogical = bounds.Width <= 0 || bounds.Height <= 0
            ? null
            : new Rect(
                DpiHelper.PhysicalToLogicalX(bounds.X), DpiHelper.PhysicalToLogicalY(bounds.Y),
                DpiHelper.PhysicalToLogicalX(bounds.Width), DpiHelper.PhysicalToLogicalY(bounds.Height));
        Relayout();
    }

    /// <summary>
    /// Positions the strip along the given docked edge. Uses SystemParameters.WorkArea in logical
    /// units, matching ToolbarWindow.SetMode's coordinate space (single primary display).
    /// </summary>
    public void SetEdge(string edge)
    {
        _edge = edge;
        Relayout();
    }

    private void Relayout()
    {
        var layout = ResumeStripLayout.Compute(
            SystemParameters.WorkArea, _edge, _bandLogical,
            ResumeStripLayout.OutSlotKeepOuts(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        LastLayout = layout;

        // No SizeToContent — explicit Width/Height take effect directly.
        var window = layout.Window;
        Left = window.Left;
        Top = window.Top;
        Width = window.Width;
        Height = window.Height;

        Place(StripBar, layout.Strip, window);
        Place(StateLabelCard, layout.Label, window);

        ResumeLayer.Children.Clear();
        var local = new Point(layout.DiscCentre.X - window.Left, layout.DiscCentre.Y - window.Top);
        _disc = DwellArcButton.Create(
            local, ResumeStripLayout.DiscRadiusDip, symbol: null,
            icon: ToolbarIcons.For(ToolbarIconKey.Play),
            iconSize: DwellArcButton.IconSize.Solo,
            accessibleName: StringTable.Current.T("overlays.pause.resume"));
        _disc.AddTo(ResumeLayer);
        _disc.UpdateProgress(_viewModel?.DwellProgress ?? 0);
        _disc.SetPaused(_viewModel?.IsDwellPaused ?? false);

        PlaceTarget();
    }

    private static void Place(FrameworkElement element, Rect rect, Rect window)
    {
        Canvas.SetLeft(element, rect.Left - window.Left);
        Canvas.SetTop(element, rect.Top - window.Top);
        element.Width = rect.Width;
        element.Height = rect.Height;
    }

    /// <summary>Reports the drawn disc to the view model as its dwell target, in physical pixels.</summary>
    private void PlaceTarget()
    {
        if (_viewModel is null || LastLayout is not { } layout) return;
        _viewModel.PlaceTarget(
            new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(layout.DiscCentre.X),
                DpiHelper.LogicalToPhysicalY(layout.DiscCentre.Y)),
            DpiHelper.LogicalToPhysicalX(ResumeStripLayout.DiscRadiusDip));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ResumeStripViewModel.IsVisible):
            case nameof(ResumeStripViewModel.IsLabelVisible):
            case nameof(ResumeStripViewModel.Label):
                ApplyResumeVisibility();
                break;
            case nameof(ResumeStripViewModel.DwellProgress):
                _disc?.UpdateProgress(_viewModel!.DwellProgress);
                break;
            case nameof(ResumeStripViewModel.IsDwellPaused):
                _disc?.SetPaused(_viewModel!.IsDwellPaused);
                break;
        }
    }

    private void ApplyResumeVisibility()
    {
        ResumeLayer.Visibility = _viewModel?.IsVisible == true ? Visibility.Visible : Visibility.Collapsed;
        StateLabelCard.Visibility = _viewModel?.IsLabelVisible == true ? Visibility.Visible : Visibility.Collapsed;
        StateLabel.Text = LabelTextFor(_viewModel?.Label ?? ResumeStripLabel.Paused);
    }
}
