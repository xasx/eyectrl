#if TOBII_AVAILABLE
namespace EyeCtrl.Platform.Tests.Gaze;

using System.Diagnostics;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging.Abstractions;

public class TobiiStreamEngineProviderStopTests
{
    [Fact]
    public void Stop_ReturnsPromptly_EvenDuringReconnectBackoff()
    {
        var provider = new TobiiStreamEngineProvider(1920, 1080,
            NullLogger<TobiiStreamEngineProvider>.Instance);
        provider.Start();

        Thread.Sleep(1500);

        var sw = Stopwatch.StartNew();
        provider.Stop();
        sw.Stop();

        Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(2500);
        provider.Dispose();
    }
}
#endif
