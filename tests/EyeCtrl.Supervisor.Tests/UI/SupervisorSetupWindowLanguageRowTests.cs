namespace EyeCtrl.Supervisor.Tests.UI;

using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Controls;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;

/// <summary>
/// The real Supervisor setup window, built headless — never shown: its content is detached into an
/// off-screen, invisible <see cref="HwndSource"/> and laid out at the user's 200 % scale through a
/// <see cref="ScaleTransform"/>. Pins that the language row's three endonyms fit their buttons on
/// one line in every state, and that the row reflects the delivery. With
/// <c>EYECTRL_WRITE_T05B_SHEET=1</c> it also writes the review sheet to
/// <c>C:\t\keys\t05b-supervisor-language-row.png</c>.
///
/// <para>
/// T06 fixup-01 F1 adds the window's keyed-English literal pins here, reusing this harness: every
/// <c>ApplyStrings</c> text, the toggle labels and state words, the diagnostic buttons, the status
/// lines, the fallback display names and the notes' size. Key coverage cannot see a key used in
/// the wrong slot, and a swap is exactly what these pins catch.
/// </para>
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
public sealed class SupervisorSetupWindowLanguageRowTests : IDisposable
{
    private const double Scale = 2.0;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eyectrl-t05b-row-" + Guid.NewGuid().ToString("N"));

    public SupervisorSetupWindowLanguageRowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>The row states the user reviews.</summary>
    public enum RowState { AtRestSystem, GermanLiveSending, GermanPendingAppDown, Superseded, Refused }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>A delivery with scripted replies and an injected disk, driven inline.</summary>
    private sealed class Fixture
    {
        public readonly List<TaskCompletionSource<AppRequestResult>> Replies = new();
        public readonly List<TaskCompletionSource<AppRequestResult>> LogonReplies = new();
        public UiLanguage? Disk = UiLanguage.System;
        public DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        public readonly SettingDelivery<UiLanguage> Language;
        public readonly SettingDelivery<bool> StartOnBoot;

        public Fixture()
        {
            Language = new SettingDelivery<UiLanguage>(
                "UiLanguage",
                (language, restart) => SettingRequest.SetUiLanguage(language, restart),
                _ =>
                {
                    var reply = new TaskCompletionSource<AppRequestResult>();
                    Replies.Add(reply);
                    return reply.Task;
                },
                work => work(),
                readDisk: () => Disk,
                runInBackground: work => work(),
                utcNow: () => Now,
                confirmSaveByReRead: true);
            StartOnBoot = new SettingDelivery<bool>(
                "StartOnBoot", (value, _) => SettingRequest.SetStartOnBoot(value),
                _ =>
                {
                    var reply = new TaskCompletionSource<AppRequestResult>();
                    LogonReplies.Add(reply);
                    return reply.Task;
                },
                work => work());
        }

        public void Drive(RowState state)
        {
            switch (state)
            {
                case RowState.AtRestSystem:
                    break;
                case RowState.GermanLiveSending:
                    Language.Request(UiLanguage.German);
                    break;
                case RowState.GermanPendingAppDown:
                    Language.Request(UiLanguage.German);
                    Replies[0].SetResult(AppRequestResult.NotAvailable("App down"));
                    break;
                case RowState.Superseded:
                    Language.Request(UiLanguage.German);
                    Replies[0].SetResult(AppRequestResult.NotAvailable("App down"));
                    Disk = UiLanguage.English;              // chosen in EyeCtrl meanwhile
                    Now = Language.NextRetryAt!.Value;
                    Language.OnRetryTick();
                    break;
                case RowState.Refused:
                    Language.Request(UiLanguage.German);
                    Replies[0].SetResult(new AppRequestResult(SettingRequestStatus.Rejected, "refused"));
                    break;
            }
        }
    }

    private SupervisorSetupWindow BuildWindow(
        Fixture fixture,
        string statusText = "Supervisor state: Supervising",
        SettingsService? settingsService = null)
    {
        var settingsPath = Path.Combine(_root, "settings.json");
        var window = new SupervisorSetupWindow(
            new EyeCtrlSettings(),
            settingsService ?? new SettingsService(settingsPath, SettingsWriter.Supervisor),
            _ => { },
            () => { },
            new StartOnBootSetupRow(fixture.StartOnBoot, () => false),
            new LanguageSetupRow(fixture.Language, () => fixture.Disk ?? UiLanguage.System, CultureInfo.GetCultureInfo("de-DE")),
            StringTable.EmbeddedEnglish,
            statusText);
        return window;
    }

