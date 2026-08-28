namespace EyeCtrl.Controls.Tests;

using System.Windows.Controls;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellControlLifecycleTests
{
    [StaFact]
    public void RegisterDwell_ForwardsToRegistrar()
    {
        var registrar = new FakeDwellRegistrar();
        var control = new TestDwellControl(registrar, "test_id", DwellCategory.Control, 40, 800);

        control.RegisterDwell();

        Check.That(registrar.RegisterCalls).HasSize(1);
        Check.That(registrar.RegisterCalls[0].Id).IsEqualTo("test_id");
        Check.That(registrar.RegisterCalls[0].Category).IsEqualTo(DwellCategory.Control);
        Check.That(registrar.RegisterCalls[0].RadiusPhysical).IsEqualTo(40);
        Check.That(registrar.RegisterCalls[0].DwellMs).IsEqualTo(800);
    }

    [StaFact]
    public void UnregisterDwell_ForwardsToRegistrar()
    {
        var registrar = new FakeDwellRegistrar();
        var control = new TestDwellControl(registrar, "test_id", DwellCategory.Control, 40, 800);
        control.RegisterDwell();

        control.UnregisterDwell();

        Check.That(registrar.UnregisterCalls).HasSize(1);
        Check.That(registrar.UnregisterCalls[0].Id).IsEqualTo("test_id");
    }

    [StaFact]
    public void Attach_AddsToPanelAndRegisters()
    {
        var registrar = new FakeDwellRegistrar();
        var control = new TestDwellControl(registrar, "test_id", DwellCategory.Control, 40, 800);
        var panel = new StackPanel();

        control.Attach(panel);

        Check.That(panel.Children.Count).IsEqualTo(1);
        Check.That(registrar.RegisterCalls).HasSize(1);
    }

    [StaFact]
    public void Detach_RemovesFromPanelAndUnregisters()
    {
        var registrar = new FakeDwellRegistrar();
        var control = new TestDwellControl(registrar, "test_id", DwellCategory.Control, 40, 800);
        var panel = new StackPanel();
        control.Attach(panel);

        control.Detach(panel);

        Check.That(panel.Children.Count).IsEqualTo(0);
        Check.That(registrar.UnregisterCalls).HasSize(1);
    }

    [StaFact]
    public void UpdateVisual_QueriesProgressAndApplies()
    {
        var registrar = new FakeDwellRegistrar();
        var control = new TestDwellControl(registrar, "test_id", DwellCategory.Control, 40, 800);

        control.UpdateVisual();

        Check.That(control.ApplyProgressCalls).HasSize(1);
        Check.That(registrar.GetProgressCalls).HasSize(1);
    }

    private class TestDwellControl : DwellControl
    {
        public List<double> ApplyProgressCalls { get; } = new();

        public TestDwellControl(IDwellRegistrar registrar, string id, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, category, radius, dwellMs)
        {
        }

        protected override Border CreateRoot() => new();

        protected override void OnActivated() { }

        protected override void ApplyProgress(double progress)
        {
            ApplyProgressCalls.Add(progress);
        }
    }

}
