namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// C10's routing: blocked → banner + ring look, cleared → hide + restore, a dismissal lasts only
/// for the current episode, and the way-out button calls the window manager's NON-INJECTING path.
/// The coordinator is driven through its internal <c>Apply</c>, so no dispatcher pump and no window
/// are needed; the detector itself is pinned in EyeCtrl.Core.Tests.
/// </summary>
public class BlockedInputCoordinatorTests
{
    private static readonly BlockedInputState Blocked = new(
        IsBlocked: true,
        BlockedInputReason.HigherIntegrityForeground,
        "test",
        new PhysicalPoint(10, 10),
        new PhysicalPoint(300, 300),
        new PhysicalPoint(300, 300),
        new ProcessTokenFacts("Taskmgr.exe", 0x3000, true, false, false),
        ProcessTokenFacts.Unknown);

    private static readonly BlockedInputState Cleared = Blocked with
    {
        IsBlocked = false,
        Reason = BlockedInputReason.Unknown,
        Trigger = "verified action move",
    };

    [Fact]
    public void ABlockedTransition_ShowsTheBannerAndMarksTheRing()
    {
        var h = new Harness();

        h.Coordinator.Apply(Blocked);

        Check.That(h.Banner.Shown).ContainsExactly(Blocked);
        Check.That(h.Ring.BlockedStates).ContainsExactly(true);
    }

    [Fact]
    public void AClearedTransition_HidesTheBannerAndRestoresTheRing()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Coordinator.Apply(Cleared);

        Check.That(h.Banner.HideCount).IsEqualTo(1);
        Check.That(h.Ring.BlockedStates).ContainsExactly(true, false);
    }

    [Fact]
    public void ADismissal_SuppressesOnlyTheCurrentEpisode()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);
        h.Banner.RaiseDismiss();

        h.Coordinator.Apply(Blocked); // same episode: no re-show
        Check.That(h.Banner.Shown).HasSize(1);

        h.Coordinator.Apply(Cleared); // the episode ends
        h.Coordinator.Apply(Blocked); // a new episode shows again
        Check.That(h.Banner.Shown).HasSize(2);
    }

    [Fact]
    public void TheEscapeButton_CallsTheNonInjectingWindowManagerAndPassesItsTarget()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Banner.RaiseEscape();

        Check.That(h.Escape.TargetLookups).IsEqualTo(1);
        Check.That(h.Escape.BroughtToFront).ContainsExactly(h.Escape.Target);
        Check.That(h.Escape.Results[0].UsedShowWindowFallback).IsTrue();
    }

    [Fact]
    public void Dispose_RestoresTheRingAndHidesTheBanner()
    {
        var h = new Harness();
        h.Coordinator.Apply(Blocked);

        h.Coordinator.Dispose();

        Check.That(h.Banner.HideCount).IsEqualTo(1);
        Check.That(h.Ring.BlockedStates[^1]).IsFalse();
    }

    private sealed class Harness
    {
        public Harness()
        {
            var detector = new BlockedInputDetector(new FakeCursor(), new FakeIntegrity());
            Banner = new FakeBanner();
            Escape = new FakeEscape();
            Ring = new FakeRing();
            Coordinator = new BlockedInputCoordinator(
                detector, Banner, Escape, Ring, Dispatcher.CurrentDispatcher, new WeakReferenceMessenger());
        }

        public FakeBanner Banner { get; }
        public FakeEscape Escape { get; }
        public FakeRing Ring { get; }
        public BlockedInputCoordinator Coordinator { get; }
    }

    private sealed class FakeBanner : IBlockedInputBanner
    {
        public List<BlockedInputState> Shown { get; } = new();
        public int HideCount { get; private set; }

        public event EventHandler? EscapeRequested;
        public event EventHandler? DismissRequested;

        public void ShowBlocked(BlockedInputState state) => Shown.Add(state);
        public void HideBlocked() => HideCount++;
        public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds) { }
        public void RaiseEscape() => EscapeRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseDismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeEscape : IEscapeWindowService
    {
        public IntPtr Target { get; } = new(42);
        public int TargetLookups { get; private set; }
        public List<IntPtr> BroughtToFront { get; } = new();
        public List<EscapeWindowResult> Results { get; } = new();

        public IntPtr? TryGetEscapeTarget()
        {
            TargetLookups++;
            return Target;
        }

        public EscapeWindowResult TryBringToFront(IntPtr hwnd)
        {
            BroughtToFront.Add(hwnd);
            var result = new EscapeWindowResult(true, false, UsedShowWindowFallback: true);
            Results.Add(result);
            return result;
        }
    }

    private sealed class FakeRing : ICursorCircleOverlay
    {
        public List<bool> BlockedStates { get; } = new();

        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPinned(bool pinned) { }
        public void SetBlocked(bool blocked) => BlockedStates.Add(blocked);
    }

    private sealed class FakeCursor : ICursorPositionProbe
    {
        public PhysicalPoint? TryGetCursorPosition() => new PhysicalPoint(300, 300);
    }

    private sealed class FakeIntegrity : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(0x2000, false, false);
        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;
        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }
}
