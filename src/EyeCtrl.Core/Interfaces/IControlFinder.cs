namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

public interface IControlFinder
{
    ControlInfo? FindTarget(PhysicalPoint point);
}
