namespace StreamEngineGazeTracker;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
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
    // ── GazeEngine pipeline ────────────────────────────────────────────
    private GazeEngine? _gazeEngine;
    private TobiiStreamEngineProvider? _gazeProvider;

    // ── Shared gaze state (CursorMoved thread → UI timer) ─────────────
    private readonly object _gazeLock = new();
    private double _filteredX;
    private double _filteredY;
    private bool _hasNewSample;
    private int _gazeCallbackCount;

    // ── Hz computation (1-second rolling window) ──────────────────────
    private readonly Queue<(DateTime time, int count)> _hzWindow = new();
    private double _currentHz;

    // ── Trail ─────────────────────────────────────────────────────────
    private const int MaxTrailPoints = 50;
    private const double TrailDotPhysicalSize = 6;
    private const double GazeDotPhysicalSize  = 16;
    private const double TrailColorR = 0;
    private const double TrailColorG = 212;
    private const double TrailColorB = 255;
    private const double GazeDotColorR = 255;
    private const double GazeDotColorG = 51;
    private const double GazeDotColorB = 102;

    private readonly List<Point> _trailPoints = new(MaxTrailPoints + 1);
    private Ellipse[] _trailDots = null!;
    private Ellipse _gazeDot = null!;
    private bool _uiInitialized;

    // ── Performance tracking ──────────────────────────────────────────
    private readonly Stopwatch _uptime = new();

    // ── UI timer ──────────────────────────────────────────────────────
    private DispatcherTimer _uiTimer = null!;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DebugText.FontSize   = 24;
        DebugOverlay.Padding = new Thickness(16);

        try
        {
            var scaleX = DpiHelper.GetScale(this);
            var screenWidth  = SystemParameters.PrimaryScreenWidth  * scaleX;
            var screenHeight = SystemParameters.PrimaryScreenHeight * scaleX;

            var logger = NullLoggerFactory.Instance.CreateLogger<TobiiStreamEngineProvider>();
            _gazeProvider = new TobiiStreamEngineProvider(screenWidth, screenHeight, logger);

            var settings = new EyeCtrlSettings();
            _gazeEngine = new GazeEngine(_gazeProvider, new NopInputSimulator(), settings);
            _gazeEngine.CursorMoved += OnCursorMoved;
            _gazeEngine.Start();

            StartUITimer();
            _uptime.Start();
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

    // ── CursorMoved handler (runs on Tobii poll thread) ───────────────
    //    GazeEngine fires this after calibration + gap interpolation + OneEuro filtering.
    //    The point is in physical screen pixels.

    private void OnCursorMoved(PhysicalPoint filteredPoint, long timestamp)
    {
        Interlocked.Increment(ref _gazeCallbackCount);

        lock (_gazeLock)
        {
            _filteredX = filteredPoint.X;
            _filteredY = filteredPoint.Y;
            _hasNewSample = true;
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
        // 1. Read latest filtered gaze point
        double fx, fy;
        bool hasSample;
        int ticksThisFrame;
        lock (_gazeLock)
        {
            fx = _filteredX;
            fy = _filteredY;
            hasSample = _hasNewSample;
        }
        ticksThisFrame = Interlocked.Exchange(ref _gazeCallbackCount, 0);

        // 2. Update Hz rolling window
        var now = DateTime.UtcNow;
        _hzWindow.Enqueue((now, ticksThisFrame));
        while (_hzWindow.Count > 0 && (now - _hzWindow.Peek().time).TotalSeconds > 1.0)
            _hzWindow.Dequeue();
        int totalInWindow = 0;
        foreach (var entry in _hzWindow)
            totalInWindow += entry.count;
        _currentHz = totalInWindow;

        // 3. Initialize visuals on first tick
        if (!_uiInitialized)
        {
            InitializeVisuals();
            _uiInitialized = true;
        }

        // 4. Convert physical screen pixel coords to canvas-local DIPs
        //    PointFromScreen maps screen coordinates (physical pixels) to canvas-local DIPs,
        //    correctly handling DPI, window position, and window chrome.
        double px = 0, py = 0;
        if (hasSample)
        {
            Point canvasPt = GazeCanvas.PointFromScreen(new Point(fx, fy));
            px = canvasPt.X;
            py = canvasPt.Y;

            // 5. Update trail
            _trailPoints.Add(new Point(px, py));
            while (_trailPoints.Count > MaxTrailPoints)
                _trailPoints.RemoveAt(0);

            // 6. Update trail dots
            double scaleX = DpiHelper.GetScale(this);
            double halfTrailDotDip = TrailDotPhysicalSize / 2.0 / scaleX;
            for (int i = 0; i < _trailDots.Length; i++)
            {
                if (i < _trailPoints.Count)
                {
                    var pt = _trailPoints[i];
                    double opacity = (double)(i + 1) / _trailPoints.Count;
                    Canvas.SetLeft(_trailDots[i], pt.X - halfTrailDotDip);
                    Canvas.SetTop(_trailDots[i], pt.Y - halfTrailDotDip);
                    _trailDots[i].Opacity = opacity * 0.7;
                    _trailDots[i].Visibility = Visibility.Visible;
                }
                else
                {
                    _trailDots[i].Visibility = Visibility.Collapsed;
                }
            }

            // 7. Update gaze dot
            double halfGazeDotDip = GazeDotPhysicalSize / 2.0 / scaleX;
            Canvas.SetLeft(_gazeDot, px - halfGazeDotDip);
            Canvas.SetTop(_gazeDot, py - halfGazeDotDip);
        }

        // 8. Update debug overlay
        DebugText.Text = hasSample
            ? $"Status: Connected\n" +
              $"Filter: ({fx:F0}, {fy:F0})\n" +
              $"Canvas: ({px:F0}, {py:F0})\n" +
              $"Gaze:   {_currentHz:F0} Hz\n" +
              $"Uptime: {_uptime.Elapsed:mm\\:ss}"
            : $"Status: Waiting for gaze...\n" +
              $"Gaze:   {_currentHz:F0} Hz\n" +
              $"Uptime: {_uptime.Elapsed:mm\\:ss}";
    }

    // ── Cleanup ───────────────────────────────────────────────────────

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer?.Stop();
        _gazeEngine?.Stop();
        _gazeEngine?.Dispose();
    }

    // ── Visual initialization ─────────────────────────────────────────

    private void InitializeVisuals()
    {
        double trailDip = DpiHelper.PhysicalToLogical(this, TrailDotPhysicalSize);
        double gazeDip  = DpiHelper.PhysicalToLogical(this, GazeDotPhysicalSize);

        _trailDots = new Ellipse[MaxTrailPoints];
        for (int i = 0; i < _trailDots.Length; i++)
        {
            _trailDots[i] = new Ellipse
            {
                Width = trailDip,
                Height = trailDip,
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(
                        (byte)TrailColorR, (byte)TrailColorG, (byte)TrailColorB)),
                Visibility = Visibility.Collapsed
            };
            GazeCanvas.Children.Add(_trailDots[i]);
        }

        _gazeDot = new Ellipse
        {
            Width = gazeDip,
            Height = gazeDip,
            Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(
                    (byte)GazeDotColorR, (byte)GazeDotColorG, (byte)GazeDotColorB))
        };
        GazeCanvas.Children.Add(_gazeDot);
    }

    // ── NopInputSimulator (sample is visualization-only) ──────────────

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
