namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Plays a probe run in virtual time through the real C5 instance C7 built (fixup 02 H1). Gaze
/// messages go through the world's messenger as the engine would send them; the phase timers run on
/// an injected <see cref="ManualTimeProvider"/>; answers are delivered with
/// <see cref="SubPixelProbeViewModel.SimulateActivated"/> so no dwell geometry is involved.
/// </summary>
internal sealed class ProbeRunDriver
{
    private static readonly PhysicalPoint Neutral = new(960, 540);

    private readonly ManualTimeProvider _time;
    private readonly IMessenger _messenger;
    private readonly SubPixelProbeViewModel _vm;
    private long _gazeUs;

    internal ProbeRunDriver(ManualTimeProvider time, IMessenger messenger, SubPixelProbeViewModel vm)
    {
        _time = time;
        _messenger = messenger;
        _vm = vm;
        _vm.BlockCompleted += _ => BlocksCompleted++;
        _vm.RunEnded += _ => RunEnded = true;
    }

    internal int BlocksCompleted { get; private set; }

    internal bool RunEnded { get; private set; }

    /// <summary>Starts the run and leaves the Intro screen.</summary>
    internal void StartToTrials()
    {
        _vm.Start();
        _vm.BeginTrials();
    }

    /// <summary>Advances the clock and sends one gaze sample, as the engine would.</summary>
    internal void Tick(long ms = 30)
    {
        _time.Advance(ms);
        _gazeUs += ms * 1000;
        _messenger.Send(new GazeCursorMovedMessage(Neutral, _gazeUs));
    }

    /// <summary>Plays the current block to its end, answering every trial with <paramref name="pick"/>.</summary>
    internal void PlayBlock(SubPixelAbCondition pick = SubPixelAbCondition.B)
    {
        int target = BlocksCompleted + 1;
        long elapsed = 0;

        while (BlocksCompleted < target && !RunEnded && elapsed < 300_000)
        {
            Tick();
            elapsed += 30;
            if (_vm.Phase == SubPixelProbePhase.Answer) AnswerCurrentTrial(pick);
        }

        Check.WithCustomMessage($"The block did not complete within {elapsed} ms of virtual time.")
            .That(BlocksCompleted).IsEqualTo(target);
    }

    /// <summary>Drives to the answer screen of the current trial and activates the picked button.</summary>
    internal void AnswerCurrentTrial(SubPixelAbCondition pick)
    {
        long elapsed = 0;
        while (!RunEnded && _vm.Phase != SubPixelProbePhase.Answer && elapsed < 60_000)
        {
            Tick();
            elapsed += 30;
        }

        Check.That(_vm.Phase).IsEqualTo(SubPixelProbePhase.Answer);
        string id = pick == _vm.Interval1Condition
            ? SubPixelProbeViewModel.AnswerOneTargetId
            : SubPixelProbeViewModel.AnswerTwoTargetId;
        _vm.SimulateActivated(id);
    }

    /// <summary>Drives until the first scored trial's interval 1 (the demo pair is played through).</summary>
    internal void DriveToFirstScoredInterval1()
    {
        long elapsed = 0;
        while (!RunEnded
               && !(_vm.Phase == SubPixelProbePhase.Interval1 && _vm.CurrentTrial is not null)
               && elapsed < 60_000)
        {
            Tick();
            elapsed += 30;
        }

        Check.That(_vm.CurrentTrial).IsNotNull();
    }
}
