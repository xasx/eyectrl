namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class DirectFilter : IGazeFilter
{
    private ScreenPoint _lastValid = new(0, 0);

    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (rawGaze.IsValid)
            _lastValid = new ScreenPoint(rawGaze.X, rawGaze.Y);
        return _lastValid;
    }

    public void Reset() => _lastValid = new(0, 0);
}
