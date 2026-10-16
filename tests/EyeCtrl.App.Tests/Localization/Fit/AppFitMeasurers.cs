namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// T09's App-side fit classes: one measurer per class, each building the class's PRODUCTION
/// control with the text under test, hosting it at 2.0 through <see cref="FitHost"/>, and
/// returning a <see cref="FitVerdict"/>. The Supervisor's classes (<c>tray63</c>,
/// <c>B260x3</c>, <c>C-fallback</c>) live in EyeCtrl.Supervisor.Tests, which is the only
/// project that can build its windows; <see cref="SupervisorMeasuredClasses"/> declares them.
///
/// <para>
/// A measurer never restates a box it can read: widths and heights come off the arranged
/// production tree (a tile's grid, a button's layout row, a toggle's state-word slot). Where a
/// box is chosen at a call site (a Settings <c>ButtonSpec</c>'s Width/Height), the call sites are
/// pinned against <see cref="SettingsButtonBox"/> by a source scan, so the box measured is the box
/// shipped.
/// </para>
/// </summary>
internal static class AppFitMeasurers
{
    /// <summary>Classes measured in EyeCtrl.Supervisor.Tests (their windows are Supervisor-internal).</summary>
    internal static readonly string[] SupervisorMeasuredClasses = ["tray63", "B260x3", "C-fallback"];

    /// <summary>Classes that are paginated rows or prose, measured by PR 2's pagination gates.</summary>
    internal static readonly string[] PaginatedClasses = ["row", "prose"];

    /// <summary>
    /// The usable work area at the user's 200 %: 3840×2400 physical is 1920×1200 DIP, less a
    /// 48 DIP taskbar. A class-C window that grows past it would slide under the taskbar.
    /// </summary>
    internal static readonly Rect ReferenceWorkArea = new(0, 0, 1920, 1152);

    /// <summary>
    /// The Settings content column a button row is laid into: production's own derivation at the
    /// 1920 DIP display (<c>HandChunkedPageFitTests.ContentColumnWidth</c>, ≈1424). Wider than the
    /// widest box (544), so <c>ConstrainToContentWidth</c> never clamps and the measured box is the
    /// drawn box. KNOWN LIMIT (review-01 D2): on an 800 DIP display the column is 416 and the 420+
    /// boxes clamp unmeasured; that is not this user's display.
    /// </summary>
    private static double SettingsColumnWidth => SettingsColumn.Value;

    private static readonly Lazy<double> SettingsColumn = new(() =>
        EyeCtrl.App.Tests.Settings.HandChunkedPageFitTests.ContentColumnWidth(SettingsContentFitTests.Xml()));

    /// <summary>The class-A budget rule: a one-line text keeps 4 DIP clear on each side of its box.</summary>
    private const double OneLineInset = 8;

    private const double A56Budget = 56;
    private const double A44Budget = 44;

