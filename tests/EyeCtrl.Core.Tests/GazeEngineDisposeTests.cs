namespace EyeCtrl.Core.Tests;

using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeEngineDisposeTests
{
    private static int PropertyChangedSubscriberCount(CursorSettings settings)
    {
        var field = typeof(ObservableObject).GetField(
            nameof(ObservableObject.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (Delegate?)field!.GetValue(settings);
        return handler?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void Dispose_UnsubscribesFromCursorSettingsPropertyChanged()
    {
        var settings = new EyeCtrlSettings();
        int before = PropertyChangedSubscriberCount(settings.Cursor);

        var engine = new GazeEngine(new FakeGazeProvider(), new FakeInputSimulator(), settings);
        Check.That(PropertyChangedSubscriberCount(settings.Cursor)).IsStrictlyGreaterThan(before);

        engine.Dispose();
        Check.That(PropertyChangedSubscriberCount(settings.Cursor)).IsEqualTo(before);
    }
}
