namespace OffScreenGazeProbe;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public partial class MainWindow : Window
{
    // ── Tunables (must match the design spec) ──────────────────────────
    // Off-screen dead-band past the bezel, and the near-edge band just inside the screen.
    // Both in physical pixels. If the off-screen predicate in the spec changes, change it here too.
    private const double EdgeMargin = 50.0;

    // ── GazeEngine pipeline ────────────────────────────────────────────
    private GazeEngine? _gazeEngine;
    private TobiiStreamEngineProvider? _gazeProvider;

    // ── Shared gaze state (gaze thread → UI timer) ─────────────────────
    private readonly object _gazeLock = new();
    private double _lastX;
    private double _lastY;
    private bool _lastValid;
    private bool _hasNewSample;

    // ── Cumulative per-edge tallies (the durable answer to the question) ──
    // We count BOTH valid-off-screen hits and valid-near-edge hits, so whichever way the
    // assumption resolves we have the data for the chosen summon predicate.
    private long _invalidSamples;
    private long _validOnScreen;
    private long _offTop, _offBottom, _offLeft, _offRight;
    private long _nearTop, _nearBottom, _nearLeft, _nearRight;
    private double _screenW;
    private double _screenH;

    // ── UI timer ──────────────────────────────────────────────────────
    private DispatcherTimer _uiTimer = null!;

