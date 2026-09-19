namespace EyeCtrl.App.Tests.Services;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Nothing in the app listened for a display change, and three geometry caches were filled once at
/// startup and never refreshed. A dock, undock, resolution change or RDP reconnect then left gaze
/// addressing the wrong pixel space with dwell targets displaced from where they were drawn — and
/// the supervisor could not see it, because a stale-geometry app still delivers frames so the
/// heartbeat stayed fresh. These pin the refresh and, as importantly, the cases where it must NOT
/// fire.
///
/// <para>
/// This class drives the production watcher, whose <c>Apply</c> calls
/// <c>DpiHelper.Update(2.0, 2.0)</c> for this user's real display geometry, so every test here that
/// applies <c>Uhd</c> changes a process-global that later tests read. It restores per test in
/// <see cref="Dispose"/>, which runs even when an assertion throws — unlike the bare trailing
/// statement the scale-republication test used to end with. <c>Dispose</c> runs after xUnit's
/// <c>BeforeAfterTestAttribute.After</c>, which is why restore-on-dispose is enough here: an
/// <c>After</c>-time assert could not see this class's cleanup, but the next test can. The
/// assembly-wide <c>DpiHelperRestore</c> hook is the second half — it covers indirect writers no
/// class-level cleanup governs, which is what made this class's five non-restoring tests reach
/// <c>KeyboardEmojiDwellFeedbackTests</c> and activate its category tab instead of its cell.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class DisplayGeometryWatcherTests : IDisposable
{
    private static readonly DisplayGeometry Hd = new(1920, 1080, 1.0, 1.0);
    private static readonly DisplayGeometry Uhd = new(3840, 2160, 2.0, 2.0);

    /// <summary>
    /// Per-test restore for the scale the production watcher republishes. xUnit disposes the test
    /// instance even when the test failed, which is the property the old trailing restore lacked.
    /// Belt-and-braces beside the assembly-wide hook; see the class doc.
    /// </summary>
    public void Dispose() => DpiHelper.Initialize(1.0, 1.0);

    private sealed class Harness
    {
        public DisplayGeometry Reported = Hd;
        public int ReadFailures;
        public List<(double W, double H)> ProviderUpdates { get; } = new();
        public List<DisplayGeometry> Messages { get; } = new();
        public IMessenger Messenger { get; } = new WeakReferenceMessenger();
        public DisplayGeometryWatcher Watcher { get; }
        public int Subscribes { get; private set; }
        public int Unsubscribes { get; private set; }

        public Harness(DisplayGeometry initial)
        {
            Messenger.Register<DisplayGeometryChangedMessage>(this, (_, m) => Messages.Add(m.Geometry));
            Watcher = new DisplayGeometryWatcher(
                new RecordingProvider(ProviderUpdates),
                Messenger,
                read: () =>
                {
                    if (ReadFailures-- > 0) throw new InvalidOperationException("system read failed");
                    return Reported;
                },
                postToUi: work => work(),   // inline: the test thread is the UI thread
                initial: initial,
                subscribe: _ => Subscribes++,
                unsubscribe: _ => Unsubscribes++);
        }

        public void FireDisplayChange() => Watcher.OnDisplaySettingsChanged();
    }

    private sealed class RecordingProvider : IGazeProvider
    {
        private readonly List<(double W, double H)> _updates;

        public RecordingProvider(List<(double W, double H)> updates) => _updates = updates;

        public void UpdateScreenBounds(double widthPx, double heightPx) => _updates.Add((widthPx, heightPx));

        public event Action<GazePoint>? GazePointReceived;
        public void Start() { }
        public void Stop() { }
        public bool IsConnected => true;
        public GazeProviderCapabilities Capabilities => GazeProviderCapabilities.None;
        public void Dispose() { }
    }

    [Fact]
    public void AResolutionChange_RefreshesTheProviderAndNotifiesConsumers()
    {
        // The core case. Before this, the provider kept scaling normalised gaze by the bounds
        // captured at construction, so a screen that grew to 3840 confined the user to the left
        // half of it — permanently, and invisibly to every existing check.
        var h = new Harness(Hd) { Reported = Uhd };

        h.FireDisplayChange();

        Check.That(h.ProviderUpdates).ContainsExactly((3840.0, 2160.0));
        Check.That(h.Messages).HasSize(1);
        Check.That(h.Messages[0]).IsEqualTo(Uhd);
    }

    [Fact]
    public void TheScaleIsRepublishedBeforeConsumersAreNotified()
    {
        // Ordering, not just occurrence. Consumers re-measure their own geometry during the
        // notification (the toolbar converts its logical window bounds to physical px through
        // DpiHelper), so a consumer that runs before the scale is updated measures itself with the
        // OLD factor and publishes a rect that belongs to neither screen.
        DpiHelper.Initialize(1.0, 1.0);
        var h = new Harness(Hd) { Reported = Uhd };
        double scaleSeenByConsumer = 0;
        h.Messenger.Register<DisplayGeometryChangedMessage>(new object(), (_, _) => scaleSeenByConsumer = DpiHelper.ScaleX);

        h.FireDisplayChange();

        Check.That(scaleSeenByConsumer).IsEqualTo(2.0);
        Check.That(DpiHelper.ScaleX).IsEqualTo(2.0);
        // No restore here: the class Dispose restores per test, including when the assertions
        // above fail. This line used to be a bare trailing Initialize(1.0, 1.0) — one failed
        // assertion away from leaking 2.0 into every later test.
    }

    [Fact]
    public void IdenticalGeometry_IsNotReapplied()
    {
        // Display-change notifications arrive in bursts and usually report the same geometry.
        // Re-applying is not free: it repositions overlays and republishes dwell targets, and doing
        // that repeatedly during a mode switch is visible to the user.
        var h = new Harness(Hd) { Reported = Hd };

        h.FireDisplayChange();
        h.FireDisplayChange();
        h.FireDisplayChange();

        Check.That(h.ProviderUpdates).IsEmpty();
        Check.That(h.Messages).IsEmpty();
    }

    [Fact]
    public void ABurstReportingTheSameNewGeometry_AppliesOnce()
    {
        var h = new Harness(Hd) { Reported = Uhd };

        h.FireDisplayChange();
        h.FireDisplayChange();
        h.FireDisplayChange();

        Check.That(h.ProviderUpdates).HasSize(1);
        Check.That(h.Messages).HasSize(1);
    }

    [Fact]
    public void ZeroSizedGeometry_IsIgnored()
    {
        // Windows reports 0x0 briefly during a mode switch and while every monitor is detached.
        // Scaling normalised gaze by zero sends every sample to the origin and shrinks the summon
        // region to nothing — which would read as total tracking failure and persist until the next
        // display change. Being one notification stale is strictly better, and the correct value
        // arrives later in the same burst.
        var h = new Harness(Hd) { Reported = new DisplayGeometry(0, 0, 1.0, 1.0) };

        h.FireDisplayChange();

        Check.That(h.ProviderUpdates).IsEmpty();
        Check.That(h.Messages).IsEmpty();

        // ...and the real geometry that follows in the burst is still applied.
        h.Reported = Uhd;
        h.FireDisplayChange();
        Check.That(h.ProviderUpdates).ContainsExactly((3840.0, 2160.0));
    }

    [Fact]
    public void AFailedRead_DoesNotThrow_AndTheNextNotificationStillWorks()
    {
        // This runs off an OS notification marshalled onto the dispatcher, where the app
        // deliberately leaves DispatcherUnhandledException unhandled — so an escaping read failure
        // would kill the process, costing the user their input channel over a transient WMI/Win32
        // hiccup.
        var h = new Harness(Hd) { Reported = Uhd, ReadFailures = 1 };

        Check.ThatCode(() => h.FireDisplayChange()).DoesNotThrow();
        Check.That(h.ProviderUpdates).IsEmpty();

        h.FireDisplayChange();
        Check.That(h.ProviderUpdates).ContainsExactly((3840.0, 2160.0));
    }

    [Fact]
    public void AThrowingConsumer_DoesNotStopTheProviderBeingUpdated()
    {
        // The worst outcome would be overlays that have moved to the new geometry while gaze still
        // arrives in the old space — a mismatch created by the fix itself. The provider update must
        // not be reachable only through a consumer's success.
        var h = new Harness(Hd) { Reported = Uhd };
        h.Messenger.Register<DisplayGeometryChangedMessage>(new object(), (_, _) => throw new InvalidOperationException("consumer blew up"));

        Check.ThatCode(() => h.FireDisplayChange()).DoesNotThrow();
        Check.That(h.ProviderUpdates).ContainsExactly((3840.0, 2160.0));
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheOsNotification()
    {
        // SystemEvents holds a STATIC reference to the handler, so a watcher that never unsubscribes
        // outlives the app object and fires into disposed services.
        var h = new Harness(Hd);
        h.Watcher.Start();

        h.Watcher.Dispose();

        Check.That(h.Subscribes).IsEqualTo(1);
        Check.That(h.Unsubscribes).IsEqualTo(1);
    }

    [Fact]
    public void StartIsIdempotent_AndDisposeStopsFurtherWork()
    {
        var h = new Harness(Hd) { Reported = Uhd };
        h.Watcher.Start();
        h.Watcher.Start();
        Check.That(h.Subscribes).IsEqualTo(1);

        h.Watcher.Dispose();
        h.FireDisplayChange();

        Check.That(h.ProviderUpdates).IsEmpty();
    }
}
