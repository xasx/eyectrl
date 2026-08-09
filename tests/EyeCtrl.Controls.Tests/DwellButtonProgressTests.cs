namespace EyeCtrl.Controls.Tests;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellButtonProgressTests
{
    [StaFact]
    public void ApplyProgress_AtZero_KeepsBaseColor()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(0);

        var brush = (SolidColorBrush)btn.Root.Background;
        Check.That(brush.Color).IsEqualTo(Tokens.SurfaceMedium);
    }

    [StaFact]
    public void ApplyProgress_AtFull_BlendsToMidpoint()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(1.0);

        // ApplyProgress blends baseColor toward Accent by progress * 0.5.
        // At progress=1.0: 0.5 blend from SurfaceMedium toward Accent.
        var brush = (SolidColorBrush)btn.Root.Background;
        var expected = ColorBlend.Blend(Tokens.SurfaceMedium, Tokens.Accent, 0.5);
        Check.That(brush.Color).IsEqualTo(expected);
    }

    [StaFact]
    public void ApplyProgress_AtHalf_IsMidpointBlend()
    {
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => { }, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestApplyProgress(0.5);

        var brush = (SolidColorBrush)btn.Root.Background;
        var expected = ColorBlend.Blend(Tokens.SurfaceMedium, Tokens.Accent, 0.25);
        Check.That(brush.Color).IsEqualTo(expected);
    }

    [StaFact]
    public void OnActivated_InvokesAction()
    {
        var clicked = false;
        var btn = new TestDwellButton(new FakeDwellRegistrar(), "id", "", () => clicked = true, Tokens.SurfaceMedium, DwellCategory.Control, 40, 800);

        btn.TestOnActivated();

        Check.That(clicked).IsTrue();
    }

    private class TestDwellButton : DwellButton
    {
        public TestDwellButton(IDwellRegistrar registrar, string id, string label, Action action, Color baseColor, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, label, action, baseColor, category, radius, dwellMs)
        {
        }

        public void TestApplyProgress(double progress) => ApplyProgress(progress);
        public void TestOnActivated() => OnActivated();
    }

    private class FakeDwellRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
