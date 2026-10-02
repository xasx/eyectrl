namespace EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// Interface for simulating mouse and keyboard input.
/// All point coordinates are in physical screen pixels.
/// </summary>
public interface IInputSimulator
{
    /// <summary>Moves the mouse cursor to the specified physical screen pixel position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void MoveCursor(PhysicalPoint point);
    /// <summary>Performs a left mouse button click at the specified position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void LeftClick(PhysicalPoint point);
    /// <summary>Performs a right mouse button click at the specified position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void RightClick(PhysicalPoint point);
    /// <summary>Performs a double-click (left button) at the specified position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void DoubleClick(PhysicalPoint point);
    /// <summary>
    /// Performs a middle mouse button click at the specified position. A default interface
    /// method (like the extended-key overloads below) so test fakes that never click do not
    /// each need a stub — but it throws rather than no-ops, so a simulator that claims the
    /// interface without really implementing it fails loudly instead of swallowing the click.
    /// </summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void MiddleClick(PhysicalPoint point) => throw new NotSupportedException("This input simulator does not implement middle clicks.");
    /// <summary>Presses and holds the left mouse button at the specified position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void MouseDown(PhysicalPoint point);
    /// <summary>Releases the left mouse button at the specified position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    void MouseUp(PhysicalPoint point);
    /// <summary>Sends a mouse wheel scroll event at the specified position.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    /// <param name="deltaX">Horizontal scroll amount in wheel delta units (typically multiples of 120).</param>
    /// <param name="deltaY">Vertical scroll amount in wheel delta units (typically multiples of 120).</param>
    void Scroll(PhysicalPoint point, int deltaX, int deltaY);
    /// <summary>Sends a keyboard key press and release.</summary>
    /// <param name="virtualKeyCode">Win32 virtual key code.</param>
    void SendKeyPress(ushort virtualKeyCode);
    /// <summary>Sends a keyboard key down (press without release).</summary>
    /// <param name="virtualKeyCode">Win32 virtual key code.</param>
    void SendKeyDown(ushort virtualKeyCode);
    /// <summary>Sends a keyboard key up (release).</summary>
    /// <param name="virtualKeyCode">Win32 virtual key code.</param>
    void SendKeyUp(ushort virtualKeyCode);
    /// <summary>Types a string of text by simulating individual keystrokes.</summary>
    /// <param name="text">The text to type.</param>
    void SendText(string text);
    /// <summary>Sends a key press combined with modifier keys (e.g., Ctrl+C).</summary>
    /// <param name="modifiers">Set of modifier keys to hold during the press.</param>
    /// <param name="virtualKeyCode">Win32 virtual key code for the main key.</param>
    void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode);
    /// <summary>Sends a keyboard key press and release, optionally marking it as an extended key.</summary>
    /// <param name="virtualKeyCode">Win32 virtual key code.</param>
    /// <param name="isExtendedKey">Whether the key is an extended key (e.g., Right Ctrl, Numpad Enter).</param>
    void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey) => SendKeyPress(virtualKeyCode);
    /// <summary>Sends a modified key press, optionally marking the main key as an extended key.</summary>
    /// <param name="modifiers">Set of modifier keys to hold during the press.</param>
    /// <param name="virtualKeyCode">Win32 virtual key code for the main key.</param>
    /// <param name="isExtendedKey">Whether the main key is an extended key.</param>
    void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey) => SendModifiedKeyPress(modifiers, virtualKeyCode);
    /// <summary>Performs a left click with the given modifiers held for the duration of the click (one atomic batch). The dispatcher routes bare clicks to <see cref="LeftClick(PhysicalPoint)"/>.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    /// <param name="modifiers">Modifier keys held down for the click. Must be non-empty when called directly.</param>
    void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");
    /// <summary>Performs a right click with the given modifiers held (one atomic batch).</summary>
    void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");
    /// <summary>Performs a middle click with the given modifiers held (one atomic batch).</summary>
    void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");
    /// <summary>Performs a double click with the given modifiers held (ONE batch: down, up, down, up between the modifier pair).</summary>
    void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified clicks.");
    /// <summary>Sends a wheel scroll with the given modifiers held; the batch leads with an absolute move so the wheel lands on <paramref name="point"/>.</summary>
    void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers) => throw new NotSupportedException("This input simulator does not implement modified scrolls.");
}
