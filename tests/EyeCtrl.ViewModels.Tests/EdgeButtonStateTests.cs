namespace EyeCtrl.ViewModels.Tests;

using System.ComponentModel;

public class EdgeButtonStateTests
{
    [Fact]
    public void DwellProgress_Set_RaisesPropertyChanged()
    {
        var state = new EdgeButtonState("Drag_cancel", "\u2715");
        var fired = new List<string?>();
        ((INotifyPropertyChanged)state).PropertyChanged += (_, e) => fired.Add(e.PropertyName);

        state.DwellProgress = 0.5;

        Check.That(fired).Contains(nameof(EdgeButtonState.DwellProgress));
        Check.That(state.DwellProgress).IsEqualTo(0.5);
    }
}