    /// <summary>The classes this project measures, with their measurers.</summary>
    internal static IReadOnlyDictionary<string, Func<string, string, FitVerdict>> Measurers { get; } =
        new Dictionary<string, Func<string, string, FitVerdict>>(StringComparer.Ordinal)
        {
            ["A105"] = Ring,
            ["A68"] = ModifierBar,
            ["A240"] = (_, text) => KeyboardNoteIndicator(text),
            ["A44"] = (_, text) => ToggleStateWord(text),
            ["A208"] = (_, text) => ModeToggleLabel(text),
            ["A56"] = ShortCaption,
            ["card"] = (key, text) => SummaryCard(key, text),
            ["tab156"] = SidebarTab,
            ["B94x2"] = (_, text) => DefaultDwellButton(text),
            ["B114"] = (_, text) => QuickActionTile(text),
            ["B260x2"] = (_, text) => SettingsButton(text, SettingsButtonBox("B260x2")),
            ["C-alert"] = CaregiverAlert,
            ["modal170"] = (_, text) => ModalButton(text, wide: false),
            ["modal460"] = (_, text) => ModalButton(text, wide: true),
            ["PV1"] = PreviewRowFit.Measure,
            ["btn360x100"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn360x100")),
            ["btn360x70"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn360x70")),
            ["btn380x100"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn380x100")),
            ["btn420x100"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn420x100")),
            ["btn420x70"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn420x70")),
            ["btn420x90"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn420x90")),
            ["btn440x100"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn440x100")),
            ["btn544x100"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn544x100")),
            ["btn544x106"] = (_, text) => SettingsButton(text, SettingsButtonBox("btn544x106")),
        };

    /// <summary>
    /// The Settings <c>ButtonSpec</c> box a button class is measured in. <c>btnWxH</c> names its
    /// box; <c>B260x2</c> is historical — its 20 buttons are 300×100 (see StringTableSchema).
    /// <c>SettingsButtonBoxes_MatchEveryCallSite</c> pins this against the source.
    /// </summary>
    internal static (double Width, double Height) SettingsButtonBox(string fitClass)
    {
        if (fitClass == "B260x2") return (300, 100);
        var match = System.Text.RegularExpressions.Regex.Match(fitClass, @"^btn(?<w>\d+)x(?<h>\d+)$");
        if (!match.Success) throw new ArgumentException($"{fitClass} is not a Settings button class.");
        return (double.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The texts one English item is measured as. A plain key is its own text. A key whose text
    /// is COMPOSED in code, or which takes arguments, is measured as the real composed strings —
    /// and a measured key with arguments but no sample rule here fails, so a later key cannot
    /// slip through unmeasured.
    /// </summary>
    internal static IReadOnlyList<string> Samples(FitCatalog.Item item)
    {
        switch (item.Key)
        {
            case "settings.models.delete.affix":
            case "settings.models.download.affix":
                return BundledModels().Select(m => ModelsPageSource.RowCaption(m.DisplayName, m.SizeBytes, item.Text)).ToList();
            case "settings.models.repair.label":
                return BundledModels()
                    .Select(m => string.Format(CultureInfo.InvariantCulture, item.Text, m.DisplayName))
                    .ToList();
            // The adaptation-preview value templates: composed at each unit's worst pair / a full
            // band, in every shipped spelling of the unit/direction word (PreviewRowFit.SamplesFor).
            case "overlays.adaptationPreview.value.absolute":
            case "overlays.adaptationPreview.value.band":
                return PreviewRowFit.SamplesFor(item.Key, item.Text);
        }

        if (item.Args.Count > 0)
            throw new InvalidOperationException(
                $"{item.Key} is in a measured fit class and takes arguments ({string.Join(", ", item.Args)}), "
                + "but AppFitMeasurers.Samples has no rule for it. Add the real composed strings, or the gate "
                + "would measure a template with raw {0} holes.");
        return [item.Text];
    }

    /// <summary>The bundled models, read from the shipped manifest the App reads.</summary>
    private static IReadOnlyList<ModelEntry> BundledModels()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "models.json");
        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("models.json did not parse.");
        if (manifest.Models.Count == 0) throw new InvalidOperationException("models.json lists no models.");
        return manifest.Models.Values.ToList();
    }

    // ────── Class A: one line ──────

    /// <summary>
    /// A105: the label of a runtime ring slot, painted through <see cref="ActionMenuRingPainter.Paint"/>
    /// (the ring's only <c>DwellArcButton.Create</c> caller) with the key's own icon, labels on.
    /// The label's INK box must stay ≥ <see cref="ArcLabelFit.RequiredClearanceDip"/> inside the
    /// progress arc's inner edge, clear of the icon.
    /// </summary>
    private static FitVerdict Ring(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var canvas = new Canvas { Width = 400, Height = 400 };
        var slot = new RingPaintSlot("fit_probe", new Point(200, 200), RingIconFor(key), text, ActionMenuItemKind.Action);
        var button = ActionMenuRingPainter.Paint(canvas, [slot], RingPaintOptions.Runtime(showLabels: true))["fit_probe"];

        using var host = FitHost.Open(canvas, family: Tokens.FontStandard);
        var fit = ArcLabelFit.MeasureButton(button, key);
        if (!fit.HasLabel) throw new InvalidOperationException("The ring painter drew no label: the A105 gate would measure nothing.");

        var ok = fit.ClearanceDip >= ArcLabelFit.RequiredClearanceDip && !fit.OverlapsIcon;
        var detail = $"ink clearance {FitText.Dip(fit.ClearanceDip)} DIP (needs ≥ {FitText.Dip(ArcLabelFit.RequiredClearanceDip)}) "
            + $"inside the arc's inner edge {FitText.Dip(fit.ArcInnerEdgeDip)}; overlaps icon: {fit.OverlapsIcon}";
        var face = FitText.ResolvedFace(button.Label!);
        if (key.StartsWith("overlays.", StringComparison.Ordinal))
            return new FitVerdict(ok, detail, face, fit.ClearanceDip - ArcLabelFit.RequiredClearanceDip);

        // A settings.ring.* key (or a synthetic probe) is ALSO drawn by the Settings preview, in a
        // different geometry: an unscaled label under each miniature slot, unbounded in width. The
        // margin measures the ring clearance only, so a preview failure is reported as Unwaivable:
        // an exception recorded against the ring cannot absorb it (review-03, F2).
        var preview = RingPreviewLabels(text);
        return new FitVerdict(ok && preview.Fits, detail + "; preview: " + preview.Detail, face,
            fit.ClearanceDip - ArcLabelFit.RequiredClearanceDip,
            Unwaivable: preview.Fits ? null : "the Settings preview's labels (" + preview.Detail + ")");
    }

    /// <summary>
    /// The Settings ring preview's labels (<c>ActionMenuRingPainter</c>'s scaled mode): unscaled
    /// TextBlocks centred under each miniature slot, with NO width bound, so a long label can
    /// collide with its neighbour or run off the painting into the caption gutter. Built through
    /// <see cref="SettingsWindow.CreateControl"/> with a real <see cref="RingPreviewSpec"/> at the
    /// default CircleSize (250) and the legibility-floor scale — the smallest preview the shell
    /// shows — with every slot carrying the text under test (the More slot with its real
    /// "{label} +N" annotation), which is the worst its neighbours can be.
    ///
    /// <para>
    /// Nothing clips these labels (the canvas and the content card do not clip), so the rule is
    /// what they can run into: every pair of label boxes must be apart; no label may reach the
    /// captions (the square's right edge plus <see cref="SettingsWindow.RingPreviewCaptionGutter"/>);
    /// and no label may reach the sidebar column — the painting starts at the content column's
    /// left, inside the card's 16 DIP padding and the content area's 16 DIP margin
    /// (SettingsWindow.xaml). Overrunning the square into that padding is allowed and logged.
    /// </para>
    /// </summary>
    private static (bool Fits, string Detail) RingPreviewLabels(string text)
    {
        const double defaultCircleSize = 250;
        var primary = new[]
        {
            InteractionAction.LeftClick, InteractionAction.RightClick, InteractionAction.DoubleClick, InteractionAction.Drag,
        };
        var visible = Enum.GetValues<InteractionAction>().ToList();
        var english = StringTable.EmbeddedEnglish;
        var spec = new RingPreviewSpec("fit_preview",
            () => RingPlan.Create(visible, primary, defaultCircleSize, new RingLabelSource(_ => text, text, text)),
            "preview, not a control", "runtime shows labels only while you dwell",
            RingPlan.MinimumLegibleScale,
            (label, count) => english.T("settings.action_menu.preview.more_annotation", label, count));
        var row = (IDwellComposite)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), SettingsWindow.ControlBtnRadius, 800, SettingsColumnWidth);
        var panel = new StackPanel { Width = SettingsColumnWidth };
        row.Attach(panel);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);

        // The painting canvas is the one whose direct children include the unscaled labels.
        var canvas = FitText.Descendants<Canvas>(panel).Single(c => c.Children.OfType<TextBlock>().Any());
        var labels = canvas.Children.OfType<TextBlock>().ToList();
        if (labels.Count < 2) throw new InvalidOperationException("The preview drew fewer than two labels: nothing to compare.");
        var boxes = labels.Select(t => new Rect(Canvas.GetLeft(t), Canvas.GetTop(t), t.ActualWidth, t.ActualHeight)).ToList();

        var minGap = double.PositiveInfinity;
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i];
                var b = boxes[j];
                var gap = Math.Max(Math.Max(b.Left - a.Right, a.Left - b.Right), Math.Max(b.Top - a.Bottom, a.Top - b.Bottom));
                minGap = Math.Min(minGap, gap);
            }
        }

        var left = boxes.Min(b => b.Left);
        var right = boxes.Max(b => b.Right);
        const double leftRoom = 16 + 16;   // ContentCard Padding + ContentArea Margin (SettingsWindow.xaml)
        var ok = minGap > 0 && left >= -leftRoom && right <= canvas.Width + SettingsWindow.RingPreviewCaptionGutter;
        return (ok, $"{labels.Count} labels at scale {FitText.Dip(RingPlan.MinimumLegibleScale)}, nearest pair "
                    + $"{FitText.Dip(minGap)} DIP apart, x {FitText.Dip(left)}..{FitText.Dip(right)} in a "
                    + $"{FitText.Dip(canvas.Width)} square (allowed {FitText.Dip(-leftRoom)}.."
                    + $"{FitText.Dip(canvas.Width + SettingsWindow.RingPreviewCaptionGutter)})");
    }

    /// <summary>The icon a ring key's slot shows: its action's icon, More's or Back's.</summary>
    internal static ActionIcon RingIconFor(string key)
    {
        var overlaysKey = key.StartsWith("settings.ring.", StringComparison.Ordinal)
            ? "overlays.ring." + key["settings.ring.".Length..]
            : key;
        if (overlaysKey == "overlays.ring.more") return ActionIcon.More;
        if (overlaysKey == "overlays.ring.back") return ActionIcon.Back;
        foreach (var action in Enum.GetValues<InteractionAction>())
        {
            if (RingLabelMap.For(action) == overlaysKey) return ActionIconMap.For(action);
        }

        // A synthetic text (the over-long probe, a pseudo-locale string under an unknown key)
        // measures under the most common slot icon.
        return ActionIconMap.For(InteractionAction.LeftClick);
    }

    /// <summary>
    /// A68: a modifier-bar button (D8) through the production edge window, latched and
    /// unlatched — the latched icon is a different drawing, so both must clear the label.
    /// </summary>
    private static FitVerdict ModifierBar(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var id = key switch
        {
            "overlays.modifier.ctrl" => ActionMenuModifierButtons.CtrlId,
            "overlays.modifier.alt" => ActionMenuModifierButtons.AltId,
            _ => ActionMenuModifierButtons.ShiftId,
        };

        var worst = (Clearance: double.PositiveInfinity, Overlap: false, Face: "", Detail: "");
        foreach (var latched in new[] { false, true })
        {
            IReadOnlySet<ModifierKey> set = latched
                ? new HashSet<ModifierKey> { ModifierKey.Shift, ModifierKey.Ctrl, ModifierKey.Alt }
                : new HashSet<ModifierKey>();
            var definition = ActionMenuModifierButtons.Definitions(600, set).Single(d => d.Id == id) with { Label = text };
            var (button, _, window) = ArcLabelFit.BuildButtonInWindow(definition);
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            using var host = FitHost.Open(root, inheritFrom: window);
            var fit = ArcLabelFit.MeasureButton(button, id);
            if (!fit.HasLabel) throw new InvalidOperationException("The modifier bar drew no label: the A68 gate would measure nothing.");
            if (fit.ClearanceDip < worst.Clearance || fit.OverlapsIcon)
                worst = (fit.ClearanceDip, worst.Overlap || fit.OverlapsIcon, FitText.ResolvedFace(button.Label!),
                    $"{(latched ? "latched" : "unlatched")}: ink clearance {FitText.Dip(fit.ClearanceDip)} DIP inside "
                    + $"{FitText.Dip(fit.ArcInnerEdgeDip)}; overlaps icon: {fit.OverlapsIcon}");
        }

        return new FitVerdict(worst.Clearance >= ArcLabelFit.RequiredClearanceDip && !worst.Overlap,
            worst.Detail + $" (needs ≥ {FitText.Dip(ArcLabelFit.RequiredClearanceDip)})", worst.Face, worst.Clearance - ArcLabelFit.RequiredClearanceDip);
    }

    /// <summary>
    /// A240: the keyboard's "→ Note" targeting indicator (sticky notes P1 D6), through the
    /// production element's own geometry — the same font tokens and the same MaxWidth the window
    /// draws. The window is SizeToContent, so the rule is one line within the bound: an over-long
    /// text would wrap to a second line (and stretch the whole keyboard), which is the failure.
    /// </summary>
    private static FitVerdict KeyboardNoteIndicator(string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var indicator = new TextBlock
        {
            Text = text,
            Foreground = Tokens.BrushAccent,
            FontSize = Tokens.FontSizeBody,
            MaxWidth = KeyboardWindow.NoteTargetingIndicatorMaxWidth,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var panel = new StackPanel();
        panel.Children.Add(indicator);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);
        return OneLine(indicator, text, KeyboardWindow.NoteTargetingIndicatorMaxWidth,
            $"indicator MaxWidth {FitText.Dip(KeyboardWindow.NoteTargetingIndicatorMaxWidth)}");
    }

    /// <summary>
    /// A44: a toggle's state word, through a real <see cref="DwellToggle"/>: one line, within the
    /// 44 DIP budget AND within the word's measured slot (the switch less its padding, which
    /// reserves the track).
    /// </summary>
    private static FitVerdict ToggleStateWord(string text)
    {
        var toggle = new DwellToggle(new SettingsAutoPagination.NoopDwellRegistrar(), "fit_probe", "Label",
            text, text, () => false, _ => { }, DwellCategory.Control, SettingsWindow.ControlBtnRadius, 800);
        var panel = new StackPanel { Width = SettingsColumnWidth };
        toggle.Attach(panel);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);

        var word = FitText.Descendants<TextBlock>(panel)
            .Single(t => AutomationProperties.GetAutomationId(t) == "Settings_Toggle_fit_probe");
        if (word.Text != text) throw new InvalidOperationException($"The state word reads '{word.Text}', not the probe text.");
        // The slot is the width the switch's content cell GIVES the word, less its padding — not
        // the word's ActualWidth: WPF arranges an over-wide Stretch child at its desired width and
        // clips it, so ActualWidth reports the text's need, never the room (a T09 PR 2 misreading:
        // "[Öƒƒöƒ]" read as a 64 DIP slot while the switch stayed 160).
        var cell = (FrameworkElement)word.Parent;
        var slot = cell.ActualWidth - word.Padding.Left - word.Padding.Right;
        var switchBox = (FrameworkElement)FitText.Ancestors(word).OfType<Border>().First(b => b.Width > 0);
        return OneLine(word, text, Math.Min(A44Budget, slot),
            $"budget {A44Budget}, slot {FitText.Dip(slot)}, switch {FitText.Dip(switchBox.ActualWidth)} wide");
    }

    /// <summary>A208: the settings mode toggle's label, through the production BuildModeToggle
    /// face: one line within the toggle's measured box less the standard one-line inset.
    /// Horizontal by construction — a one-line label can never fail a height check, and the
    /// teeth test rejects a measurer that cannot fail.</summary>
    private static FitVerdict ModeToggleLabel(string text)
    {
        var buttons = new Dictionary<string, System.Windows.Controls.Border>();
        var brushes = new Dictionary<string, System.Windows.Media.SolidColorBrush>();
        var border = SettingsWindow.BuildModeToggle(text, () => { }, buttons, brushes);
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(border);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);
        // Arrange exactly the way the sibling measurers realize layout (whatever SidebarTab /
        // modal170 do after FitHost.Open) — the invariant: border.ActualWidth is laid-out, not 0.
        var label = (System.Windows.Controls.TextBlock)border.Child;
        return OneLine(label, text, border.ActualWidth - OneLineInset,
            $"box {FitText.Dip(border.ActualWidth)} - {OneLineInset} inset");
    }

