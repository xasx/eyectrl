namespace EyeCtrl.Core.Input;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's observation point: wraps the real input sink and verifies, for every click/double-click and
/// pointed scroll, that the batch's leading absolute move actually moved the cursor. It also feeds
/// every commanded FreeLook move to <see cref="BlockedInputDetector"/>.
///
/// <para>
/// Which calls are verified is a deliberate list, not every member:
/// </para>
/// <list type="bullet">
/// <item>Click, double-click and their modifier overloads: one absolute MOVE to the point, so the
/// cursor must land within tolerance of it.</item>
/// <item><see cref="ScrollAt"/>: one batch that leads with the move, so it is verifiable the same
/// way. <see cref="ScrollAtAndRestore"/> is NOT verified: it moves to the anchor and back inside
/// one atomic batch, so "before == after" is the intended outcome and cannot tell a blocked move
/// from a successful hop. Scroll mode's entry park is a <see cref="MoveCursor"/> and is covered by
/// the sampler.</item>
/// <item><see cref="MouseDown"/>/<see cref="MouseUp"/>: a drag's press carries no claim about where
/// the pointer should end up (a repeat-mode hold may press twice), so it is not an action hit.</item>
/// <item>Keyboard input: not mouse input; UIPI treats it separately and this ticket has no evidence
/// about keyboard drops, so nothing is inferred from it.</item>
/// </list>
/// </summary>
public sealed class VerifiedInputSimulator : IInputSimulator
{
    private readonly IInputSimulator _inner;
    private readonly BlockedInputDetector _detector;
    private readonly ICursorPositionProbe _cursor;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The real (or a fake) input sink.</param>
    /// <param name="detector">Receives the observations.</param>
    /// <param name="cursor">Reads the cursor before and after each verified batch.</param>
    public VerifiedInputSimulator(
        IInputSimulator inner, BlockedInputDetector detector, ICursorPositionProbe cursor)
    {
        _inner = inner;
        _detector = detector;
        _cursor = cursor;
    }

    /// <inheritdoc />
    public void MoveCursor(PhysicalPoint point)
    {
        // N1: read BEFORE our own move writes over the spot. A click whose injection landed a frame
        // late is visible here; reading after this MoveCursor would see only the gaze point it just
        // wrote, which is exactly the button the owner is still looking at.
        var cursorBeforeMove = ReadCursor();
        _inner.MoveCursor(point);
        _detector.ReportMoveCommand(point, cursorBeforeMove);
    }

    /// <inheritdoc />
    public void LeftClick(PhysicalPoint point) => Verify(point, () => _inner.LeftClick(point));

    /// <summary>Performs a left click with modifiers held (one atomic batch), verified like a bare click.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    /// <param name="modifiers">Modifier keys held for the click.</param>
    public void LeftClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        => Verify(point, () => _inner.LeftClick(point, modifiers));

    /// <inheritdoc />
    public void RightClick(PhysicalPoint point) => Verify(point, () => _inner.RightClick(point));

    /// <summary>Performs a right click with modifiers held, verified like a bare click.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    /// <param name="modifiers">Modifier keys held for the click.</param>
    public void RightClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        => Verify(point, () => _inner.RightClick(point, modifiers));

    /// <inheritdoc />
    public void MiddleClick(PhysicalPoint point) => Verify(point, () => _inner.MiddleClick(point));

    /// <summary>Performs a middle click with modifiers held, verified like a bare click.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    /// <param name="modifiers">Modifier keys held for the click.</param>
    public void MiddleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        => Verify(point, () => _inner.MiddleClick(point, modifiers));

    /// <inheritdoc />
    public void DoubleClick(PhysicalPoint point) => Verify(point, () => _inner.DoubleClick(point));

    /// <summary>Performs a double-click with modifiers held, verified like a bare click.</summary>
    /// <param name="point">Target position in physical screen pixels.</param>
    /// <param name="modifiers">Modifier keys held for the double-click.</param>
    public void DoubleClick(PhysicalPoint point, IReadOnlySet<ModifierKey>? modifiers)
        => Verify(point, () => _inner.DoubleClick(point, modifiers));

    /// <inheritdoc />
    public void MouseDown(PhysicalPoint point) => _inner.MouseDown(point);

    /// <inheritdoc />
    public void MouseUp(PhysicalPoint point) => _inner.MouseUp(point);

    /// <inheritdoc />
    public void ReleaseLeftButton() => _inner.ReleaseLeftButton();

    /// <inheritdoc />
    public void Scroll(PhysicalPoint point, int deltaX, int deltaY) =>
        _inner.Scroll(point, deltaX, deltaY);

    /// <inheritdoc />
    public void ScrollAt(PhysicalPoint point, int deltaX, int deltaY) =>
        Verify(point, () => _inner.ScrollAt(point, deltaX, deltaY));

    /// <inheritdoc />
    public void ScrollAtAndRestore(PhysicalPoint at, PhysicalPoint restore, int deltaX, int deltaY) =>
        _inner.ScrollAtAndRestore(at, restore, deltaX, deltaY);

    /// <inheritdoc />
    public void SendKeyPress(ushort virtualKeyCode) => _inner.SendKeyPress(virtualKeyCode);

    /// <inheritdoc />
    public void SendKeyDown(ushort virtualKeyCode) => _inner.SendKeyDown(virtualKeyCode);

    /// <inheritdoc />
    public void SendKeyUp(ushort virtualKeyCode) => _inner.SendKeyUp(virtualKeyCode);

    /// <inheritdoc />
    public void SendText(string text) => _inner.SendText(text);

    /// <inheritdoc />
    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) =>
        _inner.SendModifiedKeyPress(modifiers, virtualKeyCode);

    /// <inheritdoc />
    public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey) =>
        _inner.SendKeyPress(virtualKeyCode, isExtendedKey);

    /// <inheritdoc />
    public void SendModifiedKeyPress(
        IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey) =>
        _inner.SendModifiedKeyPress(modifiers, virtualKeyCode, isExtendedKey);

    /// <inheritdoc />
    public void Scroll(PhysicalPoint point, int deltaX, int deltaY, IReadOnlySet<ModifierKey>? modifiers)
    {
        // The modified wheel batch leads with an absolute move (BuildModifiedWheelBody), so it is
        // verifiable — but only when modifiers are actually held: the empty/null set delegates to the
        // bare wheel, which does not move the pointer.
        if (modifiers is { Count: > 0 })
            Verify(point, () => _inner.Scroll(point, deltaX, deltaY, modifiers));
        else
            _inner.Scroll(point, deltaX, deltaY, modifiers);
    }

    private void Verify(PhysicalPoint target, Action send)
    {
        var before = ReadCursor();
        send();
        var after = ReadCursor();
        _detector.ReportAction(target, before, after);
    }

    private PhysicalPoint? ReadCursor()
    {
        try
        {
            return _cursor.TryGetCursorPosition();
        }
        catch (Exception)
        {
            return null; // no evidence; never let a probe fault break the input path
        }
    }
}
