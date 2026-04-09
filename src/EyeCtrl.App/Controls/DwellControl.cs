namespace EyeCtrl.App.Controls;

using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public abstract class DwellControl
{
    public string DwellId { get; }
    public Border Root { get; }
    public IDwellRegistrar Registrar { get; }
    public DwellCategory Category { get; }
    public double RadiusPhysical { get; }
    public int DwellMs { get; }

    protected DwellControl(
        IDwellRegistrar registrar,
        string dwellId,
        DwellCategory category,
        double radiusPhysical,
        int dwellMs)
    {
        Registrar = registrar;
        DwellId = dwellId;
        Category = category;
        RadiusPhysical = radiusPhysical;
        DwellMs = dwellMs;
        Root = CreateRoot();
    }

    protected abstract Border CreateRoot();
    protected abstract void OnActivated();
    protected abstract void ApplyProgress(double progress);

    public void Attach(Panel parent)
    {
        parent.Children.Add(Root);
        RegisterDwell();
    }

    public void Detach(Panel parent)
    {
        parent.Children.Remove(Root);
        UnregisterDwell();
    }

    public void RegisterDwell()
    {
        Registrar.Register(DwellId, Category, RadiusPhysical, DwellMs, OnActivated);
    }

    public void UnregisterDwell()
    {
        Registrar.Unregister(DwellId, Category);
    }

    public void UpdatePosition()
    {
        try
        {
            var pos = Root.PointToScreen(
                new Point(Root.ActualWidth / 2, Root.ActualHeight / 2));
            Registrar.UpdatePosition(DwellId, Category, new PhysicalPoint(pos.X, pos.Y));
        }
        catch (InvalidOperationException)
        {
            // Not yet rendered or not in visual tree
        }
    }

    public void UpdateVisual()
    {
        double progress = Registrar.GetProgress(DwellId, Category);
        ApplyProgress(progress);
    }
}
