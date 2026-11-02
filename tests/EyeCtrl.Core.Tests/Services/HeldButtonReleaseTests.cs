namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// C3's one release operation, shared by the Supervisor (after a force-kill) and the App (at
/// startup). The contract under test: <b>exactly one in-place button-up and only when a marker
/// exists</b>, and the marker is cleared only after a send that the live OS button state confirms
/// left the button up (C8 F3).
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
        Check.That(result.MarkerClearFailure).IsNull();
        Check.That(result.ReleasedAndCleared).IsTrue();
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
    public void TheProbe_IsReadBeforeAndAfterTheRelease_AndTheAfterReadingDecidesTheClear()
    {
        // GetAsyncKeyState is read twice since C8 F3: before the release for the log, and after it
        // to decide whether the marker may be cleared. A marker with the button already UP (the
        // crash landed between MouseUp and the marker clear) still owes its one inert button-up,
        // and an "up" reading after the send clears the marker.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var callCountsAtProbe = new List<int>();
        var release = new HeldButtonRelease(marker, input,
            leftButtonIsDown: () =>
            {
                callCountsAtProbe.Add(input.Calls.Count);
                return false;
            });

        var result = release.ReleaseIfMarked();

        Check.That(result.MarkerWasSet).IsTrue();
        Check.That(result.LeftButtonWasDown == false).IsTrue();   // the before reading
        Check.That(result.LeftButtonStillDown == false).IsTrue(); // the after reading
        Check.That(callCountsAtProbe).ContainsExactly(0, 1);      // before the send, after it
        Check.That(result.ReleasedAndCleared).IsTrue();
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.That(marker.IsSet).IsFalse();
    }

    [Fact]
    public void TheButtonStillReadingDownAfterTheRelease_KeepsTheMarkerForTheNextPath()
    {
        // C8 F3's UIPI case: SendInput accepts the call and Windows delivers nothing, so
        // GetAsyncKeyState still reads down. Clearing the marker then would lose the only record
        // that a button is owed; keeping it lets the Supervisor/startup backstop retry.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var release = new HeldButtonRelease(marker, input, leftButtonIsDown: () => true);

        var result = release.ReleaseIfMarked();

        Check.That(result.MarkerWasSet).IsTrue();
        Check.That(result.LeftButtonWasDown == true).IsTrue();
        Check.That(result.LeftButtonStillDown == true).IsTrue();
        Check.That(result.MarkerKeptBecauseButtonDown).IsTrue();
        Check.That(result.ReleasedAndCleared).IsFalse();
        Check.That(result.MarkerClearFailure).IsNull();
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.That(marker.ClearCount).IsEqualTo(0);
        Check.That(marker.IsSet).IsTrue();
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

    [Fact]
    public void AClearFailureAfterASuccessfulSend_IsReportedNotThrown()
    {
        // Fixup-01 F5: the SEND succeeded, so the button is up. A clear that throws (AV or the
        // indexer holding the file without FILE_SHARE_DELETE) must not surface as a failed
        // release — the caller's log would then hide that the button was actually released. The
        // stale marker costs one later inert LEFTUP at most.
        var failure = new IOException("the marker file is locked");
        var marker = new FakeMarker { IsSet = true, ClearFailure = failure };
        var input = new RecordingInput();

        var result = default(HeldButtonReleaseResult);
        Check.ThatCode(() => result = new HeldButtonRelease(marker, input).ReleaseIfMarked())
             .DoesNotThrow();

        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.That(result.MarkerWasSet).IsTrue();
        Check.That(result.ReleasedAndCleared).IsFalse();
        Check.That(result.MarkerClearFailure).IsSameReferenceAs(failure);
        Check.That(marker.IsSet).IsTrue();                      // stale, not lost
    }

    private sealed class FakeMarker : IHeldButtonMarker
    {
        public bool IsSet { get; set; }

        public Exception? ClearFailure { get; init; }

        public int ClearCount { get; private set; }

        public void Set() => IsSet = true;

        public void Clear()
        {
            ClearCount++;
            if (ClearFailure is not null) throw ClearFailure;
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
