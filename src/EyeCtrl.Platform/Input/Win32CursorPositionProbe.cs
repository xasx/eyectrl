namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;

/// <summary>
/// Win32 <c>GetCursorPos</c> behind R2's <see cref="ICursorPositionProbe"/>. A failed read is null
/// rather than an exception: the caller treats "no cursor" as no evidence.
/// </summary>
public sealed class Win32CursorPositionProbe : ICursorPositionProbe
{
    /// <inheritdoc />
    public PhysicalPoint? TryGetCursorPosition()
    {
        try
        {
            return InputIntegrityNativeMethods.GetCursorPos(out var point)
                ? new PhysicalPoint(point.X, point.Y)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