    /// <summary>Detaches the window's content into an invisible off-screen source at 200 %.</summary>
    private static (HwndSource Source, FrameworkElement Content) Host(SupervisorSetupWindow window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var source = new HwndSource(new HwndSourceParameters("t05b-probe")
        {
            WindowStyle = unchecked((int)0x80000000),   // WS_POPUP, no WS_VISIBLE
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });
        var device = source.CompositionTarget.TransformToDevice.M11;
        var frame = new Border
        {
            Width = window.Width,
            Background = window.Background,
            Child = content,
            LayoutTransform = new ScaleTransform(Scale / device, Scale / device),
        };
        source.RootVisual = frame;
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        frame.Arrange(new Rect(frame.DesiredSize));
        frame.UpdateLayout();
        return (source, frame);
    }

    private static double NaturalWidth(TextBlock caption)
    {
        var probe = new TextBlock
        {
            Text = caption.Text,
            FontFamily = caption.FontFamily,
            FontSize = caption.FontSize,
            FontWeight = caption.FontWeight,
            FontStyle = caption.FontStyle,
            FontStretch = caption.FontStretch,
            TextWrapping = TextWrapping.NoWrap,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    [Theory]
    [InlineData(RowState.AtRestSystem)]
    [InlineData(RowState.GermanLiveSending)]
    [InlineData(RowState.GermanPendingAppDown)]
    [InlineData(RowState.Superseded)]
    [InlineData(RowState.Refused)]
    public void EveryEndonym_FitsItsButtonOnOneLine_At200Percent(RowState state)
    {
        RunSta(() =>
        {
            var fixture = new Fixture();
            var window = BuildWindow(fixture);
            fixture.Drive(state);
            var (source, _) = Host(window);
            try
            {
                Check.That(PresentationSource.FromVisual(window.LanguageButtons[0].Root)).IsNotNull();
                Check.That(window.LanguageButtons).HasSize(3);
                foreach (var button in window.LanguageButtons)
                {
                    var caption = (TextBlock)button.Content!;
                    var room = button.Root.ActualWidth;
                    Check.WithCustomMessage($"'{caption.Text}' needs {NaturalWidth(caption):F1} DIP, has {room:F1}")
                        .That(NaturalWidth(caption) <= room).IsTrue();
                    Check.That(caption.ActualHeight < button.Root.ActualHeight).IsTrue();
                }

                // The three buttons sit on one line inside the window's 20 DIP margins.
                var row = (FrameworkElement)VisualTreeHelper.GetParent(window.LanguageButtons[0].Root);
                Check.That(row.ActualWidth < window.Width - 40 + 0.5).IsTrue();
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(RowState.AtRestSystem, "\u2713 System", false)]
    [InlineData(RowState.GermanLiveSending, "\u2713 Deutsch", false)]
    [InlineData(RowState.GermanPendingAppDown, "\u2713 Deutsch", true)]
    [InlineData(RowState.Superseded, "\u2713 English", false)]
    [InlineData(RowState.Refused, "\u2713 System", false)]
    public void TheRow_MarksTheSelection_AndShowsTheNoteOnlyWhilePending(RowState state, string marked, bool noteShown)
    {
        RunSta(() =>
        {
            var fixture = new Fixture();
            var window = BuildWindow(fixture);
            fixture.Drive(state);

            var captions = window.LanguageButtons.Select(b => ((TextBlock)b.Content!).Text).ToList();
            Check.That(captions.Count(c => c.StartsWith("\u2713 ", StringComparison.Ordinal))).IsEqualTo(1);
            Check.That(captions).Contains(marked);
            Check.That(window.LanguageNote.Visibility == Visibility.Visible).IsEqualTo(noteShown);
            if (noteShown)
            {
                Check.That(window.LanguageNote.Text).IsEqualTo(
                    "Not saved yet. Applies when EyeCtrl next starts, unless the PC or the Supervisor restarts first.");
            }
        });
    }

    [Fact]
    public void DwellingAChoice_AsksTheApp()
    {
        RunSta(() =>
        {
            var fixture = new Fixture();
            var window = BuildWindow(fixture);
            var deutsch = window.LanguageButtons[1];

            // A click is the DwellButton's mouse fallback and runs the same action as a dwell.
            deutsch.Root.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            });

            Check.That(fixture.Replies).HasSize(1);
            Check.That(fixture.Language.Pending).IsEqualTo(UiLanguage.German);
            Check.That(((TextBlock)window.LanguageButtons[1].Content!).Text).IsEqualTo("\u2713 Deutsch");
        });
    }

    [Fact]
    public void TheLogonSwitch_IsRepainted_WhenItsValueChangesWithoutADwell()
    {
        // T05a review-02 R2-3: a refused (or superseded) logon choice falls back to the disk value;
        // the switch's face must follow, or the next dwell sends the inverse of the hidden value.
        RunSta(() =>
        {
            var fixture = new Fixture();
            var window = BuildWindow(fixture);
            string Face() => FindByAutomationId(window.Content as DependencyObject ?? window, "Settings_Toggle_setup_start_on_logon")!.Text;
            Check.That(Face()).IsEqualTo("Off");

            fixture.StartOnBoot.Request(true);   // what the switch's setter does
            Check.That(Face()).IsEqualTo("On");

            fixture.LogonReplies[0].SetResult(new AppRequestResult(SettingRequestStatus.Rejected, "refused"));
            Check.That(Face()).IsEqualTo("Off");
        });
    }

    // ── T06 fixup-01 F1: the window's keyed English, pinned literally at its site ─────────────

    [Fact]
    public void TheWindowTexts_ResolveTheEnglishFromTheTable()
    {
        RunSta(() =>
        {
            var window = BuildWindow(new Fixture(), statusText: "");

            Check.That(window.Title).IsEqualTo("EyeCtrl Supervisor - Setup");
            Check.That(window.SubtitleText.Text).IsEqualTo(
                "Watchdog that keeps EyeCtrl running and falls back to another eye-control app if it crashes.");
            Check.That(window.FallbackHeadingText.Text).IsEqualTo("Fallback assistive-technology priority");
            Check.That(window.FallbackDescriptionText.Text).IsEqualTo(
                "When EyeCtrl crashes repeatedly, the supervisor launches the first installed app in this list.");
            Check.That(window.ReorderingNoteText.Text).IsEqualTo(
                "(Reordering is out of scope for this build; the default order above is shown.)");
            Check.That(window.AdvancedExpander.Header).IsEqualTo("Advanced");
            Check.That(window.MaxRetriesLabel.Text).IsEqualTo("Max retries:  ");
            Check.That(window.MaxRetriesRange.Text).IsEqualTo("(1-20)");
            Check.That(window.HangThresholdLabel.Text).IsEqualTo("Hang threshold (s):  ");
            Check.That(window.HangThresholdRange.Text).IsEqualTo("(5-300)");
            Check.That(window.DiagnosticsHeadingText.Text).IsEqualTo("Diagnostics");
            Check.That(window.StatusLabel.Text).IsEqualTo("Supervisor idle.");

            var toggles = TextsUnder(window.ToggleHost);
            Check.That(toggles).Contains("Enable supervision");
            Check.That(toggles).Contains("Start EyeCtrl at logon");
            // Both switches are off at rest; the on-word is pinned by the toggle test below.
            Check.That(toggles).Contains("Off");

            var diagnostics = TextsUnder(window.DiagnosticsHost);
            Check.That(diagnostics).Contains("Launch EyeCtrl now");
            Check.That(diagnostics).Contains("Save");

            var names = ((IEnumerable<string>)window.FallbackList.ItemsSource!).ToList();
            Check.That(names).ContainsExactly("TD Control", "Snap Core First", "Windows Eye Control");
        });
    }

    [Fact]
    public void TheEnableToggle_DrivesTheEnglishStatusLines()
    {
        RunSta(() =>
        {
            var window = BuildWindow(new Fixture(), statusText: "");

            Click(window.EnableToggle);
            Check.That(window.StatusLabel.Text).IsEqualTo(
                "Supervision enabled. (Scheduled Task install is a separate step.)");
            Check.That(TextsUnder(window.ToggleHost)).Contains("On");

            Click(window.EnableToggle);
            Check.That(window.StatusLabel.Text).IsEqualTo("Supervision disabled.");
            Check.That(TextsUnder(window.ToggleHost)).Contains("Off");
        });
    }

    [Fact]
    public void TheSaveButton_ShowsTheEnglishSavedAndFailedLines()
    {
        RunSta(() =>
        {
            var window = BuildWindow(new Fixture(), statusText: "");
            ClickButtonWithLabel(window.DiagnosticsHost, "Save");
            Check.That(window.StatusLabel.Text).IsEqualTo("Saved (supervision settings).");

            // A save that cannot land shows the keyed frame; the raw detail after it stays as-is.
            var blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var broken = BuildWindow(
                new Fixture(),
                statusText: "",
                settingsService: new SettingsService(
                    Path.Combine(blocked, "settings.json"), SettingsWriter.Supervisor));
            ClickButtonWithLabel(broken.DiagnosticsHost, "Save");
            Check.WithCustomMessage($"'{broken.StatusLabel.Text}' does not carry the keyed failure frame.")
                .That(broken.StatusLabel.Text.StartsWith("Save failed: ", StringComparison.Ordinal)).IsTrue();
        });
    }

    [Fact]
    public void BothNotes_Are15Dip()
    {
        // fixup-01: T05a's logon note matches the language note at 15 DIP.
        RunSta(() =>
        {
            var window = BuildWindow(new Fixture());

            Check.That(window.LanguageNote.FontSize).IsEqualTo(15);
            Check.That(window.StartOnBootNote.FontSize).IsEqualTo(15);
        });
    }

    /// <summary>Every non-empty TextBlock text under <paramref name="node"/>, for the sites whose
    /// control has no public text accessor (a DwellToggle's label and state word, a button's
    /// caption).</summary>
    private static List<string> TextsUnder(DependencyObject node)
    {
        var found = new List<string>();

        void Walk(DependencyObject current)
        {
            if (current is TextBlock text && !string.IsNullOrEmpty(text.Text))
                found.Add(text.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                Walk(child);
        }

        Walk(node);
        return found;
    }

    /// <summary>Clicks a DwellToggle's switch, running the same action a dwell would (and the
    /// setter under test).</summary>
    private static void Click(DwellToggle toggle)
    {
        var switchRoot = toggle.Children.OfType<DwellButton>().Single().Root;
        switchRoot.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        });
    }

    /// <summary>Clicks the DwellButton under <paramref name="host"/> whose caption is
    /// <paramref name="label"/>.</summary>
    private static void ClickButtonWithLabel(DependencyObject host, string label)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(host).OfType<FrameworkElement>())
        {
            if (!TextsUnder(child).Contains(label)) continue;

            child.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            });
            return;
        }

        throw new InvalidOperationException($"No button labelled '{label}' under {host}.");
    }

    private static TextBlock? FindByAutomationId(DependencyObject node, string id)
    {
        if (node is TextBlock text && System.Windows.Automation.AutomationProperties.GetAutomationId(text) == id)
            return text;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (FindByAutomationId(child, id) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Writes the review sheet for the user (opt-in; not part of the gate).</summary>
    [Fact]
    public void WriteReviewSheet()
    {
        if (Environment.GetEnvironmentVariable("EYECTRL_WRITE_T05B_SHEET") != "1")
            return;

        RunSta(() =>
        {
            var states = new (RowState State, string Caption)[]
            {
                (RowState.AtRestSystem, "1  At rest, System selected"),
                (RowState.GermanLiveSending, "2  Deutsch chosen, App up (live, sending)"),
                (RowState.GermanPendingAppDown, "3  Deutsch pending, App down"),
                (RowState.Superseded, "4a Superseded (changed in EyeCtrl meanwhile)"),
                (RowState.Refused, "4b Refused by EyeCtrl"),
            };

            var tiles = new List<BitmapSource>();
            foreach (var (state, caption) in states)
            {
                var fixture = new Fixture();
                var window = BuildWindow(fixture);
                fixture.Drive(state);
                var (source, frame) = Host(window);
                try
                {
                    // The detached content, not the scaled frame: rendered at 192 DPI it is exactly
                    // 2x whatever this machine's own DPI is.
                    tiles.Add(RenderTile((FrameworkElement)((Border)frame).Child, caption));
                }
                finally
                {
                    source.Dispose();
                }
            }

            var sheet = Compose(tiles);
            Directory.CreateDirectory(@"C:\t\keys");
            using var file = File.Create(@"C:\t\keys\t05b-supervisor-language-row.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            encoder.Save(file);
        });
    }

    /// <summary>The top of the window (title, language row, toggles) plus the status line, at 2.0.</summary>
    private static BitmapSource RenderTile(FrameworkElement content, string caption)
    {
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var full = new RenderTargetBitmap(
            (int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        full.Render(content);

        var label = new TextBlock
        {
            Text = caption,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            Padding = new Thickness(12, 6, 12, 6),
            Width = width * Scale,
        };
        label.Measure(new Size(width * Scale, double.PositiveInfinity));
        label.Arrange(new Rect(label.DesiredSize));
        var labelBitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * Scale), (int)Math.Ceiling(label.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        labelBitmap.Render(label);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(labelBitmap, new Rect(0, 0, labelBitmap.PixelWidth, labelBitmap.PixelHeight));
            // The window's own background (#252526): the detached content draws none of its own.
            var area = new Rect(0, labelBitmap.PixelHeight, full.PixelWidth, full.PixelHeight);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)), null, area);
            dc.DrawImage(full, area);
        }
        var tile = new RenderTargetBitmap(
            full.PixelWidth, full.PixelHeight + labelBitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        tile.Render(visual);
        return tile;
    }

    private static BitmapSource Compose(IReadOnlyList<BitmapSource> tiles)
    {
        const int Gap = 24;
        var width = tiles.Sum(t => t.PixelWidth) + Gap * (tiles.Count + 1);
        var height = tiles.Max(t => t.PixelHeight) + Gap * 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, width, height));
            var x = (double)Gap;
            foreach (var tile in tiles)
            {
                dc.DrawImage(tile, new Rect(x, Gap, tile.PixelWidth, tile.PixelHeight));
                x += tile.PixelWidth + Gap;
            }
        }
        var sheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        return sheet;
    }
}
