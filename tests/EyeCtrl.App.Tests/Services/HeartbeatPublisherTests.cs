namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;

public class HeartbeatPublisherTests
{
    [Fact]
    public void Stamp_ReflectsLastGazeSample()
    {
        var pub = new HeartbeatPublisher(supervised: false);
        pub.OnGazeSample(); // gaze thread would call this per sample
        pub.Stamp(isConnected: true, isPaused: false);
        var snapshot = pub.CurrentPayload;
        Check.That(snapshot).IsNotNull();
        Check.That(snapshot!.IsConnected).IsTrue();
        Check.That(snapshot.IsPaused).IsFalse();
        Check.That(snapshot.ShuttingDown).IsFalse();
        Check.That(snapshot.LastFrameUtc).IsCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SignalShuttingDown_StampsFinalPayload()
    {
        var pub = new HeartbeatPublisher(supervised: false);
        pub.SignalShuttingDown();
        Check.That(pub.CurrentPayload).IsNotNull();
        Check.That(pub.CurrentPayload!.ShuttingDown).IsTrue();
    }
}
