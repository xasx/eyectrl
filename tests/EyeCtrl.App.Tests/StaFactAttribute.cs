namespace EyeCtrl.App.Tests;

using System.Windows.Threading;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// xUnit v2-compatible attribute that runs a test on an STA thread (required for WPF).
/// Replace [Fact] with [StaFact] for tests that touch WPF types.
/// DpiHelper.Initialize(1.0, 1.0) must be called within the test body.
/// </summary>
[XunitTestCaseDiscoverer("EyeCtrl.App.Tests.StaFactDiscoverer", "EyeCtrl.App.Tests")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StaFactAttribute : FactAttribute { }

public class StaFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    public StaFactDiscoverer(IMessageSink diagnosticMessageSink)
    {
        _diagnosticMessageSink = diagnosticMessageSink;
    }

    public IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo factAttribute)
    {
        yield return new StaTestCase(_diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), testMethod);
    }
}

public class StaTestCase : XunitTestCase
{
    [Obsolete("Called by the deserializer")]
    public StaTestCase() { }

    public StaTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, TestMethodDisplayOptions.None, testMethod) { }

    public override async Task<RunSummary> RunAsync(
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        object[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        // Run on STA thread
        var tcs = new TaskCompletionSource<RunSummary>();
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var syncTask = base.RunAsync(diagnosticMessageSink, messageBus, constructorArguments, aggregator, cancellationTokenSource);
                syncTask.GetAwaiter().GetResult();
                var summary = syncTask.Result;
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                Dispatcher.Run();
                tcs.SetResult(summary);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await tcs.Task;
    }
}
