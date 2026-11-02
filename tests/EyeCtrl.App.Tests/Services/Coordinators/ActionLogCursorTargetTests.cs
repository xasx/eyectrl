namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Serilog;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// C10 L3/L5: the `Action:` line carries the cursor either side of the injection and the process
/// under the target. This class swaps the process-global Serilog logger, so it sits in the
/// exclusive <c>WpfApplication</c> collection (the same discipline as the backdrop and scroll-mode
/// log gates): without it, a parallel test's logger capture makes the assertion race.
/// </summary>
[Collection("WpfApplication")]
public class ActionLogCursorTargetTests
{
    [Fact]
    public void TheActionLine_CarriesTheCursorBeforeAndAfter_AndTheTargetProcess()
    {
        var input = new TestFakes.FakeInputSimulator();
        var cursor = new ScriptedCursorProbe(new PhysicalPoint(400, 300), new PhysicalPoint(500, 320));
        var integrity = new ScriptedIntegrityProbe(new ProcessTokenFacts(
            "Taskmgr.exe", 0x3000, Elevated: true, HasUIAccess: false, AccessDenied: false));

        var ctx = TestFakes.MakeContext(input) with
        {
            OverlayManager = TestFakes.MakeOverlayManager(
                glassBackdrop: new TestFakes.FakeGlassBackdropOverlay()),
            TargetHighlightVM = new TargetHighlightOverlayViewModel(),
            CursorPositionProbe = cursor,
            InputIntegrityProbe = integrity,
        };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);

        var sink = new CapturingSink();
        var previous = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

            dispatcher.HandleActionRequested(new ActionRequestedMessage(
                InteractionAction.LeftClick, new PhysicalPoint(500, 320), IsPreciseTarget: true));
        }
        finally
        {
            Log.Logger = previous;
        }

        var line = sink.Messages.Single();
        Check.WithCustomMessage($"the Action line lost its C10 fields: {line}")
            .That(line.Contains("[CursorBefore=(400,300)] [CursorAfter=(500,320)]", StringComparison.Ordinal))
            .IsTrue();
        Check.WithCustomMessage($"the Action line lost the target process/elevation: {line}")
            .That(line.Contains("[TargetProcess=Taskmgr.exe] [TargetElevated=True]", StringComparison.Ordinal))
            .IsTrue();
        Check.WithCustomMessage("the pre-C10 prefix must stay matchable by the session-log parsers.")
            .That(line.StartsWith("Action: LeftClick at (500, 320) [PreciseTarget=True]", StringComparison.Ordinal))
            .IsTrue();
    }

    /// <summary>Returns the scripted cursor readings in order (the last one repeats).</summary>
    private sealed class ScriptedCursorProbe : ICursorPositionProbe
    {
        private readonly PhysicalPoint[] _positions;
        private int _index;

        public ScriptedCursorProbe(params PhysicalPoint[] positions) => _positions = positions;

        public PhysicalPoint? TryGetCursorPosition()
        {
            var value = _positions[Math.Min(_index, _positions.Length - 1)];
            _index++;
            return value;
        }
    }

    /// <summary>Returns one fixed token-fact set for every target.</summary>
    private sealed class ScriptedIntegrityProbe : IInputIntegrityProbe
    {
        private readonly ProcessTokenFacts _facts;

        public ScriptedIntegrityProbe(ProcessTokenFacts facts) => _facts = facts;

        public SelfTokenFacts ReadSelf() => new(0x2000, false, false);
        public ProcessTokenFacts InspectForeground() => _facts;
        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => _facts;
        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => _facts;
    }

    /// <summary>Collects rendered Serilog messages for one test.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        public List<string> Messages { get; } = new();

        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }
}
