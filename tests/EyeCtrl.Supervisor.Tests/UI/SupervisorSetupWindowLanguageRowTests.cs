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
/// </summary>
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
                _ => new TaskCompletionSource<AppRequestResult>().Task, work => work());
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

    private SupervisorSetupWindow BuildWindow(Fixture fixture)
    {
        var settingsPath = Path.Combine(_root, "settings.json");
        var window = new SupervisorSetupWindow(
            new EyeCtrlSettings(),
            new SettingsService(settingsPath, SettingsWriter.Supervisor),
            _ => { },
            () => { },
            new StartOnBootSetupRow(fixture.StartOnBoot, () => false),
            new LanguageSetupRow(fixture.Language, () => fixture.Disk ?? UiLanguage.System, CultureInfo.GetCultureInfo("de-DE")),
            StringTable.EmbeddedEnglish,
            "Supervisor state: Supervising");
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
