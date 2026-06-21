namespace EyeCtrl.Sdk.WinUI.Tests;

using EyeCtrl.Sdk.WinUI;

public class AdapterGroupTrackerTests
{
    [Fact]
    public void NewTracker_IsEmpty()
    {
        var t = new AdapterGroupTracker(() => { });
        Check.That(t.IsEmpty).IsTrue();
    }

    [Fact]
    public void Add_OneId_IsNotEmpty()
    {
        var t = new AdapterGroupTracker(() => { });
        t.Add("a");
        Check.That(t.IsEmpty).IsFalse();
    }

    [Fact]
    public void Remove_LastAdapter_InvokesOnLastRemoved()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Add("a");
        t.Remove("a");
        Check.That(called).IsEqualTo(1);
        Check.That(t.IsEmpty).IsTrue();
    }

    [Fact]
    public void Remove_NotLast_DoesNotInvoke()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Add("a");
        t.Add("b");
        t.Remove("a");
        Check.That(called).IsEqualTo(0);
        Check.That(t.IsEmpty).IsFalse();
    }

    [Fact]
    public void Remove_UnknownId_DoesNotInvoke()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Remove("x");
        Check.That(called).IsEqualTo(0);
    }

    [Fact]
    public void AddAfterFullCycle_RestoresNonEmpty()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Add("a");
        t.Remove("a");
        Check.That(called).IsEqualTo(1);
        t.Add("b");
        Check.That(t.IsEmpty).IsFalse();
        t.Remove("b");
        Check.That(called).IsEqualTo(2);
    }
}
