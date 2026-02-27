namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
public interface IGazeProvider : IDisposable
{
    event Action<GazePoint>? GazePointReceived;
    void Start();
    void Stop();
    bool IsConnected { get; }
}
