namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
public interface IGazeFilter
{
    ScreenPoint Filter(GazePoint rawGaze);
    void Reset();
}
