namespace EyeCtrl.Core.Tests.Flow;

using System.Linq;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// An <see cref="IGazeProvider"/> that the test pushes synthetic gaze samples into. It never reads
/// the real cursor.
/// </summary>
internal sealed class ScriptedGazeProvider : IGazeProvider
{
    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }
    public void Start() => IsConnected = true;
    public void Stop() => IsConnected = false;
    public void Dispose() => Stop();

    /// <summary>Delivers one synthetic gaze sample to the engine.</summary>
    public void Push(GazePoint point) => GazePointReceived?.Invoke(point);
}

/// <summary>
/// An <see cref="IInputSimulator"/> that records calls instead of touching the OS cursor/keyboard.
/// </summary>
internal sealed class RecordingInputSimulator : IInputSimulator
{
    public List<(string Action, PhysicalPoint Point)> Calls { get; } = new();

    /// <summary>Number of <see cref="MoveCursor"/> calls recorded so far.</summary>
    public int MoveCount => Calls.Count(c => c.Action == "Move");

    public void MoveCursor(PhysicalPoint p) => Calls.Add(("Move", p));
    public void LeftClick(PhysicalPoint p) => Calls.Add(("LeftClick", p));
    public void RightClick(PhysicalPoint p) => Calls.Add(("RightClick", p));
    public void DoubleClick(PhysicalPoint p) => Calls.Add(("DoubleClick", p));
    public void MouseDown(PhysicalPoint p) => Calls.Add(("MouseDown", p));
    public void MouseUp(PhysicalPoint p) => Calls.Add(("MouseUp", p));
    public void Scroll(PhysicalPoint p, int dx, int dy) => Calls.Add(("Scroll", p));
    public void SendKeyPress(ushort vk) { }
    public void SendKeyDown(ushort vk) { }
    public void SendKeyUp(ushort vk) { }
    public void SendText(string text) { }
    public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
}

/// <summary>
/// Headless harness that drives the real <see cref="GazeEngine"/> + <see cref="InteractionManager"/>
/// with SYNTHETIC gaze and a RECORDING input simulator. It opens no windows and never reads or moves
/// the real cursor, so the interaction flow (fixation → activation → menu / manual-selection → action)
/// can be exercised deterministically and fast without disturbing an eye-tracking user.
///
/// Time is a virtual clock advanced by <see cref="Gaze"/>/<see cref="Hold"/>, so fixation/dwell
/// durations resolve instantly rather than in real time.
/// </summary>
internal sealed class GazeFlowHarness
{
    private long _clockUs;

    public ScriptedGazeProvider Provider { get; }
    public RecordingInputSimulator Input { get; }
    public GazeEngine Engine { get; }
    public InteractionManager Interaction => Engine.InteractionManager;

    /// <summary>Every state the machine transitioned into, in order.</summary>
    public List<InteractionState> States { get; } = new();
    public List<PhysicalPoint> ActivationButtonsShown { get; } = new();
    public List<PhysicalPoint> ActionMenusShown { get; } = new();
    public List<(InteractionAction Action, PhysicalPoint Point)> ActionsRequested { get; } = new();
    public List<PhysicalPoint> ManualSelectionsStarted { get; } = new();
    public List<PhysicalPoint> DragsStarted { get; } = new();

    public GazeFlowHarness(SelectionMode selectionMode = SelectionMode.None, EyeCtrlSettings? settings = null)
    {
        settings ??= new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct; // passthrough filter → gaze in == gaze out
        settings.SelectionMode = selectionMode;             // read by the GazeEngine ctor

        Provider = new ScriptedGazeProvider();
        Input = new RecordingInputSimulator();
        Engine = new GazeEngine(Provider, Input, settings);

        Interaction.StateChanged += s => States.Add(s);
        Interaction.ShowActivationButton += p => ActivationButtonsShown.Add(p);
        Interaction.ShowActionMenu += p => ActionMenusShown.Add(p);
        Interaction.ActionRequested += (a, p) => ActionsRequested.Add((a, p));
        Interaction.StartManualSelection += p => ManualSelectionsStarted.Add(p);
        Interaction.DragStarted += p => DragsStarted.Add(p);

        Engine.Start();
    }

    public InteractionState State => Interaction.CurrentState;

    /// <summary>Feeds one gaze sample after advancing the virtual clock by <paramref name="dtMs"/>.</summary>
    public void Gaze(double x, double y, int dtMs = 33)
    {
        _clockUs += dtMs * 1000L;
        Provider.Push(new GazePoint(x, y, _clockUs, true));
    }

    /// <summary>
    /// Feeds one INVALID sample (tracker reports eyes lost) after advancing the virtual clock.
    /// The engine still interpolates/holds a position through these — which is exactly the
    /// condition repeat-mode's validity gate has to see through.
    /// </summary>
    public void GazeInvalid(double x, double y, int dtMs = 33)
    {
        _clockUs += dtMs * 1000L;
        Provider.Push(new GazePoint(x, y, _clockUs, false));
    }

    /// <summary>Streams invalid samples for <paramref name="durationMs"/>, one every <paramref name="stepMs"/>.</summary>
    public void HoldInvalid(double x, double y, int durationMs, int stepMs = 33)
    {
        for (int elapsed = 0; elapsed < durationMs; elapsed += stepMs)
            GazeInvalid(x, y, stepMs);
    }

    /// <summary>Holds gaze at a point for <paramref name="durationMs"/>, one sample every <paramref name="stepMs"/>.</summary>
    public void Hold(double x, double y, int durationMs, int stepMs = 33)
    {
        for (int elapsed = 0; elapsed < durationMs; elapsed += stepMs)
            Gaze(x, y, stepMs);
    }

    /// <summary>
    /// Holds gaze until real fixation fires, then invokes the activation-button dwell that the
    /// ViewModel layer would fire — landing in the menu / manual-selection state for the mode.
    /// </summary>
    public void FixateAndActivate(double x, double y)
    {
        Hold(x, y, durationMs: 450);
        Interaction.OnActivationButtonDwelled();
    }
}
