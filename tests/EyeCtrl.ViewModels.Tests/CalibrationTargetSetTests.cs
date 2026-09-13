namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The view-model half of "show every target, highlight the current one". The overlay used to draw a
/// single dot that teleported on each advance, so the user had to search the screen every time; the
/// visual result is only checkable by eye, but the STATE that drives it is not, and that is what is
/// pinned here — the published set, the active index, and the per-target status derived from it.
/// <para>
/// The animation and the styling belong to <c>CalibrationWindow</c> and are on the manual checklist
/// (<c>calibration-reliability/pacing-and-target-ui</c>): "next target findable before advance" and
/// "motion legible in peripheral vision".
/// </para>
/// </summary>
public class CalibrationTargetSetTests
{
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Fire(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    private static readonly string CalibrationDir = TempTestDirectory.Create("viewmodels-calibration-set");
    private static string TempPath() =>
        System.IO.Path.Combine(CalibrationDir, "cal-" + Guid.NewGuid().ToString("N") + ".json");

    private static readonly PhysicalPoint Screen = new(1920, 1080);

    private readonly WeakReferenceMessenger _messenger = new();
    private readonly CalibrationService _service = new();
    private readonly FakeGazeProvider _provider = new();

    // 200 is the validated floor for TargetHoldMs, not an arbitrary choice — the setter rejects less.
    private CalibrationViewModel CreateVm(int pointCount = 5) =>
        new(new CalibrationSettings { PointCount = pointCount, TargetHoldMs = 200 },
            _service, _messenger, _provider, Screen, "test-monitor", TempPath());

    private CalibrationViewModel StartedVm(int pointCount = 5)
    {
        var vm = CreateVm(pointCount);
        _messenger.Send(new CalibrationRequestedMessage());
        return vm;
    }

    // ── the published set ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BeforeASession_NoTargetsArePublished()
    {
        Check.That(CreateVm().TargetPositions).IsEmpty();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void StartingASession_PublishesEveryTargetAtOnce(int pointCount)
    {
        // The whole point: the set is known up front, so a view can draw the next location before the
        // current target completes.
        var vm = StartedVm(pointCount);

        Check.That(vm.TargetPositions).HasSize(pointCount);
        Check.That(vm.TotalTargets).IsEqualTo(pointCount);
    }

    [Fact]
    public void ThePublishedSet_IsTheCollectorsOwnList_NotACopyOfTheSameCalculation()
    {
        // PROVENANCE, and it needs reference identity to mean anything. Comparing the positions
        // value-by-value cannot fail: both sides would come from the same deterministic helper, so a
        // view model that called `CalibrationTargetPositionHelper.GetPositions(...)` itself would pass
        // an equality check while having exactly the drift this design exists to prevent — the drawn
        // target and the enforced target computed independently.
        //
        // The service hands out one read-only wrapper per session, so identity is a stable, meaningful
        // thing to assert: the same object the collector indexes into, or it is not the same list.
        var vm = StartedVm(9);

        Check.That(vm.TargetPositions).IsSameReferenceAs(_service.TargetPositions);
    }

    [Fact]
    public void ThePublishedSet_AgreesWithThePerIndexLookupTheCollectorUses()
    {
        // The weaker value check kept alongside the identity one: identity proves provenance, this
        // proves the indexing is aligned — a view highlighting index i must be over the position
        // BeginTarget built the collector from.
        var vm = StartedVm(9);

        for (int i = 0; i < vm.TotalTargets; i++)
            Check.WithCustomMessage($"target {i}").That(vm.TargetPositions[i]).IsEqualTo(_service.GetTargetPosition(i));
    }

    [Fact]
    public void ThePublishedSet_IsNotMutableByAView()
    {
        // The list the collector indexes into is handed out; a consumer that could reorder or clear it
        // would be editing the session's targets from the overlay.
        var vm = StartedVm(5);

        Check.That(vm.TargetPositions).IsInstanceOfType(typeof(System.Collections.ObjectModel.ReadOnlyCollection<PhysicalPoint>));
    }

    [Fact]
    public void ThePublishedSet_MatchesTheCurrentTargetAsItAdvances()
    {
        // The index and the set must stay aligned, or a highlight lands on the wrong dot.
        var vm = StartedVm(5);

        Check.That(vm.CurrentTargetPosition).IsEqualTo(vm.TargetPositions[vm.CurrentTargetIndex]);

        AdvanceOneTarget(vm);

        Check.That(vm.CurrentTargetIndex).IsEqualTo(1);
        Check.That(vm.CurrentTargetPosition).IsEqualTo(vm.TargetPositions[1]);
    }

    [Fact]
    public void ThePublishedSet_ChangesOncePerSession_NotOncePerAdvance()
    {
        // A view builds its markers when this changes and only restyles them afterwards. Republishing
        // on every advance would rebuild the visuals mid-slide and break the animation that is the
        // whole point of ticket 2.
        //
        // Counted as CHANGE NOTIFICATIONS rather than compared by reference: reference identity also
        // depends on whether the service hands back its live list or a copy, which is not this
        // property's business and let a mutation through when the test was written that way.
        var vm = CreateVm(5);
        int publishes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalibrationViewModel.TargetPositions)) publishes++;
        };

