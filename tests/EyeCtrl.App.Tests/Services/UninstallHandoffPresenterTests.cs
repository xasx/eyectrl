namespace EyeCtrl.App.Tests.Services;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;

/// <summary>
/// C9 S6e fixup-08: the presenter that ties the uninstall row to the Settings window. Started
/// hides; RefusedWhileActive and Failed do not (today's behaviour); the watch's end is forwarded;
/// and a fault in any seam still brings Settings back. All seams are recorders — no window, no
/// process, no record file.
/// </summary>
public class UninstallHandoffPresenterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 21, 24, 0, TimeSpan.Zero);

    private sealed class FakeGateways : ISetupGateways
    {
        public SetupHandoffStart Result { get; set; } = SetupHandoffStart.Started;
        public int UninstallStarts { get; private set; }
        public Action? OnStart { get; set; }

        public bool Installed => true;
        public bool PreviousAvailable => false;
        public string? PreviousVersion => null;
        public bool PolicyBlocks => false;
        public SetupUpdateOffer? UpdateOffer => null;
        public bool UpdateOfferPending => false;
        public void BeginUpdateOfferProbe(Action whenDone) { }
        public SetupHandoffStart StartUpdateHandoff(SetupUpdateOffer offer) => throw new NotSupportedException();
        public SetupHandoffStart StartRollbackHandoff() => throw new NotSupportedException();

        public SetupHandoffStart StartUninstallHandoff()
        {
            UninstallStarts++;
            OnStart?.Invoke();
            return Result;
        }
    }

    private sealed class FakeWatch : IUninstallHandoffWatch
    {
        public DateTimeOffset StartedAt { get; private set; }
        public Action<UninstallHandoffEnd, SetupOutcome?>? Ended { get; private set; }

        public void Start(DateTimeOffset handoffStartedAt, Action<UninstallHandoffEnd, SetupOutcome?> ended)
        {
            StartedAt = handoffStartedAt;
            Ended = ended;
        }
    }

    private sealed class FakeWatchFactory : IUninstallHandoffWatchFactory
    {
        public FakeWatch Watch { get; } = new();
        public bool Throw { get; set; }
        public int Created { get; private set; }

        public IUninstallHandoffWatch Create()
        {
            Created++;
            if (Throw)
                throw new InvalidOperationException("watch factory failed");
            return Watch;
        }
    }

    private sealed class Harness
    {
        public FakeGateways Gateways { get; } = new();
        public FakeWatchFactory Watches { get; } = new();
        public List<string> Warnings { get; } = [];
        public int HideCalls { get; private set; }
        public bool HideResult { get; set; } = true;
        public bool HideThrows { get; set; }
        public List<(UninstallHandoffEnd End, SetupOutcome? Outcome)> Restores { get; } = [];
        public DateTimeOffset Now { get; set; } = Start;

        public UninstallHandoffPresenter Create() => new(
            () =>
            {
                HideCalls++;
                if (HideThrows)
                    throw new InvalidOperationException("hide failed");
                return HideResult;
            },
            (end, outcome) => Restores.Add((end, outcome)),
            Watches,
            () => Now,
            Warnings.Add);

        public void End(UninstallHandoffEnd end, SetupOutcome? outcome = null) =>
            Watches.Watch.Ended!.Invoke(end, outcome);
    }

    [Fact]
    public void Started_HidesAndStartsTheWatch_WithTheClockReadBeforeTheLaunch()
    {
        var harness = new Harness();
        // The launch advances the clock: the watch must still be anchored before it, or a record
        // written during the launch could be mistaken for an older one.
        harness.Gateways.OnStart = () => harness.Now = Start.AddSeconds(1);

        var start = harness.Create().Start(harness.Gateways);

        Check.That(start).IsEqualTo(SetupHandoffStart.Started);
        Check.That(harness.Gateways.UninstallStarts).IsEqualTo(1);
        Check.That(harness.HideCalls).IsEqualTo(1);
        Check.That(harness.Watches.Created).IsEqualTo(1);
        Check.That(harness.Watches.Watch.StartedAt).IsEqualTo(Start);
    }

    [Fact]
    public void Failed_ChangesNothing()
    {
        var harness = new Harness();
        harness.Gateways.Result = SetupHandoffStart.Failed;

        var start = harness.Create().Start(harness.Gateways);

        Check.That(start).IsEqualTo(SetupHandoffStart.Failed);
        Check.That(harness.HideCalls).IsEqualTo(0);
        Check.That(harness.Watches.Created).IsEqualTo(0);
        Check.That(harness.Restores).IsEmpty();
    }

    [Fact]
    public void RefusedWhileActive_ChangesNothing()
    {
        // A duplicate dwell while Setup is already starting: the first hand-off's window is the
        // answer, and Settings must not be hidden a second time.
        var harness = new Harness();
        harness.Gateways.Result = SetupHandoffStart.RefusedWhileActive;

        var start = harness.Create().Start(harness.Gateways);

        Check.That(start).IsEqualTo(SetupHandoffStart.RefusedWhileActive);
        Check.That(harness.HideCalls).IsEqualTo(0);
        Check.That(harness.Watches.Created).IsEqualTo(0);
    }

    [Fact]
    public void WhenNoWindowWasHidden_ThereIsNothingToWatch()
    {
        var harness = new Harness { HideResult = false };

        var start = harness.Create().Start(harness.Gateways);

        Check.That(start).IsEqualTo(SetupHandoffStart.Started);
        Check.That(harness.Watches.Created).IsEqualTo(0);
        Check.That(harness.Restores).IsEmpty();
    }

    [Theory]
    [InlineData(UninstallHandoffEnd.NotUninstalled)]
    [InlineData(UninstallHandoffEnd.Unknown)]
    [InlineData(UninstallHandoffEnd.Uninstalled)]
    public void TheWatchEnd_IsForwardedToTheRestore(UninstallHandoffEnd end)
    {
        var harness = new Harness();
        harness.Create().Start(harness.Gateways);

        harness.End(end, SetupOutcome.Cancelled);

        Check.That(harness.Restores).HasSize(1);
        Check.That(harness.Restores[0].End).IsEqualTo(end);
        Check.That(harness.Restores[0].Outcome).IsEqualTo(SetupOutcome.Cancelled);
    }

    [Fact]
    public void AHideThatThrows_NeitherWatchesNorRestores()
    {
        // Setup is running but Settings was never hidden: today's behaviour, and nothing to bring
        // back. The failure is logged, never shown to the owner.
        var harness = new Harness { HideThrows = true };

        var start = harness.Create().Start(harness.Gateways);

        Check.That(start).IsEqualTo(SetupHandoffStart.Started);
        Check.That(harness.Watches.Created).IsEqualTo(0);
        Check.That(harness.Restores).IsEmpty();
        Check.That(harness.Warnings).HasSize(1);
    }

    [Fact]
    public void AWatchThatCannotStart_RestoresSettingsAtOnce()
    {
        // Never strand: a watch seam that throws must not leave Settings hidden with no watcher.
        var harness = new Harness();
        harness.Watches.Throw = true;

        var start = harness.Create().Start(harness.Gateways);

        Check.That(start).IsEqualTo(SetupHandoffStart.Started);
        Check.That(harness.Restores).HasSize(1);
        Check.That(harness.Restores[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
        Check.That(harness.Warnings).HasSize(1);
    }
}
