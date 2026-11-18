namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Views;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// The first-run guide's window: a fullscreen click-through SurfaceDark surface (the
/// owner-approved live-check amendment, 2026-11-18 — the approved mockups' geometry is the
/// pinned layout) that hides the desktop entirely. RENDER-ONLY for text; the window BUILDS the
/// step visuals and pushes every dwell target's real centre into the VM after layout —
/// PointToScreen yields device (physical) pixels (r2 F9). ONE SLOT PER BUILDER (r4 F4):
/// SetPractice builds into StepContent, the dwell knob into KnobRow, the chrome Close into
/// ChromeCloseHost — neither overwrites another. Every builder's already-built branch
/// RE-REGISTERS (r5 F2: a re-render cleared the targets). No Close() member (r4 F2).
///
/// <para>
/// The cards ARE the Settings control (owner follow-up, 2026-11-18: „use the same controls as
/// settings"): every card is a <see cref="DwellButton"/> built the way the Settings modal builds
/// its buttons — the per-kind fill/label pairing comes from <see cref="ModalButtonPalette"/>
/// (contrast-tested; see its doc for why the pairing cannot be a constant), the label is the
/// control's own centred wrap-on-overflow TextBlock, and dwell progress is the control's OWN
/// bottom-edge bar sweep, not a hand-managed Rectangle. The approved fullscreen OUTER geometry
/// is kept: the sizes (320×190 action, 280×160 Close, 200×160 dwell ±, 360×200 chips), the
/// 12-DIP corner radius and the 32/40 DIP label scale are the approved mockups' literals, set
/// onto the control after construction — DwellButton is size-flexible by design.
/// </para>
///
/// <para>
/// Dwell protocol: the control's self-registration is INERT (see <see cref="InertRegistrar"/>) —
/// the VM owns the targets through its pinned Settings-modal latch protocol
/// (RegisterChoice/UpdateChoiceCentre/ClearChoices/RemoveChromeChoice), so the control never
/// self-activates; its mouse fallback rides <see cref="FirstRunGuideViewModel.OnChromeChoice"/>
/// (inert too: the window is click-through). Progress flows VM → window → control:
/// vm.ChoiceProgress records into the registrar's store and calls
/// <see cref="DwellControl.UpdateVisual"/>, which drives the control's own
/// <c>ApplyProgress</c> — the bar sweep — from the recorded value.
/// </para>
///
/// <para>
/// Theme preview: the XAML half (surface, title, body, read-out, practice ring) re-skins through
/// SetResourceReference + <see cref="CardResources"/> exactly as before (S2-2). A
/// <see cref="DwellButton"/> takes its palette colours at build time (the control snapshots the
/// fill colour in its own brush — see its ctor doc), so the card-local preview re-skins the
/// control half through <see cref="ApplyPreviewTheme"/>: the coordinator's ThemeChosen handler
/// applies the picked theme to Card.Resources (r1 F2, unchanged) and then calls
/// ApplyPreviewTheme, which re-resolves every built card's fill/label pairing from
/// ModalButtonPalette against the picked theme. The progress bar's fill and halo ride
/// SetResourceReference through Card.Resources, so they follow the swap without the repaint.
/// The contrast guarantee survives the swap because the REPAINT goes through the same
/// measured palette the build used — no colour is ever invented here.
/// </para>
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    private readonly FirstRunGuideViewModel _vm;
    private readonly InertRegistrar _registrar = new();
    private readonly Dictionary<string, DwellButton> _choiceButtons = new();
    private readonly Dictionary<string, ModalChoiceKind> _choiceKinds = new();
    private readonly Dictionary<string, DwellButton> _knobButtons = new();    // the knob's OWN references — SetChoices never clears them
    private readonly Dictionary<string, DwellButton> _themeButtons = new();   // the chip grid's OWN references — SetChoices never clears them
    private DwellButton? _chromeCloseButton;
    private Grid? _themeGrid;                                            // re-attached on a Theme re-render (r6 F4)
    private Ellipse? _practiceFill;
    private TextBlock? _dwellValue;
    private bool _knobBuilt;
    private bool _chromeCloseBuilt;

    /// <summary>The theme the card-local preview shows. Starts at the ambient app theme (the
    /// state the first build must match); the coordinator's ThemeChosen handler moves it through
    /// <see cref="ApplyPreviewTheme"/>. The palette resolves against THIS theme, never
    /// Tokens.Current — on the Theme step the two disagree by design (the app stays on the old
    /// theme until the pick is applied app-wide).</summary>
    private ITheme _previewTheme;

    public FirstRunGuideWindow(FirstRunGuideViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _previewTheme = Tokens.Current;

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        // Progress bindings (r3 F7): the practice fill follows the VM; the cards' progress rides
        // the control's own bar through SetChoiceProgress.
        vm.ChoiceProgress += (id, p) => SetChoiceProgress(id, p);
        vm.PracticeProgress += SetPracticeFill;

        // The dwell read-out follows the knob (ONE subscription in the ctor — no pile-up, r4 S3).
        vm.DwellTimeChanged += ms =>
        {
            if (_dwellValue is { } value) value.Text = $"{ms} ms";
        };
    }

    /// <summary>The theme preview's card-local target (the coordinator applies the resolved
    /// theme here; DynamicResource on the surface's brushes picks it up, Tokens.Current stays
    /// untouched).</summary>
    public ResourceDictionary CardResources => Card.Resources;

    public void ShowCardContent(string title, string body, bool chromeClose)
        => _vm.ShowGuideCard(title, body, chromeClose);

    /// <summary>Builds one visual DwellButton per (label, id, kind) triple — the Settings modal's
    /// control and palette pairing at the owner-approved fullscreen sizes — registers each as a
    /// chrome dwell target through the VM (Task 6), then pushes the laid-out centres (r3 F5:
    /// EVERY card gets a visual and a centre). The kind rides the tuple so the safety card's
    /// choices (which CARRY their ModalChoiceKind) colour exactly as they do in a Settings
    /// modal, and the guide's own rows map the same way Settings maps its modal choices:
    /// forward actions → Affirmative, backing out (Skip/Later) → Negative.
    /// Cards are the owner-approved fullscreen sizes (320×190 action — passed per call at the
    /// approved 32 DIP label scale).</summary>
    public void SetChoices(IReadOnlyList<(string Label, string Id, ModalChoiceKind Kind)> choices)
    {
        _vm.ClearChoices();
        _choiceButtons.Clear();
        _choiceKinds.Clear();
        ChoiceRow.Children.Clear();
        for (var i = 0; i < choices.Count; i++)
        {
            var (label, id, kind) = choices[i];
            // The approved mockup carries the 96-DIP inter-card gap on the FIRST card's right
            // margin — so a single-card row keeps the reserved gap and its card sits 96 + the
            // row's 64 margin from the edge, exactly the approved dwell-time mockup's Continue.
            var button = BuildCard(label, id, kind, 320, 190, 32,
                i == 0 ? new Thickness(0, 0, 96, 0) : default);
            _choiceButtons[id] = button;
            _choiceKinds[id] = kind;
            ChoiceRow.Children.Add(button.Root);
            _vm.RegisterChoice(id);
        }
        PushCentres();
    }

    /// <summary>Builds one card: a <see cref="DwellButton"/> coloured by the per-kind
    /// <see cref="ModalButtonPalette"/> pairing (the SAME colours the Settings modal button with
    /// this kind wears — no colour is invented here), then shaped to the owner-approved
    /// fullscreen literals: the approved size, the 12-DIP corner radius and the approved label
    /// scale (32 DIP on the action/Close/chip cards, 40 DIP on the dwell ± glyphs — approved
    /// literals, not the token ramp).
    ///
    /// <para>
    /// DIVERGENCE from the fit gates, documented once here per the live-check protocol: the
    /// catalog gates these labels as modal170 — a ONE-LINE label at the Settings modal button's
    /// face inside a 170-wide button — while THIS card renders the same strings at the approved
    /// fullscreen scale, wrapping, in the approved boxes. The gates stay UNCHANGED (modal170's
    /// measurer is the shared Settings modal geometry, not this card), so the guide simply
    /// renders larger than the class it borrows. The geometry carries it easily: a 320-wide card
    /// wraps at 320 DIP, where the longest shipped labels ("Fertigstellen", "Überspringen",
    /// "Mitternacht") stay one to two lines — far inside the 190-DIP card height.
    /// </para>
    ///
    /// <para>
    /// The dwell bar's fill and halo ride SetResourceReference through Card.Resources (S2-2),
    /// so they re-resolve on a theme preview swap without the palette repaint — the same
    /// mechanism the hand-built cards' bars rode before this became a DwellButton.
    /// </para>
    /// </summary>
    private DwellButton BuildCard(string label, string id, ModalChoiceKind kind,
        double width, double height, double fontSize, Thickness margin)
    {
        var (fill, labelBrush) = ModalButtonPalette.For(kind, _previewTheme);
        var button = new DwellButton(
            _registrar, id, label, () => _vm.OnChromeChoice(id),
            fill, DwellCategory.Modal, radiusPhysical: 0, dwellMs: 0,
            width: width, height: height, textBrush: labelBrush);
        // radiusPhysical/dwellMs are inert: the control never self-registers (the registrar is
        // the InertRegistrar below) — the VM's RegisterChoice owns the real radius (70 DIP
        // default, 40 for the chips) and the chrome dwell time.
        button.Root.Margin = margin;
        button.Root.CornerRadius = new CornerRadius(12);   // the approved mockups' card radius
        ((TextBlock)button.Content).FontSize = fontSize;   // the approved fullscreen label scale
        button.ProgressBar.SetResourceReference(Shape.FillProperty, "BrushAccent");       // S2-2
        button.ProgressBar.SetResourceReference(Shape.StrokeProperty, "BrushStrokeHalo"); // S2-2
        return button;
    }

    /// <summary>The chrome Close card in its OWN host (top-right of the surface, r3 F7 — away from
    /// the action row, so abandoning is never the neighbour of continuing; the approved layout
    /// puts the 280×160 card alone at margin 48). Kind Negative — the fill the Settings modal
    /// gives its backing-out choice (modal_no) and the Settings window gives its Close button,
    /// both AccentDanger. Idempotent; the already-built branch RE-REGISTERS (r5 F2: a re-render
    /// cleared the targets).</summary>
    public void SetChromeClose(bool visible, string label)
    {
        if (visible && !_chromeCloseBuilt)
        {
            _chromeCloseButton = BuildCard(label, "guide_close", ModalChoiceKind.Negative, 280, 160, 32, default);
            _choiceButtons["guide_close"] = _chromeCloseButton;
            _choiceKinds["guide_close"] = ModalChoiceKind.Negative;
            ChromeCloseHost.Content = _chromeCloseButton.Root;
            _vm.RegisterChoice("guide_close");
            _chromeCloseBuilt = true;
            PushCentres();
        }
        else if (visible && _chromeCloseBuilt)
        {
            // Re-render (r6 F4): SetChoices cleared the button entries — restore from the Close's
            // own field, so progress and the preview repaint keep reaching the control.
            _choiceButtons["guide_close"] = _chromeCloseButton;
            _choiceKinds["guide_close"] = ModalChoiceKind.Negative;
            _vm.RegisterChoice("guide_close");
            PushCentres();
        }
        else if (!visible && _chromeCloseBuilt)
        {
            _vm.RemoveChromeChoice("guide_close");
            _choiceButtons.Remove("guide_close");
            _choiceKinds.Remove("guide_close");
            ChromeCloseHost.Content = null;
            _chromeCloseButton = null;
            _chromeCloseBuilt = false;
        }
    }

    /// <summary>The practice target: a stroked ring with a fill element whose size follows the
    /// VM's PracticeProgress (r3 F7 — MeetDwell's lesson is "the ring fills"). The approved
    /// fullscreen ring is 220 DIP with a 6 stroke. Builds into StepContent (r4 F4's slot
    /// rule).</summary>
    public void SetPractice(bool visible)
    {
        _vm.SetPracticeVisible(visible);
        if (visible && StepContent.Content is not Grid)
        {
            _practiceFill = new Ellipse
            {
                Width = 0,
                Height = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _practiceFill.SetResourceReference(Shape.FillProperty, "BrushAccent");
            var ring = new Ellipse
            {
                Width = 220,
                Height = 220,
                StrokeThickness = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ring.SetResourceReference(Shape.StrokeProperty, "BrushAccent");
            var grid = new Grid();
            grid.Children.Add(ring);
            grid.Children.Add(_practiceFill);
            StepContent.Content = grid;
        }
        else if (!visible)
        {
            StepContent.Content = null;
            _practiceFill = null;
        }
        PushCentres();
    }

    /// <summary>The dwell knob (r3 F4): a value read-out plus −/+ chrome cards, built into its
    /// OWN row (r4 F4 — SetPractice's StepContent slot is never touched). The approved layout
    /// centers the row above the action cards. The ± cards are Neutral — the same surface as an
    /// ordinary control button, a plain control rather than a confirm. Arriving registers the ±
    /// targets; an already-built knob RE-REGISTERS on a re-render (r5 F2); leaving removes
    /// them.</summary>
    public void SetDwellKnob(bool visible)
    {
        _vm.SetSliderVisible(visible);
        if (visible && !_knobBuilt)
        {
            _dwellValue = new TextBlock
            {
                Text = $"{_vm.DwellTimeMs} ms",
                // The owner-approved fullscreen read-out (2026-11-18): a 40 DIP literal, not the
                // token ramp the fdb92cd0 font fix rode.
                FontSize = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 32, 0),
            };
            _dwellValue.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
            var plusMinus = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (glyph, id, margin) in new[]
                     {
                         (Value: "−", Id: "guide_dwell_minus", Margin: new Thickness(0)),
                         (Value: "+", Id: "guide_dwell_plus", Margin: new Thickness(96, 0, 0, 0)),
                     })
            {
                var button = BuildCard(glyph, id, ModalChoiceKind.Neutral, 200, 160, 40, margin);
                _choiceButtons[id] = button;
                _choiceKinds[id] = ModalChoiceKind.Neutral;
                _knobButtons[id] = button;
                plusMinus.Children.Add(button.Root);
                _vm.RegisterChoice(id);
            }
            KnobRow.Children.Add(_dwellValue);
            KnobRow.Children.Add(plusMinus);
            _knobBuilt = true;
            PushCentres();
        }
        else if (visible && _knobBuilt)
        {
            // Re-render (r5 F2): SetChoices cleared every chrome target — put the ± back from
            // the knob's OWN button references (SetChoices never touches _knobButtons).
            foreach (var id in new[] { "guide_dwell_minus", "guide_dwell_plus" })
            {
                _choiceButtons[id] = _knobButtons[id];
                _choiceKinds[id] = ModalChoiceKind.Neutral;
                _vm.RegisterChoice(id);
            }
            PushCentres();
        }
        else if (!visible && _knobBuilt)
        {
            KnobRow.Children.Clear();
            _vm.RemoveChromeChoice("guide_dwell_minus");
            _vm.RemoveChromeChoice("guide_dwell_plus");
            _choiceButtons.Remove("guide_dwell_minus");
            _choiceButtons.Remove("guide_dwell_plus");
            _choiceKinds.Remove("guide_dwell_minus");
            _choiceKinds.Remove("guide_dwell_plus");
            _dwellValue = null;
            _knobBuilt = false;
        }
    }

    /// <summary>The Theme step's four chips as a 2×2 grid in StepContent (r5 F4's geometry — one
    /// right-aligned row would run off the surface and the chrome circles would overlap). Each
    /// chip is a Neutral card (the same SurfaceMedium surface the old hand-built chips wore)
    /// registering at radius 40 DIP through
    /// <see cref="FirstRunGuideViewModel.RegisterChoice(string, double?)"/>. Idempotent; the
    /// already-built branch RE-REGISTERS (r5 F2). Chips live in `_themeButtons` — `SetChoices`
    /// never clears them.</summary>
    public void SetThemeGrid(bool visible, IReadOnlyList<(string Label, string Id)> chips)
    {
        if (visible)
        {
            if (_themeButtons.Count == 0)
            {
                var grid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                for (var i = 0; i < chips.Count; i++)
                {
                    var (label, id) = chips[i];
                    var button = BuildCard(label, id, ModalChoiceKind.Neutral, 360, 200, 32,
                        new Thickness(24, 24, 24, 24));
                    _themeButtons[id] = button;
                    Grid.SetRow(button.Root, i / 2);
                    Grid.SetColumn(button.Root, i % 2);
                    grid.Children.Add(button.Root);
                    _choiceButtons[id] = button;                   // r6 F4: progress + centres via _choiceButtons
                    _choiceKinds[id] = ModalChoiceKind.Neutral;
                    _vm.RegisterChoice(id, radiusDip: 40);
                }
                _themeGrid = grid;
                StepContent.Content = grid;
            }
            else
            {
                // Re-render (r6 F4): SetPractice(false) emptied StepContent — re-attach the grid
                // AND restore the chips' button entries (SetChoices cleared them), so a theme
                // can be picked on every visit and the Continue/clear bookkeeping stays live.
                if (StepContent.Content is not Grid g || !ReferenceEquals(g, _themeGrid))
                    StepContent.Content = _themeGrid;
                foreach (var (id, _) in _themeButtons)
                {
                    _choiceButtons[id] = _themeButtons[id];
                    _choiceKinds[id] = ModalChoiceKind.Neutral;
                    _vm.RegisterChoice(id, radiusDip: 40);
                }
            }
            PushCentres();
        }
        else if (_themeButtons.Count > 0)
        {
            foreach (var id in _themeButtons.Keys)
                _vm.RemoveChromeChoice(id);
            _themeButtons.Clear();
            _themeGrid = null;
            StepContent.Content = null;
        }
    }

    /// <summary>The theme pick's control-half repaint: a <see cref="DwellButton"/> colours
    /// itself at build time from <see cref="ModalButtonPalette.For(ModalChoiceKind, ITheme)"/>,
    /// so a card-local preview swap re-resolves EVERY built card's pairing against the picked
    /// theme and repaints fill and label in place. The pairing — and with it the contrast
    /// guarantee — comes from the same measured palette the build used; no colour is invented
    /// here. The progress bars need no repaint: their fill and halo ride SetResourceReference
    /// through Card.Resources (S2-2) and re-resolve on the coordinator's
    /// ThemeResourceApplier.Apply call, as does the XAML half (surface, title, body, read-out,
    /// practice ring). Called by the coordinator's ThemeChosen handler right after that Apply —
    /// the re-render that follows rebuilds only the action row, so the Close, the dwell ± and
    /// the chips (the Theme step's own subject) re-skin HERE.</summary>
    public void ApplyPreviewTheme(ITheme theme)
    {
        _previewTheme = theme;
        foreach (var (id, button) in _choiceButtons)
        {
            var (fill, labelBrush) = ModalButtonPalette.For(_choiceKinds[id], theme);
            button.Root.Background = new SolidColorBrush(fill);
            ((TextBlock)button.Content).Foreground = labelBrush;
        }
    }

    private void SetChoiceProgress(string id, double progress)
    {
        // Record first, then let the CONTROL render: UpdateVisual queries the registrar's store
        // and runs DwellButton's own ApplyProgress (the bottom bar's ScaleX sweep). The VM's
        // ChoiceProgress contract is untouched — this is its window-side consumer, now feeding
        // the control's own progress rendering instead of a hand-managed Rectangle.
        _registrar.Record(id, progress);
        if (_choiceButtons.TryGetValue(id, out var button))
            button.UpdateVisual();
    }

    private void SetPracticeFill(double progress)
    {
        if (_practiceFill is { } fill)
        {
            var size = 214 * Math.Clamp(progress, 0, 1);   // inside the 220 ring (2×3 stroke)
            fill.Width = size;
            fill.Height = size;
        }
    }

    /// <summary>Step 4 hides the WHOLE window (the calibration window takes the screen) and the
    /// advance re-shows it — through the window's own Show/Hide path, never raw Visibility
    /// writes (r1 F26).</summary>
    public void HideGuide() => Hide();
    public void ShowGuide() => Show();

    /// <summary>Pushes every registered target's REAL centre (device px via PointToScreen) into
    /// the VM. Runs after a layout pass; the dispatcher round-trip lets a fresh card's layout
    /// complete first (r2 F9). The surface fills the window at a size fixed in the constructor,
    /// so SizeChanged never fires — an explicit push per build is the mechanism. The geometry
    /// element is the control's <see cref="DwellControl.Root"/> — the Border the approved sizes
    /// land on.</summary>
    private void PushCentres()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            foreach (var (id, button) in _choiceButtons)
            {
                var root = button.Root;
                if (!root.IsVisible) continue;
                var centre = root.PointToScreen(new Point(root.ActualWidth / 2, root.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
            if (StepContent.Content is Grid { Children.Count: > 1 } grid
                && grid.Children[0] is Ellipse ring && ring.IsVisible)
            {
                var centre = ring.PointToScreen(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2));
                _vm.SetPracticeCentre(new PhysicalPoint(centre.X, centre.Y));
            }
            foreach (var (id, button) in _themeButtons)                   // the chip grid's centres
            {
                var root = button.Root;
                if (!root.IsVisible) continue;
                var centre = root.PointToScreen(new Point(root.ActualWidth / 2, root.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
        });
    }

    /// <summary>The registrar the guide's DwellButtons are constructed with — INERT on the
    /// lifecycle half, live on the progress half. The guide's dwell lifecycle is the ViewModel's
    /// (the pinned Settings-modal latch protocol: RegisterChoice at the origin + Hold,
    /// UpdateChoiceCentre's positioned mark, ClearTargets-before-dispatch), so the control never
    /// self-registers and never self-activates — Register/Unregister/UpdatePosition are no-ops
    /// and <see cref="DwellControl.OnActivated"/> can never fire. GetProgress answers from the
    /// progress the window records off vm.ChoiceProgress, so
    /// <see cref="DwellControl.UpdateVisual"/> drives the control's OWN
    /// <see cref="DwellButton.ApplyProgress"/> bar sweep from the VM's detector — the VM event
    /// contract stays exactly as pinned, and the hand-managed progress Rectangles die.</summary>
    private sealed class InertRegistrar : IDwellRegistrar
    {
        private readonly Dictionary<string, double> _progress = new();

        /// <summary>Records the latest progress for an id (the window's vm.ChoiceProgress
        /// consumer calls this before UpdateVisual).</summary>
        public void Record(string id, double progress) => _progress[id] = progress;

        /// <inheritdoc />
        public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs,
            Action onActivated) { }

        /// <inheritdoc />
        public void Unregister(string id, DwellCategory category) { }

        /// <inheritdoc />
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }

        /// <inheritdoc />
        public double GetProgress(string id, DwellCategory category) =>
            _progress.TryGetValue(id, out var progress) ? progress : 0;
    }
}
