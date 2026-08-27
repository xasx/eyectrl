namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class GazeEngineTests
{
    [Fact]
    public void ProcessGaze_MovesCursor()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        Check.That(simulator.Actions).HasElementThatMatches(a => a.Action == "Move");
    }

    [Fact]
    public void ActionRequested_LeftClick_FiresEvent()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        // Subscribe to ActionRequested (App.xaml.cs handles execution via message bus)
        InteractionAction? firedAction = null;
        engine.InteractionManager.ActionRequested += (action, _) => firedAction = action;

        // Directly trigger action through the interaction manager
        engine.InteractionManager.OnFixationStarted(new PhysicalPoint(200, 200));
        engine.InteractionManager.OnActivationButtonDwelled();
        engine.InteractionManager.OnActionSelected(InteractionAction.LeftClick);

        Check.That(firedAction).IsEqualTo(InteractionAction.LeftClick);
    }

    [Fact]
    public void SuppressFixation_DefaultsToFalse()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();

        var engine = new GazeEngine(provider, simulator, settings);

        Check.That(engine.SuppressFixation).IsFalse();
    }

    [Fact]
    public void SuppressFixation_CanBeToggled()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();

        var engine = new GazeEngine(provider, simulator, settings);

        engine.SuppressFixation = true;
        Check.That(engine.SuppressFixation).IsTrue();

        engine.SuppressFixation = false;
        Check.That(engine.SuppressFixation).IsFalse();
    }
}
