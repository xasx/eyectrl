namespace EyeCtrl.Controls.Tests;

using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

public class DwellToggleVisualTests
{
    [StaFact]
    public void RefreshVisual_OnState_ShowsCheckedSymbol()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        var text = (TextBlock)toggle.Root.Child;
        Check.That(text.Text).StartsWith("\u2611 ");
    }

    [StaFact]
    public void RefreshVisual_OffState_ShowsUncheckedSymbol()
    {
        var state = false;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        var text = (TextBlock)toggle.Root.Child;
        Check.That(text.Text).StartsWith("\u2610 ");
    }

    [StaFact]
    public void Toggle_FlipsState()
    {
        var state = false;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        toggle.TestOnActivated();

        Check.That(state).IsTrue();
    }

    [StaFact]
    public void ApplyProgress_BlendsTowardAccent()
    {
        var state = true;
        var toggle = new TestDwellToggle(new FakeDwellRegistrar(), "id", "Label", () => state, v => state = v, DwellCategory.Control, 40, 800);

        toggle.TestApplyProgress(1.0);

        // ApplyProgress blends baseColor toward Accent by progress * 0.5.
        // At progress=1.0: 0.5 blend from OnColor (ToggleOn state) toward Accent.
        var brush = (SolidColorBrush)toggle.Root.Background;
        var expected = ColorBlend.Blend(Tokens.ToggleOn, Tokens.Accent, 0.5);
        Check.That(brush.Color).IsEqualTo(expected);
    }

    private class TestDwellToggle : DwellToggle
    {
        public TestDwellToggle(IDwellRegistrar registrar, string id, string label, Func<bool> get, Action<bool> set, DwellCategory category, double radius, int dwellMs)
            : base(registrar, id, label, get, set, category, radius, dwellMs)
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
