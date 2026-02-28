namespace EyeCtrl.Core;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class GazeEngine : IDisposable
{
    private readonly IGazeProvider _gazeProvider;
    private readonly IInputSimulator _inputSimulator;
    private readonly EyeCtrlSettings _settings;
    private readonly IGazeFilter _filter;
    private readonly FixationDetector _fixationDetector;

    public InteractionManager InteractionManager { get; }
    public event Action<ScreenPoint, long>? CursorMoved;

    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings)
    {
        _gazeProvider = gazeProvider;
        _inputSimulator = inputSimulator;
        _settings = settings;

        _filter = GazeFilterFactory.Create(settings.Cursor);
        _fixationDetector = new FixationDetector(
            settings.Dwell.FixationRadiusPixels,
            settings.Dwell.FixationDetectionMs);

        InteractionManager = new InteractionManager
        {
            ZoomEnabled = settings.Zoom.Enabled
        };

        _gazeProvider.GazePointReceived += OnGazePoint;
        _fixationDetector.FixationStarted += InteractionManager.OnFixationStarted;
        _fixationDetector.FixationEnded += InteractionManager.OnFixationEnded;
        InteractionManager.ActionRequested += OnActionRequested;
    }

    public void Start()
    {
        _gazeProvider.Start();
    }

    public void Stop()
    {
        _gazeProvider.Stop();
    }

    private void OnGazePoint(GazePoint gaze)
    {
        if (InteractionManager.CurrentState == InteractionState.Paused)
            return;

        var filtered = _filter.Filter(gaze);

        if (InteractionManager.CurrentState == InteractionState.FreeLook)
        {
            _inputSimulator.MoveCursor(filtered);
        }

        CursorMoved?.Invoke(filtered, gaze.TimestampMicroseconds);
        _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
    }

    private void OnActionRequested(InteractionAction action, ScreenPoint point)
    {
        switch (action)
        {
            case InteractionAction.LeftClick:
                _inputSimulator.LeftClick(point);
                break;
            case InteractionAction.RightClick:
                _inputSimulator.RightClick(point);
                break;
            case InteractionAction.DoubleClick:
                _inputSimulator.DoubleClick(point);
                break;
            case InteractionAction.DragStart:
                _inputSimulator.MouseDown(point);
                break;
            case InteractionAction.DragEnd:
                _inputSimulator.MouseUp(point);
                break;
            case InteractionAction.ScrollUp:
                _inputSimulator.Scroll(point, 0, 3);
                break;
            case InteractionAction.ScrollDown:
                _inputSimulator.Scroll(point, 0, -3);
                break;
            case InteractionAction.ScrollLeft:
                _inputSimulator.Scroll(point, -3, 0);
                break;
            case InteractionAction.ScrollRight:
                _inputSimulator.Scroll(point, 3, 0);
                break;
        }
    }

    public void Dispose()
    {
        _gazeProvider.GazePointReceived -= OnGazePoint;
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