        _messenger.Send(new CalibrationRequestedMessage());
        Check.That(publishes).IsEqualTo(1);

        AdvanceOneTarget(vm);
        AdvanceOneTarget(vm);

        Check.That(publishes).IsEqualTo(1);
        Check.That(vm.TargetPositions).HasSize(5);
    }

    // ── per-target status ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AtTheStart_TheFirstTargetIsCurrentAndEveryOtherIsUpcoming()
    {
        var vm = StartedVm(5);

        Check.That(vm.StatusOf(0)).IsEqualTo(CalibrationTargetStatus.Current);
        for (int i = 1; i < vm.TotalTargets; i++)
            Check.WithCustomMessage($"target {i}").That(vm.StatusOf(i)).IsEqualTo(CalibrationTargetStatus.Upcoming);
    }

    [Fact]
    public void AfterAnAdvance_ThePreviousTargetIsDone_AndExactlyOneIsCurrent()
    {
        // "Exactly one" is the load-bearing half: the visual states must not imply that more than one
        // target accepts gaze, because only one does.
        var vm = StartedVm(5);
        AdvanceOneTarget(vm);

        Check.That(vm.StatusOf(0)).IsEqualTo(CalibrationTargetStatus.Done);
        Check.That(vm.StatusOf(1)).IsEqualTo(CalibrationTargetStatus.Current);
        Check.That(vm.StatusOf(2)).IsEqualTo(CalibrationTargetStatus.Upcoming);

        int currentCount = 0;
        for (int i = 0; i < vm.TotalTargets; i++)
            if (vm.StatusOf(i) == CalibrationTargetStatus.Current) currentCount++;
        Check.That(currentCount).IsEqualTo(1);
    }

    [Fact]
    public void OutsideACollectingSession_NothingIsCurrent()
    {
        // Training and Active no longer show targets. Reporting one as Current there would invite a
        // view to keep a live band ring on screen over a finished session.
        var vm = StartedVm(5);
        vm.Cancel();

        Check.That(vm.State).IsEqualTo(CalibrationState.Idle);
        for (int i = 0; i < vm.TargetPositions.Count; i++)
            Check.WithCustomMessage($"target {i}").That(vm.StatusOf(i)).IsNotEqualTo(CalibrationTargetStatus.Current);
    }

    [Fact]
    public void StatusOf_AnIndexOutsideTheSet_DoesNotThrow()
    {
        // A view that lags the set by one frame must not take the process down with it; the crash
        // policy in this app turns an overlay exception into a process kill.
        var vm = StartedVm(3);

        Check.ThatCode(() => vm.StatusOf(-1)).DoesNotThrow();
        Check.ThatCode(() => vm.StatusOf(99)).DoesNotThrow();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives real gaze at the current target until the view model moves on. Uses the actual collection
    /// path rather than poking the index, so the set/index alignment is tested against how it really
    /// advances.
    /// </summary>
    private void AdvanceOneTarget(CalibrationViewModel vm)
    {
        var startIndex = vm.CurrentTargetIndex;
        var target = vm.CurrentTargetPosition;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        long ts = 0;
        while (vm.CurrentTargetIndex == startIndex && DateTime.UtcNow < deadline)
        {
            _provider.Fire(new GazePoint(target.X, target.Y, ts, IsValid: true));
            ts += 33_000;
            Thread.Sleep(1);
        }

        Check.WithCustomMessage("the view model never advanced past the first target")
            .That(vm.CurrentTargetIndex).IsStrictlyGreaterThan(startIndex);
    }
}
