namespace EyeCtrl.Sdk.WinUI.Tests;

using EyeCtrl.Sdk.WinUI;
using Xunit;

public class AdapterGroupTrackerTests
{
    [Fact]
    public void NewTracker_IsEmpty()
    {
        var t = new AdapterGroupTracker(() => { });
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Add_OneId_IsNotEmpty()
    {
        var t = new AdapterGroupTracker(() => { });
        t.Add("a");
        Assert.False(t.IsEmpty);
    }

    [Fact]
    public void Remove_LastAdapter_InvokesOnLastRemoved()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Add("a");
        t.Remove("a");
        Assert.Equal(1, called);
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Remove_NotLast_DoesNotInvoke()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Add("a");
        t.Add("b");
        t.Remove("a");
        Assert.Equal(0, called);
        Assert.False(t.IsEmpty);
    }

    [Fact]
    public void Remove_UnknownId_DoesNotInvoke()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Remove("x");
        Assert.Equal(0, called);
    }

    [Fact]
    public void AddAfterFullCycle_RestoresNonEmpty()
    {
        var called = 0;
        var t = new AdapterGroupTracker(() => called++);
        t.Add("a");
        t.Remove("a");
        Assert.Equal(1, called);
        t.Add("b");
        Assert.False(t.IsEmpty);
        t.Remove("b");
        Assert.Equal(2, called);
    }
}
