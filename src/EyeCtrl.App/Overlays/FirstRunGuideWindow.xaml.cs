namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// The first-run guide's window (spec §5): fullscreen click-through backdrop with a centered
/// interactive card. RENDER-ONLY for text; the window BUILDS the step visuals and pushes every
/// dwell target's real centre into the VM after layout — PointToScreen yields device (physical)
/// pixels (r2 F9). ONE SLOT PER BUILDER (r4 F4): SetPractice builds into StepContent, the dwell
/// knob into KnobRow, the chrome Close into ChromeCloseHost — neither overwrites another. Every
/// builder's already-built branch RE-REGISTERS (r5 F2: a re-render cleared the targets). No
/// Close() member (r4 F2). Code-built brushes ride SetResourceReference (S2-2), so they resolve
/// and RE-RESOLVE through Card.Resources like DynamicResource — a theme preview swap updates
/// the card, the chips, the Close and the practice ring together, not just the XAML half.
/// </summary>
public sealed partial class FirstRunGuideWindow : OverlayWindow
{
    private readonly FirstRunGuideViewModel _vm;
    private readonly Dictionary<string, FrameworkElement> _choiceCards = new();
    private readonly Dictionary<string, Rectangle> _progressBars = new();
    private readonly Dictionary<string, Border> _knobCards = new();      // the knob's OWN references — SetChoices never clears them
    private readonly Dictionary<string, Rectangle> _knobBars = new();
    private readonly Dictionary<string, Border> _themeCards = new();     // the chip grid's OWN references — SetChoices never clears them
    private readonly Dictionary<string, Rectangle> _themeBars = new();
    private Border? _chromeCloseCard;
    private Rectangle? _chromeCloseBar;
    private Grid? _themeGrid;                                            // re-attached on a Theme re-render (r6 F4)
    private Ellipse? _practiceFill;
    private TextBlock? _dwellValue;
    private bool _knobBuilt;
    private bool _chromeCloseBuilt;

    public FirstRunGuideWindow(FirstRunGuideViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        // Progress bindings (r3 F7): the cards' bars and the practice fill follow the VM.
        vm.ChoiceProgress += (id, p) => SetProgressBar(id, p);
        vm.PracticeProgress += SetPracticeFill;

        // The dwell read-out follows the knob (ONE subscription in the ctor — no pile-up, r4 S3).
        vm.DwellTimeChanged += ms =>
        {
            if (_dwellValue is { } value) value.Text = $"{ms} ms";
        };
    }

    /// <summary>The theme preview's card-local target (Task 12 applies the resolved theme here;
    /// DynamicResource on the card's brushes picks it up, Tokens.Current stays untouched).</summary>
    public ResourceDictionary CardResources => Card.Resources;

    public void ShowCardContent(string title, string body, bool chromeClose)
        => _vm.ShowGuideCard(title, body, chromeClose);

    /// <summary>Builds one visual card per (label, id) pair — background, border, label,
    /// progress bar — registers each as a chrome dwell target through the VM (Task 6), then
    /// pushes the laid-out centres (r3 F5: EVERY card gets a visual and a centre). Cards are
    /// modal-button sized (170 × 100 DIP, r3 F7).</summary>
    public void SetChoices(IReadOnlyList<(string Label, string Id)> choices)
    {
        _vm.ClearChoices();
        _choiceCards.Clear();
        _progressBars.Clear();
        ChoiceRow.Children.Clear();
        foreach (var (label, id) in choices)
        {
            var card = BuildCard(label, id);
            _choiceCards[id] = card;
            ChoiceRow.Children.Add(card);
            _vm.RegisterChoice(id);
        }
        PushCentres();
    }

