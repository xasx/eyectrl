namespace EyeCtrl.Controls.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Records every registration and the callback it carried, and reports whatever progress the
/// test sets — so tests drive the real <see cref="DwellControl.UpdateVisual"/> and the real
/// activation path rather than a test-only hook on the production type.
/// </summary>
public class FakeDwellRegistrar : IDwellRegistrar
{
    public double Progress { get; set; }

    public List<string> Registered { get; } = [];

    public Dictionary<string, Action> Callbacks { get; } = [];

    public List<(string Id, DwellCategory Category, double RadiusPhysical, int DwellMs)> RegisterCalls { get; } = [];

    public List<(string Id, DwellCategory Category)> UnregisterCalls { get; } = [];

    public List<(string Id, DwellCategory Category)> GetProgressCalls { get; } = [];

    public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated)
    {
        Registered.Add(id);
        Callbacks[id] = onActivated;
        RegisterCalls.Add((id, category, radius, dwellMs));
    }

    public void Unregister(string id, DwellCategory category)
    {
        Registered.Remove(id);
        Callbacks.Remove(id);
        UnregisterCalls.Add((id, category));
    }

    public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }

    public double GetProgress(string id, DwellCategory category)
    {
        GetProgressCalls.Add((id, category));
        return Progress;
    }

    /// <summary>Fires the callback Register stored — the activation path three test files drive
    /// (DwellColorPickerLabelTests.cs:354, DwellComboBoxSelectionTests.cs:203, DwellSliderPlateTests.cs:141).</summary>
    public void Activate(string id) => Callbacks[id]();
}
