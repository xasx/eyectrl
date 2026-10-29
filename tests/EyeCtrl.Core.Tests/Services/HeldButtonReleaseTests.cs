namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// C3's one release operation, shared by the Supervisor (after a force-kill) and the App (at
/// startup). The contract under test: <b>exactly one in-place button-up and only when a marker
/// exists</b>, and the marker is cleared only after a successful send.
/// </summary>
public class HeldButtonReleaseTests
{
    [Fact]
    public void WithAMarker_SendsExactlyOneInPlaceReleaseAndClearsIt()
    {
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();

        var result = new HeldButtonRelease(marker, input).ReleaseIfMarked();

        Check.That(result.MarkerWasSet).IsTrue();
        Check.That(result.LeftButtonWasDown).IsNull();          // no probe supplied
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.That(marker.IsSet).IsFalse();
    }

    [Fact]
    public void WithoutAMarker_SendsNothingAndLeavesTheMarkerClear()
    {
        // THE safety rule (ticket C3, behaviour 4): never send a button-up with no marker. The
        // owner may be holding a physical mouse, or using a second gaze mouse.
        var marker = new FakeMarker { IsSet = false };
        var input = new RecordingInput();

        var result = new HeldButtonRelease(marker, input).ReleaseIfMarked();

        Check.That(result).IsEqualTo(HeldButtonReleaseResult.NoMarker);
        Check.That(input.Calls).IsEmpty();
        Check.That(marker.ClearCount).IsEqualTo(0);
    }

    [Fact]
    public void TheProbe_IsReadBeforeTheRelease_AndReportedButNeverDecides()
    {
        // GetAsyncKeyState is diagnostic only: a marker with the button already UP (the crash
        // landed between MouseUp and the marker clear) still owes its one inert button-up, and a
        // probe error must not suppress it. Here the marker is set and the probe says up.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var probedAfterSend = (bool?)null;
        var release = new HeldButtonRelease(marker, input,
            leftButtonIsDown: () =>
            {
                probedAfterSend = input.Calls.Count == 0 ? (bool?)null : true;
                return false;
            });

        var result = release.ReleaseIfMarked();

        Check.That(result.MarkerWasSet).IsTrue();
        Check.That(result.LeftButtonWasDown == false).IsTrue();  // the probe said UP...
        Check.That(probedAfterSend).IsNull();                   // read BEFORE the release        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
    }

    [Fact]
    public void AFailedSend_PropagatesAndKeepsTheMarkerForTheNextPath()
    {
        // Dispose (or the App's startup backstop) is the retry: a release that threw must stay
        // owed rather than be marked done.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput { ThrowOnRelease = true };

        var thrown = false;
        try { new HeldButtonRelease(marker, input).ReleaseIfMarked(); }
        catch (InvalidOperationException) { thrown = true; }

        Check.That(thrown).IsTrue();
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");   // attempted
        Check.That(marker.IsSet).IsTrue();                              // still owed
    }

    private sealed class FakeMarker : IHeldButtonMarker
    {
        public bool IsSet { get; set; }

        public int ClearCount { get; private set; }

        public void Set() => IsSet = true;

        public void Clear()
        {
            ClearCount++;
            IsSet = false;
        }
    }

    /// <summary>Records the in-place release; no real <c>SendInput</c> anywhere.</summary>
    private sealed class RecordingInput : IInputSimulator
    {
        public List<string> Calls { get; } = new();

        public bool ThrowOnRelease { get; init; }

        public void ReleaseLeftButton()
        {
            Calls.Add("ReleaseLeftButton");
            if (ThrowOnRelease)
                throw new InvalidOperationException("SendInput refused the release");
        }

        public void MoveCursor(PhysicalPoint point) => Calls.Add("MoveCursor");
        public void LeftClick(PhysicalPoint point) => Calls.Add("LeftClick");
        public void RightClick(PhysicalPoint point) => Calls.Add("RightClick");
        public void DoubleClick(PhysicalPoint point) => Calls.Add("DoubleClick");
        public void MouseDown(PhysicalPoint point) => Calls.Add("MouseDown");
        public void MouseUp(PhysicalPoint point) => Calls.Add("MouseUp");
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) => Calls.Add("Scroll");
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