    /// <summary>
    /// A56: the eye-care captions, each through its own production element — the banner's ring
    /// captions, the exercise's Skip button and the summary's labels. One line within 56 DIP and
    /// within the element's slot where it has one.
    /// </summary>
    private static FitVerdict ShortCaption(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        if (key.StartsWith("eye-care.exercise.", StringComparison.Ordinal))
        {
            var window = new EyeCareExerciseWindow();
            window.SkipButton.Content = text;
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            using var host = FitHost.Open(root, inheritFrom: window);
            var skipText = FitText.Descendants<TextBlock>(window.SkipButton).Single();
            var skip = window.SkipButton;
            var slot = skip.ActualWidth - skip.Padding.Left - skip.Padding.Right
                       - skip.BorderThickness.Left - skip.BorderThickness.Right;
            return OneLine(skipText, text, Math.Min(A56Budget, slot), $"budget {A56Budget}, button slot {FitText.Dip(slot)}");
        }

        var banner = new EyeCareBannerWindow();
        var caption = key == "eye-care.banner.start" ? banner.StartLabel : banner.SnoozeLabel;
        caption.Text = text;
        var bannerRoot = (FrameworkElement)banner.Content;
        banner.Content = null;
        using var bannerHost = FitHost.Open(bannerRoot, inheritFrom: banner);
        var ring = ((FrameworkElement)caption.Parent).ActualWidth;
        return OneLine(caption, text, Math.Min(A56Budget, ring - OneLineInset),
            $"budget {A56Budget}, ring {FitText.Dip(ring)} − {OneLineInset}");
    }

