namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public partial class ActionMenuWindow : OverlayWindow
{
    private const double ButtonRadius = 50;
    private const double RingRadius = 160;

    private readonly DwellDetector _dwellDetector = new();
    private readonly Dictionary<string, (Ellipse Background, Path ProgressArc, ArcSegment Arc)> _buttonVisuals = new();
    private readonly Dictionary<string, InteractionAction> _actionMap = new();

    public event Action<InteractionAction>? ActionSelected;
    public event Action? BackSelected;

    public ActionMenuWindow()
    {
        InitializeComponent();

        _dwellDetector.TargetActivated += OnTargetActivated;
        _dwellDetector.TargetProgress += OnTargetProgress;
    }

    public void Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs)
    {
        _dwellDetector.ClearTargets();
        _buttonVisuals.Clear();
        _actionMap.Clear();
        MenuCanvas.Children.Clear();

        var allItems = new List<(string Id, string Label)>();
        foreach (var action in actions)
        {
            var id = action.ToString();
            allItems.Add((id, GetActionSymbol(action)));
            _actionMap[id] = action;
        }
        if (showBackButton)
            allItems.Add(("Back", "\u21A9")); // ↩ Back arrow

        var canvasCenter = new Point(Width / 2, Height / 2);
        var angleStep = 360.0 / allItems.Count;

        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, label) = allItems[i];
            var angleRad = (angleStep * i - 90) * Math.PI / 180;
            var bx = canvasCenter.X + RingRadius * Math.Cos(angleRad);
            var by = canvasCenter.Y + RingRadius * Math.Sin(angleRad);

            var buttonCenter = new ScreenPoint(bx, by);
            _dwellDetector.AddTarget(new DwellTarget(id, buttonCenter, ButtonRadius, dwellTimeMs));

            // Background circle
            var bg = new Ellipse
            {
                Width = ButtonRadius * 2,
                Height = ButtonRadius * 2,
                Fill = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                Stroke = new SolidColorBrush(Color.FromArgb(100, 100, 100, 100)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(bg, bx - ButtonRadius);
            Canvas.SetTop(bg, by - ButtonRadius);
            MenuCanvas.Children.Add(bg);

            // Progress arc (dwell indicator ring)
            var arcRadius = ButtonRadius - 4;
            var arcSegment = new ArcSegment
            {
                Point = new Point(bx, by - arcRadius),
                Size = new Size(arcRadius, arcRadius),
                IsLargeArc = false,
                SweepDirection = SweepDirection.Clockwise
            };
            var figure = new PathFigure
            {
                StartPoint = new Point(bx, by - arcRadius),
                IsClosed = false
            };
            figure.Segments.Add(arcSegment);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var progressPath = new Path
            {
                Data = geometry,
                Stroke = Brushes.Lime,
                StrokeThickness = 4
            };
            MenuCanvas.Children.Add(progressPath);

            _buttonVisuals[id] = (bg, progressPath, arcSegment);

            // Symbol label
            var textBlock = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 22,
                TextAlignment = TextAlignment.Center,
                Width = ButtonRadius * 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(textBlock, bx - ButtonRadius);
            Canvas.SetTop(textBlock, by - textBlock.DesiredSize.Height / 2);
            MenuCanvas.Children.Add(textBlock);
        }

        Left = center.X - Width / 2;
        Top = center.Y - Height / 2;
        Show();
    }

    public void UpdateGaze(ScreenPoint screenPoint, long timestampMicroseconds)
    {
        // Convert screen coordinates to local canvas coordinates
        var localX = screenPoint.X - Left;
        var localY = screenPoint.Y - Top;
        _dwellDetector.Update(new ScreenPoint(localX, localY), timestampMicroseconds);
    }

    private void OnTargetActivated(string targetId)
    {
        if (targetId == "Back")
            BackSelected?.Invoke();
        else if (_actionMap.TryGetValue(targetId, out var action))
            ActionSelected?.Invoke(action);
    }

    private void OnTargetProgress(string targetId, double progress)
    {
        if (!_buttonVisuals.TryGetValue(targetId, out var visuals)) return;

        var (bg, _, arcSegment) = visuals;
        var centerX = Canvas.GetLeft(bg) + ButtonRadius;
        var centerY = Canvas.GetTop(bg) + ButtonRadius;
        var radius = ButtonRadius - 4;

        var angle = progress * 360;
        var radians = angle * Math.PI / 180;

        var endX = centerX + radius * Math.Sin(radians);
        var endY = centerY - radius * Math.Cos(radians);

        arcSegment.Point = new Point(endX, endY);
        arcSegment.IsLargeArc = angle > 180;
    }

    /// <summary>
    /// Returns a clear Unicode symbol for each action.
    /// </summary>
    private static string GetActionSymbol(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "\u261D",     // ☝ Index pointing up (tap/click)
        InteractionAction.RightClick => "\u2630",    // ☰ Trigram (context menu)
        InteractionAction.DoubleClick => "\u270C",   // ✌ Victory hand (two taps)
        InteractionAction.DragStart => "\u270A",     // ✊ Raised fist (grab)
        InteractionAction.DragEnd => "\u270B",       // ✋ Raised hand (release)
        InteractionAction.ScrollUp => "\u25B2",      // ▲ Up triangle
        InteractionAction.ScrollDown => "\u25BC",    // ▼ Down triangle
        InteractionAction.ScrollLeft => "\u25C0",    // ◀ Left triangle
        InteractionAction.ScrollRight => "\u25B6",   // ▶ Right triangle
        _ => action.ToString()
    };
}