    // ── Brushes ───────────────────────────────────────────────────────
    private static readonly Brush IdleBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x4d));
    private static readonly Brush OffScreenBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32)); // green
    private static readonly Brush NearBrush      = new SolidColorBrush(Color.FromRgb(0xc7, 0x7b, 0x17)); // orange
    private static readonly Brush InvalidBrush   = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)); // gray

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var scaleX = DpiHelper.GetScale(this);
            _screenW = SystemParameters.PrimaryScreenWidth * scaleX;
            _screenH = SystemParameters.PrimaryScreenHeight * scaleX;

            var logger = NullLoggerFactory.Instance.CreateLogger<TobiiStreamEngineProvider>();
            _gazeProvider = new TobiiStreamEngineProvider(_screenW, _screenH, logger);

            var settings = new EyeCtrlSettings();
            _gazeEngine = new GazeEngine(_gazeProvider, new NopInputSimulator(), settings);

            // CRITICAL: subscribe to GazeSampled (RAW, pre-filter, carries IsValid + physical px),
            // NOT CursorMoved (filtered/clamped, loses the off-edge coordinate we're measuring).
            _gazeEngine.GazeSampled += OnGazeSampled;
            _gazeEngine.Start();

            StartUITimer();
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(this, ex.Message, "StreamEngine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            });
        }
    }

    // ── GazeSampled handler (runs on Tobii poll thread) ────────────────
    //    Classifies the sample and tallies counters under the lock. UI reads the latest
    //    sample separately; counters are only ever written here and read on the UI thread
    //    at 60Hz, so the lock guards both consistently.

    private void OnGazeSampled(GazePoint sample)
    {
        lock (_gazeLock)
        {
            _lastX = sample.X;
            _lastY = sample.Y;
            _lastValid = sample.IsValid;
            _hasNewSample = true;

            if (!sample.IsValid)
            {
                _invalidSamples++;
                return;
            }

            // Off-screen classification (the spec's summon predicate)
            var offTop    = sample.Y < -EdgeMargin;
            var offBottom = sample.Y > _screenH + EdgeMargin;
            var offLeft   = sample.X < -EdgeMargin;
            var offRight  = sample.X > _screenW + EdgeMargin;
            // Near-edge classification (the fallback predicate, if off-screen never fires)
            var nearTop    = sample.Y >= 0          && sample.Y < EdgeMargin;
            var nearBottom = sample.Y <= _screenH   && sample.Y > _screenH - EdgeMargin;
            var nearLeft   = sample.X >= 0          && sample.X < EdgeMargin;
            var nearRight  = sample.X <= _screenW   && sample.X > _screenW - EdgeMargin;

            if (offTop) _offTop++;
            if (offBottom) _offBottom++;
            if (offLeft) _offLeft++;
            if (offRight) _offRight++;
            if (nearTop) _nearTop++;
            if (nearBottom) _nearBottom++;
            if (nearLeft) _nearLeft++;
            if (nearRight) _nearRight++;

            if (!offTop && !offBottom && !offLeft && !offRight)
                _validOnScreen++;
        }
    }

    // ── UI timer ──────────────────────────────────────────────────────

    private void StartUITimer()
    {
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _uiTimer.Tick += OnUITick;
        _uiTimer.Start();
    }

    private void OnUITick(object? sender, EventArgs e)
    {
        double x, y;
        bool valid, hasSample;
        long invalid, onScreen, offT, offB, offL, offR, nT, nB, nL, nR;
        lock (_gazeLock)
        {
            x = _lastX; y = _lastY; valid = _lastValid; hasSample = _hasNewSample;
            invalid = _invalidSamples; onScreen = _validOnScreen;
            offT = _offTop; offB = _offBottom; offL = _offLeft; offR = _offRight;
            nT = _nearTop; nB = _nearBottom; nL = _nearLeft; nR = _nearRight;
        }

        if (!hasSample)
            return;

        // ── Raw readout + verdict ──
        RawText.Text = valid
            ? $"valid  x={x,7:F1}  y={y,7:F1}  (screen {_screenW:F0}x{_screenH:F0} physical px)"
            : "INVALID (tracker lost eyes)";

        var offTop    = valid && y < -EdgeMargin;
        var offBottom = valid && y > _screenH + EdgeMargin;
        var offLeft   = valid && x < -EdgeMargin;
        var offRight  = valid && x > _screenW + EdgeMargin;
        var anyOff    = offTop || offBottom || offLeft || offRight;

        var nearTop    = valid && !offTop    && y >= 0        && y < EdgeMargin;
        var nearBottom = valid && !offBottom && y <= _screenH && y > _screenH - EdgeMargin;
        var nearLeft   = valid && !offLeft   && x >= 0        && x < EdgeMargin;
        var nearRight  = valid && !offRight  && x <= _screenW && x > _screenW - EdgeMargin;

        if (anyOff)
        {
            VerdictText.Text = $"OFF-SCREEN (valid!) — summon gesture works on this edge";
            VerdictText.Foreground = Brushes.LightGreen;
        }
        else if (valid)
        {
            VerdictText.Text = nearTop || nearBottom || nearLeft || nearRight
                ? "Valid, near edge band"
                : "Valid, on-screen";
            VerdictText.Foreground = (nearTop || nearBottom || nearLeft || nearRight)
                ? Brushes.Orange : Brushes.White;
        }
        else
        {
            VerdictText.Text = "INVALID — tracker lost the eyes (no coordinate)";
            VerdictText.Foreground = Brushes.Gray;
        }

        // ── Edge cells: highlight the active edge ──
        UpdateEdge(TopEdge,    TopEdgeText,    offTop,    nearTop,    "TOP");
        UpdateEdge(BottomEdge, BottomEdgeText, offBottom, nearBottom, "BOTTOM");
        UpdateEdge(LeftEdge,   LeftEdgeText,   offLeft,   nearLeft,   "LEFT");
        UpdateEdge(RightEdge,  RightEdgeText,  offRight,  nearRight,  "RIGHT");

        // ── Cumulative counters ──
        CountersText.Text =
            $"Totals — invalid:{invalid}  valid-on-screen:{onScreen}\n" +
            $"Off-screen (valid):  T={offT}  B={offB}  L={offL}  R={offR}\n" +
            $"Near-edge  (valid):  T={nT}   B={nB}   L={nL}   R={nR}";
    }

    private static void UpdateEdge(Border cell, TextBlock label, bool off, bool near, string name)
    {
        if (off)
        {
            cell.Background = OffScreenBrush;
            label.Foreground = Brushes.White;
            label.Text = $"{name}: OFF-SCREEN";
        }
        else if (near)
        {
            cell.Background = NearBrush;
            label.Foreground = Brushes.White;
            label.Text = $"{name}: near";
        }
        else
        {
            cell.Background = IdleBrush;
            label.Foreground = Brushes.White;
            label.Text = name;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer?.Stop();
        _gazeEngine?.Stop();
        _gazeEngine?.Dispose();
    }

    // ── NopInputSimulator (probe is read-only, never moves the cursor) ──

    private sealed class NopInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint point) { }
        public void LeftClick(PhysicalPoint point) { }
        public void RightClick(PhysicalPoint point) { }
        public void DoubleClick(PhysicalPoint point) { }
        public void MouseDown(PhysicalPoint point) { }
        public void MouseUp(PhysicalPoint point) { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
