namespace EyeCtrl.Core.Models;
public record GazePoint(double X, double Y, long TimestampMicroseconds, bool IsValid);
