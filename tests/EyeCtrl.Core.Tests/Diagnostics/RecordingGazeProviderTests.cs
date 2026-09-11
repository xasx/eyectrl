namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The decorator must be invisible to the pipeline it observes. These pin the two properties that
/// make that true: every sample is re-raised unchanged whatever the recorder does, and no recording
/// fault can escape into the provider — on the Tobii poll thread a managed throw unwinds through a
/// native frame and is treated as a device error, which costs the user their only input channel.
/// </summary>
public class RecordingGazeProviderTests
{
    private sealed class FakeInnerProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }
        public GazeProviderCapabilities Capabilities => GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose;

        public void Start() { StartCount++; IsConnected = true; }
        public void Stop() { StopCount++; IsConnected = false; }
        public void Dispose() => Disposed = true;
        public void Emit(GazePoint p) => GazePointReceived?.Invoke(p);
        public bool HasSubscribers => GazePointReceived is not null;
    }

    private sealed class RecordingSpy : IGazeRecordingSink
    {
        public List<GazePoint> Recorded { get; } = new();
        public int FlushRequests { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? ThrowOnRecord { get; set; }
        public bool IsRecording => true;

        public void Record(in GazePoint sample)
        {
            if (ThrowOnRecord is { } ex) throw ex;
            Recorded.Add(sample);
        }

        public void RequestFlush() => FlushRequests++;
        public void Dispose() => Disposed = true;
    }

    private static (RecordingGazeProvider Provider, FakeInnerProvider Inner, RecordingSpy Sink, DiagnosticsSettings Settings, List<Exception> Faults)
        New(bool recording)
    {
        var inner = new FakeInnerProvider();
        var sink = new RecordingSpy();
        var settings = new DiagnosticsSettings { RecordRawGaze = recording };
        var faults = new List<Exception>();
        return (new RecordingGazeProvider(inner, sink, settings, faults.Add), inner, sink, settings, faults);
    }

    [Fact]
    public void Sample_IsReRaisedUnchanged()
    {
        var (provider, inner, _, _, _) = New(recording: true);
        using var _p = provider;
        var seen = new List<GazePoint>();
        provider.GazePointReceived += seen.Add;

        var sample = new GazePoint(123.5, 456.25, 99_000, IsValid: true);
        inner.Emit(sample);

        Check.That(seen).HasSize(1);
        Check.That(seen[0]).IsEqualTo(sample);
    }

    [Fact]
    public void Sample_IsRecorded_WhenEnabled()
    {
        var (provider, inner, sink, _, _) = New(recording: true);
        using var _p = provider;

        inner.Emit(new GazePoint(1, 2, 10, true));
        inner.Emit(new GazePoint(3, 4, 20, false));

        Check.That(sink.Recorded).HasSize(2);
        Check.That(sink.Recorded[1].IsValid).IsFalse();   // invalid samples are recorded too — gaps are data
    }

    [Fact]
    public void Disabled_RecordsNothing_ButStillReRaises()
    {
        // The shipped default. With the gate off the recorder must be inert, not merely quiet.
        var (provider, inner, sink, _, _) = New(recording: false);
        using var _p = provider;
        var seen = new List<GazePoint>();
        provider.GazePointReceived += seen.Add;

        for (int i = 0; i < 50; i++)
            inner.Emit(new GazePoint(i, i, i * 1000, true));

        Check.That(sink.Recorded).IsEmpty();
        Check.That(seen).HasSize(50);
    }

    [Fact]
    public void Gate_IsReadPerSample()
    {
        var (provider, inner, sink, settings, _) = New(recording: false);
        using var _p = provider;

        inner.Emit(new GazePoint(1, 1, 10, true));
        settings.RecordRawGaze = true;
        inner.Emit(new GazePoint(2, 2, 20, true));

        Check.That(sink.Recorded).HasSize(1);
        Check.That(sink.Recorded[0].X).IsEqualTo(2.0);
    }

    [Fact]
    public void SinkFault_DoesNotEscape_AndSelfDisables()
    {
        var (provider, inner, sink, _, faults) = New(recording: true);
        using var _p = provider;
        var seen = new List<GazePoint>();
        provider.GazePointReceived += seen.Add;
        sink.ThrowOnRecord = new InvalidOperationException("disk on fire");

        Check.ThatCode(() => inner.Emit(new GazePoint(1, 1, 10, true))).DoesNotThrow();

        Check.That(provider.RecordingFaulted).IsTrue();
        Check.That(faults).HasSize(1);
        Check.That(seen).HasSize(1);   // the sample still reached the pipeline
    }

    [Fact]
    public void AfterFault_NoFurtherRecordingAttempts_AndOnlyOneReport()
    {
        // A repeating fault would be a per-sample try/catch storm on the gaze thread, and would bury
        // the log. One report, then the tap is done for the session.
        var (provider, inner, sink, _, faults) = New(recording: true);
        using var _p = provider;
        sink.ThrowOnRecord = new InvalidOperationException("disk on fire");

        for (int i = 0; i < 20; i++)
            inner.Emit(new GazePoint(i, i, i * 1000, true));

        Check.That(faults).HasSize(1);
        Check.That(sink.Recorded).IsEmpty();
    }

    [Fact]
    public void ConsumerException_PropagatesUnchanged()
    {
        // The decorator is transparent in both directions: it must not turn a pipeline fault into a
        // silently swallowed one just by being in the chain.
        var (provider, inner, _, _, _) = New(recording: true);
        using var _p = provider;
        provider.GazePointReceived += _ => throw new InvalidOperationException("consumer");

        Check.ThatCode(() => inner.Emit(new GazePoint(1, 1, 10, true)))
             .Throws<InvalidOperationException>();
    }

    [Fact]
    public void Stop_RequestsFlush_AndForwards()
    {
        var (provider, inner, sink, _, _) = New(recording: true);
        using var _p = provider;

        provider.Stop();

        Check.That(sink.FlushRequests).IsEqualTo(1);
        Check.That(inner.StopCount).IsEqualTo(1);
    }

    [Fact]
    public void Stop_ForwardsEvenWhenTheFlushFaults()
    {
        // Stopping the tracker must not depend on the diagnostic succeeding.
        var inner = new FakeInnerProvider();
        var sink = new ThrowingFlushSink();
        var faults = new List<Exception>();
        using var provider = new RecordingGazeProvider(
            inner, sink, new DiagnosticsSettings { RecordRawGaze = true }, faults.Add);

        Check.ThatCode(() => provider.Stop()).DoesNotThrow();
        Check.That(inner.StopCount).IsEqualTo(1);
        Check.That(faults).HasSize(1);
    }

    private sealed class ThrowingFlushSink : IGazeRecordingSink
    {
        public bool IsRecording => true;
        public void Record(in GazePoint sample) { }
        public void RequestFlush() => throw new IOException("flush failed");
        public void Dispose() { }
    }

    [Fact]
    public void FaultReport_IsOncePerInstance_AcrossDifferentFaultSites()
    {
        // The constructor promises the callback fires at most once. Three sites can report — record,
        // flush and dispose — so without a latch a record fault followed by a flush fault would report
        // twice and quietly break that contract. Once per SITE would also be wrong: a failing recorder
        // that reports on every teardown step is noise, not diagnosis.
        var inner = new FakeInnerProvider();
        var sink = new AlwaysThrowingSink();
        var faults = new List<Exception>();
        var provider = new RecordingGazeProvider(
            inner, sink, new DiagnosticsSettings { RecordRawGaze = true }, faults.Add);

        inner.Emit(new GazePoint(1, 1, 10, true));   // record fault
        provider.Stop();                             // flush fault
        provider.Dispose();                          // dispose fault

        Check.That(faults).HasSize(1);
    }

    private sealed class AlwaysThrowingSink : IGazeRecordingSink
    {
        public bool IsRecording => true;
        public void Record(in GazePoint sample) => throw new IOException("record failed");
        public void RequestFlush() => throw new IOException("flush failed");
        public void Dispose() => throw new IOException("dispose failed");
    }

    [Fact]
    public void PassThroughMembers_AreUnchangedToConsumers()
    {
        var (provider, inner, _, _, _) = New(recording: true);
        using var _p = provider;

        provider.Start();

        Check.That(inner.StartCount).IsEqualTo(1);
        Check.That(provider.IsConnected).IsTrue();
        Check.That(provider.Capabilities).IsEqualTo(GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose);
        Check.That(provider.Inner).IsSameReferenceAs(inner);
    }

    [Fact]
    public void Dispose_UnsubscribesFromInner_AndDisposesBoth()
    {
        var (provider, inner, sink, _, _) = New(recording: true);

        provider.Dispose();

        Check.That(inner.HasSubscribers).IsFalse();
        Check.That(sink.Disposed).IsTrue();
        Check.That(inner.Disposed).IsTrue();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (provider, _, _, _, _) = New(recording: true);
        provider.Dispose();
        Check.ThatCode(() => provider.Dispose()).DoesNotThrow();
    }
}
