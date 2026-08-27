namespace EyeCtrl.Core.Tests.EyeCare;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class GazeEngineSampledTests
{
    [Fact]
    public void GazeSampled_FiresWithRawPoint_BeforeInterpolation()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);

        GazePoint? seen = null;
        engine.GazeSampled += p => seen = p;

        var raw = new GazePoint(100, 200, 5_000_000, IsValid: true);
        provider.SimulateGaze(raw);

        Check.That(seen.HasValue).IsTrue();
        Check.That(seen!.Value.IsValid).IsTrue();
        Check.That(seen!.Value.X).IsEqualTo(100.0);
        Check.That(seen!.Value.Y).IsEqualTo(200.0);
    }

    [Fact]
    public void GazeSampled_CarriesInvalidFlag_WhenEyesLost()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        using var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);

        bool? sawValid = null;
        engine.GazeSampled += p => sawValid = p.IsValid;

        provider.SimulateGaze(new GazePoint(0, 0, 6_000_000, IsValid: false));

        Check.That(sawValid).IsNotNull();
        Check.That(sawValid!.Value).IsFalse();
    }
}