    /// <summary>
    /// The reference exercise window: the window is <c>PrimaryScreenWidth</c> wide, which on the
    /// user's 3840×2400 display at 200 % is 1920 DIP.
    /// </summary>
    internal const double ExerciseWindowWidth = 1920;

    /// <summary>
    /// card: the eye-care summary card's labels (review-01 R1). The card is an auto-width panel
    /// centred in the full-screen exercise window, so a label has no box of its own; its real bound
    /// is that it stays on one line (the labels never wrap) and the card it widens still fits the
    /// window. The card is laid out with the text in its slot, every other summary text at its
    /// English, and the stat values at their widest ("100%", "100%", "9999"), and its natural width
    /// must fit <see cref="ExerciseWindowWidth"/>.
    /// </summary>
    private static FitVerdict SummaryCard(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new EyeCareExerciseWindow();
        var label = key switch
        {
            "eye-care.summary.accuracy" => window.AccuracyLabel,
            "eye-care.summary.steadiness" => window.SteadinessLabel,
            "eye-care.summary.reactionMs" => window.ReactionMsLabel,
            _ => window.SummaryTitle,
        };
        label.Text = text;
        window.SummaryAccuracy.Text = "100%";
        window.SummarySteadiness.Text = "100%";
        window.SummaryReactionTime.Text = "9999";
        window.ReactionTimePanel.Visibility = Visibility.Visible;
        window.SummaryCard.Visibility = Visibility.Visible;

        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);
        var card = window.SummaryCard;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var cardWidth = card.DesiredSize.Width;
        host.Relayout();
        var lineWidth = FitText.NaturalWidth(label, text);
        var ok = label.TextWrapping == TextWrapping.NoWrap && cardWidth <= ExerciseWindowWidth;
        return new FitVerdict(ok,
            $"one line {FitText.Dip(lineWidth)} DIP; the card {FitText.Dip(cardWidth)} wide vs the {ExerciseWindowWidth} DIP window",
            FitText.ResolvedFace(label), ExerciseWindowWidth - cardWidth);
    }

    /// <summary>tab156: a sidebar tab, rebuilt element for element (see <see cref="SidebarTabFit"/>).</summary>
    private static FitVerdict SidebarTab(string key, string text)
    {
        var tab = SidebarTabFit.Measure(SidebarTabFit.IconFor(key), text);
        var overhang = tab.LabelRight - tab.BorderRight;
        var ok = tab.LabelOneLine && tab.LabelRight <= tab.ColumnWidth && overhang < SidebarTabFit.ToleratedOverhang;
        return new FitVerdict(ok,
            $"label right {FitText.Dip(tab.LabelRight)} vs column {FitText.Dip(tab.ColumnWidth)}; overhangs its tab by "
            + $"{FitText.Dip(overhang)} (must be < {FitText.Dip(SidebarTabFit.ToleratedOverhang)})",
            tab.Face, Math.Min(tab.ColumnWidth - tab.LabelRight, SidebarTabFit.ToleratedOverhang - overhang));
    }

    private static FitVerdict OneLine(TextBlock element, string text, double room, string roomNote)
    {
        if (element.TextWrapping != TextWrapping.NoWrap && element.TextWrapping != TextWrapping.WrapWithOverflow)
        {
            // A wrapping element is not a one-line class; refuse rather than mis-measure.
            throw new InvalidOperationException($"{element.Name} wraps; a one-line class cannot measure it.");
        }

        var width = FitText.NaturalWidth(element, text);
        return new FitVerdict(width <= room,
            $"one line {FitText.Dip(width)} DIP vs room {FitText.Dip(room)} ({roomNote})",
            FitText.ResolvedFace(element), room - width);
    }

    // ────── Class B: wrapping buttons ──────

    /// <summary>B94x2: a default 100×100 <see cref="DwellButton"/> (no keys today; kept for T10).</summary>
    private static FitVerdict DefaultDwellButton(string text)
    {
        var button = new DwellButton(new SettingsAutoPagination.NoopDwellRegistrar(), "fit_probe", text, () => { },
            Colors.Gray, DwellCategory.Control, SettingsWindow.ControlBtnRadius, 800);
        var panel = new StackPanel();
        button.Attach(panel);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);
        return WrappedInButton(host, button);
    }

    /// <summary>
    /// A Settings button of a fixed box, built through <see cref="SettingsWindow.CreateControl"/>
    /// — the render path's own <c>ButtonSpec</c> → <c>ButtonRow</c> → <see cref="DwellButton"/>.
    /// </summary>
    private static FitVerdict SettingsButton(string text, (double Width, double Height) box)
    {
        var spec = new ButtonSpec("fit_probe", text, () => { }, ButtonKind.Default, box.Width, box.Height);
        var button = (DwellButton)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(), SettingsWindow.ControlBtnRadius, 800, SettingsColumnWidth);
        SettingsWindow.ConstrainToContentWidth(button.Root, SettingsColumnWidth);
        var panel = new StackPanel { Width = SettingsColumnWidth };
        button.Attach(panel);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);
        return WrappedInButton(host, button);
    }

    /// <summary>
    /// The class-B rule on a laid-out <see cref="DwellButton"/>: every unbreakable run fits the
    /// label's width, and the label's full wrapped height fits the content row (the button's
    /// height less the dwell bar's reserved row).
    /// </summary>
    private static FitVerdict WrappedInButton(FitHost host, DwellButton button)
    {
        var label = (TextBlock)button.Content!;
        var layout = (Grid)label.Parent;
        var roomWidth = layout.ActualWidth;
        var roomHeight = layout.RowDefinitions[0].ActualHeight;
        return Wrapped(host, label, roomWidth, roomHeight, $"box {FitText.Dip(button.Root.ActualWidth)}×{FitText.Dip(button.Root.ActualHeight)}");
    }

    private static FitVerdict Wrapped(FitHost host, TextBlock label, double roomWidth, double roomHeight, string boxNote)
    {
        if (label.TextWrapping == TextWrapping.NoWrap)
            throw new InvalidOperationException("The label does not wrap; a class-B rule cannot measure it.");

        var (run, runWidth) = FitText.WidestRun(label, label.Text);
        var height = FitText.NaturalHeight(host, label, roomWidth);
        var ok = runWidth <= roomWidth && height <= roomHeight;
        return new FitVerdict(ok,
            $"widest run '{run}' {FitText.Dip(runWidth)} vs width {FitText.Dip(roomWidth)}; wrapped height "
            + $"{FitText.Dip(height)} vs {FitText.Dip(roomHeight)} ({boxNote})",
            FitText.ResolvedFace(label), Math.Min(roomWidth - runWidth, roomHeight - height));
    }

    /// <summary>
    /// B114: a quick-actions tile, through the grid window's own <c>DataTemplate</c>. Every run
    /// fits the label's wrap width (the tile's inner width less the label's margins — 106 DIP
    /// today), and the tile's icon + label + bar stack fits the tile's inner height.
    /// </summary>
    private static FitVerdict QuickActionTile(string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new QuickActionsGridWindow();
        var tileVm = new QuickActionTileViewModel(new QuickAction { Label = text, Type = QuickActionType.KeyboardShortcut });
        window.TilesControl.ItemsSource = new[] { tileVm };
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, inheritFrom: window);

        var tile = FitText.Descendants<Border>(root).Single(b => b.Name == "TileBorder");
        var grid = (Grid)tile.Child;
        var stack = (StackPanel)grid.Children[0];
        var label = FitText.Descendants<TextBlock>(stack).Single(t => t.Text == text);
        var roomWidth = grid.ActualWidth - label.Margin.Left - label.Margin.Right;

        var (run, runWidth) = FitText.WidestRun(label, text);
        var stackHeight = FitText.NaturalHeight(host, stack, grid.ActualWidth);
        var ok = runWidth <= roomWidth && stackHeight <= grid.ActualHeight;
        return new FitVerdict(ok,
            $"widest run '{run}' {FitText.Dip(runWidth)} vs label width {FitText.Dip(roomWidth)}; icon+label+bar "
            + $"{FitText.Dip(stackHeight)} vs tile inner height {FitText.Dip(grid.ActualHeight)}",
            FitText.ResolvedFace(label), Math.Min(roomWidth - runWidth, grid.ActualHeight - stackHeight));
    }

    /// <summary>
    /// modal170/modal460: a modal button built by <see cref="SettingsWindow.BuildModalButton"/>,
    /// the builder the modal itself uses. The label never wraps: one line ≤ the box less 8.
    /// </summary>
    private static FitVerdict ModalButton(string text, bool wide)
    {
        var button = SettingsWindow.BuildModalButton("fit_probe", text, Colors.Gray, Brushes.White, wide, () => { },
            new Dictionary<string, Border>(), new Dictionary<string, SolidColorBrush>());
        var panel = new StackPanel();
        panel.Children.Add(button);
        using var host = FitHost.Open(panel, family: Tokens.FontStandard);
        var label = (TextBlock)button.Child;
        return OneLine(label, text, button.ActualWidth - OneLineInset,
            $"box {FitText.Dip(button.ActualWidth)} − {OneLineInset}");
    }

    // ────── Class C: growing windows ──────

    /// <summary>
    /// C-alert: the caregiver alert, prepared by its own <see cref="CaregiverAlertWindow.Prepare"/>
    /// against the user's work area. The window grows in height only; its bottom edge stays at the
    /// work area's bottom less 48; the grown window fits the work area; at 2.0 the banner's
    /// laid-out height fits the window; the title (which never wraps) fits on one line and every
    /// run of the message fits the banner's text width.
    /// </summary>
    private static FitVerdict CaregiverAlert(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CaregiverAlertWindow();
        var isTitle = key == "caregiver-alert.title";
        if (isTitle) window.TitleText.Text = text;
        var message = isTitle ? StringTable.EmbeddedEnglish.T("caregiver-alert.poor-tracking") : text;
        window.Prepare(message, ReferenceWorkArea);

        var bottom = window.Top + window.Height;
        var anchored = Math.Abs(bottom - (ReferenceWorkArea.Bottom - CaregiverAlertWindow.MarginDip)) < 1e-6;
        var inside = window.Top >= ReferenceWorkArea.Top;
        var height = window.Height;
        var minHeight = window.MinHeight;

        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: window.Width, inheritFrom: window);
        var laidOut = FitText.NaturalHeight(host, root, window.Width);
        var banner = window.Banner;
        var textRoom = window.Width - banner.Padding.Left - banner.Padding.Right
                       - banner.BorderThickness.Left - banner.BorderThickness.Right;
        var titleWidth = FitText.NaturalWidth(window.TitleText, window.TitleText.Text);
        var (run, runWidth) = FitText.WidestRun(window.Message, message);

        var ok = anchored && inside && height >= minHeight && laidOut <= height + 0.5
                 && titleWidth <= textRoom && runWidth <= textRoom;
        return new FitVerdict(ok,
            $"window {FitText.Dip(window.Width)}×{FitText.Dip(height)} (min {FitText.Dip(minHeight)}), top {FitText.Dip(window.Top)}, "
            + $"bottom {FitText.Dip(bottom)} (anchor {FitText.Dip(ReferenceWorkArea.Bottom - CaregiverAlertWindow.MarginDip)}); "
            + $"laid out at 2.0: {FitText.Dip(laidOut)}; title one line {FitText.Dip(titleWidth)} and widest run '{run}' "
            + $"{FitText.Dip(runWidth)} vs text room {FitText.Dip(textRoom)}",
            FitText.ResolvedFace(isTitle ? window.TitleText : window.Message),
            Math.Min(textRoom - titleWidth, textRoom - runWidth));
    }
}