    private Border BuildCard(string label, string id)
    {
        var bar = new Rectangle
        {
            Height = 3,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bar.SetResourceReference(Shape.FillProperty, "BrushAccent");     // S2-2: re-resolves on a theme swap
        _progressBars[id] = bar;
        var labelText = new TextBlock
        {
            Text = label,
            Margin = new Thickness(12, 8, 12, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
        var card = new Border
        {
            Width = 170,
            Height = 100,
            Child = new StackPanel { Children = { labelText, bar } },
            Margin = new Thickness(16, 0, 0, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        card.SetResourceReference(Border.BackgroundProperty, "BrushSurfaceMedium");
        card.SetResourceReference(Border.BorderBrushProperty, "BrushAccent");
        return card;
    }

    /// <summary>The chrome Close card in its OWN host (top-right of the card, r3 F7 — away from
    /// the action row, so abandoning is never the neighbour of continuing). Idempotent; the
    /// already-built branch RE-REGISTERS (r5 F2: a re-render cleared the targets).</summary>
    public void SetChromeClose(bool visible, string label)
    {
        if (visible && !_chromeCloseBuilt)
        {
            _chromeCloseCard = BuildCard(label, "guide_close");
            _chromeCloseBar = (Rectangle)((StackPanel)_chromeCloseCard.Child).Children[1];
            _choiceCards["guide_close"] = _chromeCloseCard;
            _progressBars["guide_close"] = _chromeCloseBar;
            ChromeCloseHost.Content = _chromeCloseCard;
            _vm.RegisterChoice("guide_close");
            _chromeCloseBuilt = true;
            PushCentres();
        }
        else if (visible && _chromeCloseBuilt)
        {
            // Re-render (r6 F4): SetChoices cleared the card and bar entries — restore BOTH from
            // the Close's own fields, so an accidental dwell on Close fills its bar visibly.
            _choiceCards["guide_close"] = _chromeCloseCard;
            _progressBars["guide_close"] = _chromeCloseBar;
            _vm.RegisterChoice("guide_close");
            PushCentres();
        }
        else if (!visible && _chromeCloseBuilt)
        {
            _vm.RemoveChromeChoice("guide_close");
            _choiceCards.Remove("guide_close");
            _progressBars.Remove("guide_close");
            ChromeCloseHost.Content = null;
            _chromeCloseCard = null;
            _chromeCloseBar = null;
            _chromeCloseBuilt = false;
        }
    }

    /// <summary>The practice target: a stroked ring with a fill element whose size follows the
    /// VM's PracticeProgress (r3 F7 — MeetDwell's lesson is "the ring fills"). Builds into
    /// StepContent (r4 F4's slot rule).</summary>
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
                Width = 120,
                Height = 120,
                StrokeThickness = 4,
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
    /// OWN row (r4 F4 — SetPractice's StepContent slot is never touched). Arriving registers the
    /// ± targets; an already-built knob RE-REGISTERS on a re-render (r5 F2); leaving removes
    /// them.</summary>
    public void SetDwellKnob(bool visible)
    {
        _vm.SetSliderVisible(visible);
        if (visible && !_knobBuilt)
        {
            _dwellValue = new TextBlock
            {
                Text = $"{_vm.DwellTimeMs} ms",
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
            };
            _dwellValue.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
            var plusMinus = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (glyph, id) in new[] { ("−", "guide_dwell_minus"), ("+", "guide_dwell_plus") })
            {
                var card = BuildCard(glyph, id);
                _choiceCards[id] = card;
                _progressBars[id] = (Rectangle)((StackPanel)card.Child).Children[1];
                _knobCards[id] = card;
                _knobBars[id] = _progressBars[id];
                plusMinus.Children.Add(card);
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
            // the knob's OWN card references (SetChoices never touches _knobCards/_knobBars).
            foreach (var id in new[] { "guide_dwell_minus", "guide_dwell_plus" })
            {
                _choiceCards[id] = _knobCards[id];
                _progressBars[id] = _knobBars[id];
                _vm.RegisterChoice(id);
            }
            PushCentres();
        }
        else if (!visible && _knobBuilt)
        {
            KnobRow.Children.Clear();
            _vm.RemoveChromeChoice("guide_dwell_minus");
            _vm.RemoveChromeChoice("guide_dwell_plus");
            _choiceCards.Remove("guide_dwell_minus");
            _choiceCards.Remove("guide_dwell_plus");
            _progressBars.Remove("guide_dwell_minus");
            _progressBars.Remove("guide_dwell_plus");
            _dwellValue = null;
            _knobBuilt = false;
        }
    }

    private void SetProgressBar(string id, double progress)
    {
        if (_progressBars.TryGetValue(id, out var bar) && _choiceCards.TryGetValue(id, out var card))
            bar.Width = (card.ActualWidth - 2) * Math.Clamp(progress, 0, 1);
    }

    /// <summary>The Theme step's four chips as a 2×2 grid in StepContent (r5 F4's geometry —
    /// six 170-wide cards in one right-aligned row overflow the 496-DIP inner width, and
    /// 70-DIP circles would overlap). Each chip registers at radius 40 DIP through
    /// <see cref="FirstRunGuideViewModel.RegisterChoice(string, double?)"/>. Idempotent; the
    /// already-built branch RE-REGISTERS (r5 F2). Chips live in `_themeCards` — `SetChoices`
    /// never clears them.</summary>
    public void SetThemeGrid(bool visible, IReadOnlyList<(string Label, string Id)> chips)
    {
        if (visible)
        {
            if (_themeCards.Count == 0)
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
                    var card = BuildChipCard(label, id);
                    _themeCards[id] = card;
                    _themeBars[id] = (Rectangle)((StackPanel)card.Child).Children[1];
                    Grid.SetRow(card, i / 2);
                    Grid.SetColumn(card, i % 2);
                    grid.Children.Add(card);
                    _choiceCards[id] = card;                       // r6 F4: progress + centres via _choiceCards
                    _progressBars[id] = _themeBars[id];
                    _vm.RegisterChoice(id, radiusDip: 40);
                }
                _themeGrid = grid;
                StepContent.Content = grid;
            }
            else
            {
                // Re-render (r6 F4): SetPractice(false) emptied StepContent — re-attach the grid
                // AND restore the chips' card/bar entries (SetChoices cleared them), so a theme
                // can be picked on every visit and the Continue/clear bookkeeping stays live.
                if (StepContent.Content is not Grid g || !ReferenceEquals(g, _themeGrid))
                    StepContent.Content = _themeGrid;
                foreach (var (id, _) in _themeCards)
                {
                    _choiceCards[id] = _themeCards[id];
                    _progressBars[id] = _themeBars[id];
                    _vm.RegisterChoice(id, radiusDip: 40);
                }
            }
            PushCentres();
        }
        else if (_themeCards.Count > 0)
        {
            foreach (var id in _themeCards.Keys)
                _vm.RemoveChromeChoice(id);
            _themeCards.Clear();
            _themeBars.Clear();
            _themeGrid = null;
            StepContent.Content = null;
        }
    }

    private Border BuildChipCard(string label, string id)
    {
        var bar = new Rectangle
        {
            Height = 3,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bar.SetResourceReference(Shape.FillProperty, "BrushAccent");     // S2-2: re-resolves on a theme swap
        _progressBars[id] = bar;
        var labelText = new TextBlock
        {
            Text = label,
            Margin = new Thickness(12, 8, 12, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
        var chip = new Border
        {
            Width = 170,
            Height = 80,
            Child = new StackPanel { Children = { labelText, bar } },
            Margin = new Thickness(16, 20, 16, 20),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "BrushSurfaceMedium");
        chip.SetResourceReference(Border.BorderBrushProperty, "BrushAccent");
        return chip;
    }

    private void SetPracticeFill(double progress)
    {
        if (_practiceFill is { } fill)
        {
            var size = 116 * Math.Clamp(progress, 0, 1);   // inside the 120 ring
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
    /// complete first (r2 F9). Card is fixed-size, so SizeChanged never fires — an explicit push
    /// per build is the mechanism.</summary>
    private void PushCentres()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            foreach (var (id, card) in _choiceCards)
            {
                if (!card.IsVisible) continue;
                var centre = card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
            if (StepContent.Content is Grid { Children.Count: > 1 } grid
                && grid.Children[0] is Ellipse ring && ring.IsVisible)
            {
                var centre = ring.PointToScreen(new Point(ring.ActualWidth / 2, ring.ActualHeight / 2));
                _vm.SetPracticeCentre(new PhysicalPoint(centre.X, centre.Y));
            }
            foreach (var (id, chip) in _themeCards)                       // the chip grid's centres
            {
                if (!chip.IsVisible) continue;
                var centre = chip.PointToScreen(new Point(chip.ActualWidth / 2, chip.ActualHeight / 2));
                _vm.UpdateChoiceCentre(id, new PhysicalPoint(centre.X, centre.Y));
            }
        });
    }
}
