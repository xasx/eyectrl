namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Direct pass-through filter with no smoothing.
/// Returns positions in physical screen pixels.
/// </summary>
public class DirectFilter : IGazeFilter
{
    private PhysicalPoint _lastValid = new(0, 0);

    /// <inheritdoc />
    public PhysicalPoint Filter(GazePoint rawGaze)
    {
        if (rawGaze.IsValid)
            _lastValid = new PhysicalPoint(rawGaze.X, rawGaze.Y);
        return _lastValid;
    }

    /// <inheritdoc />
    public void Reset() => _lastValid = new(0, 0);
}
