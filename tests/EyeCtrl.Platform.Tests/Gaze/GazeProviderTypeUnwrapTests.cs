namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging.Abstractions;
using NFluent;
using Xunit;

/// <summary>
/// <see cref="GazeProviderFactory.GetProviderType"/> answers Mouse for anything it does not recognise,
/// so a decorated provider would make the toolbar tell the user their eye tracker is not in use.
/// These pin the unwrap — the guarantee the raw-gaze recorder depends on to stay invisible.
/// </summary>
public class GazeProviderTypeUnwrapTests
{
    private sealed class NullSink : IGazeRecordingSink
    {
        public bool IsRecording => true;
        public void Record(in GazePoint sample) { }
        public void RequestFlush() { }
        public void Dispose() { }
    }

    private sealed class PassThroughDecorator : IGazeProviderDecorator
    {
        public PassThroughDecorator(IGazeProvider inner) => Inner = inner;
        public IGazeProvider Inner { get; }
        public event Action<GazePoint>? GazePointReceived { add { } remove { } }
        public bool IsConnected => Inner.IsConnected;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public void WrappedMouseProvider_StillReportsMouse()
    {
        using var inner = new MouseGazeProvider(logger: NullLogger<MouseGazeProvider>.Instance);
        using var wrapped = new RecordingGazeProvider(
            inner, new NullSink(), new DiagnosticsSettings { RecordRawGaze = true });

        Check.That(GazeProviderFactory.GetProviderType(wrapped)).IsEqualTo(GazeProviderType.Mouse);
    }

    [Fact]
    public void NestedDecorators_AreUnwrappedToTheRealProvider()
    {
        // Nothing nests this deep today; the unwrap is a loop rather than a single step so that adding
        // a second decorator later cannot silently re-break the toolbar's engine readout.
        using var inner = new MouseGazeProvider(logger: NullLogger<MouseGazeProvider>.Instance);
        var wrapped = new PassThroughDecorator(new PassThroughDecorator(inner));

        Check.That(GazeProviderFactory.GetProviderType(wrapped)).IsEqualTo(GazeProviderType.Mouse);
    }

    [Fact]
    public void UndecoratedProvider_IsUnaffected()
    {
        using var inner = new MouseGazeProvider(logger: NullLogger<MouseGazeProvider>.Instance);

        Check.That(GazeProviderFactory.GetProviderType(inner)).IsEqualTo(GazeProviderType.Mouse);
    }

#if TOBII_AVAILABLE
    [Fact]
    public void WrappedStreamEngineProvider_StillReportsStreamEngine()
    {
        // The case that matters: with recording on, the toolbar must not tell a Tobii user they are on
        // mouse simulation. Constructing the provider does not touch the device — connection happens in
        // Start(), which this test never calls.
        using var inner = new TobiiStreamEngineProvider(
            1920, 1080, NullLogger<TobiiStreamEngineProvider>.Instance);
        using var wrapped = new RecordingGazeProvider(
            inner, new NullSink(), new DiagnosticsSettings { RecordRawGaze = true });

        Check.That(GazeProviderFactory.GetProviderType(wrapped)).IsEqualTo(GazeProviderType.StreamEngine);
    }
#endif
}
