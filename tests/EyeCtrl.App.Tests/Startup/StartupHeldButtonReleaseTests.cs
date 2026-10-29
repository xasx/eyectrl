namespace EyeCtrl.App.Tests.Startup;

using System.IO;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// C3's startup backstop. A hard kill mid-drag (the Supervisor's force-kill, Task Manager, a
/// native crash) leaves the OS left button down; the drag's marker is the only thing that survives
/// the process, and this is what reads it on the next start. All input here is a recording fake —
/// no test may send real <c>SendInput</c> or touch the real <c>%APPDATA%</c> marker.
/// </summary>
public class StartupHeldButtonReleaseTests
{
    [Fact]
    public void APresentMarker_SendsExactlyOneInPlaceReleaseAndClearsIt()
    {
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var lines = new List<string>();

        var released = StartupHeldButtonRelease.Run(marker, input, leftButtonIsDown: null, lines.Add);

        Check.That(released).IsTrue();
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
        Check.That(marker.IsSet).IsFalse();
        Check.That(marker.ClearCount).IsEqualTo(1);
        Check.That(lines).HasSize(1);
        Check.WithCustomMessage("the startup line did not say what it did.")
             .That(lines[0]).Contains("button-up");
    }

    [Fact]
    public void AnAbsentMarker_SendsNoInputAtAllAndWritesNoLine()
    {
        // The owner may be holding a physical mouse (or a second gaze mouse). With no marker this
        // must be a completely silent no-op — "no input" includes no log line to confuse diagnosis.
        var marker = new FakeMarker { IsSet = false };
        var input = new RecordingInput();
        var lines = new List<string>();

        var released = StartupHeldButtonRelease.Run(marker, input, leftButtonIsDown: null, lines.Add);

        Check.That(released).IsFalse();
        Check.That(input.Calls).IsEmpty();
        Check.That(marker.ClearCount).IsEqualTo(0);
        Check.That(lines).IsEmpty();
    }

    [Fact]
    public void TheLogLine_ReportsTheProbeReading()
    {
        // GetAsyncKeyState is logged for diagnosis ("the marker was stale, the button was up") but
        // never gates the release — the marker does.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput();
        var lines = new List<string>();

        StartupHeldButtonRelease.Run(marker, input, leftButtonIsDown: () => true, lines.Add);

        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains("VK_LBUTTON=True");
        Check.That(input.Calls).ContainsExactly("ReleaseLeftButton");
    }

    [Fact]
    public void AFailedRelease_KeepsTheMarkerAndStillLetsTheAppStart()
    {
        // The app must start anyway: the next launch (or the Supervisor's post-kill release) still
        // finds the marker, so the button is not lost — only deferred.
        var marker = new FakeMarker { IsSet = true };
        var input = new RecordingInput { ThrowOnRelease = true };
        var lines = new List<string>();

        var released = StartupHeldButtonRelease.Run(marker, input, leftButtonIsDown: null, lines.Add);

        Check.That(released).IsFalse();
        Check.That(marker.IsSet).IsTrue();
        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains("failed");
    }

    [Fact]
    public void TheStartupCallSite_IsAfterTheGuard_AndBeforeAnyStateChangeOrOverlay()
    {
        // Ordering is part of the contract (ticket C3):
        //  - AFTER the single-instance guard: a duplicate that is about to exit must never release
        //    the RUNNING primary's drag button;
        //  - BEFORE the gaze engine starts, the interaction state machine can enter Paused
        //    (PauseLaunchProvenance), and any EventCoordinator/overlay/note window exists:
        //    NoteInputPolicy forbids synthesized input while paused, and this is startup SendInput.
        // A source-order gate is the only way to state that about App.xaml.cs, which no unit test
        // can run (it builds the whole production graph).
        var source = SourceScanner.StripComments(File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "App.xaml.cs")));

        var callSite = Occurrences(source, "StartupHeldButtonRelease.Run(");
        Check.That(callSite).HasSize(1);

        var guard = IndexOf(source, "_singleInstance.TryAcquire(");
        var run = callSite[0];
        Check.WithCustomMessage("the backstop fires before the single-instance guard: a duplicate "
                + "instance exiting would release the running primary's drag button.")
             .That(run).IsStrictlyGreaterThan(guard);

        foreach (var (marker, why) in new[]
        {
            ("gazeEngine.Start();", "the gaze engine is already running"),
            ("PauseLaunchProvenance.Apply(", "the interaction state machine can already be Paused"),
            ("_eventCoordinator = new EventCoordinator();", "the overlay/note/trail windows can already exist"),
        })
        {
            Check.WithCustomMessage(
                    $"the startup release runs after {why} — {marker} appears before it in App.xaml.cs.")
                 .That(run).IsStrictlyLessThan(IndexOf(source, marker));
        }

        // The SAME marker instance the drag writes must reach the drag coordinator via the context.
        Check.That(Occurrences(source, "HeldButtonMarker: heldButtonMarker")).HasSize(1);
    }

    private static int IndexOf(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Check.WithCustomMessage($"App.xaml.cs no longer contains '{marker}'.")
             .That(index).IsStrictlyGreaterThan(-1);
        return index;
    }

    private static List<int> Occurrences(string source, string marker)
    {
        var found = new List<int>();
        for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            found.Add(at);
        }
        return found;
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
